using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Settings;
using Windows.UI;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class SettingsViewModel(AppSettingsService settingsService) : ObservableObject
{
    public event EventHandler? SettingsApplied;

    [ObservableProperty]
    public partial string EmsUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double EdgeCdpPort { get; set; }

    [ObservableProperty]
    public partial bool CheckLoginBeforeCollection { get; set; }

    [ObservableProperty]
    public partial string DataDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExportDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool TrackRecentExports { get; set; }

    [ObservableProperty]
    public partial int DefaultCollectionModeIndex { get; set; }

    [ObservableProperty]
    public partial int LogLevelIndex { get; set; }

    [ObservableProperty]
    public partial bool SaveNdjsonLog { get; set; }

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial bool CompactDataTable { get; set; }

    [ObservableProperty]
    public partial bool ReduceMotion { get; set; }

    [ObservableProperty]
    public partial int PageTransitionStyleIndex { get; set; }

    [ObservableProperty]
    public partial bool SaveWindowPlacement { get; set; }

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    public partial bool LaunchAtLogin { get; set; }

    [ObservableProperty]
    public partial bool ShowInSendTo { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial double TemperatureWarningThreshold { get; set; } = 2.0;

    [ObservableProperty]
    public partial Color OfflineStatusColor { get; set; } = Color.FromArgb(255, 128, 128, 128);

    [ObservableProperty]
    public partial Color TemperatureWarningColor { get; set; } = Color.FromArgb(255, 217, 119, 6);

    public IReadOnlyList<ColorPresetOption> OfflineStatusColorOptions { get; } =
    [
        new("浅灰", Color.FromArgb(255, 154, 160, 166)),
        new("中灰", Color.FromArgb(255, 128, 128, 128)),
        new("深灰", Color.FromArgb(255, 95, 99, 104)),
    ];

    public IReadOnlyList<ColorPresetOption> TemperatureWarningColorOptions { get; } =
    [
        new("浅黄", Color.FromArgb(255, 233, 196, 106)),
        new("琥珀", Color.FromArgb(255, 217, 119, 6)),
        new("深黄", Color.FromArgb(255, 180, 83, 9)),
    ];

    [ObservableProperty]
    public partial ColorPresetOption? SelectedOfflineStatusColor { get; set; }

    [ObservableProperty]
    public partial ColorPresetOption? SelectedTemperatureWarningColor { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "设置尚未加载";

    public string SettingsPath => settingsService.SettingsPath;

    public void SetStatus(string text)
    {
        StatusText = text;
    }

    public void Load()
    {
        Apply(settingsService.Load());
        StatusText = "已加载设置";
    }

    [RelayCommand]
    private void Save()
    {
        if (!ValidateBeforeSave())
        {
            return;
        }

        var settings = ToSettings();
        settingsService.Save(settings);
        Apply(settingsService.Current);
        StatusText = "设置已保存";
        SettingsApplied?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Reset()
    {
        settingsService.Reset();
        Apply(settingsService.Current);
        StatusText = "已恢复默认设置";
        SettingsApplied?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(AppSettings settings)
    {
        EmsUrl = settings.EmsUrl;
        EdgeCdpPort = settings.EdgeCdpPort;
        CheckLoginBeforeCollection = true;
        DataDirectory = settings.DataDirectory;
        ExportDirectory = settings.ExportDirectory;
        TrackRecentExports = settings.TrackRecentExports;
        DefaultCollectionModeIndex = 0;
        LogLevelIndex = settings.LogLevel.ToUpperInvariant() switch
        {
            "ERROR" => 0,
            "DEBUG" => 2,
            _ => 1,
        };
        SaveNdjsonLog = settings.SaveNdjsonLog;
        ThemeIndex = settings.Theme.ToLowerInvariant() switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        CompactDataTable = settings.CompactDataTable;
        ReduceMotion = settings.ReduceMotion;
        PageTransitionStyleIndex = settings.PageTransitionStyle.ToLowerInvariant() switch
        {
            "none" => 0,
            "slide" => 2,
            _ => 1,
        };
        SaveWindowPlacement = settings.SaveWindowPlacement;
        StartMinimized = settings.StartMinimized;
        LaunchAtLogin = settings.LaunchAtLogin;
        ShowInSendTo = settings.ShowInSendTo;
        CloseToTray = settings.CloseToTray;
        TemperatureWarningThreshold = settings.TemperatureWarningThreshold;
        OfflineStatusColor = ParseColor(settings.OfflineStatusColor, Color.FromArgb(255, 128, 128, 128));
        TemperatureWarningColor = ParseColor(settings.TemperatureWarningColor, Color.FromArgb(255, 217, 119, 6));
    }

    private AppSettings ToSettings()
    {
        return new AppSettings
        {
            EmsUrl = EmsUrl,
            EdgeCdpPort = Convert.ToInt32(Math.Round(EdgeCdpPort)),
            CheckLoginBeforeCollection = true,
            DataDirectory = DataDirectory,
            ExportDirectory = ExportDirectory,
            TrackRecentExports = TrackRecentExports,
            DefaultCollectionMode = "edge-cdp",
            LogLevel = LogLevelIndex switch
            {
                0 => "ERROR",
                2 => "DEBUG",
                _ => "INFO",
            },
            SaveNdjsonLog = SaveNdjsonLog,
            Theme = ThemeIndex switch
            {
                1 => "light",
                2 => "dark",
                _ => "system",
            },
            CompactDataTable = CompactDataTable,
            ReduceMotion = ReduceMotion,
            PageTransitionStyle = PageTransitionStyleIndex switch
            {
                0 => "none",
                2 => "slide",
                _ => "fade",
            },
            SaveWindowPlacement = SaveWindowPlacement,
            StartMinimized = StartMinimized,
            LaunchAtLogin = LaunchAtLogin,
            ShowInSendTo = ShowInSendTo,
            CloseToTray = CloseToTray,
            TemperatureWarningThreshold = TemperatureWarningThreshold,
            OfflineStatusColor = ToHex(OfflineStatusColor),
            TemperatureWarningColor = ToHex(TemperatureWarningColor),
        };
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        var candidate = value?.Trim();
        if (candidate is null || (candidate.Length != 7 && candidate.Length != 9) || candidate[0] != '#')
        {
            return fallback;
        }

        try
        {
            var offset = candidate.Length == 9 ? 1 : 0;
            var alpha = candidate.Length == 9 ? Convert.ToByte(candidate.Substring(1, 2), 16) : (byte)255;
            var red = Convert.ToByte(candidate.Substring(1 + offset, 2), 16);
            var green = Convert.ToByte(candidate.Substring(3 + offset, 2), 16);
            var blue = Convert.ToByte(candidate.Substring(5 + offset, 2), 16);
            return Color.FromArgb(alpha, red, green, blue);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static string ToHex(Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    partial void OnSelectedOfflineStatusColorChanged(ColorPresetOption? value)
    {
        if (value is not null && value.Color != OfflineStatusColor)
        {
            OfflineStatusColor = value.Color;
        }
    }

    partial void OnSelectedTemperatureWarningColorChanged(ColorPresetOption? value)
    {
        if (value is not null && value.Color != TemperatureWarningColor)
        {
            TemperatureWarningColor = value.Color;
        }
    }

    partial void OnOfflineStatusColorChanged(Color value)
    {
        SelectedOfflineStatusColor = FindPreset(OfflineStatusColorOptions, value, 1);
    }

    partial void OnTemperatureWarningColorChanged(Color value)
    {
        SelectedTemperatureWarningColor = FindPreset(TemperatureWarningColorOptions, value, 1);
    }

    private static ColorPresetOption FindPreset(
        IReadOnlyList<ColorPresetOption> options,
        Color value,
        int fallbackIndex)
    {
        return options.FirstOrDefault(option => option.Color == value) ?? options[fallbackIndex];
    }

    private bool ValidateBeforeSave()
    {
        var portError = AppSettingsValidator.ValidateEdgeCdpPortInput(EdgeCdpPort);
        if (portError is not null)
        {
            StatusText = portError;
            return false;
        }

        var error = AppSettingsValidator.Validate(ToSettings());
        if (error is not null)
        {
            StatusText = error;
            return false;
        }

        return true;
    }
}
