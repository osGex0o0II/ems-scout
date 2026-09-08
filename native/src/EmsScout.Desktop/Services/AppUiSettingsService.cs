using EmsScout.Application.Settings;
using Microsoft.UI.Xaml;

namespace EmsScout.Desktop.Services;

public sealed class AppUiSettingsService(AppSettingsService settingsService)
{
    public event EventHandler? SettingsChanged;

    public void ApplyTheme(FrameworkElement root)
    {
        root.RequestedTheme = ThemeFrom(settingsService.Current.Theme);
    }

    public ElementTheme CurrentTheme => ThemeFrom(settingsService.Current.Theme);

    public bool CompactDataTable => settingsService.Current.CompactDataTable;

    public bool TrackRecentExports => settingsService.Current.TrackRecentExports;

    public double TemperatureWarningThreshold => settingsService.Current.TemperatureWarningThreshold;

    public string OfflineStatusColor => settingsService.Current.OfflineStatusColor;

    public string TemperatureWarningColor => settingsService.Current.TemperatureWarningColor;

    public bool ReduceMotion => settingsService.Current.ReduceMotion;

    public string PageTransitionStyle => settingsService.Current.PageTransitionStyle;

    public bool CloseToTray => settingsService.Current.CloseToTray;

    public void ApplyCurrentSettings(FrameworkElement root)
    {
        ApplyTheme(root);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

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
