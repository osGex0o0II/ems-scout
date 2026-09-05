using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using EmsScout.Application.Collection;

namespace EmsScout.Desktop.Services;

public sealed class NodeCollectionTaskRunner(string workspaceRoot)
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex AnsiEscapeRegex = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))",
        RegexOptions.Compiled);

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
            FileName = NodeRuntimeResolver.Resolve(),
            WorkingDirectory = WorkspaceRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
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
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }
            var line = AnsiEscapeRegex.Replace(rawLine, string.Empty);
            if (!string.IsNullOrWhiteSpace(line))
            {
                onOutput(line);
            }
        }
    }
}
