using EmsScout.Application.Updates;
using Windows.System;

namespace EmsScout.Desktop.Services;

public sealed class WindowsAppUpdateLauncher : IAppUpdateLauncher
{
    public async Task<bool> LaunchAsync(
        Uri appInstallerUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appInstallerUri);
        if (!appInstallerUri.IsAbsoluteUri ||
            !string.Equals(appInstallerUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("App Installer source must use HTTPS.", nameof(appInstallerUri));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var source = Uri.EscapeDataString(appInstallerUri.AbsoluteUri);
        var launched = await Launcher.LaunchUriAsync(new Uri($"ms-appinstaller:?source={source}"));
        if (launched)
        {
            Microsoft.UI.Xaml.Application.Current.Exit();
        }

        return launched;
    }
}
