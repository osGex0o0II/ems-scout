using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Collection;
using EmsScout.Application.Devices;
using EmsScout.Application.Logging;
using EmsScout.Application.Groups;
using EmsScout.Application.Quality;
using EmsScout.Application.Settings;
using EmsScout.Application.Workflows;
using EmsScout.Desktop.Services;
using EmsScout.Infrastructure.Logging;
using EmsScout.Infrastructure.Sidecar;
using Microsoft.UI.Dispatching;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class AuditViewModel(
    INativeQualityAuditService qualityAuditService,
    IRealtimeQualityAuditService realtimeQualityAuditService,
    IRealtimeReconciliationService realtimeReconciliationService,
    ICollectionRunRepository collectionRunRepository,
    INavigationService navigationService,
    NodeCollectionTaskRunner runner,
    AppDataPathService pathService,
    IApplicationLogger applicationLogger,
    DataContextService dataContext,
    IAreaGroupRepository areaGroupRepository) : ObservableObject
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private bool _contextAttached;

    public DataContextService DataContext { get; } = dataContext;

    public IAreaGroupRepository AreaGroupRepository { get; } = areaGroupRepository;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunQualityAuditCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunRealtimeAuditCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyReconciliationFilterCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenReconciliationItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkRunAnomalyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearRunAnomalyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreRunCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRunCommand))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedRun))]
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedRun))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "等待审计数据";

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
    [NotifyPropertyChangedFor(nameof(CanRestoreSelectedRun))]
    public partial CollectionRunRow? SelectedRun { get; set; }

    public ObservableCollection<DataFacetItem> Facets { get; } = [];

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

    public ObservableCollection<ScheduleAuditRow> ScheduleAuditRows { get; } = [];

    public string ScheduleAuditSummaryText { get; private set; } = "尚未读取计划状态审计";


    public bool CanDeleteSelectedRun => CanDeleteRun();

    public bool CanRestoreSelectedRun => CanRestoreRun();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        AttachDataContext();
        if (DataContext.Options.Count == 0)
        {
            await DataContext.RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
        SelectedReconciliationBuilding ??= ReconciliationBuildingOptions.FirstOrDefault();
        SelectedReconciliationType ??= ReconciliationTypeOptions.FirstOrDefault();
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusText = "正在刷新审计中心";
        try
        {
            if (DataContext.Options.Count == 0)
            {
                await DataContext.RefreshAsync(cancellationToken).ConfigureAwait(true);
            }
            await RefreshQualityAsync(cancellationToken).ConfigureAwait(true);
            await RefreshRealtimeQualityAsync(cancellationToken).ConfigureAwait(true);
            await RefreshReconciliationAsync(cancellationToken).ConfigureAwait(true);
            await RefreshRunsAsync(cancellationToken).ConfigureAwait(true);
            await RefreshScheduleAuditAsync(cancellationToken).ConfigureAwait(true);
            RefreshFacets();
            StatusText = "审计中心已刷新";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RunQualityAuditAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusText = "正在运行基础质量审计";
        try
        {
            var request = DataContext.RunId is { } runId
                ? NativeQualityAuditRequest.ForRun(runId)
                : NativeQualityAuditRequest.LatestCompletedRun;
            var report = await qualityAuditService
                .AuditAsync(request, cancellationToken)
                .ConfigureAwait(true);
            ApplyQualityReport(report);
            RefreshFacets();
            StatusText = report is null
                ? "没有可审计的已完成采集批次"
                : report.Summary.IssueCount > 0
                    ? "基础质量审计已完成，存在待复核项"
                    : "基础质量审计已完成";
        }
        catch (Exception ex)
        {
            ApplyQualityError(ex);
            StatusText = "基础质量审计运行失败：" + applicationLogger.WriteFailure(ex, "audit").DisplayText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RunRealtimeAuditAsync(CancellationToken cancellationToken = default)
    {
        await RunAuditScriptAsync(
            "实时点位审计",
            Path.Combine("scripts", "audit-realtime-data.js"),
            [],
            cancellationToken).ConfigureAwait(true);
    }

    private async Task RunAuditScriptAsync(
        string label,
        string script,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        StatusText = $"正在运行{label}";
        try
        {
            var result = await runner.RunWorkflowScriptAsync(
                script,
                arguments,
                line => _dispatcherQueue.TryEnqueue(() =>
                {
                    StatusText = line.StartsWith("[stderr] ", StringComparison.Ordinal)
                        ? $"{label}：{line}"
                        : $"正在运行{label}：{line}";
                }),
                _ => { },
                cancellationToken,
                pathService.BuildDataEnvironment()).ConfigureAwait(true);

            if (!result.IsSuccessful)
            {
                StatusText = $"{label}未完成：{result.Outcome}，退出码 {result.ExitCode}";
                return;
            }

            StatusText = result.Outcome == WorkflowTerminalOutcome.SucceededWithFindings
                ? $"{label}已完成并发现需复核项，正在刷新结果"
                : $"{label}已完成，正在刷新结果";
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"{label}运行失败：" + applicationLogger.WriteFailure(ex, "audit").DisplayText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task ApplyReconciliationFilter()
    {
        IsBusy = true;
        StatusText = "正在筛选实时对账";
        try
        {
            await RefreshReconciliationAsync().ConfigureAwait(true);
            RefreshFacets();
            StatusText = "实时对账筛选已更新";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshQualityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var request = DataContext.RunId is { } runId
                ? NativeQualityAuditRequest.ForRun(runId)
                : NativeQualityAuditRequest.LatestCompletedRun;
            var report = await qualityAuditService.AuditAsync(request, cancellationToken).ConfigureAwait(true);
            ApplyQualityReport(report);
        }
        catch (Exception ex)
        {
            ApplyQualityError(ex);
        }
    }

    private async Task RefreshScheduleAuditAsync(CancellationToken cancellationToken)
    {
        try
        {
            var records = await AreaGroupRepository
                .EvaluateSchedulesAsync(DataContext.RunId, DateTimeOffset.Now, cancellationToken)
                .ConfigureAwait(true);
            ScheduleAuditRows.Clear();
            foreach (var record in records)
            {
                ScheduleAuditRows.Add(new ScheduleAuditRow(record));
            }

            var issues = records.Count(record => record.ResultCode == "not_enabled");
            var unexpected = records.Count(record => record.ResultCode == "unexpected_running");
            var observedAt = records.FirstOrDefault()?.ObservedAt;
            ScheduleAuditSummaryText = records.Count == 0
                ? "所选数据时间没有可执行的计划规则"
                : $"数据时间 {observedAt}，已检查 {records.Count:N0} 台设备；未按计划启用 {issues:N0} 台，计划外运行 {unexpected:N0} 台";
            OnPropertyChanged(nameof(ScheduleAuditSummaryText));
        }
        catch (Exception ex)
        {
            ScheduleAuditRows.Clear();
            ScheduleAuditSummaryText = "计划状态审计失败：" + applicationLogger.WriteFailure(ex, "schedule-audit").DisplayText;
            OnPropertyChanged(nameof(ScheduleAuditSummaryText));
        }
    }

    public async Task SelectDataContextAsync(DataContextOption? option, CancellationToken cancellationToken = default)
    {
        DataContext.Select(option);
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    private void AttachDataContext()
    {
        if (_contextAttached)
        {
            return;
        }

        DataContext.ContextChanged += async (_, _) =>
        {
            if (!IsBusy)
            {
                await RefreshAsync().ConfigureAwait(true);
            }
        };
        _contextAttached = true;
    }

    private void ApplyQualityReport(QualityAuditReport? report)
    {
        QualityIssues.Clear();
        if (report is null)
        {
            QualityStatusText = "没有可审计的已完成采集批次";
            QualitySummaryText = "导入采集快照后显示结果";
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

    private void ApplyQualityError(Exception exception)
    {
        QualityIssues.Clear();
        QualityStatusText = "质量审计读取失败";
        QualitySummaryText = exception.Message;
        QualityGeneratedText = "--";
    }

    private async Task RefreshRealtimeQualityAsync(CancellationToken cancellationToken)
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
            RealtimeQualitySummaryText = applicationLogger.WriteFailure(ex, "audit").DisplayText;
            RealtimeQualityGeneratedText = "--";
        }
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
                    Limit: 120),
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
            ReconciliationSummaryText = applicationLogger.WriteFailure(ex, "audit").DisplayText;
            ReconciliationGeneratedText = "--";
        }
    }

    private async Task RefreshRunsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var runs = await collectionRunRepository.ListAsync(80, cancellationToken).ConfigureAwait(true);
            var selectedId = SelectedRun?.Id;
            Runs.Clear();
            foreach (var run in runs)
            {
                Runs.Add(new CollectionRunRow(run));
            }

            SelectedRun = selectedId.HasValue
                ? Runs.FirstOrDefault(run => run.Id == selectedId.Value)
                : null;
            RunsStatusText = Runs.Count == 0
                ? "暂无历史批次"
                : $"已读取 {Runs.Count:N0} 个历史批次";
        }
        catch (Exception ex)
        {
            Runs.Clear();
            SelectedRun = null;
            RunsStatusText = "历史批次读取失败：" + applicationLogger.WriteFailure(ex, "audit").DisplayText;
        }
    }

    private bool CanOpenReconciliationItem() => SelectedReconciliationItem is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpenReconciliationItem))]
    private void OpenReconciliationItem()
    {
        if (SelectedReconciliationItem is null)
        {
            return;
        }

        navigationService.NavigateToData(DataNavigationRequest.From(SelectedReconciliationItem.NavigationTarget));
    }

    private bool CanMarkRunAnomaly() => SelectedRun is { IsAnomaly: false } && !IsBusy;

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
        StatusText = $"已标记异常批次 #{runId}";
        await RefreshRunsAsync().ConfigureAwait(true);
        RefreshFacets();
    }

    private bool CanClearRunAnomaly() => SelectedRun is { IsAnomaly: true } && !IsBusy;

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
        StatusText = $"已取消异常标记 #{runId}";
        await RefreshRunsAsync().ConfigureAwait(true);
        RefreshFacets();
    }

    private bool CanRestoreRun() => SelectedRun is { IsAnomaly: false } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRestoreRun))]
    public async Task RestoreRunAsync()
    {
        if (SelectedRun is null)
        {
            return;
        }

        IsBusy = true;
        var runId = SelectedRun.Id;
        try
        {
            StatusText = $"正在恢复历史批次 #{runId}";
            var result = await collectionRunRepository.RestoreCurrentAsync(runId).ConfigureAwait(true);
            var scopeText = result.IsPartial
                ? $"已仅替换 {string.Join("、", result.Buildings)}，其他楼栋保持不变"
                : "已替换全部当前数据";
            var backupText = result.BackupRunId.HasValue
                ? $"；原数据已备份为批次 #{result.BackupRunId.Value}"
                : string.Empty;
            StatusText = $"已恢复批次 #{result.RunId}：{result.RestoredCards:N0} 张卡片；{scopeText}{backupText}";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = "历史批次恢复失败：" + applicationLogger.WriteFailure(ex, "audit").DisplayText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public bool CanDeleteRun() => SelectedRun is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDeleteRun))]
    public async Task DeleteRunAsync()
    {
        if (SelectedRun is null)
        {
            return;
        }

        IsBusy = true;
        var runId = SelectedRun.Id;
        try
        {
            StatusText = $"正在删除历史批次 #{runId}";
            var result = await collectionRunRepository.DeleteAsync(runId).ConfigureAwait(true);
            StatusText = $"已删除历史批次 #{result.RunId}：{result.DeletedCards:N0} 张历史卡片";
            SelectedRun = null;
            await RefreshRunsAsync().ConfigureAwait(true);
            RefreshFacets();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void OpenData()
    {
        navigationService.NavigateToData(new DataNavigationRequest());
    }

    private void RefreshFacets()
    {
        Facets.Clear();
        Facets.Add(new DataFacetItem("质量问题", SumIssueCounts(), "基础枚举"));
        Facets.Add(new DataFacetItem("实时异常", SumRealtimeCategoryCounts(), "点位审计"));
        Facets.Add(new DataFacetItem("对账差异", ReconciliationItems.Count, "实时源"));
        Facets.Add(new DataFacetItem("历史批次", Runs.Count, "可恢复"));
        Facets.Add(new DataFacetItem("异常隔离", Runs.Count(run => run.IsAnomaly), "批次"));
    }

    private int SumIssueCounts()
    {
        return QualityIssues.Sum(issue => issue.Count);
    }

    private int SumRealtimeCategoryCounts()
    {
        return RealtimeQualityCategories.Sum(category => category.Count);
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

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string FormatDateTime(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : value;
    }
}
