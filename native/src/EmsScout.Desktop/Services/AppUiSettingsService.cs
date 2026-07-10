using EmsScout.Application.Settings;
using Microsoft.UI.Xaml;

namespace EmsScout.Desktop.Services;

public sealed class AppUiSettingsService(AppSettingsService settingsService)
{
    public void ApplyTheme(FrameworkElement root)
    {
        root.RequestedTheme = ThemeFrom(settingsService.Current.Theme);
    }

    public ElementTheme CurrentTheme => ThemeFrom(settingsService.Current.Theme);

    public bool CompactDataTable => settingsService.Current.CompactDataTable;

    public bool TrackRecentExports => settingsService.Current.TrackRecentExports;

    private static ElementTheme ThemeFrom(string theme)
    {
        return theme.ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
