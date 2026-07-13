using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Logging;
using EmsScout.Application.Settings;
using EmsScout.Application.Workflows;
using EmsScout.Infrastructure.Logging;
using EmsScout.Infrastructure.Migrations;
using EmsScout.Infrastructure.Storage;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class SettingsViewModel(
    AppSettingsService settingsService,
    AppDataPathService pathService,
    LegacyOutMigrationService legacyOutMigrationService,
    SqliteSchemaMigrator schemaMigrator,
    ApplicationOperationState operationState,
    IApplicationLogger applicationLogger) : ObservableObject
{
    private bool _operationStateAttached;

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

    public bool CanEditCriticalSettings => !operationState.IsCollectionTaskRunning;

    public async Task MigrateLegacyOutAsync(
        string legacyOutDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!CanChangeSettings())
        {
            StatusText = "采集任务运行中，不能迁移或切换数据目录";
            return;
        }

        if (string.IsNullOrWhiteSpace(legacyOutDirectory))
        {
            StatusText = "未选择旧数据目录";
            return;
        }

        var selectedDataDirectory = Path.GetFullPath(pathService.ResolveWorkspacePath(DataDirectory));
        var configuredPaths = pathService.Capture();
        var configuredDataDirectory = configuredPaths.DataDirectory;
        if (!string.Equals(
                selectedDataDirectory,
                configuredDataDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            StatusText = "请先保存当前数据目录，再迁移旧数据";
            return;
        }

        try
        {
            var result = await legacyOutMigrationService
                .MigrateIfNeededAsync(legacyOutDirectory, configuredPaths.DataDirectory, cancellationToken)
                .ConfigureAwait(true);
            if (result.Status == LegacyOutMigrationStatus.Migrated)
            {
                await schemaMigrator
                    .MigrateAsync(configuredPaths.DatabasePath, cancellationToken: cancellationToken)
                    .ConfigureAwait(true);
            }
            StatusText = result.Status switch
            {
                LegacyOutMigrationStatus.Migrated => "旧数据已迁移，数据库结构已升级",
                LegacyOutMigrationStatus.DestinationAlreadyInitialized => "当前数据目录已有数据库，未覆盖旧数据",
                LegacyOutMigrationStatus.SourceMissing => "所选目录不含 ac.db，未迁移",
                LegacyOutMigrationStatus.SourceAndDestinationAreSame => "所选目录已是当前数据目录",
                _ => "旧数据迁移未执行",
            };
        }
        catch (Exception ex)
        {
            StatusText = "旧数据迁移失败：" + applicationLogger.WriteFailure(ex, "settings").DisplayText;
        }
    }

    public void Load()
    {
        AttachOperationState();
        Apply(settingsService.Load());
        StatusText = CanEditCriticalSettings ? "已加载设置" : "采集任务运行中，关键设置已锁定";
    }

    [RelayCommand(CanExecute = nameof(CanChangeSettings))]
    private async Task SaveAsync()
    {
        if (!CanChangeSettings())
        {
            StatusText = "采集任务运行中，不能迁移或切换数据目录";
            return;
        }

        if (!ValidateBeforeSave())
        {
            return;
        }

        var settings = ToSettings();
        try
        {
            await MigrateExistingDatabaseAsync(settings).ConfigureAwait(true);
            settingsService.Save(settings);
            Apply(settingsService.Current);
            StatusText = "设置已保存";
            SettingsApplied?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusText = "设置未保存：" + applicationLogger.WriteFailure(ex, "settings").DisplayText;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeSettings))]
    private async Task ResetAsync()
    {
        if (!CanChangeSettings())
        {
            StatusText = "采集任务运行中，不能迁移或切换数据目录";
            return;
        }

        try
        {
            var defaults = new AppSettings();
            await MigrateExistingDatabaseAsync(defaults).ConfigureAwait(true);
            settingsService.Reset();
            Apply(settingsService.Current);
            StatusText = "已恢复默认设置";
            SettingsApplied?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusText = "未恢复默认设置：" + applicationLogger.WriteFailure(ex, "settings").DisplayText;
        }
    }

    private void Apply(AppSettings settings)
    {
        EmsUrl = settings.EmsUrl;
        EdgeCdpPort = settings.EdgeCdpPort;
        CheckLoginBeforeCollection = settings.CheckLoginBeforeCollection;
        DataDirectory = settings.DataDirectory;
        ExportDirectory = settings.ExportDirectory;
        TrackRecentExports = settings.TrackRecentExports;
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

    private async Task MigrateExistingDatabaseAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var paths = pathService.Capture(settings);
        if (File.Exists(paths.DatabasePath))
        {
            await schemaMigrator.MigrateAsync(paths.DatabasePath, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
    }

    private bool CanChangeSettings() => CanEditCriticalSettings;

    private void AttachOperationState()
    {
        if (_operationStateAttached)
        {
            return;
        }

        operationState.CollectionTaskStateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanEditCriticalSettings));
            SaveCommand.NotifyCanExecuteChanged();
            ResetCommand.NotifyCanExecuteChanged();
            StatusText = CanEditCriticalSettings
                ? "采集任务已结束，可以修改关键设置"
                : "采集任务运行中，关键设置已锁定";
        };
        _operationStateAttached = true;
    }
}
