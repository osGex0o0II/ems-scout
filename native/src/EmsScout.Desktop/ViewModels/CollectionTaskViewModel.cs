using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
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
    private bool _currentDataUpdatedThisRun;
    private bool _buildingEventsAttached;
    private bool _environmentChecked;
    private bool _nodeReady;
    private bool _dependenciesReady;
    private bool _enumScriptReady;
    private bool _validationScriptReady;
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
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartTask))]
    public partial bool IsEnvironmentReady { get; private set; }

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
    public partial string LogCategory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RecaptureText { get; set; } = string.Empty;

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
        new("1号", "1号 科研综合楼", true),
        new("2号", "2号", true),
        new("3号", "3号", true),
        new("4号", "4号", true),
        new("5号", "5号", true),
        new("6号", "6号", true),
    ];

    public ObservableCollection<CollectionTaskModeOption> TaskModes { get; } =
        new(CollectionTaskModeCatalog.Options.Where(option => option.Value != CollectionTaskModeValues.Custom));

    public ObservableCollection<CollectionTaskLogRow> Logs { get; } = [];

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

    public string StartButtonText => SelectedTaskMode?.StartButtonText ?? "开始任务";

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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        LoadSettingsDefaults();
        SelectedTaskMode ??= TaskModes.FirstOrDefault(
            mode => mode.Value == CollectionTaskModeValues.CollectImport);
        ApplyTaskModePreset(SelectedTaskMode);
        AttachBuildingEvents();
        UpdateSelectedBuildingsText();
        ResetStages(BuildExecutionPlan(SelectedTaskMode));

        var settings = settingsService.Load();
        var dataDirectory = pathService.ResolveWorkspacePath(settings.DataDirectory);
        CanOpenDataAfterImport = File.Exists(Path.Combine(dataDirectory, "ac.db"));
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
        SelectedBuildingsText = selected.Count == 0
            ? "尚未选择楼栋"
            : $"已选择 {selected.Count} 栋：{string.Join("、", selected)}";
        CurrentDataImpactText = selected.Count == 0
            ? "选择至少一栋楼后才能开始"
            : $"成功后只更新 {string.Join("、", selected)}，其他楼栋保持不变";
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
                : settings.DefaultCollectionMode.Equals("auto-launch", StringComparison.OrdinalIgnoreCase)
                    ? PreflightCheckRow.Ok("采集浏览器", "开始任务时自动打开 Edge")
                    : PreflightCheckRow.Warning("采集浏览器", "尚未启动，请点击“打开采集浏览器”"));
            PreflightChecks.Add(_emsUrlReady
                ? cdpStatus.EmsPageCount > 0
                    ? PreflightCheckRow.Ok("EMS 页面", cdpStatus.LoginDetail)
                    : PreflightCheckRow.Unknown("EMS 页面", cdpStatus.LoginDetail)
                : PreflightCheckRow.Warning("EMS 页面", "系统设置中的 EMS 地址无效"));
            EnvironmentText = $"Node {nodeVersion}；依赖 {nodeDependencies}；浏览器 {cdpStatus.Detail}";
            CollectionBrowserActionText = settings.DefaultCollectionMode.Equals("auto-launch", StringComparison.OrdinalIgnoreCase)
                ? "采集时自动打开 EMS"
                : "打开采集浏览器";
            UpdateEnvironmentReadiness();
            StatusText = IsEnvironmentReady ? "等待任务启动" : "采集准备未完成";
            AddLog(EnvironmentText);
        }
        catch (Exception ex)
        {
            _environmentChecked = true;
            IsEnvironmentReady = false;
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
        var autoLaunch = settings.DefaultCollectionMode.Equals("auto-launch", StringComparison.OrdinalIgnoreCase);
        if (usesBrowser && !autoLaunch)
        {
            if (!_cdpReachable)
            {
                missing.Add("采集浏览器");
            }
            else if (settings.CheckLoginBeforeCollection && _emsPageCount == 0)
            {
                missing.Add("已打开的 EMS 页面");
            }
        }

        IsEnvironmentReady = missing.Count == 0;
        ReadinessTitle = IsEnvironmentReady ? "可以开始任务" : "需要完成采集准备";
        ReadinessGlyph = IsEnvironmentReady ? "\uE930" : "\uE7BA";
        ReadinessDetail = IsEnvironmentReady
            ? autoLaunch && usesBrowser
                ? "开始后将自动打开 Edge，请在浏览器中完成 EMS 登录"
                : "采集浏览器和本地运行环境均已就绪"
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
        _activeStageKey = string.Empty;
        IsRunning = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = "准备采集";
        ResetStages(plan);
        var settings = settingsService.Load();
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
        if (runEnumeration &&
            settings.CheckLoginBeforeCollection &&
            settings.DefaultCollectionMode.Equals("edge-cdp", StringComparison.OrdinalIgnoreCase))
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
                ProgressText = runQualityAfterImport || runRealtimeDetailsAfterImport ? "SQLite 已导入" : "100%";
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
            ProgressText = "100%";
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
            ProgressText = "已停止";
            SetActiveStageTerminalState("已停止", "用户停止了任务");
            StatusText = _currentDataUpdatedThisRun
                ? "任务已停止；当前数据已经更新，后续检查未完成"
                : "任务已停止；当前数据未更改";
            AddLog(StatusText);
        }
        catch (Exception ex)
        {
            IsProgressIndeterminate = false;
            ProgressText = "任务失败";
            SetActiveStageTerminalState("失败", ex.Message);
            StatusText = _currentDataUpdatedThisRun
                ? "任务失败；当前数据已经更新，后续步骤未完成：" + ex.Message
                : "任务失败；当前数据未更改：" + ex.Message;
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

    private bool CanOpenEms()
    {
        var settings = settingsService.Load();
        return !IsRunning && !IsCheckingEnvironment &&
               settings.DefaultCollectionMode.Equals("edge-cdp", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand(CanExecute = nameof(CanOpenEms))]
    private async Task OpenEmsAsync()
    {
        var settings = settingsService.Load();
        try
        {
            var edgePath = ResolveEdgePath();
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
        var mode = settings.DefaultCollectionMode.Equals("auto-launch", StringComparison.OrdinalIgnoreCase)
            ? "--auto-launch"
            : "--edge";
        var args = new List<string>
        {
            mode,
            "--append",
            "--bldg=" + string.Join(",", buildings),
            "--log-level=" + settings.LogLevel,
            "--out-dir=" + pathService.ResolveWorkspacePath(settings.DataDirectory),
            "--ems-url=" + settings.EmsUrl,
            "--cdp-url=http://127.0.0.1:" + settings.EdgeCdpPort,
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
            [],
            cancellationToken,
            BuildTaskEnvironment(settings));
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
        var browserMode = settings.DefaultCollectionMode.Equals("edge-cdp", StringComparison.OrdinalIgnoreCase)
            ? "cdp"
            : "persistent";
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
        var environment = new Dictionary<string, string>(pathService.BuildDataEnvironment())
        {
            ["EMS_URL"] = settings.EmsUrl,
            ["CDP_URL"] = "http://127.0.0.1:" + settings.EdgeCdpPort,
            ["REALTIME_BROWSER_MODE"] = settings.DefaultCollectionMode.Equals("edge-cdp", StringComparison.OrdinalIgnoreCase)
                ? "cdp"
                : "persistent",
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
        double progressSpan = 100)
    {
        _activeProgressBase = progressBase;
        _activeProgressSpan = progressSpan;
        _activeProgressLabel = label;
        StatusText = "正在执行：" + label;
        AddLog("开始 " + label);
        var exitCode = await runner.RunNodeScriptAsync(
            script,
            args,
            AddLog,
            cancellationToken,
            environment).ConfigureAwait(true);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"{label} 失败，退出码 {exitCode}");
        }

        AddLog(label + " 完成");
    }

    private void AddLog(string message)
    {
        var normalized = NormalizeLogMessage(message);
        var row = new CollectionTaskLogRow(DateTime.Now.ToString("HH:mm:ss"), normalized);
        _dispatcherQueue.TryEnqueue(() =>
        {
            ApplyProgressEvent(message);
            Logs.Add(row);
            while (Logs.Count > 300)
            {
                Logs.RemoveAt(0);
            }
        });
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
            if (root.TryGetProperty("message", out var message))
            {
                var text = message.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            if (root.TryGetProperty("deviceDone", out var done) &&
                root.TryGetProperty("deviceTotal", out var deviceTotal) &&
                deviceTotal.GetInt32() > 0)
            {
                var buildingName = root.TryGetProperty("building", out var realtimeBuilding)
                    ? realtimeBuilding.GetString()
                    : string.Empty;
                return $"实时详情：{buildingName} 设备 {done.GetInt32()}/{deviceTotal.GetInt32()}";
            }

            var building = root.TryGetProperty("bldg", out var bldg) ? bldg.GetString() : string.Empty;
            var current = root.TryGetProperty("curSa", out var curSa) ? curSa.GetInt32() : 0;
            var total = root.TryGetProperty("totalSa", out var totalSa) ? totalSa.GetInt32() : 0;
            var cards = root.TryGetProperty("cards", out var cardCount) ? cardCount.GetInt32() : 0;
            var accumulated = root.TryGetProperty("acc", out var acc) ? acc.GetInt32() : 0;
            return $"采集进度：{building} 子区 {current}/{total}，本页 {cards} 张，累计 {accumulated} 张";
        }
        catch
        {
            return "采集进度 " + json;
        }
    }

    private void ApplyProgressEvent(string message)
    {
        if (!message.StartsWith("[PROGRESS]", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(message["[PROGRESS]".Length..]);
            var root = document.RootElement;
            if (root.TryGetProperty("percent", out var percentProperty) &&
                percentProperty.TryGetDouble(out var rawPercent))
            {
                var percent = Math.Clamp(rawPercent, 0, 100);
                var value = _activeProgressBase + percent / 100d * _activeProgressSpan;
                ProgressValue = Math.Clamp(value, 0, 100);
                var progressMessage = root.TryGetProperty("message", out var msg) ? msg.GetString() : string.Empty;
                ProgressText = string.IsNullOrWhiteSpace(progressMessage)
                    ? $"{ProgressValue:0}% · {_activeProgressLabel}"
                    : $"{ProgressValue:0}% · {progressMessage}";
                IsProgressIndeterminate = false;
                return;
            }

            var building = root.TryGetProperty("bldg", out var bldg) ? bldg.GetString() ?? string.Empty : string.Empty;
            var current = root.TryGetProperty("curSa", out var curSa) ? curSa.GetInt32() : 0;
            var total = root.TryGetProperty("totalSa", out var totalSa) ? totalSa.GetInt32() : 0;
            if (total <= 0)
            {
                return;
            }

            var buildingIndex = FindActiveBuildingIndex(building);
            var buildingCount = Math.Max(_activeCollectionBuildings.Count, 1);
            var currentRatio = Math.Clamp(current / (double)total, 0, 1);
            var collectionRatio = Math.Clamp((buildingIndex + currentRatio) / buildingCount, 0, 1);
            var enumeratorPercent = _activeProgressBase + collectionRatio * _activeProgressSpan;
            ProgressValue = enumeratorPercent;
            ProgressText = $"{enumeratorPercent:0}% · {building} 子区 {current}/{total}";
            IsProgressIndeterminate = false;
        }
        catch
        {
            IsProgressIndeterminate = true;
        }
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
                FileName = "node",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
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
                FileName = "node",
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
                    $"发现 EMS 页面：{ValueOrDash(first.Title)}。预检不能证明已登录，采集/验证会二次检查登录态");
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
