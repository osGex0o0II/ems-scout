using System.Diagnostics;

namespace EmsScout.Infrastructure.Sidecar;

public static class OwnedEdgeCdpEndpoint
{
    public static async Task<int> WaitForPortAsync(
        string profileDirectory,
        Process process,
        CancellationToken cancellationToken = default)
    {
        var activePortPath = Path.Combine(profileDirectory, "DevToolsActivePort");
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException("Owned Edge exited before its CDP endpoint was ready.");
            }

            if (TryReadPort(activePortPath, out var port))
            {
                return port;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Owned Edge did not publish DevToolsActivePort within five seconds.");
    }

    public static bool TryReadPort(string activePortPath, out int port)
    {
        port = 0;
        try
        {
            var lines = File.ReadAllLines(activePortPath);
            return lines.Length >= 2 &&
                   int.TryParse(lines[0], out port) &&
                   port is > 0 and <= 65535 &&
                   lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
