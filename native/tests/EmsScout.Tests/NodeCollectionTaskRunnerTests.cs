using System.Collections.Concurrent;
using System.Diagnostics;
using EmsScout.Application.Workflows;
using EmsScout.Infrastructure.Sidecar;

namespace EmsScout.Tests;

public sealed class NodeCollectionTaskRunnerTests
{
    [Fact]
    public async Task MalformedWorkflowOutputIsRejected()
    {
        if (!CanRunNode())
        {
            return;
        }

        var workspace = await CreateFaultWorkspaceAsync("process.stdout.write('{bad json\\n');");
        try
        {
            var runner = new NodeCollectionTaskRunner(workspace);
            var error = await Assert.ThrowsAsync<WorkflowEventParseException>(() =>
                runner.RunWorkflowScriptAsync("child.js", [], _ => { }, _ => { }, CancellationToken.None));

            Assert.Contains("not valid JSON", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task TruncatedWorkflowOutputIsRejected()
    {
        if (!CanRunNode())
        {
            return;
        }

        var workspace = await CreateFaultWorkspaceAsync(StartedEventSource());
        try
        {
            var runner = new NodeCollectionTaskRunner(workspace);
            var error = await Assert.ThrowsAsync<WorkflowEventParseException>(() =>
                runner.RunWorkflowScriptAsync("child.js", [], _ => { }, _ => { }, CancellationToken.None));

            Assert.Contains("without a terminal event", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationWatchdogKillsUnresponsiveSidecarWithoutMaskingCancellation()
    {
        if (!CanRunNode())
        {
            return;
        }

        var workspace = await CreateFaultWorkspaceAsync(StartedEventSource() + "setInterval(()=>{},1000);");
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var runner = new NodeCollectionTaskRunner(
                workspace,
                cancellationGracePeriod: TimeSpan.FromMilliseconds(200));
            var stopwatch = Stopwatch.StartNew();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runner.RunWorkflowScriptAsync("child.js", [], _ => { }, _ => { }, cancellation.Token));

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Forced cancellation took {stopwatch.Elapsed}.");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationUsesControlChannelAndReceivesCancelledTerminal()
    {
        if (!CanRunNode())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "ems-scout-sidecar-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "sidecar"));
        try
        {
            foreach (var file in new[] { "runner.js", "workflow-event.js", "legacy-line-adapter.js" })
            {
                File.Copy(
                    Path.Combine(repositoryRoot, "sidecar", file),
                    Path.Combine(workspace, "sidecar", file));
            }

            const string scriptName = "long-running-child.js";
            await File.WriteAllTextAsync(
                Path.Combine(workspace, scriptName),
                "process.on('SIGINT',()=>process.exit(0));setInterval(()=>{},1000);");

            var events = new ConcurrentQueue<WorkflowEventV1>();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var runner = new NodeCollectionTaskRunner(workspace);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunWorkflowScriptAsync(
                scriptName,
                [],
                _ => { },
                events.Enqueue,
                cancellation.Token));

            var captured = events.ToArray();
            Assert.Equal(WorkflowEventType.Started, captured.First().Type);
            Assert.Contains(captured, item =>
                item.Type == WorkflowEventType.Action && item.Action == "cancel_requested");
            var terminal = Assert.Single(captured, item => item.Type == WorkflowEventType.Terminal);
            Assert.Equal(WorkflowTerminalOutcome.Cancelled, terminal.Outcome);
            Assert.Equal(130, terminal.ExitCode);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task DisposingRunnerTerminatesAnActiveSidecar()
    {
        if (!CanRunNode())
        {
            return;
        }

        var workspace = await CreateFaultWorkspaceAsync(StartedEventSource() + "setInterval(()=>{},1000);");
        try
        {
            using var runner = new NodeCollectionTaskRunner(workspace);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = runner.RunWorkflowScriptAsync(
                "child.js",
                [],
                _ => { },
                workflowEvent =>
                {
                    if (workflowEvent.Type == WorkflowEventType.Started) started.TrySetResult();
                },
                CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

            runner.Dispose();

            await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static bool CanRunNode()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            process?.WaitForExit(5000);
            return process is { HasExited: true, ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> CreateFaultWorkspaceAsync(string runnerSource)
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "ems-scout-sidecar-fault-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "sidecar"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "sidecar", "runner.js"), runnerSource);
        await File.WriteAllTextAsync(Path.Combine(workspace, "child.js"), "process.exit(0);");
        return workspace;
    }

    private static string StartedEventSource() => """
        const workflowId=process.argv.find(value=>value.startsWith('--workflow-id=')).split('=')[1];
        const stage=process.argv.find(value=>value.startsWith('--stage=')).split('=')[1];
        console.log(JSON.stringify({
          contractVersion:'ems.workflow-event/v1',workflowId,seq:1,
          timestamp:new Date().toISOString(),type:'started',stage
        }));
        """;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                File.Exists(Path.Combine(directory.FullName, "sidecar", "runner.js")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate the repository root for Sidecar tests.");
    }
}
