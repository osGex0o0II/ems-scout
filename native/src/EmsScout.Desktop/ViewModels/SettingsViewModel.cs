using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Settings;

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
    public partial string StatusText { get; private set; } = "设置尚未加载";

    public string SettingsPath => settingsService.SettingsPath;

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
        CheckLoginBeforeCollection = settings.CheckLoginBeforeCollection;
        DataDirectory = settings.DataDirectory;
        ExportDirectory = settings.ExportDirectory;
        TrackRecentExports = settings.TrackRecentExports;
        DefaultCollectionModeIndex = settings.DefaultCollectionMode.Equals("auto-launch", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
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
    }

    private AppSettings ToSettings()
    {
        return new AppSettings
        {
            EmsUrl = EmsUrl,
            EdgeCdpPort = Convert.ToInt32(Math.Round(EdgeCdpPort)),
            CheckLoginBeforeCollection = CheckLoginBeforeCollection,
            DataDirectory = DataDirectory,
            ExportDirectory = ExportDirectory,
            TrackRecentExports = TrackRecentExports,
            DefaultCollectionMode = DefaultCollectionModeIndex == 1 ? "auto-launch" : "edge-cdp",
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
        };
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
