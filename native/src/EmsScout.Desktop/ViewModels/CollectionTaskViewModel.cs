using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Collection;
using EmsScout.Application.Devices;
using EmsScout.Application.Errors;
using EmsScout.Application.Logging;
using EmsScout.Application.Quality;
using EmsScout.Application.Settings;
using EmsScout.Application.Workflows;
using EmsScout.Desktop.Services;
using EmsScout.Infrastructure.Importing;
using EmsScout.Infrastructure.Errors;
using EmsScout.Infrastructure.Logging;
using EmsScout.Infrastructure.Sidecar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class CollectionTaskViewModel(
    NodeCollectionTaskRunner runner,
    AppSettingsService settingsService,
    AppDataPathService pathService,
    INavigationService navigationService,
    IQualityAuditService qualityAuditService,
    IRealtimeQualityAuditService realtimeQualityAuditService,
    IRealtimeReconciliationService realtimeReconciliationService,
    ICollectionRunRepository collectionRunRepository,
    CollectionSnapshotReader snapshotReader,
    CollectionSnapshotImporter snapshotImporter,
    INativeQualityAuditService nativeQualityAuditService,
    ApplicationOperationState operationState,
    CollectionEnvironmentProbe environmentProbe,
    IRecaptureLocationSource recaptureLocationSource,
    IApplicationLogger applicationLogger) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _activeTask;
    private bool _stopRequested;
    private IReadOnlyList<string> _activeCollectionBuildings = [];
    private double _activeProgressBase;
    private double _activeProgressSpan = 100;
    private string _activeProgressLabel = string.Empty;
    private string _activeStageKey = string.Empty;
    private string? _activeWorkflowId;
    private bool _currentDataUpdatedThisRun;
    private string? _activeDataDirectory;
    private bool _buildingEventsAttached;
    private bool _operationStateAttached;
    private DateTimeOffset? _activeRunStartedAt;
    private DateTimeOffset? _lastActivityAt;
    private bool _environmentChecked;
    private bool _nodeReady;
    private bool _dependenciesReady;
    private bool _enumScriptReady;
    private bool _realtimeScriptReady;
    private bool _realtimeAuditScriptReady;
    private bool _databaseReady;
    private bool _snapshotReady;
    private bool _emsUrlReady;
    private bool _cdpReachable;
    private int _emsPageCount;
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private DispatcherQueueTimer? _heartbeatTimer;
    private Process? _ownedEdgeProcess;
    private string? _ownedEdgeSessionRoot;
    private int? _ownedEdgeCdpPort;
    private IReadOnlyList<RecaptureLocation> _recaptureLocations = [];
    private bool _syncingRecaptureBuildingSelection;
    private bool _isLogsExpanded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckEnvironmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCollectionBrowserCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenAuditCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenReconciliationItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkRunAnomalyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearRunAnomalyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllBuildingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearBuildingSelectionCommand))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedRun))]
    [NotifyPropertyChangedFor(nameof(CanEditTaskOptions))]
    [NotifyPropertyChangedFor(nameof(CanEditCustomTaskOptions))]
    [NotifyPropertyChangedFor(nameof(CanEditBuildingSelectionOptions))]
    [NotifyPropertyChangedFor(nameof(CanSelectRecaptureSeat))]
    [NotifyPropertyChangedFor(nameof(CanSelectRecaptureFloor))]
    [NotifyPropertyChangedFor(nameof(CanStartTask))]
    [NotifyPropertyChangedFor(nameof(StartPrimaryButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(StartSecondaryButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(CollectionBrowserPrimaryButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(CollectionBrowserSecondaryButtonVisibility))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckEnvironmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCollectionBrowserCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllBuildingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearBuildingSelectionCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditTaskOptions))]
    [NotifyPropertyChangedFor(nameof(CanEditCustomTaskOptions))]
    [NotifyPropertyChangedFor(nameof(CanEditBuildingSelectionOptions))]
    [NotifyPropertyChangedFor(nameof(CanSelectRecaptureSeat))]
    [NotifyPropertyChangedFor(nameof(CanSelectRecaptureFloor))]
    [NotifyPropertyChangedFor(nameof(CanStartTask))]
    [NotifyPropertyChangedFor(nameof(StartPrimaryButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(StartSecondaryButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(CollectionBrowserPrimaryButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(CollectionBrowserSecondaryButtonVisibility))]
    public partial bool IsCheckingEnvironment { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "等待任务启动";

    [ObservableProperty]
    public partial string EnvironmentText { get; private set; } = "尚未检查";

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = "尚未开始";

    [ObservableProperty]
    public partial string CurrentActivityText { get; private set; } = "尚未开始";

    [ObservableProperty]
    public partial string CurrentBuildingText { get; private set; } = "未开始";

    [ObservableProperty]
    public partial string CollectedCountText { get; private set; } = "--";

    [ObservableProperty]
    public partial string DataUpdateText { get; private set; } = "尚未更新";

    [ObservableProperty]
    public partial string RunDurationText { get; private set; } = "未开始";

    [ObservableProperty]
    public partial string LastHeartbeatText { get; private set; } = "尚未更新";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartTask))]
    [NotifyPropertyChangedFor(nameof(StartPrimaryButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(StartSecondaryButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(CollectionBrowserPrimaryButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(CollectionBrowserSecondaryButtonVisibility))]
    public partial bool IsEnvironmentReady { get; private set; }

    [ObservableProperty]
    public partial string ReadinessTitle { get; private set; } = "正在检查采集环境";

    [ObservableProperty]
    public partial string ReadinessDetail { get; private set; } = "请稍候";

    [ObservableProperty]
    public partial string ReadinessGlyph { get; private set; } = "\uE9D9";

    [ObservableProperty]
    public partial string PreflightDetailsHeader { get; private set; } = "0/4 已通过";

    [ObservableProperty]
    public partial double PreflightProgressValue { get; private set; }

    [ObservableProperty]
    public partial string CollectionBrowserActionText { get; private set; } = "打开采集浏览器";

    [ObservableProperty]
    public partial string CollectionBrowserActionGlyph { get; private set; } = "\uE774";

    [ObservableProperty]
    public partial string CollectionBrowserActionToolTip { get; private set; } = "打开 EMS Scout 专用采集浏览器";

    [ObservableProperty]
    public partial bool IsCollectionBrowserOpen { get; private set; }

    public Visibility StartPrimaryButtonVisibility => CanStartTask ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StartSecondaryButtonVisibility => CanStartTask ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CollectionBrowserPrimaryButtonVisibility => ShouldEmphasizeCollectionBrowser
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CollectionBrowserSecondaryButtonVisibility => ShouldEmphasizeCollectionBrowser
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool IsLogsExpanded
    {
        get => _isLogsExpanded;
        set
        {
            if (!SetProperty(ref _isLogsExpanded, value))
            {
                return;
            }

            OnPropertyChanged(nameof(TaskProgressVisibility));
            OnPropertyChanged(nameof(LogsExpandButtonVisibility));
            OnPropertyChanged(nameof(LogsRestoreButtonVisibility));
            OnPropertyChanged(nameof(LogsGridRow));
            OnPropertyChanged(nameof(LogsGridRowSpan));
        }
    }

    public Visibility TaskProgressVisibility => IsLogsExpanded
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility LogsExpandButtonVisibility => IsLogsExpanded
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility LogsRestoreButtonVisibility => IsLogsExpanded
        ? Visibility.Visible
        : Visibility.Collapsed;

    public int LogsGridRow => IsLogsExpanded ? 0 : 1;

    public int LogsGridRowSpan => IsLogsExpanded ? 2 : 1;

    private bool ShouldEmphasizeCollectionBrowser
    {
        get
        {
            var plan = BuildExecutionPlan(SelectedTaskMode);
            return !IsCollectionBrowserOpen &&
                   !IsRunning &&
                   !IsCheckingEnvironment &&
                   _environmentChecked &&
                   (plan.RunEnumeration || plan.RunRealtimeDetails) &&
                   !_cdpReachable;
        }
    }

    [ObservableProperty]
    public partial string SelectedBuildingsText { get; private set; } = "已选择 6 栋楼";

    [ObservableProperty]
    public partial string CurrentDataImpactText { get; private set; } = "采集成功后将更新所选楼栋的当前数据";

    public Visibility CurrentDataImpactVisibility => AllBuildingsSelected
        ? Visibility.Collapsed
        : Visibility.Visible;

    private bool AllBuildingsSelected =>
        Buildings.Count > 0 && Buildings.All(building => building.IsSelected);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskModeDescription))]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    [NotifyPropertyChangedFor(nameof(CanEditCustomTaskOptions))]
    [NotifyPropertyChangedFor(nameof(IsRecaptureMode))]
    [NotifyPropertyChangedFor(nameof(CanEditBuildingSelectionOptions))]
    [NotifyPropertyChangedFor(nameof(StartPrimaryButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(StartSecondaryButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(CollectionBrowserPrimaryButtonVisibility))]
    [NotifyPropertyChangedFor(nameof(CollectionBrowserSecondaryButtonVisibility))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllBuildingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearBuildingSelectionCommand))]
    public partial CollectionTaskModeOption? SelectedTaskMode { get; set; }

    [ObservableProperty]
    public partial bool RunImportAfterCollect { get; set; } = true;

    [ObservableProperty]
    public partial bool RunQualityAfterImport { get; set; } = true;

    [ObservableProperty]
    public partial bool RunRealtimeDetailsAfterImport { get; set; } = true;

    [ObservableProperty]
    public partial bool RunRealtimeAuditAfterDetails { get; set; } = true;

    [ObservableProperty]
    public partial bool EnableLogFile { get; set; } = true;

    [ObservableProperty]
    public partial string LogCategory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RecaptureText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RecaptureLocationStatus { get; private set; } = "正在读取可补采位置";

    [ObservableProperty]
    public partial RecaptureLocationOption? SelectedRecaptureBuilding { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectRecaptureSeat))]
    public partial RecaptureLocationOption? SelectedRecaptureSeat { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectRecaptureFloor))]
    public partial RecaptureLocationOption? SelectedRecaptureFloor { get; set; }

    [ObservableProperty]
    public partial bool EnableSelfDiagnose { get; set; }

    [ObservableProperty]
    public partial bool DisableNetworkMonitor { get; set; }

    [ObservableProperty]
    public partial double RealtimeBatchSize { get; set; } = 20;

    [ObservableProperty]
    public partial double RealtimeReopenEvery { get; set; } = 3;

    [ObservableProperty]
    public partial double RealtimeTimeoutMs { get; set; } = 15000;

    [ObservableProperty]
    public partial double RealtimeMaxDevices { get; set; }

    [ObservableProperty]
    public partial bool RefreshInventoryBeforeRealtime { get; set; } = true;

    [ObservableProperty]
    public partial bool SkipInventoryCheck { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenDataCommand))]
    public partial bool CanOpenDataAfterImport { get; private set; }

    [ObservableProperty]
    public partial string QualityStatusText { get; private set; } = "尚未读取质量审计";

    [ObservableProperty]
    public partial string QualitySummaryText { get; private set; } = "--";

    [ObservableProperty]
    public partial string QualityGeneratedText { get; private set; } = "--";

    [ObservableProperty]
    public partial string RealtimeQualityStatusText { get; private set; } = "尚未读取实时审计";

    [ObservableProperty]
    public partial string RealtimeQualitySummaryText { get; private set; } = "--";

    [ObservableProperty]
    public partial string RealtimeQualityGeneratedText { get; private set; } = "--";

    [ObservableProperty]
    public partial string ReconciliationStatusText { get; private set; } = "尚未读取实时对账";

    [ObservableProperty]
    public partial string ReconciliationSummaryText { get; private set; } = "--";

    [ObservableProperty]
    public partial string ReconciliationGeneratedText { get; private set; } = "--";

    [ObservableProperty]
    public partial string ReconciliationSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ReconciliationFilterOption? SelectedReconciliationBuilding { get; set; }

    [ObservableProperty]
    public partial ReconciliationFilterOption? SelectedReconciliationType { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReconciliationItemCommand))]
    public partial ReconciliationItemRow? SelectedReconciliationItem { get; set; }

    [ObservableProperty]
    public partial string RunsStatusText { get; private set; } = "尚未读取历史批次";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkRunAnomalyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearRunAnomalyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRunCommand))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedRun))]
    public partial CollectionRunRow? SelectedRun { get; set; }

    public ObservableCollection<CollectionBuildingOption> Buildings { get; } =
    [
        new("1号", "1号楼", true),
        new("2号", "2号楼", true),
        new("3号", "3号楼", true),
        new("4号", "4号楼", true),
        new("5号", "5号楼", true),
        new("6号", "6号楼", true),
    ];

    public ObservableCollection<CollectionTaskModeOption> TaskModes { get; } =
        new(CollectionTaskModeCatalog.Options.Where(option => option.Value is
            CollectionTaskModeValues.Full or
            CollectionTaskModeValues.Recapture));

    public ObservableCollection<RecaptureLocationOption> RecaptureBuildingOptions { get; } = [];

    public ObservableCollection<RecaptureLocationOption> RecaptureSeatOptions { get; } = [];

    public ObservableCollection<RecaptureLocationOption> RecaptureFloorOptions { get; } = [];

    public ObservableCollection<CollectionTaskLogRow> Logs { get; } = [];

    public bool CanClearLogs => Logs.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearLogs))]
    private void ClearLogs()
    {
        Logs.Clear();
        OnPropertyChanged(nameof(CanClearLogs));
        ClearLogsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ToggleLogsExpanded() => IsLogsExpanded = !IsLogsExpanded;

    public ObservableCollection<CollectionStageRow> Stages { get; } = [];

    public ObservableCollection<PreflightCheckRow> PreflightChecks { get; } =
    [
        PreflightCheckRow.Pending("本地采集组件", "等待检查"),
        PreflightCheckRow.Pending("当前数据", "等待检查"),
        PreflightCheckRow.Pending("采集浏览器", "等待检查"),
        PreflightCheckRow.Pending("EMS 页面", "等待检查"),
    ];

    public ObservableCollection<QualityAuditIssueRow> QualityIssues { get; } = [];

    public ObservableCollection<RealtimeQualityCategoryRow> RealtimeQualityCategories { get; } = [];

    public ObservableCollection<RealtimeQualityBuildingRow> RealtimeQualityBuildings { get; } = [];

    public ObservableCollection<ReconciliationFilterOption> ReconciliationBuildingOptions { get; } =
    [
        new(string.Empty, "全部楼栋"),
        new("1号", "1号"),
        new("2号", "2号"),
        new("3号", "3号"),
        new("4号", "4号"),
        new("5号", "5号"),
        new("6号", "6号"),
    ];

    public ObservableCollection<ReconciliationFilterOption> ReconciliationTypeOptions { get; } =
    [
        new(string.Empty, "全部差异"),
        new(RealtimeReconciliationTypes.NewDevice, "新增实时"),
        new(RealtimeReconciliationTypes.MissingInRealtime, "缺实时"),
        new(RealtimeReconciliationTypes.MatchFailed, "匹配失败"),
        new(RealtimeReconciliationTypes.VirtualOverride, "虚拟纳管"),
        new(RealtimeReconciliationTypes.DuplicateRender, "重复渲染"),
        new(RealtimeReconciliationTypes.DataNoise, "数据噪声"),
    ];

    public ObservableCollection<ReconciliationTypeCountRow> ReconciliationTypeCounts { get; } = [];

    public ObservableCollection<ReconciliationItemRow> ReconciliationItems { get; } = [];

    public ObservableCollection<CollectionRunRow> Runs { get; } = [];

    public string WorkspaceRoot => runner.WorkspaceRoot;

    public bool CanEditTaskOptions => !IsRunning && !IsCheckingEnvironment;

    public bool CanEditBuildingSelectionOptions => CanEditTaskOptions && !IsRecaptureMode;

    public bool CanSelectRecaptureSeat => CanEditTaskOptions && RecaptureSeatOptions.Count > 1;

    public bool CanSelectRecaptureFloor => CanEditTaskOptions && RecaptureFloorOptions.Count > 1;

    public bool CanEditCustomTaskOptions => CanEditTaskOptions && IsCustomTaskMode;

    public string TaskModeDescription => SelectedTaskMode?.Description ?? "请选择任务模式";

    public string StartButtonText => SelectedTaskMode?.StartButtonText ?? "开始任务";

    public bool IsRecaptureMode => string.Equals(
        SelectedTaskMode?.Value,
        CollectionTaskModeValues.Recapture,
        StringComparison.OrdinalIgnoreCase);

    public bool CanOpenAudit => !IsRunning;

    private bool IsCustomTaskMode => string.Equals(SelectedTaskMode?.Value, CollectionTaskModeValues.Custom, StringComparison.OrdinalIgnoreCase);

    public bool CanDeleteSelectedRun => CanDeleteRun();

    public bool CanStartTask => CanStart();

    private void NotifyActionButtonPriorityChanged()
    {
        OnPropertyChanged(nameof(CanStartTask));
        OnPropertyChanged(nameof(StartPrimaryButtonVisibility));
        OnPropertyChanged(nameof(StartSecondaryButtonVisibility));
        OnPropertyChanged(nameof(CollectionBrowserPrimaryButtonVisibility));
        OnPropertyChanged(nameof(CollectionBrowserSecondaryButtonVisibility));
    }

    partial void OnIsRunningChanged(bool value)
    {
        UpdateCollectionBrowserPresentation();
    }

    partial void OnSelectedTaskModeChanged(CollectionTaskModeOption? value)
    {
        ApplyTaskModePreset(value);
        if (IsRecaptureMode)
        {
            EnsureRecaptureSelection();
            SynchronizeBuildingSelectionWithRecapture();
        }
        ResetStages(BuildExecutionPlan(value));
        UpdateEnvironmentReadiness();
        NotifyActionButtonPriorityChanged();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        CleanupStaleBrowserSessions();
        LoadSettingsDefaults();
        SelectedTaskMode ??= TaskModes.FirstOrDefault(
            mode => mode.Value == CollectionTaskModeValues.Full);
        ApplyTaskModePreset(SelectedTaskMode);
        AttachBuildingEvents();
        AttachOperationState();
        await LoadRecaptureLocationsAsync(cancellationToken).ConfigureAwait(true);
        UpdateSelectedBuildingsText();
        ResetStages(BuildExecutionPlan(SelectedTaskMode));

        var settings = settingsService.Load();
        var dataDirectory = pathService.ResolveWorkspacePath(settings.DataDirectory);
        CanOpenDataAfterImport = File.Exists(Path.Combine(dataDirectory, "ac.db"));
        DataUpdateText = CanOpenDataAfterImport ? "已有当前数据" : "首次采集后创建数据库";
        await Task.CompletedTask;
    }

    public void LoadSettingsDefaults()
    {
        var settings = settingsService.Load();
        EnableLogFile = settings.SaveNdjsonLog;
    }

    private void ApplyTaskModePreset(CollectionTaskModeOption? mode)
    {
        if (mode is null || IsRunning || IsCheckingEnvironment)
        {
            return;
        }

        switch (mode.Value)
        {
            case CollectionTaskModeValues.Full:
            case CollectionTaskModeValues.Recapture:
                RunImportAfterCollect = true;
                RunQualityAfterImport = true;
                RunRealtimeDetailsAfterImport = true;
                RunRealtimeAuditAfterDetails = true;
                break;
            case CollectionTaskModeValues.CollectImport:
                RunImportAfterCollect = true;
                RunQualityAfterImport = true;
                RunRealtimeDetailsAfterImport = false;
                RunRealtimeAuditAfterDetails = false;
                break;
            case CollectionTaskModeValues.EnumerateOnly:
                RunImportAfterCollect = false;
                RunQualityAfterImport = false;
                RunRealtimeDetailsAfterImport = false;
                RunRealtimeAuditAfterDetails = false;
                break;
            case CollectionTaskModeValues.RealtimeDetailsOnly:
                RunImportAfterCollect = false;
                RunQualityAfterImport = false;
                RunRealtimeDetailsAfterImport = true;
                RunRealtimeAuditAfterDetails = true;
                break;
            case CollectionTaskModeValues.ValidateOnly:
            case CollectionTaskModeValues.ImportOnly:
            case CollectionTaskModeValues.QualityOnly:
            case CollectionTaskModeValues.RealtimeAuditOnly:
                RunImportAfterCollect = false;
                RunQualityAfterImport = false;
                RunRealtimeDetailsAfterImport = false;
                RunRealtimeAuditAfterDetails = false;
                break;
        }

        OnPropertyChanged(nameof(CanEditCustomTaskOptions));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditBuildingSelectionOptions))]
    private void SelectAllBuildings()
    {
        foreach (var building in Buildings)
        {
            building.IsSelected = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditBuildingSelectionOptions))]
    private void ClearBuildingSelection()
    {
        foreach (var building in Buildings)
        {
            building.IsSelected = false;
        }
    }

    private void AttachBuildingEvents()
    {
        if (_buildingEventsAttached)
        {
            return;
        }

        foreach (var building in Buildings)
        {
            building.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CollectionBuildingOption.IsSelected))
                {
                    if (IsRecaptureMode && !_syncingRecaptureBuildingSelection)
                    {
                        SynchronizeBuildingSelectionWithRecapture();
                    }
                    UpdateSelectedBuildingsText();
                    StartCommand.NotifyCanExecuteChanged();
                    NotifyActionButtonPriorityChanged();
                }
            };
        }

        _buildingEventsAttached = true;
    }

    private void AttachOperationState()
    {
        if (_operationStateAttached)
        {
            return;
        }

        operationState.OperationStateChanged += (_, _) =>
        {
            StartCommand.NotifyCanExecuteChanged();
            NotifyActionButtonPriorityChanged();
        };
        _operationStateAttached = true;
    }

    partial void OnSelectedRecaptureBuildingChanged(RecaptureLocationOption? value)
    {
        ReplaceOptions(
            RecaptureSeatOptions,
            value is null
                ? []
                : RecaptureLocationCatalog.SeatOptions(_recaptureLocations, value.Value));
        SelectedRecaptureSeat = RecaptureSeatOptions.FirstOrDefault();
        SynchronizeBuildingSelectionWithRecapture();
        RefreshRecaptureTarget();
    }

    partial void OnSelectedRecaptureSeatChanged(RecaptureLocationOption? value)
    {
        ReplaceOptions(
            RecaptureFloorOptions,
            SelectedRecaptureBuilding is null
                ? []
                : RecaptureLocationCatalog.FloorOptions(
                    _recaptureLocations,
                    SelectedRecaptureBuilding.Value,
                    value?.Value));
        SelectedRecaptureFloor = RecaptureFloorOptions.FirstOrDefault();
        OnPropertyChanged(nameof(CanSelectRecaptureSeat));
        RefreshRecaptureTarget();
    }

    partial void OnSelectedRecaptureFloorChanged(RecaptureLocationOption? value)
    {
        OnPropertyChanged(nameof(CanSelectRecaptureFloor));
        RefreshRecaptureTarget();
    }

    private async Task LoadRecaptureLocationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            _recaptureLocations = await recaptureLocationSource.LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _recaptureLocations = [];
            AddLog("补采位置读取失败：" + ApplicationFailureClassifier.Classify(ex).DisplayText);
        }

        ReplaceOptions(
            RecaptureBuildingOptions,
            RecaptureLocationCatalog.BuildingOptions(_recaptureLocations));
        EnsureRecaptureSelection();
        RefreshRecaptureTarget();
    }

    private void EnsureRecaptureSelection()
    {
        if (SelectedRecaptureBuilding is null && RecaptureBuildingOptions.Count > 0)
        {
            SelectedRecaptureBuilding = RecaptureBuildingOptions[0];
        }
    }

    private void SynchronizeBuildingSelectionWithRecapture()
    {
        if (!IsRecaptureMode || SelectedRecaptureBuilding is null || _syncingRecaptureBuildingSelection)
        {
            return;
        }

        _syncingRecaptureBuildingSelection = true;
        try
        {
            foreach (var building in Buildings)
            {
                building.IsSelected = building.Value == SelectedRecaptureBuilding.Value;
            }
        }
        finally
        {
            _syncingRecaptureBuildingSelection = false;
        }
    }

    private void RefreshRecaptureTarget()
    {
        RecaptureText = RecaptureLocationCatalog.BuildTargetArgument(
            _recaptureLocations,
            SelectedRecaptureBuilding?.Value,
            SelectedRecaptureSeat?.Value,
            SelectedRecaptureFloor?.Value);
        if (RecaptureText.Length == 0)
        {
            RecaptureLocationStatus = "当前数据中没有可用的补采位置，请先完成一次采集";
        }
        else
        {
            var seat = string.IsNullOrEmpty(SelectedRecaptureSeat?.Value) ? "全部座号" : SelectedRecaptureSeat.Label;
            var floor = string.IsNullOrEmpty(SelectedRecaptureFloor?.Value) ? "全部楼层" : SelectedRecaptureFloor.Label;
            var count = RecaptureText.Count(character => character == ',') + 1;
            RecaptureLocationStatus = $"已定位 {SelectedRecaptureBuilding?.Label} · {seat} · {floor}，共 {count} 个区域";
        }

        UpdateEnvironmentReadiness();
        StartCommand.NotifyCanExecuteChanged();
        NotifyActionButtonPriorityChanged();
    }

    private static void ReplaceOptions(
        ObservableCollection<RecaptureLocationOption> target,
        IReadOnlyList<RecaptureLocationOption> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private void UpdateSelectedBuildingsText()
    {
        var selected = Buildings.Where(building => building.IsSelected).Select(building => building.Value).ToList();
        SelectedBuildingsText = selected.Count == 0
            ? "尚未选择楼栋"
            : selected.Count == Buildings.Count
                ? $"已选择全部 {selected.Count} 栋楼"
                : $"已选择 {selected.Count} 栋：{string.Join("、", selected)}";
        CurrentDataImpactText = selected.Count == 0
            ? "选择至少一栋楼后才能开始"
            : selected.Count == Buildings.Count
                ? string.Empty
                : $"成功后只更新 {string.Join("、", selected)}，其他楼栋保\u2060持不变";
        OnPropertyChanged(nameof(CurrentDataImpactVisibility));
    }

    private void ResetStages(CollectionTaskExecutionPlan plan)
    {
        Stages.Clear();
        if (plan.RunEnumeration)
        {
            Stages.Add(new CollectionStageRow("collect", "采集楼栋", "等待", "从 EMS 读取空调卡片"));
        }

        if (plan.RunValidation)
        {
            Stages.Add(new CollectionStageRow("validate", "校验结果", "等待", "检查卡片和楼栋数据是否完整"));
        }

        if (plan.RunImport)
        {
            Stages.Add(new CollectionStageRow("import", "更新当前数据", "等待", "只替换所选楼栋的数据"));
        }

        if (plan.RunQuality)
        {
            Stages.Add(new CollectionStageRow("quality", "质量检查", "等待", "识别缺失、重复和异常字段"));
        }

        if (plan.RunRealtimeDetails || plan.RunRealtimeAudit)
        {
            Stages.Add(new CollectionStageRow("realtime", "实时详情与审计", "等待", "更新实时点位并检查差异"));
        }
    }

    private void SetStageState(string key, string state, string detail)
    {
        var index = -1;
        for (var i = 0; i < Stages.Count; i++)
        {
            if (Stages[i].Key == key)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        if (state == "进行中")
        {
            _activeStageKey = key;
            CurrentActivityText = detail;
        }

        Stages[index] = new CollectionStageRow(key, Stages[index].Label, state, detail);
    }

    private void SetActiveStageTerminalState(string state, string detail)
    {
        if (!string.IsNullOrWhiteSpace(_activeStageKey))
        {
            SetStageState(_activeStageKey, state, detail);
        }
    }

    [RelayCommand]
    private Task RefreshAudit() => RefreshAuditAsync();

    [RelayCommand]
    private Task RefreshRuns() => RefreshRunsAsync();

    public async Task RefreshAuditAndRunsAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAuditAsync(cancellationToken).ConfigureAwait(true);
        await RefreshRealtimeAuditAsync(cancellationToken).ConfigureAwait(true);
        await RefreshReconciliationAsync(cancellationToken).ConfigureAwait(true);
        await RefreshRunsAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task RefreshAuditAsync(
        CancellationToken cancellationToken = default,
        string? databasePath = null)
    {
        try
        {
            var report = databasePath is null
                ? await qualityAuditService.LoadLatestAsync(cancellationToken).ConfigureAwait(true)
                : await nativeQualityAuditService
                    .AuditAsync(
                        new NativeQualityAuditRequest(QualityAuditSourceKind.LatestCompletedRun, databasePath),
                        cancellationToken)
                    .ConfigureAwait(true);
            QualityIssues.Clear();
            if (report is null)
            {
                QualityStatusText = "未找到质量审计文件";
                QualitySummaryText = "采集或手动运行质量审计后显示结果";
                QualityGeneratedText = "--";
                return;
            }

            foreach (var issue in report.Issues)
            {
                QualityIssues.Add(new QualityAuditIssueRow(issue));
            }

            QualityStatusText = report.IsStale
                ? "质量审计可能过期"
                : report.Summary.IssueCount > 0 ? "存在待复核质量问题" : "质量审计通过";
            QualitySummaryText =
                $"总数 {report.Summary.TotalCards:N0}；问题 {report.Summary.IssueCount:N0}；未知通讯 {report.Summary.UnknownCommunication:N0}；缺 indicator {report.Summary.MissingIndicator:N0}";
            QualityGeneratedText = string.IsNullOrWhiteSpace(report.GeneratedAtLocal)
                ? report.SourcePath
                : $"生成时间 {report.GeneratedAtLocal}";
            if (report.IsStale)
            {
                QualityGeneratedText += "；" + report.StaleReason;
            }
        }
        catch (Exception ex)
        {
            QualityIssues.Clear();
            QualityStatusText = "质量审计读取失败";
            QualitySummaryText = applicationLogger.WriteFailure(ex, "collection", "quality_read_failed").DisplayText;
            QualityGeneratedText = "--";
        }
    }

    [RelayCommand]
    private Task RefreshRealtimeAudit() => RefreshRealtimeAuditAsync();

    private async Task RefreshRealtimeAuditAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var report = await realtimeQualityAuditService.LoadLatestAsync(cancellationToken).ConfigureAwait(true);
            RealtimeQualityCategories.Clear();
            RealtimeQualityBuildings.Clear();
            if (report is null)
            {
                RealtimeQualityStatusText = "未找到实时审计文件";
                RealtimeQualitySummaryText = "运行实时详情采集和点位审计后显示结果";
                RealtimeQualityGeneratedText = "--";
                return;
            }

            foreach (var category in report.DeviceAnomalyCategories)
            {
                RealtimeQualityCategories.Add(new RealtimeQualityCategoryRow(category));
            }

            foreach (var building in report.Buildings)
            {
                RealtimeQualityBuildings.Add(new RealtimeQualityBuildingRow(building));
            }

            RealtimeQualityStatusText = report.CollectionOk
                ? report.DeviceAnomalyRows > 0 ? "实时采集通过，存在设备异常" : "实时审计通过"
                : "实时采集存在阻断错误";
            RealtimeQualitySummaryText =
                $"实时 {report.TotalRows:N0} 行；唯一设备 {report.UniqueDevices:N0}；采集错误 {report.CollectionErrorCount:N0}；异常设备 {report.DeviceAnomalyRows:N0}；异常事件 {report.DeviceAnomalyEvents:N0}";
            RealtimeQualityGeneratedText = string.IsNullOrWhiteSpace(report.CreatedAt)
                ? report.SourcePath
                : $"生成时间 {FormatDateTime(report.CreatedAt)}";
            if (!string.IsNullOrWhiteSpace(report.SummarySource))
            {
                RealtimeQualityGeneratedText += "；来源 " + report.SummarySource;
            }
        }
        catch (Exception ex)
        {
            RealtimeQualityCategories.Clear();
            RealtimeQualityBuildings.Clear();
            RealtimeQualityStatusText = "实时审计读取失败";
            RealtimeQualitySummaryText = applicationLogger.WriteFailure(ex, "collection", "realtime_quality_read_failed").DisplayText;
            RealtimeQualityGeneratedText = "--";
        }
    }

    [RelayCommand]
    private Task RefreshReconciliation() => RefreshReconciliationAsync();

    [RelayCommand]
    private async Task ApplyReconciliationFilter()
    {
        await RefreshReconciliationAsync().ConfigureAwait(true);
    }

    private async Task RefreshReconciliationAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ReconciliationStatusText = "正在分析实时对账";
            var result = await realtimeReconciliationService.AnalyzeAsync(
                new RealtimeReconciliationQuery(
                    Building: EmptyToNull(SelectedReconciliationBuilding?.Value),
                    DiffType: EmptyToNull(SelectedReconciliationType?.Value),
                    SearchText: EmptyToNull(ReconciliationSearchText),
                    Limit: 80),
                cancellationToken).ConfigureAwait(true);

            ReconciliationTypeCounts.Clear();
            foreach (var item in result.Summary.ByType.OrderBy(item => ReconciliationTypeSort(item.Key)))
            {
                ReconciliationTypeCounts.Add(new ReconciliationTypeCountRow(item.Key, item.Value));
            }

            ReconciliationItems.Clear();
            foreach (var item in result.Items)
            {
                ReconciliationItems.Add(new ReconciliationItemRow(item));
            }

            SelectedReconciliationItem = ReconciliationItems.FirstOrDefault();
            ReconciliationStatusText = result.Summary.DiffItemCount > 0
                ? "存在实时源差异"
                : "实时源对账通过";
            ReconciliationSummaryText =
                $"DB {result.Summary.DbCount:N0}；实时 {result.Summary.RealtimeCount:N0}；差额 {result.Summary.Difference:+#;-#;0}；差异 {result.Summary.DiffItemCount:N0}；精确 {result.Summary.ExactMatches:N0}；宽松 {result.Summary.RelaxedMatches:N0}；人工 {result.Summary.ManualMatches:N0}";
            var sourceTime = result.Summary.SourceUpdatedAt.HasValue
                ? result.Summary.SourceUpdatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "未知";
            ReconciliationGeneratedText =
                $"数据时间 {sourceTime}；分析时间 {result.Summary.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}；规则 {RealtimeReconciliationTypes.RuleVersion}";
        }
        catch (Exception ex)
        {
            ReconciliationTypeCounts.Clear();
            ReconciliationItems.Clear();
            SelectedReconciliationItem = null;
            ReconciliationStatusText = "实时对账读取失败";
            ReconciliationSummaryText = applicationLogger.WriteFailure(ex, "collection", "reconciliation_read_failed").DisplayText;
            ReconciliationGeneratedText = "--";
        }
    }

    private bool CanOpenReconciliationItem() => SelectedReconciliationItem is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanOpenReconciliationItem))]
    private void OpenReconciliationItem()
    {
        if (SelectedReconciliationItem is null)
        {
            return;
        }

        navigationService.NavigateToData(DataNavigationRequest.From(SelectedReconciliationItem.NavigationTarget));
    }

    private async Task RefreshRunsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var runs = await collectionRunRepository.ListAsync(50, cancellationToken).ConfigureAwait(true);
            var selectedId = SelectedRun?.Id;
            Runs.Clear();
            foreach (var run in runs)
            {
                Runs.Add(new CollectionRunRow(run));
            }

            SelectedRun = Runs.FirstOrDefault(run => run.Id == selectedId) ?? Runs.FirstOrDefault();
            RunsStatusText = Runs.Count == 0
                ? "暂无历史批次"
                : $"已读取 {Runs.Count:N0} 个历史批次";
        }
        catch (Exception ex)
        {
            Runs.Clear();
            SelectedRun = null;
            RunsStatusText = "历史批次读取失败：" + applicationLogger.WriteFailure(ex, "collection", "runs_read_failed").DisplayText;
        }
    }

    private bool CanCheckEnvironment() => !IsRunning && !IsCheckingEnvironment;

    [RelayCommand(CanExecute = nameof(CanCheckEnvironment))]
    private async Task CheckEnvironmentAsync()
    {
        IsCheckingEnvironment = true;
        CheckEnvironmentCommand.NotifyCanExecuteChanged();
        try
        {
            var settings = settingsService.Load();
            RefreshCollectionBrowserState();
            var dataDirectory = pathService.ResolveWorkspacePath(settings.DataDirectory);
            var sidecarRoot = runner.ApplicationRoot;
            var probe = await environmentProbe.ProbeAsync(new CollectionEnvironmentProbeRequest(
                runner.RuntimePath,
                sidecarRoot,
                dataDirectory,
                GetEffectiveCdpPort(settings),
                settings.EmsUrl)).ConfigureAwait(true);
            var cdpStatus = probe.Cdp;

            _environmentChecked = true;
            _nodeReady = probe.NodeVersion is not "不可用" and not "检查超时";
            _dependenciesReady = probe.NodeModulesPresent && probe.NodeDependencies == "可用";
            _enumScriptReady = probe.EnumerationSidecarReady;
            _realtimeScriptReady = probe.RealtimeScriptReady;
            _realtimeAuditScriptReady = probe.RealtimeAuditScriptReady;
            _databaseReady = probe.DatabaseReady;
            _snapshotReady = probe.SnapshotReady;
            _emsUrlReady = probe.EmsUrlReady;
            _cdpReachable = cdpStatus.IsReachable;
            _emsPageCount = cdpStatus.EmsPageCount;

            EnvironmentText = $"Node {probe.NodeVersion}；依赖 {probe.NodeDependencies}；浏览器 {cdpStatus.Detail}";
            UpdateEnvironmentReadiness();
            StatusText = IsEnvironmentReady ? "等待任务启动" : "采集准备未完成";
            AddLog(EnvironmentText);
        }
        catch (Exception ex)
        {
            var failure = ApplicationFailureClassifier.Classify(ex);
            _environmentChecked = true;
            IsEnvironmentReady = false;
            ReadinessTitle = "采集环境检查失败";
            ReadinessDetail = failure.UserMessage + "；" + failure.SuggestedAction;
            ReadinessGlyph = "\uE7BA";
            EnvironmentText = "检查失败：" + failure.DisplayText;
            AddFailureLog(failure, ex);
            AddLog(EnvironmentText);
        }
        finally
        {
            IsCheckingEnvironment = false;
            CheckEnvironmentCommand.NotifyCanExecuteChanged();
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanStart()
    {
        if (IsRunning ||
            IsCheckingEnvironment ||
            !IsEnvironmentReady ||
            operationState.IsUpdateInstallPending)
        {
            return false;
        }

        var plan = BuildExecutionPlan(SelectedTaskMode);
        return (!plan.RequiresBuildings || Buildings.Any(building => building.IsSelected)) &&
               (!IsRecaptureMode || RecaptureText.Length > 0);
    }

    private void UpdateEnvironmentReadiness()
    {
        var plan = BuildExecutionPlan(SelectedTaskMode);
        var settings = settingsService.Current;

        if (!_environmentChecked)
        {
            IsEnvironmentReady = false;
            ReadinessTitle = "正在检查";
            ReadinessDetail = "检查完成后才能开始任务";
            ReadinessGlyph = "\uE9D9";
            PreflightDetailsHeader = "0/4 已通过";
            PreflightProgressValue = 0;
            return;
        }

        var usesNode = plan.RunEnumeration || plan.RunRealtimeDetails || plan.RunRealtimeAudit;
        var usesBrowser = plan.RunEnumeration || plan.RunRealtimeDetails;
        var localFailures = new List<string>();
        if (usesNode && !_nodeReady) localFailures.Add("Node 运行时不可用");
        if (usesNode && !_dependenciesReady) localFailures.Add("Playwright 依赖不可用");
        if (plan.RunEnumeration && !_enumScriptReady) localFailures.Add("采集脚本缺失");
        if (plan.RunRealtimeDetails && !_realtimeScriptReady) localFailures.Add("实时详情脚本缺失");
        if (plan.RunRealtimeAudit && !_realtimeAuditScriptReady) localFailures.Add("实时审计脚本缺失");
        var localReady = localFailures.Count == 0;

        var dataReady = true;
        var dataDetail = _databaseReady
            ? _snapshotReady ? "当前数据库和采集快照可用" : "当前数据库可用"
            : "首次采集后创建当前数据库";
        if (!plan.RunEnumeration && (plan.RunValidation || plan.RunImport) && !_snapshotReady)
        {
            dataReady = false;
            dataDetail = "缺少可用的采集快照";
        }
        else if (!plan.RunImport &&
                 (plan.RunQuality || plan.RunRealtimeDetails || plan.RunRealtimeAudit) &&
                 !_databaseReady)
        {
            dataReady = false;
            dataDetail = "缺少当前数据库";
        }
        if (IsRecaptureMode && RecaptureText.Length == 0)
        {
            dataReady = false;
            dataDetail = "当前数据中没有可用的补采位置，请先完成一次采集";
        }

        var browserReady = !usesBrowser || _cdpReachable;
        var browserDetail = usesBrowser
            ? browserReady ? "采集浏览器已连接" : "请先打开采集浏览器"
            : "当前任务不需要采集浏览器";
        var emsReady = !usesBrowser ||
                       (_emsUrlReady && (!settings.CheckLoginBeforeCollection || _emsPageCount > 0));
        var emsDetail = !usesBrowser
            ? "当前任务不需要 EMS 页面"
            : !_emsUrlReady
                ? "请在系统设置中填写有效的 EMS 地址"
                : _emsPageCount == 0 && settings.CheckLoginBeforeCollection
                    ? "请在采集浏览器中完成 EMS 登录"
                    : "EMS 页面和登录态可用";

        PreflightChecks.Clear();
        PreflightChecks.Add(localReady
            ? PreflightCheckRow.Ok("本地采集组件", "运行时、依赖和任务脚本可用")
            : PreflightCheckRow.Warning("本地采集组件", string.Join("；", localFailures)));
        PreflightChecks.Add(dataReady
            ? PreflightCheckRow.Ok("当前数据", dataDetail)
            : PreflightCheckRow.Warning("当前数据", dataDetail));
        PreflightChecks.Add(browserReady
            ? PreflightCheckRow.Ok("采集浏览器", browserDetail)
            : PreflightCheckRow.Warning("采集浏览器", browserDetail));
        PreflightChecks.Add(emsReady
            ? PreflightCheckRow.Ok("EMS 页面", emsDetail)
            : PreflightCheckRow.Warning("EMS 页面", emsDetail));

        var localRequirement = new CollectionPreflightRequirement(
            "本地采集组件",
            localReady ? string.Empty : string.Join("；", localFailures),
            localReady);
        var dataRequirement = new CollectionPreflightRequirement("当前数据", dataDetail, dataReady);
        var browserRequirement = new CollectionPreflightRequirement("采集浏览器", browserDetail, browserReady);
        var emsRequirement = new CollectionPreflightRequirement(
            _emsUrlReady ? "EMS 登录" : "EMS 地址",
            emsDetail,
            emsReady);
        IReadOnlyList<CollectionPreflightRequirement> requirements;
        if (!_emsUrlReady && usesBrowser)
        {
            requirements = [localRequirement, emsRequirement, browserRequirement, dataRequirement];
        }
        else
        {
            requirements = [localRequirement, browserRequirement, emsRequirement, dataRequirement];
        }

        var summary = CollectionPreflightSummaryBuilder.Build(requirements);
        IsEnvironmentReady = summary.IsReady;
        ReadinessTitle = summary.Title;
        ReadinessDetail = summary.Detail;
        ReadinessGlyph = summary.IsReady ? "\uE930" : "\uE7BA";
        PreflightDetailsHeader = summary.DetailsHeader;
        PreflightProgressValue = summary.TotalCount == 0
            ? 0
            : summary.PassedCount * 100d / summary.TotalCount;
        StartCommand.NotifyCanExecuteChanged();
        NotifyActionButtonPriorityChanged();
    }

    private CollectionTaskExecutionPlan BuildExecutionPlan(CollectionTaskModeOption? mode)
    {
        return CollectionTaskModeCatalog.BuildPlan(
            mode?.Value,
            new CollectionCustomTaskOptions(
                RunImportAfterCollect,
                RunQualityAfterImport,
                RunRealtimeDetailsAfterImport,
                RunRealtimeAuditAfterDetails));
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var selectedBuildings = Buildings.Where(item => item.IsSelected).Select(item => item.Value).ToList();
        var plan = BuildExecutionPlan(SelectedTaskMode);
        if (!CanStart())
        {
            StatusText = IsEnvironmentReady ? "请先选择采集楼栋" : ReadinessDetail;
            AddLog("任务未启动：" + StatusText);
            return;
        }

        if (plan.RequiresBuildings && selectedBuildings.Count == 0)
        {
            AddLog("至少选择一栋楼。");
            StatusText = "未选择采集范围";
            return;
        }

        IDisposable operationLease;
        try
        {
            operationLease = operationState.BeginCollectionTask();
        }
        catch (InvalidOperationException)
        {
            StatusText = operationState.IsUpdateInstallPending
                ? "软件更新正在启动，不能开始采集"
                : "已有采集任务正在运行";
            AddLog("任务未启动：" + StatusText);
            return;
        }
        using var operationLeaseScope = operationLease;

        _activeTask = new CancellationTokenSource();
        _stopRequested = false;
        _currentDataUpdatedThisRun = false;
        _activeStageKey = string.Empty;
        IsRunning = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = "准备采集";
        CurrentActivityText = "正在准备采集";
        CurrentBuildingText = "准备中";
        CollectedCountText = "--";
        DataUpdateText = "尚未更新";
        RunDurationText = "0 秒";
        LastHeartbeatText = "正在启动";
        StartHeartbeat();
        ResetStages(plan);
        var settings = settingsService.Load();
        _activeDataDirectory = pathService.Capture(settings).DataDirectory;
        var runEnumeration = plan.RunEnumeration;
        var runValidation = plan.RunValidation;
        var runImportAfterCollect = plan.RunImport;
        var runQualityAfterImport = plan.RunQuality;
        var runRealtimeDetailsAfterImport = plan.RunRealtimeDetails;
        var runRealtimeAuditAfterDetails = plan.RunRealtimeAudit;
        var enableLogFile = EnableLogFile;
        var logCategory = LogCategory.Trim();
        var recaptureText = RecaptureText.Trim();
        var enableSelfDiagnose = EnableSelfDiagnose;
        var disableNetworkMonitor = DisableNetworkMonitor;
        var realtimeBatchSize = ClampInt(RealtimeBatchSize, 1, 100);
        var realtimeReopenEvery = ClampInt(RealtimeReopenEvery, 0, 50);
        var realtimeTimeoutMs = ClampInt(RealtimeTimeoutMs, 3000, 120000);
        var realtimeMaxDevices = ClampInt(RealtimeMaxDevices, 0, 20000);
        var refreshInventoryBeforeRealtime = RefreshInventoryBeforeRealtime;
        var skipInventoryCheck = SkipInventoryCheck;
        if (runEnumeration && settings.CheckLoginBeforeCollection)
        {
            var cdpStatus = await environmentProbe
                .CheckEdgeCdpAsync(GetEffectiveCdpPort(settings), settings.EmsUrl)
                .ConfigureAwait(true);
            if (!cdpStatus.IsReachable || cdpStatus.EmsPageCount == 0)
            {
                StatusText = "采集启动已阻止：未发现可采集 EMS 页面";
                AddLog(StatusText);
                AddLog(cdpStatus.LoginDetail);
                _activeTask.Dispose();
                _activeTask = null;
                _activeDataDirectory = null;
                IsRunning = false;
                StartCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
                CheckEnvironmentCommand.NotifyCanExecuteChanged();
                return;
            }

            AddLog("启动前检查：" + cdpStatus.LoginDetail);
        }
        else if (runEnumeration)
        {
            AddLog("启动前登录态检查已关闭：将由枚举脚本按当前设置继续。");
        }

        _activeCollectionBuildings = selectedBuildings;
        var enumProgressCeiling = runRealtimeDetailsAfterImport
            ? 58
            : runImportAfterCollect
                ? runQualityAfterImport ? 85 : 90
            : 100;
        StatusText = plan.RunningStatus;
        AddLog("任务启动：" + StatusText);
        AddLog($"任务模式：{plan.Label}；楼栋 {ValueOrDash(string.Join("、", selectedBuildings))}");
        AddLog($"本次选项：枚举 {(runEnumeration ? "开启" : "关闭")}；校验 {(runValidation ? "开启" : "关闭")}；导入 SQLite {(runImportAfterCollect ? "开启" : "关闭")}；基础质量检查 {(runQualityAfterImport ? "开启" : "关闭")}；实时详情 {(runRealtimeDetailsAfterImport ? "开启" : "关闭")}；实时审计 {(runRealtimeAuditAfterDetails ? "开启" : "关闭")}；日志文件 {(enableLogFile ? "开启" : "关闭")}");
        if (runEnumeration && (logCategory.Length > 0 || recaptureText.Length > 0 || enableSelfDiagnose || disableNetworkMonitor))
        {
            AddLog($"枚举高级参数：日志类别 {ValueOrDash(logCategory)}；补采 {ValueOrDash(recaptureText)}；自检 {(enableSelfDiagnose ? "开启" : "关闭")}；网络监听 {(disableNetworkMonitor ? "关闭" : "开启")}");
        }

        if (runRealtimeDetailsAfterImport)
        {
            AddLog($"实时高级参数：批量 {realtimeBatchSize}；重开间隔 {realtimeReopenEvery}；超时 {realtimeTimeoutMs}ms；最大设备 {realtimeMaxDevices}；刷新清单 {(refreshInventoryBeforeRealtime ? "开启" : "关闭")}；跳过清单检查 {(skipInventoryCheck ? "开启" : "关闭")}");
        }
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        CheckEnvironmentCommand.NotifyCanExecuteChanged();

        try
        {
            if (runEnumeration)
            {
                SetStageState("collect", "进行中", "正在从 EMS 读取所选楼栋");
                await RunEnumerationAsync(
                    selectedBuildings,
                    settings,
                    enableLogFile,
                    logCategory,
                    recaptureText,
                    enableSelfDiagnose,
                    disableNetworkMonitor,
                    enumProgressCeiling,
                    _activeTask.Token);
                _snapshotReady = true;
                SetStageState("collect", "已完成", "所选楼栋采集完成");
                ProgressValue = Math.Max(ProgressValue, enumProgressCeiling);
                ProgressText = runValidation
                    ? "采集完成，准备校验 JSON"
                    : runImportAfterCollect ? "采集完成，准备导入 SQLite" : "采集完成";
            }

            if (runValidation)
            {
                SetStageState("validate", "进行中", "正在检查采集结果完整性");
                IsProgressIndeterminate = true;
                ProgressValue = Math.Max(ProgressValue, runImportAfterCollect ? enumProgressCeiling + 2 : 20);
                ProgressText = "正在校验采集 JSON";
                await RunValidationAsync(_activeTask.Token);
                IsProgressIndeterminate = false;
                SetStageState("validate", "已完成", "采集结果校验通过");
                ProgressValue = Math.Max(ProgressValue, runImportAfterCollect ? enumProgressCeiling + 4 : 100);
                ProgressText = runImportAfterCollect ? "JSON 校验通过，准备导入 SQLite" : "JSON 校验通过";
            }

            if (runImportAfterCollect)
            {
                SetStageState("import", "进行中", "正在更新所选楼栋的当前数据");
                IsProgressIndeterminate = true;
                var importProgress = runRealtimeDetailsAfterImport
                    ? 68
                    : runQualityAfterImport ? 88 : 94;
                ProgressValue = Math.Max(ProgressValue, importProgress - 2);
                ProgressText = "正在导入 SQLite";
                await RunImportAsync(selectedBuildings, _activeTask.Token);
                IsProgressIndeterminate = false;
                _currentDataUpdatedThisRun = true;
                _databaseReady = true;
                CanOpenDataAfterImport = true;
                DataUpdateText = "当前数据已更新";
                SetStageState("import", "已完成", "当前数据已更新");
                ProgressValue = Math.Max(ProgressValue, importProgress);
                ProgressText = runQualityAfterImport || runRealtimeDetailsAfterImport ? "SQLite 已导入" : "100%";
            }

            if (runImportAfterCollect && runQualityAfterImport)
            {
                SetStageState("quality", "进行中", "正在检查数据质量");
                IsProgressIndeterminate = true;
                var qualityProgress = runRealtimeDetailsAfterImport ? 74 : 96;
                ProgressValue = Math.Max(ProgressValue, qualityProgress - 2);
                ProgressText = "正在运行数据质量检查";
                await RunQualityAsync(_activeTask.Token);
                IsProgressIndeterminate = false;
                ProgressValue = qualityProgress;
                await RefreshAuditAsync(_activeTask.Token, ActiveDatabasePath).ConfigureAwait(true);
                SetStageState(
                    "quality",
                    QualityIssues.Count > 0 ? "需复核" : "已完成",
                    QualityIssues.Count > 0 ? $"发现 {QualityIssues.Sum(issue => issue.Count):N0} 项待复核问题" : "质量检查通过");
            }

            if (runRealtimeDetailsAfterImport)
            {
                SetStageState("realtime", "进行中", "正在更新实时详情");
                IsProgressIndeterminate = true;
                var realtimeBase = Math.Max(ProgressValue, runImportAfterCollect || runQualityAfterImport ? 74 : enumProgressCeiling);
                ProgressValue = realtimeBase;
                ProgressText = "正在更新实时详情";
                await RunRealtimeDetailsAsync(
                    selectedBuildings,
                    settings,
                    enableLogFile,
                    realtimeBatchSize,
                    realtimeReopenEvery,
                    realtimeTimeoutMs,
                    realtimeMaxDevices,
                    refreshInventoryBeforeRealtime,
                    skipInventoryCheck,
                    realtimeBase,
                    23,
                    _activeTask.Token);
                IsProgressIndeterminate = false;
                ProgressValue = Math.Max(ProgressValue, 97);
                ProgressText = runRealtimeAuditAfterDetails ? "实时详情已更新，准备审计" : "实时详情已更新";
                if (!runRealtimeAuditAfterDetails)
                {
                    SetStageState("realtime", "已完成", "实时详情已更新");
                }
            }

            if (runRealtimeAuditAfterDetails)
            {
                SetStageState("realtime", "进行中", "正在运行实时点位审计");
                IsProgressIndeterminate = true;
                ProgressValue = Math.Max(ProgressValue, 98);
                ProgressText = "正在运行实时点位审计";
                await RunRealtimeAuditAsync(settings, _activeTask.Token);
                IsProgressIndeterminate = false;
                ProgressValue = 99;
                await RefreshRealtimeAuditAsync(_activeTask.Token).ConfigureAwait(true);
                SetStageState("realtime", "已完成", "实时详情和点位审计已完成");
            }

            if (runRealtimeDetailsAfterImport)
            {
                await RefreshReconciliationAsync(_activeTask.Token).ConfigureAwait(true);
            }

            IsProgressIndeterminate = false;
            ProgressValue = 100;
            ProgressText = "100%";
            StatusText = _currentDataUpdatedThisRun
                ? QualityIssues.Count > 0
                    ? "任务完成，当前数据已更新，存在待复核质量问题"
                    : "任务完成，当前数据已更新"
                : plan.CompletedStatus(runImportAfterCollect, runRealtimeDetailsAfterImport);
            CurrentActivityText = StatusText;
            DataUpdateText = _currentDataUpdatedThisRun ? "当前数据已更新" : DataUpdateText;
            AddLog(StatusText);
            StopHeartbeat();
        }
        catch (OperationCanceledException)
        {
            StopHeartbeat();
            IsProgressIndeterminate = false;
            ProgressText = "已停止";
            SetActiveStageTerminalState("已停止", "用户停止了任务");
            StatusText = _currentDataUpdatedThisRun
                ? "任务已停止；当前数据已经更新，后续检查未完成"
                : "任务已停止；当前数据未更改";
            CurrentActivityText = StatusText;
            DataUpdateText = _currentDataUpdatedThisRun ? "当前数据已更新；后续步骤未完成" : "当前数据未更改";
            AddLog(StatusText);
        }
        catch (Exception ex)
        {
            StopHeartbeat();
            var failure = ApplicationFailureClassifier.Classify(ex);
            IsProgressIndeterminate = false;
            var cancelled = failure.Category == ApplicationErrorCategory.Cancelled;
            ProgressText = cancelled ? "已停止" : "任务失败";
            SetActiveStageTerminalState(cancelled ? "已停止" : "失败", failure.DisplayText);
            StatusText = (_currentDataUpdatedThisRun
                ? cancelled
                    ? "任务已停止；当前数据已经更新，后续步骤未完成："
                    : "任务失败；当前数据已经更新，后续步骤未完成："
                : cancelled ? "任务已停止；当前数据未更改：" : "任务失败；当前数据未更改：") +
                failure.DisplayText;
            CurrentActivityText = StatusText;
            DataUpdateText = _currentDataUpdatedThisRun ? "当前数据已更新；后续步骤未完成" : "当前数据未更改";
            AddFailureLog(failure, ex);
            AddLog(StatusText);
        }
        finally
        {
            _activeTask?.Dispose();
            _activeTask = null;
            _activeCollectionBuildings = [];
            _activeProgressBase = 0;
            _activeProgressSpan = 100;
            _activeProgressLabel = string.Empty;
            _activeWorkflowId = null;
            _activeDataDirectory = null;
            IsRunning = false;
            _stopRequested = false;
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            CheckEnvironmentCommand.NotifyCanExecuteChanged();
            OpenDataCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanStop() => IsRunning && !_stopRequested;

    private bool CanOpenData() => CanOpenDataAfterImport && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanOpenData))]
    private void OpenData()
    {
        navigationService.NavigateToData(new DataNavigationRequest());
    }

    [RelayCommand(CanExecute = nameof(CanOpenAudit))]
    private void OpenAudit()
    {
        navigationService.NavigateToAudit();
    }

    private bool CanToggleCollectionBrowser() => !IsRunning && !IsCheckingEnvironment;

    [RelayCommand(CanExecute = nameof(CanToggleCollectionBrowser))]
    private async Task ToggleCollectionBrowserAsync()
    {
        RefreshCollectionBrowserState();
        if (IsCollectionBrowserOpen)
        {
            if (!TryDisposeOwnedBrowser())
            {
                StatusText = "无法关闭采集浏览器，请关闭其中的页面后重试";
                UpdateCollectionBrowserPresentation();
                return;
            }

            StatusText = "采集浏览器已关闭";
            AddLog(StatusText);
            UpdateCollectionBrowserPresentation();
            await CheckEnvironmentAsync().ConfigureAwait(true);
            return;
        }

        await OpenOwnedCollectionBrowserAsync().ConfigureAwait(true);
    }

    private async Task OpenOwnedCollectionBrowserAsync()
    {
        var settings = settingsService.Load();
        try
        {
            if (!TryDisposeOwnedBrowser())
            {
                throw new InvalidOperationException("The previous owned Edge process is still running.");
            }
            var edgePath = ResolveEdgePath();
            _ownedEdgeSessionRoot = Path.Combine(
                AppStorageDefaults.ProductDirectory,
                "browser-sessions",
                Guid.NewGuid().ToString("N"));
            var profilePath = Path.Combine(_ownedEdgeSessionRoot, "profile");
            Directory.CreateDirectory(profilePath);
            var startInfo = new ProcessStartInfo
            {
                FileName = edgePath,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--edge-skip-compat-layer-relaunch");
            startInfo.ArgumentList.Add("--remote-debugging-port=0");
            startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
            startInfo.ArgumentList.Add("--user-data-dir=" + profilePath);
            startInfo.ArgumentList.Add("about:blank");
            _ownedEdgeProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Edge process could not be started.");
            _ownedEdgeCdpPort = await OwnedEdgeCdpEndpoint
                .WaitForPortAsync(profilePath, _ownedEdgeProcess)
                .ConfigureAwait(true);
            IsCollectionBrowserOpen = true;
            UpdateCollectionBrowserPresentation();
            StatusText = "采集浏览器已打开，请在其中完成 EMS 登录";
            AddLog($"已启动采集专用 Edge，CDP 端口 {_ownedEdgeCdpPort}");

            var emsOpened = false;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(800).ConfigureAwait(true);
                var cdpStatus = await environmentProbe
                    .CheckEdgeCdpAsync(GetEffectiveCdpPort(settings), settings.EmsUrl)
                    .ConfigureAwait(true);
                if (cdpStatus.IsReachable)
                {
                    if (_ownedEdgeProcess.HasExited)
                    {
                        throw new InvalidOperationException("Owned Edge exited before EMS navigation.");
                    }
                    await CollectionEnvironmentProbe
                        .OpenEmsPageAsync(GetEffectiveCdpPort(settings), settings.EmsUrl)
                        .ConfigureAwait(true);
                    emsOpened = true;
                    await CheckEnvironmentAsync().ConfigureAwait(true);
                    break;
                }
            }
            if (!emsOpened)
            {
                throw new InvalidOperationException("Owned Edge CDP did not become reachable before EMS navigation.");
            }
        }
        catch (Exception ex)
        {
            TryDisposeOwnedBrowser();
            RefreshCollectionBrowserState();
            var failure = ApplicationFailureClassifier.Classify(ex);
            StatusText = "无法打开采集浏览器：" + failure.DisplayText;
            AddFailureLog(failure, ex);
            AddLog(StatusText);
        }
    }

    private static string ResolveEdgePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? "msedge.exe";
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (_stopRequested)
        {
            return;
        }

        _stopRequested = true;
        _activeTask?.Cancel();
        AddLog("正在停止任务...");
        StopCommand.NotifyCanExecuteChanged();
    }

    private bool CanMarkRunAnomaly() => SelectedRun is { IsAnomaly: false } && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanMarkRunAnomaly))]
    private async Task MarkRunAnomalyAsync()
    {
        if (SelectedRun is null)
        {
            return;
        }

        var runId = SelectedRun.Id;
        await collectionRunRepository
            .SetAnomalyAsync(runId, true, "采集数据异常，已隔离")
            .ConfigureAwait(true);
        AddLog($"已标记异常批次：#{runId}");
        await RefreshRunsAsync().ConfigureAwait(true);
    }

    private bool CanClearRunAnomaly() => SelectedRun is { IsAnomaly: true } && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanClearRunAnomaly))]
    private async Task ClearRunAnomalyAsync()
    {
        if (SelectedRun is null)
        {
            return;
        }

        var runId = SelectedRun.Id;
        await collectionRunRepository
            .SetAnomalyAsync(runId, false, string.Empty)
            .ConfigureAwait(true);
        AddLog($"已取消异常标记：#{runId}");
        await RefreshRunsAsync().ConfigureAwait(true);
    }

    private bool CanRestoreRun() => SelectedRun is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRestoreRun))]
    private async Task RestoreRunAsync()
    {
        if (SelectedRun is null)
        {
            return;
        }

        var runId = SelectedRun.Id;
        StatusText = $"正在恢复历史批次 #{runId}";
        var result = await collectionRunRepository.RestoreCurrentAsync(runId).ConfigureAwait(true);
        CanOpenDataAfterImport = true;
        AddLog($"已恢复批次 #{result.RunId}：{result.RestoredCards:N0} 张卡片");
        StatusText = "已恢复历史批次到当前数据";
        await RefreshAuditAndRunsAsync().ConfigureAwait(true);
        OpenDataCommand.NotifyCanExecuteChanged();
    }

    public bool CanDeleteRun() => SelectedRun is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanDeleteRun))]
    public async Task DeleteRunAsync()
    {
        if (SelectedRun is null)
        {
            return;
        }

        var runId = SelectedRun.Id;
        StatusText = $"正在删除历史批次 #{runId}";
        var result = await collectionRunRepository.DeleteAsync(runId).ConfigureAwait(true);
        AddLog($"已删除历史批次 #{result.RunId}：{result.DeletedCards:N0} 张历史卡片");
        StatusText = "已删除历史批次";
        SelectedRun = null;
        await RefreshRunsAsync().ConfigureAwait(true);
    }

    private async Task RunEnumerationAsync(
        IReadOnlyList<string> buildings,
        AppSettings settings,
        bool enableLogFile,
        string logCategory,
        string recaptureText,
        bool enableSelfDiagnose,
        bool disableNetworkMonitor,
        double progressSpan,
        CancellationToken cancellationToken)
    {
        var mode = "--edge";
        var args = new List<string>
        {
            mode,
            "--append",
            "--bldg=" + string.Join(",", buildings),
            "--log-level=" + settings.LogLevel,
            "--out-dir=" + ActiveDataDirectory,
            "--cdp-url=http://127.0.0.1:" + GetEffectiveCdpPort(settings),
        };
        if (!settings.CheckLoginBeforeCollection)
        {
            args.Add("--skip-login-check");
        }

        if (enableLogFile)
        {
            args.Add("--log-file");
        }

        if (!string.IsNullOrWhiteSpace(logCategory))
        {
            args.Add("--log-category=" + logCategory.Trim());
        }

        if (!string.IsNullOrWhiteSpace(recaptureText))
        {
            args.Add("--recapture=" + recaptureText.Trim());
        }

        if (enableSelfDiagnose)
        {
            args.Add("--self-diagnose");
        }

        if (disableNetworkMonitor)
        {
            args.Add("--no-net-monitor");
        }

        await RunStepAsync(
            "卡片枚举",
            Path.Combine("sidecar", "collect.js"),
            args,
            cancellationToken,
            BuildTaskEnvironment(settings),
            progressBase: 0,
            progressSpan: progressSpan);
    }

    private async Task RunImportAsync(
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken)
    {
        AddLog("开始原生 SQLite 导入");
        Directory.CreateDirectory(ActiveDataDirectory);
        var report = await snapshotImporter.ImportAsync(
            new CollectionImportRequest(
                ActiveSnapshotPath,
                ActiveDatabasePath,
                buildings,
                Apply: true),
            cancellationToken).ConfigureAwait(true);
        AddLog(
            $"原生导入完成：批次 #{report.RunId?.ToString() ?? "-"}；" +
            $"楼栋 {report.SnapshotSelected.BuildingCount:N0}；设备 {report.SnapshotSelected.UniqueCardCount:N0}；" +
            $"去重观察 {report.SnapshotSelected.DeduplicatedObservationCount:N0}");
        if (!string.IsNullOrWhiteSpace(report.MigrationBackupPath))
        {
            AddLog("迁移备份：" + report.MigrationBackupPath);
        }
    }

    private async Task RunValidationAsync(
        CancellationToken cancellationToken)
    {
        AddLog("开始校验 CollectionSnapshot v1");
        var result = await snapshotReader
            .ReadAsync(ActiveSnapshotPath, cancellationToken)
            .ConfigureAwait(true);
        AddLog(
            $"快照校验通过：workflow {result.Snapshot.WorkflowId}；" +
            $"楼栋 {result.Snapshot.Counts.BuildingCount:N0}；页面 {result.Snapshot.Counts.PageCount:N0}；" +
            $"原始卡片 {result.Snapshot.Counts.RawCardCount:N0}；唯一卡片 {result.Snapshot.Counts.UniqueCardCount:N0}");
    }

    private async Task RunQualityAsync(CancellationToken cancellationToken)
    {
        AddLog("开始原生 SQLite 质量检查");
        var report = await nativeQualityAuditService
            .AuditAsync(
                new NativeQualityAuditRequest(QualityAuditSourceKind.LatestCompletedRun, ActiveDatabasePath),
                cancellationToken)
            .ConfigureAwait(true);
        if (report is null)
        {
            throw new InvalidOperationException("数据库中没有可审计的已完成采集批次。");
        }

        AddLog(
            $"原生质量检查完成：设备 {report.Summary.TotalCards:N0}；" +
            $"待复核 {report.Summary.IssueCount:N0}；已知问题 {report.Summary.KnownFindings:N0}");
    }

    private Task RunRealtimeDetailsAsync(
        IReadOnlyList<string> buildings,
        AppSettings settings,
        bool enableLogFile,
        int batchSize,
        int reopenEvery,
        int timeoutMs,
        int maxDevices,
        bool refreshInventory,
        bool skipInventory,
        double progressBase,
        double progressSpan,
        CancellationToken cancellationToken)
    {
        var browserMode = "cdp";
        var args = new List<string>
        {
            "--buildings=" + string.Join(",", buildings),
            "--browser-mode=" + browserMode,
            "--batch-size=" + batchSize,
            "--reopen-every=" + reopenEvery,
            "--timeout=" + timeoutMs,
            "--write-latest",
            "--skip-audit",
        };
        if (refreshInventory)
        {
            args.Add("--refresh-inventory");
        }

        if (skipInventory)
        {
            args.Add("--skip-inventory");
        }

        if (maxDevices > 0)
        {
            args.Add("--max-devices=" + maxDevices);
        }

        if (enableLogFile)
        {
            args.Add("--log-file");
        }

        return RunStepAsync(
            "实时详情采集",
            Path.Combine("scripts", "collect-realtime-all-batch.js"),
            args,
            cancellationToken,
            BuildTaskEnvironment(settings),
            progressBase,
            progressSpan);
    }

    private Task RunRealtimeAuditAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        return RunStepAsync(
            "实时点位审计",
            Path.Combine("scripts", "audit-realtime-data.js"),
            [],
            cancellationToken,
            BuildTaskEnvironment(settings));
    }

    private IReadOnlyDictionary<string, string> BuildTaskEnvironment(AppSettings settings)
    {
        var environment = new Dictionary<string, string>(BuildDataEnvironment(ActiveDataDirectory))
        {
            ["EMS_URL"] = settings.EmsUrl,
            ["CDP_URL"] = "http://127.0.0.1:" + GetEffectiveCdpPort(settings),
            ["REALTIME_BROWSER_MODE"] = "cdp",
        };
        return environment;
    }

    private string ActiveDataDirectory => _activeDataDirectory
        ?? throw new InvalidOperationException("The collection task data directory has not been initialized.");

    private int GetEffectiveCdpPort(AppSettings settings)
    {
        if (_ownedEdgeCdpPort is not { } ownedPort)
        {
            return settings.EdgeCdpPort;
        }
        if (_ownedEdgeProcess is null || _ownedEdgeProcess.HasExited)
        {
            throw new InvalidOperationException("Owned Edge is no longer running.");
        }
        return ownedPort;
    }

    private string ActiveSnapshotPath => Path.Combine(ActiveDataDirectory, "collection_snapshot_v1.json");

    private string ActiveDatabasePath => Path.Combine(ActiveDataDirectory, "ac.db");

    private static IReadOnlyDictionary<string, string> BuildDataEnvironment(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        return new Dictionary<string, string>
        {
            ["EMS_OUT_DIR"] = dataDirectory,
            ["EMS_JSON_PATH"] = Path.Combine(dataDirectory, "enum_full_v5.json"),
            ["EMS_SNAPSHOT_PATH"] = Path.Combine(dataDirectory, "collection_snapshot_v1.json"),
            ["EMS_DB_PATH"] = Path.Combine(dataDirectory, "ac.db"),
            ["EMS_QUALITY_OUT"] = dataDirectory,
        };
    }

    private async Task RunStepAsync(
        string label,
        string script,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        double progressBase = 0,
        double progressSpan = 100,
        bool exitCodeTwoMeansFindings = false)
    {
        _activeProgressBase = progressBase;
        _activeProgressSpan = progressSpan;
        _activeProgressLabel = label;
        StatusText = "正在执行：" + label;
        AddLog("开始 " + label);
        var result = await runner.RunWorkflowScriptAsync(
            script,
            args,
            AddLog,
            AddWorkflowEvent,
            cancellationToken,
            environment,
            exitCodeTwoMeansFindings).ConfigureAwait(true);

        if (!result.IsSuccessful)
        {
            throw new WorkflowExecutionException(
                label,
                result.Outcome,
                result.ExitCode,
                result.Message);
        }

        AddLog(result.Outcome == WorkflowTerminalOutcome.SucceededWithFindings
            ? label + " 完成，但存在需复核项"
            : label + " 完成");
    }

    private void AddWorkflowEvent(WorkflowEventV1 workflowEvent)
    {
        _activeWorkflowId = workflowEvent.WorkflowId;
        if (workflowEvent.Type != WorkflowEventType.Progress || workflowEvent.Progress is null)
        {
            return;
        }

        var progressJson = workflowEvent.Progress.Data is { } data
            ? data.GetRawText()
            : JsonSerializer.Serialize(new
            {
                percent = workflowEvent.Progress.Percent,
                message = workflowEvent.Progress.Message,
                current = workflowEvent.Progress.Current,
                total = workflowEvent.Progress.Total,
                unit = workflowEvent.Progress.Unit,
            });
        AddLog("[PROGRESS]" + progressJson);
    }

    private void AddLog(string message)
    {
        var normalized = NormalizeLogMessage(message);
        var level = message.StartsWith("[stderr]", StringComparison.Ordinal)
            ? ApplicationLogLevel.Warning
            : ApplicationLogLevel.Information;
        applicationLogger.Write(new ApplicationLogEvent(
            level,
            "collection",
            message.StartsWith("[PROGRESS]", StringComparison.Ordinal) ? "workflow_progress" : "task_message",
            normalized,
            new ApplicationLogContext(_activeWorkflowId, EmptyToNull(_activeStageKey))));
        var row = new CollectionTaskLogRow(DateTime.Now.ToString("HH:mm:ss"), normalized);
        _dispatcherQueue.TryEnqueue(() =>
        {
            MarkActivity();
            ApplyProgressEvent(message);
            Logs.Add(row);
            while (Logs.Count > 300)
            {
                Logs.RemoveAt(0);
            }

            OnPropertyChanged(nameof(CanClearLogs));
            ClearLogsCommand.NotifyCanExecuteChanged();
        });
    }

    private void AddFailureLog(ApplicationFailure failure, Exception exception)
    {
        applicationLogger.Write(new ApplicationLogEvent(
            ApplicationLogLevel.Error,
            "collection",
            "operation_failed",
            failure.Title,
            new ApplicationLogContext(
                _activeWorkflowId,
                EmptyToNull(_activeStageKey),
                failure.Code,
                failure.IsRetryable),
            exception,
            new Dictionary<string, object?>
            {
                ["suggestedAction"] = failure.SuggestedAction,
            }));
    }

    private static string NormalizeLogMessage(string message)
    {
        if (message.StartsWith("[PROGRESS]", StringComparison.Ordinal))
        {
            return CollectionProgressPresenter.Parse(message["[PROGRESS]".Length..]).LogText;
        }

        return message;
    }

    private void ApplyProgressEvent(string message)
    {
        if (!message.StartsWith("[PROGRESS]", StringComparison.Ordinal))
        {
            return;
        }

        var progress = CollectionProgressPresenter.Parse(message["[PROGRESS]".Length..]);
        if (!progress.IsValid)
        {
            IsProgressIndeterminate = true;
            CurrentActivityText = progress.LogText;
            return;
        }

        CurrentActivityText = progress.LogText;
        if (!string.IsNullOrWhiteSpace(progress.Building))
        {
            CurrentBuildingText = progress.Building;
        }

        if (progress.Percent is { } percent)
        {
            var value = _activeProgressBase + percent / 100d * _activeProgressSpan;
            ProgressValue = Math.Clamp(value, 0, 100);
            ProgressText = string.IsNullOrWhiteSpace(progress.ProgressMessage)
                ? $"{ProgressValue:0}% · {_activeProgressLabel}"
                : $"{ProgressValue:0}% · {progress.ProgressMessage}";
            if (progress.Current > 0 && progress.Total > 0)
            {
                CollectedCountText = $"{progress.Current:N0} / {progress.Total:N0}";
            }
            IsProgressIndeterminate = false;
            return;
        }

        if (!progress.IsEnumeration || progress.Total <= 0 || string.IsNullOrWhiteSpace(progress.Building))
        {
            return;
        }

        var buildingIndex = FindActiveBuildingIndex(progress.Building);
        var buildingCount = Math.Max(_activeCollectionBuildings.Count, 1);
        var currentRatio = Math.Clamp(progress.Current / (double)progress.Total, 0, 1);
        var collectionRatio = Math.Clamp((buildingIndex + currentRatio) / buildingCount, 0, 1);
        var enumeratorPercent = _activeProgressBase + collectionRatio * _activeProgressSpan;
        ProgressValue = enumeratorPercent;
        ProgressText = $"{enumeratorPercent:0}% · {progress.Building} 子区 {progress.Current}/{progress.Total}";
        if (progress.AccumulatedCards > 0)
        {
            CollectedCountText = $"{progress.AccumulatedCards:N0} 张（本页 {progress.PageCards:N0} 张）";
        }
        IsProgressIndeterminate = false;
    }

    public void Dispose()
    {
        StopHeartbeat();
        _activeTask?.Cancel();
        TryDisposeOwnedBrowser();
    }

    private void RefreshCollectionBrowserState()
    {
        if (_ownedEdgeProcess is not null && !IsOwnedBrowserProcessRunning())
        {
            TryDisposeOwnedBrowser();
        }

        IsCollectionBrowserOpen = IsOwnedBrowserProcessRunning();
        UpdateCollectionBrowserPresentation();
    }

    private void UpdateCollectionBrowserPresentation()
    {
        if (IsCollectionBrowserOpen)
        {
            CollectionBrowserActionText = "关闭采集浏览器";
            CollectionBrowserActionGlyph = "\uE711";
            CollectionBrowserActionToolTip = IsRunning
                ? "采集期间不能关闭浏览器"
                : "关闭 EMS Scout 专用采集浏览器";
            NotifyActionButtonPriorityChanged();
            return;
        }

        CollectionBrowserActionText = "打开采集浏览器";
        CollectionBrowserActionGlyph = "\uE774";
        CollectionBrowserActionToolTip = "打开 EMS Scout 专用采集浏览器";
        NotifyActionButtonPriorityChanged();
    }

    private bool IsOwnedBrowserProcessRunning()
    {
        try
        {
            return _ownedEdgeProcess is not null && !_ownedEdgeProcess.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryDisposeOwnedBrowser()
    {
        if (_ownedEdgeProcess is not null)
        {
            try
            {
                if (!_ownedEdgeProcess.HasExited) _ownedEdgeProcess.Kill(entireProcessTree: true);
                _ownedEdgeProcess.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                applicationLogger.Write(new ApplicationLogEvent(
                    ApplicationLogLevel.Warning,
                    "collection",
                    "owned_edge_stop_failed",
                    "Failed to stop the owned Edge process during cleanup.",
                    Exception: ex));
            }

            if (IsOwnedBrowserProcessRunning())
            {
                IsCollectionBrowserOpen = true;
                return false;
            }

            _ownedEdgeProcess.Dispose();
            _ownedEdgeProcess = null;
        }

        if (!string.IsNullOrWhiteSpace(_ownedEdgeSessionRoot))
        {
            DeleteBrowserSessionWithRetry(_ownedEdgeSessionRoot);
        }
        _ownedEdgeSessionRoot = null;
        _ownedEdgeCdpPort = null;
        IsCollectionBrowserOpen = false;
        return true;
    }

    private void CleanupStaleBrowserSessions()
    {
        var root = Path.Combine(AppStorageDefaults.ProductDirectory, "browser-sessions");
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
            {
                DeleteBrowserSessionWithRetry(directory);
            }
        }
    }

    private void DeleteBrowserSessionWithRetry(string directory)
    {
        var ownedRoot = Path.GetFullPath(Path.Combine(AppStorageDefaults.ProductDirectory, "browser-sessions"));
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(ownedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            applicationLogger.Write(new ApplicationLogEvent(
                ApplicationLogLevel.Warning,
                "collection",
                "browser_session_cleanup_refused",
                "Refused to delete a browser session outside the owned session root."));
            return;
        }

        for (var attempt = 0; attempt < 5 && Directory.Exists(target); attempt++)
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (IOException)
            {
                if (attempt < 4) Thread.Sleep(100 * (attempt + 1));
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt < 4) Thread.Sleep(100 * (attempt + 1));
            }
        }
        if (Directory.Exists(target))
        {
            applicationLogger.Write(new ApplicationLogEvent(
                ApplicationLogLevel.Warning,
                "collection",
                "browser_session_cleanup_failed",
                "The owned browser session directory could not be removed after retries."));
        }
    }

    private void StartHeartbeat()
    {
        _activeRunStartedAt = DateTimeOffset.Now;
        _lastActivityAt = _activeRunStartedAt;
        _heartbeatTimer ??= _dispatcherQueue.CreateTimer();
        _heartbeatTimer.Interval = TimeSpan.FromSeconds(1);
        _heartbeatTimer.Tick -= OnHeartbeatTick;
        _heartbeatTimer.Tick += OnHeartbeatTick;
        _heartbeatTimer.Start();
        UpdateHeartbeatText();
    }

    private void StopHeartbeat()
    {
        _heartbeatTimer?.Stop();
        UpdateHeartbeatText();
    }

    private void MarkActivity()
    {
        if (_activeRunStartedAt is null)
        {
            return;
        }

        _lastActivityAt = DateTimeOffset.Now;
        UpdateHeartbeatText();
    }

    private void OnHeartbeatTick(DispatcherQueueTimer sender, object args)
    {
        UpdateHeartbeatText();
    }

    private void UpdateHeartbeatText()
    {
        if (_activeRunStartedAt is not { } startedAt)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var elapsed = now - startedAt;
        RunDurationText = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        LastHeartbeatText = _lastActivityAt is { } lastActivity
            ? $"最近活动 {Math.Max(0, (int)(now - lastActivity).TotalSeconds)} 秒前"
            : "尚未更新";
    }

    private int FindActiveBuildingIndex(string building)
    {
        for (var i = 0; i < _activeCollectionBuildings.Count; i++)
        {
            if (string.Equals(_activeCollectionBuildings[i], building, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private static int ClampInt(double value, int min, int max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return min;
        }

        return Math.Clamp(Convert.ToInt32(Math.Round(value)), min, max);
    }

    private static string ValueOrDash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int ReconciliationTypeSort(string type)
    {
        return type switch
        {
            RealtimeReconciliationTypes.NewDevice => 0,
            RealtimeReconciliationTypes.MissingInRealtime => 1,
            RealtimeReconciliationTypes.MatchFailed => 2,
            RealtimeReconciliationTypes.VirtualOverride => 3,
            RealtimeReconciliationTypes.DuplicateRender => 4,
            RealtimeReconciliationTypes.DataNoise => 5,
            _ => 99,
        };
    }

    private static string FormatDateTime(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : value;
    }

}
