using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Collection;
using EmsScout.Application.Devices;
using EmsScout.Application.Quality;
using EmsScout.Application.Settings;
using EmsScout.Desktop.Services;
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
    ICollectionRunRepository collectionRunRepository) : ObservableObject
{
    private CancellationTokenSource? _activeTask;
    private bool _stopRequested;
    private IReadOnlyList<string> _activeCollectionBuildings = [];
    private double _activeProgressBase;
    private double _activeProgressSpan = 100;
    private string _activeProgressLabel = string.Empty;
    private string _activeStageKey = string.Empty;
    private string _lastStepFailureDetail = string.Empty;
    private string _lastProgressLocation = string.Empty;
    private bool _currentDataUpdatedThisRun;
    private bool _buildingEventsAttached;
    private bool _environmentChecked;
    private bool _nodeReady;
    private bool _dependenciesReady;
    private bool _enumScriptReady;
    private bool _validationScriptReady;
    private bool _initialized;
    private DateTimeOffset _taskStartedAt;
    private bool _importScriptReady;
    private bool _qualityScriptReady;
    private bool _realtimeScriptReady;
    private bool _realtimeAuditScriptReady;
    private bool _databaseReady;
    private bool _jsonReady;
    private bool _emsUrlReady;
    private bool _cdpReachable;
    private int _emsPageCount;
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private DispatcherQueueTimer? _progressTimer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckEnvironmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenEmsCommand))]
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
    [NotifyPropertyChangedFor(nameof(CanStartTask))]
    [NotifyPropertyChangedFor(nameof(IsStartHighlighted))]
    [NotifyPropertyChangedFor(nameof(IsOpenBrowserHighlighted))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckEnvironmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenEmsCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllBuildingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearBuildingSelectionCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditTaskOptions))]
    [NotifyPropertyChangedFor(nameof(CanEditCustomTaskOptions))]
    [NotifyPropertyChangedFor(nameof(CanStartTask))]
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
    public partial string ProgressLocationText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ProgressDeviceText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ProgressOverallText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ProgressElapsedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ProgressSpeedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ProgressPageText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressPageValue { get; private set; }

    [ObservableProperty]
    public partial bool ShowCompletionCelebration { get; private set; }

    [ObservableProperty]
    public partial string CollectionCompletionText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CollectionCompletedAtText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CollectionDurationText { get; private set; } = string.Empty;

    public ObservableCollection<CollectionProgressBuildingRow> ProgressBuildings { get; } = [];

    [ObservableProperty]
    public partial string PreflightSummaryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool PreflightExpanded { get; set; }

    [ObservableProperty]
    public partial string TaskSummaryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool RunLogsExpanded { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartTask))]
    public partial bool IsEnvironmentReady { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartHighlighted))]
    [NotifyPropertyChangedFor(nameof(IsOpenBrowserHighlighted))]
    public partial bool IsCollectionBrowserConnected { get; private set; }

    [ObservableProperty]
    public partial string ReadinessTitle { get; private set; } = "正在检查采集环境";

    [ObservableProperty]
    public partial string ReadinessDetail { get; private set; } = "请稍候";

    [ObservableProperty]
    public partial string ReadinessGlyph { get; private set; } = "\uE9D9";

    [ObservableProperty]
    public partial string CollectionBrowserActionText { get; private set; } = "打开采集浏览器";

    [ObservableProperty]
    public partial string SelectedBuildingsText { get; private set; } = "已选择 6 栋楼";

    [ObservableProperty]
    public partial string CurrentDataImpactText { get; private set; } = "采集成功后将更新所选楼栋的当前数据";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskModeDescription))]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    [NotifyPropertyChangedFor(nameof(CanEditCustomTaskOptions))]
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
    public partial string SelectedLogSeverity { get; set; } = "ERROR";

    [ObservableProperty]
    public partial string LogCategory { get; set; } = string.Empty;

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
        new("1号", "1号", true),
        new("2号", "2号", true),
        new("3号", "3号", true),
        new("4号", "4号", true),
        new("5号", "5号", true),
        new("6号", "6号", true),
    ];

    public ObservableCollection<CollectionTaskModeOption> TaskModes { get; } =
        new(CollectionTaskModeCatalog.Options.Where(option => option.Value != CollectionTaskModeValues.Custom));

    public ObservableCollection<string> LogSeverityOptions { get; } = ["ERROR", "WARN", "INFO", "全部"];

    public ObservableCollection<double> RealtimeBatchSizeOptions { get; } = [10, 20, 50, 100];

    public ObservableCollection<double> RealtimeReopenEveryOptions { get; } = [0, 1, 3, 5, 10];

    public ObservableCollection<double> RealtimeTimeoutOptions { get; } = [5000, 10000, 15000, 30000, 60000];

    public ObservableCollection<CollectionTaskLogRow> Logs { get; } = [];

    public ObservableCollection<CollectionTaskLogRow> FilteredLogs { get; } = [];

    public ObservableCollection<CollectionStageRow> Stages { get; } = [];

    public ObservableCollection<PreflightCheckRow> PreflightChecks { get; } =
    [
        PreflightCheckRow.Pending("Node 运行时", "等待检查"),
        PreflightCheckRow.Pending("Node 依赖", "等待检查"),
        PreflightCheckRow.Pending("采集脚本", "等待检查"),
        PreflightCheckRow.Pending("数据文件", "等待检查"),
        PreflightCheckRow.Pending("Edge CDP", "等待检查"),
        PreflightCheckRow.Pending("EMS 地址", "等待检查"),
        PreflightCheckRow.Pending("EMS 登录态", "等待检查"),
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

    public bool CanEditCustomTaskOptions => CanEditTaskOptions && IsCustomTaskMode;

    public string TaskModeDescription => SelectedTaskMode?.Description ?? "请选择任务模式";

    public string StartButtonText => "开始采集";

    public bool IsStartHighlighted => !IsRunning && IsCollectionBrowserConnected;

    public bool IsOpenBrowserHighlighted => !IsRunning && !IsCollectionBrowserConnected;

    private bool IsCustomTaskMode => string.Equals(SelectedTaskMode?.Value, CollectionTaskModeValues.Custom, StringComparison.OrdinalIgnoreCase);

    public bool CanDeleteSelectedRun => CanDeleteRun();

    public bool CanStartTask => CanStart();

    partial void OnSelectedTaskModeChanged(CollectionTaskModeOption? value)
    {
        ApplyTaskModePreset(value);
        ResetStages(BuildExecutionPlan(value));
        UpdateEnvironmentReadiness();
        OnPropertyChanged(nameof(CanStartTask));
    }

    partial void OnSelectedLogSeverityChanged(string value) => RefreshFilteredLogs();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Idempotent: skip full re-init if already initialized and task is running.
        // Only refresh display-sensitive state (settings, db existence check).
        if (_initialized && IsRunning)
        {
            var settings = settingsService.Load();
            var dataDirectory = pathService.ResolveWorkspacePath(settings.DataDirectory);
            CanOpenDataAfterImport = File.Exists(Path.Combine(dataDirectory, "ac.db"));
            await Task.CompletedTask;
            return;
        }

        LoadSettingsDefaults();
        SelectedTaskMode ??= TaskModes.FirstOrDefault(
            mode => mode.Value == CollectionTaskModeValues.Full);
        ApplyTaskModePreset(SelectedTaskMode);
        AttachBuildingEvents();
        UpdateSelectedBuildingsText();
        ResetStages(BuildExecutionPlan(SelectedTaskMode));

        var settings2 = settingsService.Load();
        var dataDirectory2 = pathService.ResolveWorkspacePath(settings2.DataDirectory);
        CanOpenDataAfterImport = File.Exists(Path.Combine(dataDirectory2, "ac.db"));
        _initialized = true;
        await Task.CompletedTask;
    }

    public void LoadSettingsDefaults()
    {
        EnableLogFile = true;
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

    private bool CanEditBuildingSelection() => CanEditTaskOptions;

    [RelayCommand(CanExecute = nameof(CanEditBuildingSelection))]
    private void SelectAllBuildings()
    {
        foreach (var building in Buildings)
        {
            building.IsSelected = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditBuildingSelection))]
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
                    UpdateSelectedBuildingsText();
                    StartCommand.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(CanStartTask));
                }
            };
        }

        _buildingEventsAttached = true;
    }

    private void UpdateSelectedBuildingsText()
    {
        var selected = Buildings.Where(building => building.IsSelected).Select(building => building.Value).ToList();
        if (!IsRunning)
        {
            InitializeProgressBuildings(selected);
        }
        SelectedBuildingsText = selected.Count == 0
            ? "尚未选择楼栋"
            : $"已选择 {selected.Count} 栋：{string.Join("、", selected)}";
        CurrentDataImpactText = selected.Count == 0
            ? "选择至少一栋楼后才能开始"
            : $"成功后只更新 {string.Join("、", selected)}，其他楼栋保持不变";
    }

    private void InitializeProgressBuildings(IReadOnlyList<string> buildings)
    {
        ProgressBuildings.Clear();
        foreach (var building in buildings)
        {
            ProgressBuildings.Add(new CollectionProgressBuildingRow(building));
        }
    }

    private void UpdateProgressBuildings(string building, int buildingIndex, string? stage)
    {
        if (ProgressBuildings.Count == 0)
        {
            return;
        }

        var index = buildingIndex > 0
            ? buildingIndex - 1
            : ProgressBuildings.IndexOf(ProgressBuildings.FirstOrDefault(row =>
                string.Equals(row.Building, building, StringComparison.OrdinalIgnoreCase))!);
        if (index < 0 || index >= ProgressBuildings.Count)
        {
            return;
        }

        for (var i = 0; i < ProgressBuildings.Count; i++)
        {
            if (i < index || (i == index && string.Equals(stage, "building_done", StringComparison.OrdinalIgnoreCase)))
            {
                ProgressBuildings[i].MarkCompleted();
            }
            else if (i == index)
            {
                ProgressBuildings[i].MarkCurrent();
            }
            else
            {
                ProgressBuildings[i].MarkPending();
            }
        }
    }

    private void MarkAllProgressBuildingsCompleted()
    {
        foreach (var building in ProgressBuildings)
        {
            building.MarkCompleted();
        }
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

    private async Task RefreshAuditAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var report = await qualityAuditService.LoadLatestAsync(cancellationToken).ConfigureAwait(true);
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
            QualitySummaryText = ex.Message;
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
            RealtimeQualitySummaryText = ex.Message;
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
            ReconciliationSummaryText = ex.Message;
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
            RunsStatusText = "历史批次读取失败：" + ex.Message;
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
            var dataDirectory = pathService.ResolveWorkspacePath(settings.DataDirectory);
            var nodeModules = Directory.Exists(Path.Combine(runner.WorkspaceRoot, "node_modules"));
            var enumScript = File.Exists(Path.Combine(runner.WorkspaceRoot, "src", "enumerate.js"));
            var validationScript = File.Exists(Path.Combine(runner.WorkspaceRoot, "src", "enum-validator.js"));
            var importScript = File.Exists(Path.Combine(runner.WorkspaceRoot, "scripts", "import.js"));
            var qualityScript = File.Exists(Path.Combine(runner.WorkspaceRoot, "scripts", "quality-report.js"));
            var realtimeScript = File.Exists(Path.Combine(runner.WorkspaceRoot, "scripts", "collect-realtime-all-batch.js"));
            var realtimeAuditScript = File.Exists(Path.Combine(runner.WorkspaceRoot, "scripts", "audit-realtime-data.js"));
            var dbPath = File.Exists(Path.Combine(dataDirectory, "ac.db"));
            var jsonPath = File.Exists(Path.Combine(dataDirectory, "enum_full_v5.json"));
            var nodeVersion = await ReadNodeVersionAsync().ConfigureAwait(true);
            var nodeDependencies = await CheckNodeDependenciesAsync(runner.WorkspaceRoot).ConfigureAwait(true);
            var cdpStatus = await CheckEdgeCdpAsync(settings.EdgeCdpPort, settings.EmsUrl).ConfigureAwait(true);

            _environmentChecked = true;
            _nodeReady = nodeVersion != "不可用";
            _dependenciesReady = nodeModules && nodeDependencies == "可用";
            _enumScriptReady = enumScript;
            _validationScriptReady = validationScript;
            _importScriptReady = importScript;
            _qualityScriptReady = qualityScript;
            _realtimeScriptReady = realtimeScript;
            _realtimeAuditScriptReady = realtimeAuditScript;
            _databaseReady = dbPath;
            _jsonReady = jsonPath;
            _emsUrlReady = Uri.TryCreate(settings.EmsUrl, UriKind.Absolute, out _);
            _cdpReachable = cdpStatus.IsReachable;
            IsCollectionBrowserConnected = _cdpReachable;
            _emsPageCount = cdpStatus.EmsPageCount;

            PreflightChecks.Clear();
            PreflightChecks.Add(!_nodeReady
                ? PreflightCheckRow.Warning("Node 运行时", "未检测到 node")
                : PreflightCheckRow.Ok("Node 运行时", nodeVersion));
            PreflightChecks.Add(_dependenciesReady
                ? PreflightCheckRow.Ok("Node 依赖", "better-sqlite3、playwright 可加载")
                : PreflightCheckRow.Warning(
                    "Node 依赖",
                    $"node_modules {(nodeModules ? "存在" : "缺失")}；运行依赖 {nodeDependencies}"));
            PreflightChecks.Add(enumScript && validationScript && importScript && qualityScript
                ? PreflightCheckRow.Ok("基础流程", "采集、校验、导入和质量检查可用")
                : PreflightCheckRow.Warning(
                    "基础流程",
                    $"采集 {(enumScript ? "可用" : "缺失")}；校验 {(validationScript ? "可用" : "缺失")}；导入 {(importScript ? "可用" : "缺失")}；质量 {(qualityScript ? "可用" : "缺失")}"));
            PreflightChecks.Add(dbPath
                ? PreflightCheckRow.Ok("当前数据", "数据库可用")
                : PreflightCheckRow.Unknown("当前数据", "首次采集后创建数据库"));
            PreflightChecks.Add(cdpStatus.IsReachable
                ? PreflightCheckRow.Ok("采集浏览器", cdpStatus.Detail)
                : PreflightCheckRow.Warning("采集浏览器", "尚未启动，请点击“打开采集浏览器”"));
            PreflightChecks.Add(_emsUrlReady
                ? cdpStatus.EmsPageCount > 0
                    ? PreflightCheckRow.Ok("EMS 页面", cdpStatus.LoginDetail)
                    : PreflightCheckRow.Unknown("EMS 页面", cdpStatus.LoginDetail)
                : PreflightCheckRow.Warning("EMS 页面", "系统设置中的 EMS 地址无效"));
            EnvironmentText = $"Node {nodeVersion}；依赖 {nodeDependencies}；浏览器 {cdpStatus.Detail}";
            CollectionBrowserActionText = "打开采集浏览器";
            UpdateEnvironmentReadiness();
            StatusText = IsEnvironmentReady ? "等待任务启动" : "采集准备未完成";
            var passed = PreflightChecks.Count(r => r.State == "通过");
            PreflightSummaryText = IsEnvironmentReady
                ? $"环境已就绪，共 {PreflightChecks.Count} 项检查通过"
                : $"环境检查未通过（{PreflightChecks.Count - passed}/{PreflightChecks.Count} 项异常）";
            PreflightExpanded = !IsEnvironmentReady;
            AddLog(EnvironmentText);
        }
        catch (Exception ex)
        {
            _environmentChecked = true;
            IsEnvironmentReady = false;
            IsCollectionBrowserConnected = false;
            ReadinessTitle = "采集环境检查失败";
            ReadinessDetail = ex.Message;
            ReadinessGlyph = "\uE7BA";
            EnvironmentText = "检查失败：" + ex.Message;
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
        if (IsRunning || IsCheckingEnvironment || !IsEnvironmentReady)
        {
            return false;
        }

        var plan = BuildExecutionPlan(SelectedTaskMode);
        return !plan.RequiresBuildings || Buildings.Any(building => building.IsSelected);
    }

    private void UpdateEnvironmentReadiness()
    {
        var settings = settingsService.Load();
        var plan = BuildExecutionPlan(SelectedTaskMode);
        var missing = new List<string>();

        if (!_environmentChecked)
        {
            IsEnvironmentReady = false;
            ReadinessTitle = "正在检查采集环境";
            ReadinessDetail = "检查完成后才能开始任务";
            ReadinessGlyph = "\uE9D9";
            return;
        }

        if (!_nodeReady) missing.Add("Node 运行时");
        if (!_dependenciesReady) missing.Add("运行依赖");
        if (plan.RunEnumeration && !_enumScriptReady) missing.Add("采集脚本");
        if (plan.RunValidation && !_validationScriptReady) missing.Add("校验脚本");
        if (plan.RunImport && !_importScriptReady) missing.Add("导入脚本");
        if (plan.RunQuality && !_qualityScriptReady) missing.Add("质量检查脚本");
        if (plan.RunRealtimeDetails && !_realtimeScriptReady) missing.Add("实时详情脚本");
        if (plan.RunRealtimeAudit && !_realtimeAuditScriptReady) missing.Add("实时审计脚本");
        if (!plan.RunEnumeration && (plan.RunValidation || plan.RunImport) && !_jsonReady) missing.Add("已有采集结果");
        if (!plan.RunImport && (plan.RunQuality || plan.RunRealtimeDetails || plan.RunRealtimeAudit) && !_databaseReady) missing.Add("当前数据库");
        if ((plan.RunEnumeration || plan.RunRealtimeDetails) && !_emsUrlReady) missing.Add("有效 EMS 地址");

        var usesBrowser = plan.RunEnumeration || plan.RunRealtimeDetails;
        if (usesBrowser)
        {
            if (!_cdpReachable)
            {
                missing.Add("采集浏览器");
            }
            else if (_emsPageCount == 0)
            {
                missing.Add("已打开的 EMS 页面");
            }
        }

        IsEnvironmentReady = missing.Count == 0;
        ReadinessTitle = IsEnvironmentReady
            ? "已就绪，可以开始采集"
            : usesBrowser && !_cdpReachable
                ? "请先打开采集浏览器"
                : usesBrowser && _emsPageCount == 0
                    ? "请先登录 EMS"
                    : "需要完成采集准备";
        ReadinessGlyph = IsEnvironmentReady ? "\uE930" : "\uE7BA";
        ReadinessDetail = IsEnvironmentReady
            ? "采集浏览器已连接；开始采集时会自动检查 EMS 登录状态"
            : usesBrowser && !_cdpReachable
                ? "请先打开采集浏览器并登录 EMS"
                : usesBrowser && _emsPageCount == 0
                    ? "已连接浏览器，请在 EMS 页面完成登录"
                    : "待处理：" + string.Join("、", missing.Distinct());
        StartCommand.NotifyCanExecuteChanged();
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

        _activeTask = new CancellationTokenSource();
        _stopRequested = false;
        _currentDataUpdatedThisRun = false;
        ShowCompletionCelebration = false;
        CollectionCompletionText = string.Empty;
        CollectionCompletedAtText = string.Empty;
        CollectionDurationText = string.Empty;
        _activeStageKey = string.Empty;
        IsRunning = true;
        ClearLogs();
        _lastProgressLocation = string.Empty;
        ReadinessTitle = "正在采集...";
        ReadinessDetail = string.Empty;
        InitializeProgressBuildings(selectedBuildings);
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = "准备采集";
        ProgressLocationText = string.Empty;
        ProgressDeviceText = string.Empty;
        ProgressOverallText = string.Empty;
        ProgressElapsedText = string.Empty;
        ProgressSpeedText = string.Empty;
        ProgressPageText = string.Empty;
        ProgressPageValue = 0;
        EnsureProgressTimer();
        _progressTimer!.Start();
        ResetStages(plan);
        var settings = settingsService.Load();
        var runEnumeration = plan.RunEnumeration;
        var runValidation = plan.RunValidation;
        var runImportAfterCollect = plan.RunImport;
        var runQualityAfterImport = plan.RunQuality;
        var runRealtimeDetailsAfterImport = plan.RunRealtimeDetails;
        var runRealtimeAuditAfterDetails = plan.RunRealtimeAudit;
        const bool enableLogFile = true;
        var logCategory = string.Empty;
        var enableSelfDiagnose = EnableSelfDiagnose;
        var disableNetworkMonitor = DisableNetworkMonitor;
        var realtimeBatchSize = ClampInt(RealtimeBatchSize, 1, 100);
        var realtimeReopenEvery = ClampInt(RealtimeReopenEvery, 0, 50);
        var realtimeTimeoutMs = ClampInt(RealtimeTimeoutMs, 3000, 120000);
        var realtimeMaxDevices = ClampInt(RealtimeMaxDevices, 0, 20000);
        var refreshInventoryBeforeRealtime = RefreshInventoryBeforeRealtime;
        var skipInventoryCheck = SkipInventoryCheck;
        if (runEnumeration)
        {
            var cdpStatus = await CheckEdgeCdpAsync(settings.EdgeCdpPort, settings.EmsUrl).ConfigureAwait(true);
            if (!cdpStatus.IsReachable || cdpStatus.EmsPageCount == 0)
            {
                StatusText = "采集启动已阻止：未发现可采集 EMS 页面";
                AddLog(StatusText);
                AddLog(cdpStatus.LoginDetail);
                _activeTask.Dispose();
                _activeTask = null;
                IsRunning = false;
                StartCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
                CheckEnvironmentCommand.NotifyCanExecuteChanged();
                return;
            }

            AddLog("启动前检查：" + cdpStatus.LoginDetail);
        }

        _activeCollectionBuildings = selectedBuildings;
        var enumProgressCeiling = runRealtimeDetailsAfterImport
            ? 58
            : runImportAfterCollect
                ? runQualityAfterImport ? 85 : 90
            : 100;
        StatusText = plan.RunningStatus;
        _taskStartedAt = DateTimeOffset.Now;
        TaskSummaryText = string.Empty;
        AddLog("任务启动：" + StatusText);
        AddLog($"任务模式：{plan.Label}；楼栋 {ValueOrDash(string.Join("、", selectedBuildings))}");
        AddLog($"本次选项：枚举 {(runEnumeration ? "开启" : "关闭")}；校验 {(runValidation ? "开启" : "关闭")}；导入 SQLite {(runImportAfterCollect ? "开启" : "关闭")}；基础质量检查 {(runQualityAfterImport ? "开启" : "关闭")}；实时详情 {(runRealtimeDetailsAfterImport ? "开启" : "关闭")}；实时审计 {(runRealtimeAuditAfterDetails ? "开启" : "关闭")}；日志文件 {(enableLogFile ? "开启" : "关闭")}");
        if (runEnumeration && (enableSelfDiagnose || disableNetworkMonitor))
        {
            AddLog($"枚举高级参数：日志级别 {SelectedLogSeverity}；自检 {(enableSelfDiagnose ? "开启" : "关闭")}；网络监听 {(disableNetworkMonitor ? "关闭" : "开启")}");
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
                AddLog("进入采集页面，等待页面变化（原因：等待 EMS 页面完成加载并读取首批设备数据）");
                await RunEnumerationAsync(
                    selectedBuildings,
                    settings,
                    enableLogFile,
                    logCategory,
                    enableSelfDiagnose,
                    disableNetworkMonitor,
                    enumProgressCeiling,
                    _activeTask.Token);
                MarkAllProgressBuildingsCompleted();
                SetStageState("collect", "已完成", "所选楼栋采集完成");
                ProgressValue = Math.Max(ProgressValue, enumProgressCeiling);
                ProgressText = runValidation
                    ? "采集完成，准备校验 JSON"
                    : runImportAfterCollect ? "采集完成，准备导入 SQLite" : "采集完成";
            }

            if (runValidation)
            {
                SetStageState("validate", "进行中", "正在检查采集结果完整性");
                ProgressValue = Math.Max(ProgressValue, runImportAfterCollect ? enumProgressCeiling + 2 : 20);
                ProgressText = "正在校验采集 JSON";
                await RunValidationAsync(selectedBuildings, settings, _activeTask.Token);
                SetStageState("validate", "已完成", "采集结果校验通过");
                ProgressValue = Math.Max(ProgressValue, runImportAfterCollect ? enumProgressCeiling + 4 : 100);
                ProgressText = runImportAfterCollect ? "JSON 校验通过，准备导入 SQLite" : "JSON 校验通过";
            }

            if (runImportAfterCollect)
            {
                SetStageState("import", "进行中", "正在更新所选楼栋的当前数据");
                var importProgress = runRealtimeDetailsAfterImport
                    ? 68
                    : runQualityAfterImport ? 88 : 94;
                ProgressValue = Math.Max(ProgressValue, importProgress - 2);
                ProgressText = "正在导入 SQLite";
                await RunImportAsync(selectedBuildings, settings, _activeTask.Token);
                _currentDataUpdatedThisRun = true;
                _databaseReady = true;
                CanOpenDataAfterImport = true;
                SetStageState("import", "已完成", "当前数据已更新");
                ProgressValue = Math.Max(ProgressValue, importProgress);
                ProgressText = runQualityAfterImport || runRealtimeDetailsAfterImport ? "SQLite 已导入" : "采集流程完成";
            }

            if (runImportAfterCollect && runQualityAfterImport)
            {
                SetStageState("quality", "进行中", "正在检查数据质量");
                var qualityProgress = runRealtimeDetailsAfterImport ? 74 : 96;
                ProgressValue = Math.Max(ProgressValue, qualityProgress - 2);
                ProgressText = "正在运行数据质量检查";
                await RunQualityAsync(settings, _activeTask.Token);
                ProgressValue = qualityProgress;
                await RefreshAuditAsync(_activeTask.Token).ConfigureAwait(true);
                SetStageState(
                    "quality",
                    QualityIssues.Count > 0 ? "需复核" : "已完成",
                    QualityIssues.Count > 0 ? $"发现 {QualityIssues.Sum(issue => issue.Count):N0} 项待复核问题" : "质量检查通过");
            }

            if (runRealtimeDetailsAfterImport)
            {
                SetStageState("realtime", "进行中", "正在更新实时详情");
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
                ProgressValue = Math.Max(ProgressValue, 98);
                ProgressText = "正在运行实时点位审计";
                await RunRealtimeAuditAsync(settings, _activeTask.Token);
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
            ProgressText = "采集完成";
            ProgressElapsedText = FormatElapsed(DateTimeOffset.Now - _taskStartedAt);
            ProgressSpeedText = "读取速度：已完成";
            // Ensure progress text is consistent at 100%
            if (TryExtractTotalFromProgress(out var finalTotal) && finalTotal > 0)
            {
                ProgressOverallText = $"总体进度：{finalTotal} / {finalTotal} 台 · 100%";
            }
            var elapsed = DateTimeOffset.Now - _taskStartedAt;
            var elapsedText = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
                : $"{elapsed.Seconds} 秒";
            var completedAt = DateTimeOffset.Now;
            CollectionCompletionText = QualityIssues.Count > 0
                ? "采集完成，数据已更新（有待复核项）"
                : "采集完成，数据已更新";
            CollectionCompletedAtText = $"完成时间：{completedAt:yyyy-MM-dd HH:mm:ss}";
            CollectionDurationText = $"本次用时：{elapsedText}";
            var cardCount = ProgressOverallText.Contains('/') ? ProgressOverallText.Split('·')[0].Trim() : string.Empty;
            TaskSummaryText = QualityIssues.Count > 0
                ? $"采集完成 · {cardCount}· 用时 {elapsedText} · 有待复核问题"
                : $"采集完成 · {cardCount}· 用时 {elapsedText}";
            ShowCompletionCelebration = true;
            AddLog($"采集完成：完成时间 {completedAt:yyyy-MM-dd HH:mm:ss}；本次用时 {elapsedText}");
            StatusText = _currentDataUpdatedThisRun
                ? QualityIssues.Count > 0
                    ? "任务完成，当前数据已更新，存在待复核质量问题"
                    : "任务完成，当前数据已更新"
                : plan.CompletedStatus(runImportAfterCollect, runRealtimeDetailsAfterImport);
            AddLog(StatusText);
        }
        catch (OperationCanceledException)
        {
            IsProgressIndeterminate = false;
            ShowCompletionCelebration = false;
            ProgressText = "已停止";
            ProgressElapsedText = FormatElapsed(DateTimeOffset.Now - _taskStartedAt);
            SetActiveStageTerminalState("已停止", "用户停止了任务");
            var elapsed = DateTimeOffset.Now - _taskStartedAt;
            var elapsedText = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
                : $"{elapsed.Seconds} 秒";
            TaskSummaryText = $"任务已停止 · 用时 {elapsedText}";
            StatusText = _currentDataUpdatedThisRun
                ? "任务已停止；当前数据已经更新，后续检查未完成"
                : "任务已停止；当前数据未更改";
            AddLog(StatusText);
        }
        catch (Exception ex)
        {
            IsProgressIndeterminate = false;
            ShowCompletionCelebration = false;
            ProgressText = "任务失败";
            ProgressElapsedText = FormatElapsed(DateTimeOffset.Now - _taskStartedAt);
            SetActiveStageTerminalState("失败", ex.Message);
            // 进度卡只保留当前状态；完整失败原因统一放到运行记录，避免同一错误重复显示。
            TaskSummaryText = string.Empty;
            RunLogsExpanded = true;
            StatusText = _currentDataUpdatedThisRun
                ? "任务失败；当前数据已经更新，后续步骤未完成"
                : "任务失败；当前数据未更改";
            AddLog($"任务失败：{ex.Message}");
        }
        finally
        {
            _activeTask?.Dispose();
            _activeTask = null;
            _activeCollectionBuildings = [];
            _activeProgressBase = 0;
            _activeProgressSpan = 100;
            _activeProgressLabel = string.Empty;
            _progressTimer?.Stop();
            IsRunning = false;
            ReadinessTitle = IsEnvironmentReady ? "已就绪，可以开始采集" : "采集准备未完成";
            ReadinessDetail = string.Empty;
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

    private bool CanOpenEms()
    {
        return !IsRunning && !IsCheckingEnvironment;
    }

    [RelayCommand(CanExecute = nameof(CanOpenEms))]
    private async Task OpenEmsAsync()
    {
        var settings = settingsService.Load();
        try
        {
            var edgePath = EdgeRuntimeResolver.Resolve();
            var profilePath = Path.Combine(
                pathService.ResolveWorkspacePath(settings.DataDirectory),
                ".edge_cdp_profile");
            Directory.CreateDirectory(profilePath);
            var startInfo = new ProcessStartInfo
            {
                FileName = edgePath,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add($"--remote-debugging-port={settings.EdgeCdpPort}");
            startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
            startInfo.ArgumentList.Add("--user-data-dir=" + profilePath);
            startInfo.ArgumentList.Add(settings.EmsUrl);
            Process.Start(startInfo);
            StatusText = "采集浏览器已打开，请在其中完成 EMS 登录";
            AddLog($"已启动采集专用 Edge，CDP 端口 {settings.EdgeCdpPort}");

            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(800).ConfigureAwait(true);
                var cdpStatus = await CheckEdgeCdpAsync(settings.EdgeCdpPort, settings.EmsUrl).ConfigureAwait(true);
                if (cdpStatus.IsReachable)
                {
                    await CheckEnvironmentAsync().ConfigureAwait(true);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = "无法打开采集浏览器：" + ex.Message;
            AddLog(StatusText);
        }
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
        bool enableSelfDiagnose,
        bool disableNetworkMonitor,
        double progressSpan,
        CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "--edge",
            "--append",
            "--bldg=" + string.Join(",", buildings),
            "--log-level=" + settings.LogLevel,
            "--out-dir=" + pathService.ResolveWorkspacePath(settings.DataDirectory),
            "--ems-url=" + settings.EmsUrl,
            "--cdp-url=http://127.0.0.1:" + settings.EdgeCdpPort,
        };
        if (enableLogFile)
        {
            args.Add("--log-file");
        }

        if (!string.IsNullOrWhiteSpace(logCategory))
        {
            args.Add("--log-category=" + logCategory.Trim());
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
            Path.Combine("src", "enumerate.js"),
            args,
            cancellationToken,
            pathService.BuildDataEnvironment(),
            progressBase: 0,
            progressSpan: progressSpan);
    }

    private Task RunImportAsync(
        IReadOnlyList<string> buildings,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        return RunStepAsync(
            "导入数据库",
            Path.Combine("scripts", "import.js"),
            ["--bldg=" + string.Join(",", buildings)],
            cancellationToken,
            pathService.BuildDataEnvironment());
    }

    private Task RunValidationAsync(
        IReadOnlyList<string> buildings,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var args = new List<string>();
        if (buildings.Count > 0)
        {
            args.Add("--bldg=" + string.Join(",", buildings));
        }

        return RunStepAsync(
            "采集结果校验",
            Path.Combine("scripts", "validate-enum.js"),
            args,
            cancellationToken,
            pathService.BuildDataEnvironment());
    }

    private Task RunQualityAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        return RunStepAsync(
            "数据质量检查",
            Path.Combine("scripts", "quality-report.js"),
            ["--run-id=latest-imported"],
            cancellationToken,
            BuildTaskEnvironment(settings),
            stepKey: "quality");
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
        var args = new List<string>
        {
            "--buildings=" + string.Join(",", buildings),
            "--browser-mode=cdp",
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
        var environment = new Dictionary<string, string>(pathService.BuildDataEnvironment())
        {
            ["EMS_URL"] = settings.EmsUrl,
            ["CDP_URL"] = "http://127.0.0.1:" + settings.EdgeCdpPort,
            ["REALTIME_BROWSER_MODE"] = "cdp",
        };
        return environment;
    }

    private async Task RunStepAsync(
        string label,
        string script,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        double progressBase = 0,
        double progressSpan = 100,
        string stepKey = "default")
    {
        _activeProgressBase = progressBase;
        _activeProgressSpan = progressSpan;
        _activeProgressLabel = label;
        StatusText = "正在执行：" + label;
        ReadinessTitle = "正在采集：" + label;
        _lastStepFailureDetail = string.Empty;
        AddLog("开始 " + label);
        var exitCode = await runner.RunNodeScriptAsync(
            script,
            args,
            AddLog,
            cancellationToken,
            environment).ConfigureAwait(true);

        if (!CollectionStepExitPolicy.IsAccepted(stepKey, exitCode))
        {
            var detail = string.IsNullOrWhiteSpace(_lastStepFailureDetail)
                ? string.Empty
                : "：" + _lastStepFailureDetail;
            throw new InvalidOperationException($"{label} 失败{detail}（退出码 {exitCode}）");
        }

        AddLog(exitCode == 2
            ? label + " 完成，发现待复核质量问题"
            : label + " 完成");
    }

    private void AddLog(string message)
    {
        var normalized = NormalizeLogMessage(message);
        const string qualityGatePrefix = "QUALITY GATE FAIL ";
        var qualityGateIndex = normalized.IndexOf(qualityGatePrefix, StringComparison.OrdinalIgnoreCase);
        if (qualityGateIndex >= 0)
        {
            _lastStepFailureDetail = "质量门槛未通过：" + normalized[(qualityGateIndex + qualityGatePrefix.Length)..];
        }
        _dispatcherQueue.TryEnqueue(() =>
        {
            ApplyProgressEvent(message);
            var displayMessage = FormatOperationalLog(NormalizeLogMessage(message));
            var row = new CollectionTaskLogRow(DateTime.Now.ToString("HH:mm:ss"), displayMessage, ClassifyLogSeverity(displayMessage));
            if (Logs.Any(existing => string.Equals(existing.Message, row.Message, StringComparison.Ordinal)))
            {
                return;
            }
            Logs.Add(row);
            if (MatchesLogFilter(row))
            {
                FilteredLogs.Add(row);
            }
            while (Logs.Count > 300)
            {
                var removed = Logs[0];
                Logs.RemoveAt(0);
                FilteredLogs.Remove(removed);
            }
        });
    }

    private string FormatOperationalLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.StartsWith("任务", StringComparison.Ordinal))
        {
            return message;
        }

        var context = string.IsNullOrWhiteSpace(_lastProgressLocation) ? "当前页面" : _lastProgressLocation;
        var qualityGate = Regex.Match(
            message,
            @"QUALITY GATE FAIL\s+(?<building>\S+)\s+F(?<floor>\S+)\s+(?<subArea>\S+)\s+(?<page>[^:]+):\s*(?<details>.*?)(?:\s+reason=(?<reason>\S+))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        if (qualityGate.Success)
        {
            var building = qualityGate.Groups["building"].Value;
            var subArea = qualityGate.Groups["subArea"].Value;
            var page = qualityGate.Groups["page"].Value.Trim();
            var reason = qualityGate.Groups["reason"].Value.Trim();
            var reasonText = reason switch
            {
                var value when value.StartsWith("unresolved_comm:", StringComparison.OrdinalIgnoreCase) =>
                    $"通信状态未确认（{(value.Length > 16 ? value[16..] : value)} 台）",
                "template_values_unconfirmed" => "页面仍是默认模板值",
                "placeholder_cards" => "存在占位设备名称",
                "duplicate_collapse" => "页面卡片数量异常减少",
                "active_fields_incomplete" => "开机或关机设备字段不完整",
                _ when !string.IsNullOrWhiteSpace(reason) => reason,
                _ => "设备状态数据不完整",
            };
            var pageText = string.Equals(page, "default", StringComparison.OrdinalIgnoreCase)
                ? "默认页"
                : page;
            var locationBuilding = building.EndsWith("号", StringComparison.Ordinal) ? building + "楼" : building;
            var location = string.Join(" · ", new[] { locationBuilding, subArea, pageText }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var details = qualityGate.Groups["details"].Value.Trim()
                .Replace("sw=", "开关=", StringComparison.OrdinalIgnoreCase)
                .Replace("mode=", "模式=", StringComparison.OrdinalIgnoreCase)
                .Replace("tmp=", "室温=", StringComparison.OrdinalIgnoreCase)
                .Replace("set=", "设定温度=", StringComparison.OrdinalIgnoreCase)
                .Replace("fan=", "风速=", StringComparison.OrdinalIgnoreCase)
                .Replace("comm=", "通信=", StringComparison.OrdinalIgnoreCase)
                .Replace("ind=", "指示图=", StringComparison.OrdinalIgnoreCase)
                .Replace("ph=", "占位=", StringComparison.OrdinalIgnoreCase)
                .Replace("active=", "有效设备=", StringComparison.OrdinalIgnoreCase);
            return $"质量检查失败（{location}）；原因：{reasonText}；明细：{details}";
        }

        if (message.Contains("known source indicator defect", StringComparison.OrdinalIgnoreCase))
        {
            var exactContext = ExtractBracketContext(message) ?? context;
            return $"设备状态待复核（{exactContext}；原因：EMS 指示图未显示，其他关键数据已保留）";
        }

        if (message.Contains("PAGE_SWITCH_TIMEOUT", StringComparison.OrdinalIgnoreCase))
        {
            return $"页面切换等待超时（{context}；原因：未检测到页面数据变化）" + ExtractDuration(message);
        }

        if (message.Contains("SVG_DATA_TIMEOUT", StringComparison.OrdinalIgnoreCase))
        {
            return $"页面数据等待超时（{context}；原因：SVG 卡片数据未稳定）" + ExtractDuration(message);
        }

        if (message.Contains("WS_TIMEOUT", StringComparison.OrdinalIgnoreCase))
        {
            return $"页面数据等待超时（{context}；原因：WebSocket 数据未稳定）" + ExtractDuration(message);
        }

        if (message.Contains("SVG_TIMEOUT", StringComparison.OrdinalIgnoreCase))
        {
            return $"页面指示图等待超时（{context}；原因：状态图标未全部加载，已继续采集）" + ExtractDuration(message);
        }

        if (message.Contains("WAIT_CARDS", StringComparison.OrdinalIgnoreCase))
        {
            var detail = message[(message.IndexOf(':') + 1)..].Trim();
            detail = detail.Replace("real", "真实卡片", StringComparison.OrdinalIgnoreCase)
                .Replace("switch images not loaded", "开关状态图未加载", StringComparison.OrdinalIgnoreCase)
                .Replace("comm", "通信状态", StringComparison.OrdinalIgnoreCase)
                .Replace("waiting", "继续等待", StringComparison.OrdinalIgnoreCase)
                .Replace("ms", "毫秒", StringComparison.OrdinalIgnoreCase);
            return $"等待页面卡片加载（{context}；原因：{detail}）";
        }

        if (message.Contains("FIRST PAGE", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) || message.Contains("round", StringComparison.OrdinalIgnoreCase)))
        {
            var exactContext = ExtractBracketContext(message) ?? ExtractFirstPageContext(message) ?? context;
            return $"首屏页面仍在等待数据（{exactContext}；原因：设备数据或页面质量尚未稳定）" + ExtractDuration(message);
        }

        if (message.Contains("adaptive polling", StringComparison.OrdinalIgnoreCase))
        {
            var exactContext = ExtractBracketContext(message) ?? context;
            return $"等待页面数据稳定（{exactContext}；原因：首屏质量未达标，最长等待 45 秒）";
        }

        if (message.Contains("progressive retry", StringComparison.OrdinalIgnoreCase))
        {
            return $"页面数据质量未达标，正在重试（{context}；原因：等待设备状态完整）";
        }

        if (message.Contains("进入页面", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("开始等待", StringComparison.OrdinalIgnoreCase))
        {
            return $"进入页面，等待页面变化（{context}；原因：等待 EMS 页面完成加载）";
        }

        return message;
    }

    private static string ExtractDuration(string message)
    {
        var match = Regex.Match(message, @"(?:after|等待)\s*(\d+)\s*ms", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? $"；已等待 {match.Groups[1].Value} 毫秒" : string.Empty;
    }

    private static string? ExtractFirstPageContext(string message)
    {
        var marker = message.IndexOf("FIRST PAGE ", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        var value = message[(marker + "FIRST PAGE ".Length)..];
        foreach (var endMarker in new[] { " timeout", " round", " accepting", " using" })
        {
            var index = value.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                value = value[..index];
            }
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Replace(" ", " · ", StringComparison.Ordinal);
    }

    private static string? ExtractBracketContext(string message)
    {
        var start = message.IndexOf('[', StringComparison.Ordinal);
        var end = start < 0 ? -1 : message.IndexOf(']', start + 1);
        if (start < 0 || end <= start)
        {
            return null;
        }

        var value = message[(start + 1)..end].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value.Replace(" ", " · ", StringComparison.Ordinal);
    }

    private void EnsureProgressTimer()
    {
        if (_progressTimer is not null)
        {
            return;
        }

        _progressTimer = _dispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromSeconds(1);
        _progressTimer.Tick += (_, _) =>
        {
            if (IsRunning)
            {
                ProgressElapsedText = FormatElapsed(DateTimeOffset.Now - _taskStartedAt);
            }
        };
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var totalSeconds = Math.Max(0, (int)elapsed.TotalSeconds);
        return totalSeconds >= 3600
            ? $"已用时：{totalSeconds / 3600} 小时 {(totalSeconds % 3600) / 60} 分"
            : totalSeconds >= 60
                ? $"已用时：{totalSeconds / 60} 分 {totalSeconds % 60} 秒"
                : $"已用时：{totalSeconds} 秒";
    }

    public void ClearLogs()
    {
        Logs.Clear();
        FilteredLogs.Clear();
    }

    private bool MatchesLogFilter(CollectionTaskLogRow row) =>
        SelectedLogSeverity == "全部" || string.Equals(row.Severity, SelectedLogSeverity, StringComparison.OrdinalIgnoreCase);

    private void RefreshFilteredLogs()
    {
        FilteredLogs.Clear();
        foreach (var row in Logs.Where(MatchesLogFilter))
        {
            FilteredLogs.Add(row);
        }
    }

    private static string ClassifyLogSeverity(string message)
    {
        if (IsWaitTimeoutMessage(message))
        {
            return "WARN";
        }

        if (message.Contains("失败", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("退出码", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("无法", StringComparison.OrdinalIgnoreCase) ||
            (message.Contains("超时", StringComparison.OrdinalIgnoreCase) &&
             !message.Contains("等待", StringComparison.OrdinalIgnoreCase)) ||
            (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) &&
             !message.Contains("waiting", StringComparison.OrdinalIgnoreCase)))
        {
            return "ERROR";
        }

        if (message.Contains("WARN", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("LOW QUALITY", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("待复核", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("等待", StringComparison.OrdinalIgnoreCase))
        {
            return "WARN";
        }

        return "INFO";
    }

    private static bool IsPageWaitMessage(string message)
    {
        var waiting = message.Contains("等待", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("waiting", StringComparison.OrdinalIgnoreCase);
        var pageState = message.Contains("页面", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("数据", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("变化", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("加载", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("就绪", StringComparison.OrdinalIgnoreCase);
        return waiting && pageState;
    }

    private static bool IsWaitTimeoutMessage(string message)
    {
        var waiting = message.Contains("等待", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("waiting", StringComparison.OrdinalIgnoreCase);
        return waiting &&
               (message.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("45秒", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeLogMessage(string message)
    {
        if (message.StartsWith("[PROGRESS]", StringComparison.Ordinal))
        {
            return FormatProgressMessage(message["[PROGRESS]".Length..]);
        }

        return message;
    }

    private static string FormatProgressMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var parts = new List<string>();

            var building = GetString(root, "building", "bldg");
            var seat = GetString(root, "seat", "zuo");
            var buildingIndex = GetInt(root, "buildingIndex", 0);
            var buildingTotal = GetInt(root, "buildingTotal", 0);
            if (string.IsNullOrWhiteSpace(building) && buildingTotal > 0)
            {
                building = $"第{buildingIndex}/{buildingTotal}栋";
            }

            var floorText = GetString(root, "floorText");
            if (string.IsNullOrWhiteSpace(floorText) && root.TryGetProperty("floor", out var floorElem))
            {
                floorText = floorElem.GetInt32() switch
                {
                    -2 => "BM",
                    -1 => "B1F",
                    var f => $"{f}F",
                };
            }
            var pageName = GetString(root, "pageName");
            var currentSubArea = GetInt(root, "curSa", 0);
            var totalSubArea = GetInt(root, "totalSa", 0);

            if (!string.IsNullOrWhiteSpace(building))
            {
                parts.Add(building);
                if (!string.IsNullOrWhiteSpace(seat))
                {
                    parts.Add(seat);
                }
                if (!string.IsNullOrWhiteSpace(floorText))
                {
                    parts.Add(floorText);
                    if (!string.IsNullOrWhiteSpace(pageName) && pageName != "default")
                    {
                        parts.Add(pageName);
                    }
                }
                else if (totalSubArea > 0)
                {
                    parts.Add($"子区 {currentSubArea}/{totalSubArea}");
                }
            }

            if (root.TryGetProperty("deviceDone", out var done) &&
                root.TryGetProperty("deviceTotal", out var deviceTotal) &&
                deviceTotal.GetInt32() > 0)
            {
                parts.Add($"设备 {done.GetInt32()}/{deviceTotal.GetInt32()}");
            }

            var cards = GetInt(root, "cards", 0);
            var accumulated = GetInt(root, "acc", 0);
            if (cards > 0 && accumulated > 0)
            {
                parts.Add($"本页 {cards} 张，累计 {accumulated} 张");
            }

            if (parts.Count > 0)
            {
                return string.Join(" · ", parts);
            }

            if (root.TryGetProperty("message", out var message))
            {
                var text = message.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return "采集进度";
        }
        catch
        {
            return "采集进度 " + json;
        }
    }

    private static string GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return string.Empty;
    }

    private static int GetInt(JsonElement root, string name, int fallback)
    {
        if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetInt32();
        }

        return fallback;
    }

    private void ApplyProgressEvent(string message)
    {
        if (!message.StartsWith("[PROGRESS]", StringComparison.Ordinal))
        {
            if (message.Contains("未检测到 EMS 登录", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("请在采集浏览器中登录", StringComparison.OrdinalIgnoreCase) ||
                     (message.Contains("请在", StringComparison.OrdinalIgnoreCase) &&
                      message.Contains("登录 EMS", StringComparison.OrdinalIgnoreCase)))
            {
                IsProgressIndeterminate = true;
                ProgressText = "等待 EMS 登录";
                StatusText = "请在采集浏览器中完成 EMS 登录";
            }
            else if (IsPageWaitMessage(message) || IsWaitTimeoutMessage(message))
            {
                IsProgressIndeterminate = true;
                ProgressText = "正在等待页面变化...";
                StatusText = "正在等待页面变化";
            }

            return;
        }

        try
        {
            using var document = JsonDocument.Parse(message["[PROGRESS]".Length..]);
            var root = document.RootElement;

            var building = GetString(root, "building", "bldg");
            var seat = GetString(root, "seat", "zuo");
            var buildingIndex = GetInt(root, "buildingIndex", 0);
            var floorText = GetString(root, "floorText");
            if (string.IsNullOrWhiteSpace(floorText) && root.TryGetProperty("floor", out var floorElem) && floorElem.ValueKind == JsonValueKind.Number)
            {
                floorText = floorElem.GetInt32() switch
                {
                    -2 => "BM",
                    -1 => "B1F",
                    var f => $"{f}F",
                };
            }
            var pageName = GetString(root, "pageName");

            var deviceDone = GetInt(root, "deviceDone", 0);
            var deviceTotal = GetInt(root, "deviceTotal", 0);
            var overallDone = GetInt(root, "overallDone", 0);
            var overallTotal = GetInt(root, "overallTotal", 0);
            var elapsedMs = GetInt(root, "elapsedMs", 0);
            var floorIndex = GetInt(root, "floorIndex", GetInt(root, "curSa", 0));
            var floorTotal = GetInt(root, "floorTotal", GetInt(root, "totalSa", 0));
            var stage = GetString(root, "stage");

            UpdateProgressBuildings(building, buildingIndex, stage);

            if (floorTotal > 0 && floorIndex > 0)
            {
                var pageRatio = deviceTotal > 0
                    ? Math.Clamp(deviceDone / (double)deviceTotal, 0, 1)
                    : floorIndex / (double)floorTotal;
                ProgressPageValue = Math.Clamp(pageRatio * 100, 0, 100);
                ProgressPageText = $"当前页面：{(string.IsNullOrWhiteSpace(floorText) ? "读取中" : floorText)} · {floorIndex}/{floorTotal}";
            }

            if (elapsedMs > 0)
            {
                ProgressElapsedText = FormatElapsed(TimeSpan.FromMilliseconds(elapsedMs));
                var speedCount = overallDone > 0 ? overallDone : deviceDone;
                ProgressSpeedText = speedCount > 0
                    ? $"读取速度：{speedCount / (elapsedMs / 60000d):0.#} 台/分钟"
                    : "读取速度：正在计算";
            }

            var locationParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(building))
            {
                locationParts.Add(building);
            }
            if (!string.IsNullOrWhiteSpace(seat))
            {
                locationParts.Add(seat);
            }
            if (!string.IsNullOrWhiteSpace(floorText))
            {
                locationParts.Add(floorText);
                if (!string.IsNullOrWhiteSpace(pageName) && pageName != "default")
                {
                    locationParts.Add(pageName);
                }
            }
            ProgressLocationText = locationParts.Count > 0 ? string.Join(" · ", locationParts) : string.Empty;
            if (!string.IsNullOrWhiteSpace(ProgressLocationText))
            {
                _lastProgressLocation = ProgressLocationText;
            }

            if (deviceTotal > 0)
            {
                ProgressDeviceText = $"当前楼栋：{deviceDone} / {deviceTotal} 台";
            }
            else
            {
                ProgressDeviceText = string.Empty;
            }

            if (overallTotal > 0)
            {
                var overallPercent = Math.Clamp(overallDone / (double)overallTotal, 0, 1);
                ProgressOverallText = $"总体进度：{overallDone} / {overallTotal} 台 · {overallPercent * 100:0}%";
            }

            var percentValue = GetInt(root, "percent", 0);
            if (overallTotal > 0 && overallDone > 0)
            {
                percentValue = (int)Math.Round(overallDone / (double)overallTotal * 100);
            }

            if (root.TryGetProperty("percent", out var percentProperty) ||
                percentValue > 0 || IsEnumeratorProgress(root))
            {
                var rawPercent = root.TryGetProperty("percent", out var p) && p.TryGetDouble(out var d)
                    ? d
                    : (overallTotal > 0 ? overallDone / (double)overallTotal * 100 : CombineEnumeratorPercent(root, building));
                var percent = Math.Clamp((double)rawPercent, 0, 100);
                var value = _activeProgressBase + percent / 100d * _activeProgressSpan;
                ProgressValue = Math.Clamp(value, 0, 100);
                var progressMessage = root.TryGetProperty("message", out var msg) ? msg.GetString() : string.Empty;
                ProgressText = string.IsNullOrWhiteSpace(progressMessage)
                    ? (string.IsNullOrWhiteSpace(building) ? "正在采集" : $"正在采集 {building}")
                    : progressMessage;
                IsProgressIndeterminate = false;
                return;
            }

            var current = GetInt(root, "curSa", 0);
            var total = GetInt(root, "totalSa", 0);
            if (total > 0)
            {
                var collectionBuildingIndex = FindActiveBuildingIndex(building);
                var buildingCount = Math.Max(_activeCollectionBuildings.Count, 1);
                var currentRatio = Math.Clamp(current / (double)total, 0, 1);
                var collectionRatio = Math.Clamp((collectionBuildingIndex + currentRatio) / buildingCount, 0, 1);
                var enumeratorPercent = _activeProgressBase + collectionRatio * _activeProgressSpan;
                ProgressValue = enumeratorPercent;
                if (string.IsNullOrWhiteSpace(ProgressText) || ProgressText == "准备采集" || ProgressText == "正在采集")
                {
                    ProgressText = string.IsNullOrWhiteSpace(building) ? "正在采集" : $"正在采集 {building}";
                }
                IsProgressIndeterminate = false;
            }
        }
        catch
        {
            IsProgressIndeterminate = true;
        }
    }

    private static bool IsEnumeratorProgress(JsonElement root)
    {
        return root.TryGetProperty("t", out var t) && t.GetString() == "c";
    }

    private static double CombineEnumeratorPercent(JsonElement root, string building)
    {
        var current = GetInt(root, "curSa", 0);
        var total = GetInt(root, "totalSa", 0);
        if (total <= 0)
        {
            return 0;
        }

        return Math.Clamp(current / (double)total * 100, 0, 100);
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

    private static async Task<string> ReadNodeVersionAsync()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = NodeRuntimeResolver.Resolve(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            startInfo.ArgumentList.Add("--version");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "不可用";
            }

            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0 ? output.Trim() : "不可用";
        }
        catch
        {
            return "不可用";
        }
    }

    private static async Task<string> CheckNodeDependenciesAsync(string workspaceRoot)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = NodeRuntimeResolver.Resolve(),
                WorkingDirectory = workspaceRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add("require('better-sqlite3'); require('playwright'); console.log('ok')");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "不可用";
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return "检查超时";
            }

            if (process.ExitCode == 0)
            {
                return "可用";
            }

            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(error) ? "加载失败" : error.Trim().Split(Environment.NewLine)[0];
        }
        catch
        {
            return "不可用";
        }
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

    private bool TryExtractTotalFromProgress(out int total)
    {
        total = 0;
        if (string.IsNullOrWhiteSpace(ProgressOverallText)) return false;
        // Format: "总体进度：1495 / 1493 台 · 100%"
        var match = System.Text.RegularExpressions.Regex.Match(ProgressOverallText, @"/\s*(\d+)\s*台");
        if (match.Success && int.TryParse(match.Groups[1].Value, out total))
        {
            return true;
        }
        return false;
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

    private static async Task<EdgeCdpCheckResult> CheckEdgeCdpAsync(int port, string emsUrl)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/json/version").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new EdgeCdpCheckResult(false, 0, $"{port} 未就绪", "CDP 未就绪，无法核实 EMS 页面");
            }

            try
            {
                using var pagesResponse = await client.GetAsync($"http://127.0.0.1:{port}/json/list").ConfigureAwait(false);
                if (!pagesResponse.IsSuccessStatusCode)
                {
                    return new EdgeCdpCheckResult(true, 0, $"{port} 可访问；页面列表读取失败", "只能证明 CDP 可达，不能证明 EMS 已登录");
                }

                await using var stream = await pagesResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
                var pages = document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement.EnumerateArray().ToList()
                    : [];
                var emsPages = pages
                    .Select(ReadCdpPage)
                    .Where(page => IsLikelyEmsPage(page.Url, emsUrl))
                    .ToList();
                if (emsPages.Count == 0)
                {
                    return new EdgeCdpCheckResult(true, 0, $"{port} 可访问；未发现 EMS 标签页", "未发现 EMS 页面；请先在 Edge 中打开并登录 EMS");
                }

                var first = emsPages[0];
                return new EdgeCdpCheckResult(
                    true,
                    emsPages.Count,
                    $"{port} 可访问；发现 {emsPages.Count} 个 EMS 标签页",
                    $"发现 EMS 页面：{ValueOrDash(first.Title)}。开始采集时会自动检查登录状态");
            }
            catch
            {
                return new EdgeCdpCheckResult(true, 0, $"{port} 可访问；页面列表读取失败", "只能证明 CDP 可达，不能证明 EMS 已登录");
            }
        }
        catch
        {
            return new EdgeCdpCheckResult(false, 0, $"{port} 未就绪", "CDP 未就绪，无法核实 EMS 页面");
        }
    }

    private static CdpPageInfo ReadCdpPage(JsonElement element)
    {
        return new CdpPageInfo(
            Url: element.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
            Title: element.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty);
    }

    private static bool IsLikelyEmsPage(string pageUrl, string emsUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl))
        {
            return false;
        }

        try
        {
            var expected = new Uri(emsUrl);
            var current = new Uri(pageUrl);
            return string.Equals(current.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
                   (current.AbsolutePath.Contains("/ui", StringComparison.OrdinalIgnoreCase) ||
                    expected.AbsolutePath.Contains(current.AbsolutePath, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return pageUrl.Contains("172.29.248.4", StringComparison.OrdinalIgnoreCase) ||
                   pageUrl.Contains("/ui", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private sealed record EdgeCdpCheckResult(
        bool IsReachable,
        int EmsPageCount,
        string Detail,
        string LoginDetail);

    private sealed record CdpPageInfo(string Url, string Title);
}

public sealed record ReconciliationFilterOption(string Value, string Label);

public sealed class ReconciliationTypeCountRow(string type, int count)
{
    public string Type { get; } = type;

    public string Label { get; } = ReconciliationLabels.TypeLabel(type);

    public string CountText { get; } = count.ToString("N0");
}

public sealed class ReconciliationItemRow
{
    public ReconciliationItemRow(RealtimeReconciliationItem item)
    {
        Source = item;
        Type = item.Type;
        TypeLabel = ReconciliationLabels.TypeLabel(item.Type);
        Severity = item.Severity;
        Building = item.Building;
        FloorLabel = string.IsNullOrWhiteSpace(item.FloorLabel) ? "--" : item.FloorLabel;
        Name = string.IsNullOrWhiteSpace(item.Name) ? "--" : item.Name;
        Location = $"DB {ValueOrDash(item.DbLocation)} / RT {ValueOrDash(item.RealtimeLocation)}";
        DevId = string.IsNullOrWhiteSpace(item.DevId) ? "--" : item.DevId;
        ConfidenceText = item.Confidence.ToString("P0");
        Reason = item.Reason;
        RuleDescription = item.RuleDescription;
        EvidenceSummary = item.EvidenceSummary;
        DecisionPathText = string.Join(Environment.NewLine, item.DecisionPath.Select(step => "- " + step));
        NavigationTarget = DeviceNavigationTargetFactory.FromReconciliationItem(item);
    }

    public RealtimeReconciliationItem Source { get; }

    public string Type { get; }

    public string TypeLabel { get; }

    public string Severity { get; }

    public string Building { get; }

    public string FloorLabel { get; }

    public string Name { get; }

    public string Location { get; }

    public string DevId { get; }

    public string ConfidenceText { get; }

    public string Reason { get; }

    public string RuleDescription { get; }

    public string EvidenceSummary { get; }

    public string DecisionPathText { get; }

    public DeviceNavigationTarget NavigationTarget { get; }

    private static string ValueOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "--" : value;
}

public static class ReconciliationLabels
{
    public static string TypeLabel(string type)
    {
        return type switch
        {
            RealtimeReconciliationTypes.NewDevice => "新增实时",
            RealtimeReconciliationTypes.MissingInRealtime => "缺实时",
            RealtimeReconciliationTypes.MatchFailed => "匹配失败",
            RealtimeReconciliationTypes.DuplicateRender => "重复渲染",
            RealtimeReconciliationTypes.VirtualOverride => "虚拟纳管",
            RealtimeReconciliationTypes.DataNoise => "数据噪声",
            _ => type,
        };
    }
}
