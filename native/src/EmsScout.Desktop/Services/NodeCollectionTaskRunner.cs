using System.Diagnostics;

namespace EmsScout.Desktop.Services;

public sealed class NodeCollectionTaskRunner(string workspaceRoot)
{
    public string WorkspaceRoot { get; } = workspaceRoot;

    public async Task<int> RunNodeScriptAsync(
        string relativeScriptPath,
        IReadOnlyList<string> arguments,
        Action<string> onOutput,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var scriptPath = Path.Combine(WorkspaceRoot, relativeScriptPath);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("Cannot find Node.js script.", scriptPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = WorkspaceRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

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
            throw new InvalidOperationException("Failed to start Node.js process.");
        }

        await using var registration = cancellationToken.Register(() =>
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
        });

        var stdout = PumpAsync(process.StandardOutput, onOutput);
        var stderr = PumpAsync(process.StandardError, line => onOutput("[stderr] " + line));
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onOutput)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                onOutput(line);
            }
        }
    }
}
