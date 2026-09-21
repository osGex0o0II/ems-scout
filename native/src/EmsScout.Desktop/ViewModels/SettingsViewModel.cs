using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Settings;
using Windows.UI;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class SettingsViewModel(
    AppSettingsService settingsService,
    AppDataPathService pathService,
    LocalLogCleanupService localLogCleanupService) : ObservableObject
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
    public partial double RealtimeBatchSize { get; set; } = 20;

    [ObservableProperty]
    public partial double RealtimeReopenEvery { get; set; } = 3;

    [ObservableProperty]
    public partial double RealtimeTimeoutMs { get; set; } = 15000;

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

    [ObservableProperty]
    public partial string DashboardNormalMode { get; set; } = "制冷";

    [ObservableProperty]
    public partial double DashboardTemperatureMin { get; set; } = 22;

    [ObservableProperty]
    public partial double DashboardTemperatureMax { get; set; } = 26;

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

    public IReadOnlyList<double> RealtimeBatchSizeOptions { get; } = [10, 20, 50, 100];

    public IReadOnlyList<double> RealtimeReopenEveryOptions { get; } = [0, 1, 3, 5, 10];

    public IReadOnlyList<double> RealtimeTimeoutOptions { get; } = [5000, 10000, 15000, 30000, 60000];

    public IReadOnlyList<string> DashboardNormalModeOptions { get; } =
        ["制冷", "制热", "通风", "送暖", "地暖", "制热+地暖"];

    [ObservableProperty]
    public partial ColorPresetOption? SelectedOfflineStatusColor { get; set; }

    [ObservableProperty]
    public partial ColorPresetOption? SelectedTemperatureWarningColor { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "设置尚未加载";

    public string SettingsPath => settingsService.SettingsPath;

    public LocalLogCleanupPreview PreviewLocalLogCleanup() => localLogCleanupService.Preview();

    public LocalLogCleanupResult ClearLocalLogs()
    {
        var result = localLogCleanupService.Clear();
        StatusText = result.IsComplete
            ? result.DeletedCount == 0
                ? "没有可清理的本地日志"
                : $"已清理 {result.DeletedCount:N0} 个本地日志文件"
            : $"已清理 {result.DeletedCount:N0} 个日志文件；跳过 {result.SkippedPaths.Count:N0} 个；失败 {result.FailedPaths.Count:N0} 个";
        return result;
    }

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
        try
        {
            settingsService.SaveValidated(settings, pathService.WorkspaceRoot);
        }
        catch (InvalidOperationException exception)
        {
            StatusText = exception.Message;
            return;
        }
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
        RealtimeBatchSize = settings.RealtimeBatchSize;
        RealtimeReopenEvery = settings.RealtimeReopenEvery;
        RealtimeTimeoutMs = settings.RealtimeTimeoutMs;
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
        DashboardNormalMode = settings.DashboardNormalMode;
        DashboardTemperatureMin = settings.DashboardTemperatureMin;
        DashboardTemperatureMax = settings.DashboardTemperatureMax;
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
            RealtimeBatchSize = ClampToInt(RealtimeBatchSize, 1, 100),
            RealtimeReopenEvery = ClampToInt(RealtimeReopenEvery, 0, 50),
            RealtimeTimeoutMs = ClampToInt(RealtimeTimeoutMs, 3000, 120000),
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
            DashboardNormalMode = DashboardNormalMode,
            DashboardTemperatureMin = DashboardTemperatureMin,
            DashboardTemperatureMax = DashboardTemperatureMax,
        };
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        var decoded = HexColorCodec.Decode(
            value,
            new RgbaColor(fallback.A, fallback.R, fallback.G, fallback.B));
        return Color.FromArgb(decoded.Alpha, decoded.Red, decoded.Green, decoded.Blue);
    }

    private static string ToHex(Color color) => HexColorCodec.Encode(
        new RgbaColor(color.A, color.R, color.G, color.B));

    private static int ClampToInt(double value, int minimum, int maximum)
    {
        return double.IsFinite(value)
            ? Math.Clamp(Convert.ToInt32(Math.Round(value)), minimum, maximum)
            : minimum;
    }

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
        SelectedOfflineStatusColor = FindPreset(OfflineStatusColorOptions, value);
    }

    partial void OnTemperatureWarningColorChanged(Color value)
    {
        SelectedTemperatureWarningColor = FindPreset(TemperatureWarningColorOptions, value);
    }

    private static ColorPresetOption? FindPreset(
        IReadOnlyList<ColorPresetOption> options,
        Color value)
    {
        return options.FirstOrDefault(option => option.Color == value);
    }

    private bool ValidateBeforeSave()
    {
        var portError = AppSettingsValidator.ValidateEdgeCdpPortInput(EdgeCdpPort);
        if (portError is not null)
        {
            StatusText = portError;
            return false;
        }

        var error = AppSettingsValidator.Validate(ToSettings(), pathService.WorkspaceRoot);
        if (error is not null)
        {
            StatusText = error;
            return false;
        }

        return true;
    }
}
