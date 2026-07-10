using Microsoft.Playwright;

namespace EmsScout.Collection;

public sealed class PlaywrightEdgeCdpProbe
{
    public async Task<bool> CanConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint.ToString()).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return browser.Contexts.Count > 0;
    }
}
