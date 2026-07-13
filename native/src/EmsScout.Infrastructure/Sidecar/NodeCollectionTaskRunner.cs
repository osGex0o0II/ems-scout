using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using EmsScout.Application.Workflows;

namespace EmsScout.Infrastructure.Sidecar;

public sealed record NodeWorkflowRunResult(
    string WorkflowId,
    int ExitCode,
    WorkflowTerminalOutcome Outcome,
    string? Message)
{
    public bool IsSuccessful => Outcome is
        WorkflowTerminalOutcome.Succeeded or
        WorkflowTerminalOutcome.SucceededWithFindings;
}

public sealed class NodeCollectionTaskRunner : IDisposable
{
    private static readonly TimeSpan DefaultCancellationGracePeriod = TimeSpan.FromSeconds(8);
    private readonly TimeSpan cancellationGracePeriod;
    private readonly ConcurrentDictionary<int, Process> activeProcesses = new();
    private int disposed;

    public NodeCollectionTaskRunner(
        string workspaceRoot,
        TimeSpan? cancellationGracePeriod = null)
    {
        WorkspaceRoot = workspaceRoot;
        this.cancellationGracePeriod = cancellationGracePeriod ?? DefaultCancellationGracePeriod;
        if (this.cancellationGracePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cancellationGracePeriod),
                "Cancellation grace period must be greater than zero.");
        }
    }

    public string WorkspaceRoot { get; }

    public bool UsesBundledRuntime => ResolveLayout().IsBundled;

    public string RuntimePath => ResolveLayout().RuntimePath;

    public string ApplicationRoot => ResolveLayout().ApplicationRoot;

    public async Task<NodeWorkflowRunResult> RunWorkflowScriptAsync(
        string relativeScriptPath,
        IReadOnlyList<string> arguments,
        Action<string> onHumanOutput,
        Action<WorkflowEventV1> onEvent,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        bool exitCodeTwoMeansFindings = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var layout = ResolveLayout();
        var scriptPath = Path.Combine(layout.ApplicationRoot, relativeScriptPath);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("Cannot find Node.js workflow script.", scriptPath);
        }

        if (!File.Exists(layout.RunnerPath))
        {
            throw new FileNotFoundException("Cannot find the EMS Scout sidecar runner.", layout.RunnerPath);
        }

        var stage = NormalizeStage(Path.GetFileNameWithoutExtension(relativeScriptPath));
        var workflowId = $"{stage}-{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo
        {
            FileName = layout.RuntimePath,
            WorkingDirectory = layout.ApplicationRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(layout.RunnerPath);
        startInfo.ArgumentList.Add("--workflow-id=" + workflowId);
        startInfo.ArgumentList.Add("--stage=" + stage);
        if (exitCodeTwoMeansFindings)
        {
            startInfo.ArgumentList.Add("--exit-2-outcome=succeeded_with_findings");
        }
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(layout.RuntimePath);
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["EMS_WORKFLOW_ID"] = workflowId;
        startInfo.Environment["EMS_SIDECAR_APP_ROOT"] = layout.ApplicationRoot;
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the EMS Scout sidecar process.");
        }
        activeProcesses.TryAdd(process.Id, process);
        if (Volatile.Read(ref disposed) != 0)
        {
            TryKill(process);
            throw new ObjectDisposedException(nameof(NodeCollectionTaskRunner));
        }

        var cancellationRequested = 0;
        var cancellationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processExitedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() =>
        {
            if (TryRequestCancellation(process, workflowId))
            {
                Interlocked.Exchange(ref cancellationRequested, 1);
                cancellationSignal.TrySetResult();
            }
        });
        var cancellationWatchdog = EnforceCancellationGracePeriodAsync(
            process,
            cancellationSignal.Task,
            processExitedSignal.Task,
            cancellationGracePeriod);

        var validator = new WorkflowEventStreamValidator();
        var stdout = PumpWorkflowEventsAsync(
            process.StandardOutput,
            workflowId,
            validator,
            onEvent);
        var stderr = PumpHumanOutputAsync(
            process.StandardError,
            line => onHumanOutput("[stderr] " + line));

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            processExitedSignal.TrySetResult();
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (Exception) when (Volatile.Read(ref cancellationRequested) != 0)
        {
            TryKill(process);
            throw new OperationCanceledException("The EMS Scout workflow was cancelled.", cancellationToken);
        }
        catch
        {
            TryKill(process);
            throw;
        }
        finally
        {
            processExitedSignal.TrySetResult();
            await cancellationWatchdog.ConfigureAwait(false);
            activeProcesses.TryRemove(process.Id, out _);
        }

        validator.EnsureComplete();
        var terminal = await stdout.ConfigureAwait(false);
        if (terminal.ExitCode != process.ExitCode)
        {
            throw new WorkflowEventParseException(
                $"Sidecar terminal exitCode {terminal.ExitCode} does not match process exit code {process.ExitCode}.");
        }

        if (Volatile.Read(ref cancellationRequested) != 0)
        {
            if (terminal.Outcome != WorkflowTerminalOutcome.Cancelled)
            {
                throw new WorkflowEventParseException(
                    $"Sidecar acknowledged cancellation but ended with outcome '{terminal.Outcome}'.");
            }

            throw new OperationCanceledException("The EMS Scout workflow was cancelled.", cancellationToken);
        }

        return new NodeWorkflowRunResult(
            workflowId,
            process.ExitCode,
            terminal.Outcome ?? throw new WorkflowEventParseException("Sidecar terminal outcome is missing."),
            terminal.Message);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (var process in activeProcesses.Values)
        {
            TryKill(process);
        }
    }

    private SidecarLayout ResolveLayout()
    {
        var bundledRoot = Path.Combine(AppContext.BaseDirectory, "Sidecar");
        var bundledRuntime = Path.Combine(bundledRoot, "runtime", "node.exe");
        var bundledApplication = Path.Combine(bundledRoot, "app");
        var bundledRunner = Path.Combine(bundledApplication, "sidecar", "runner.js");
        if (File.Exists(bundledRuntime) && File.Exists(bundledRunner))
        {
            return new SidecarLayout(
                bundledRuntime,
                bundledApplication,
                bundledRunner,
                IsBundled: true);
        }

        return new SidecarLayout(
            "node",
            WorkspaceRoot,
            Path.Combine(WorkspaceRoot, "sidecar", "runner.js"),
            IsBundled: false);
    }

    private static async Task<WorkflowEventV1> PumpWorkflowEventsAsync(
        StreamReader reader,
        string expectedWorkflowId,
        WorkflowEventStreamValidator validator,
        Action<WorkflowEventV1> onEvent)
    {
        WorkflowEventV1? terminal = null;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var workflowEvent = WorkflowEventParser.Parse(line);
            if (!workflowEvent.WorkflowId.Equals(expectedWorkflowId, StringComparison.Ordinal))
            {
                throw new WorkflowEventParseException(
                    $"Sidecar emitted workflowId '{workflowEvent.WorkflowId}' instead of '{expectedWorkflowId}'.");
            }
            validator.Accept(workflowEvent);
            onEvent(workflowEvent);
            if (workflowEvent.IsTerminal)
            {
                terminal = workflowEvent;
            }
        }

        return terminal ?? throw new WorkflowEventParseException("Sidecar stdout ended without a terminal event.");
    }

    private static async Task PumpHumanOutputAsync(StreamReader reader, Action<string> onOutput)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                onOutput(line);
            }
        }
    }

    private static string NormalizeStage(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')
            {
                output.Append(character);
            }
            else
            {
                output.Append('-');
            }
        }

        var stage = output.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(stage) ? "workflow" : stage[..Math.Min(stage.Length, 64)];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static bool TryRequestCancellation(Process process, string workflowId)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            process.StandardInput.WriteLine(WorkflowControlWriter.CreateCancel(workflowId));
            process.StandardInput.Flush();
            return true;
        }
        catch (Exception error) when (error is
            InvalidOperationException or
            IOException or
            System.ComponentModel.Win32Exception)
        {
            if (IsRunning(process))
            {
                TryKill(process);
                return true;
            }

            return false;
        }
    }

    private static async Task EnforceCancellationGracePeriodAsync(
        Process process,
        Task cancellationSignal,
        Task processExitedSignal,
        TimeSpan gracePeriod)
    {
        if (await Task.WhenAny(cancellationSignal, processExitedSignal).ConfigureAwait(false) != cancellationSignal)
        {
            return;
        }

        var timeout = Task.Delay(gracePeriod);
        if (await Task.WhenAny(processExitedSignal, timeout).ConfigureAwait(false) == timeout)
        {
            TryKill(process);
        }
    }

    private static bool IsRunning(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private sealed record SidecarLayout(
        string RuntimePath,
        string ApplicationRoot,
        string RunnerPath,
        bool IsBundled);
}
