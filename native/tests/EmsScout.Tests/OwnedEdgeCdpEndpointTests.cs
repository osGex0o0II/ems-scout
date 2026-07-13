using EmsScout.Infrastructure.Sidecar;

namespace EmsScout.Tests;

public sealed class OwnedEdgeCdpEndpointTests
{
    [Fact]
    public async Task ReadsOnlyBrowserOwnedDevToolsActivePortShape()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ems-owned-edge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var activePort = Path.Combine(directory, "DevToolsActivePort");
        try
        {
            await File.WriteAllTextAsync(activePort, "43123\n/devtools/browser/owned-session\n");
            Assert.True(OwnedEdgeCdpEndpoint.TryReadPort(activePort, out var port));
            Assert.Equal(43123, port);

            await File.WriteAllTextAsync(activePort, "43123\n/devtools/page/not-browser\n");
            Assert.False(OwnedEdgeCdpEndpoint.TryReadPort(activePort, out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
