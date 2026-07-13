using EmsScout.Application.Devices;
using EmsScout.Application.Collection;
using EmsScout.Application.Quality;
using EmsScout.Application.Attention;
using EmsScout.Domain;

namespace EmsScout.Application;

public sealed class DashboardOverviewService(
    IDeviceReadRepository repository,
    IQualityAuditService qualityAuditService,
    IRealtimeQualityAuditService realtimeQualityAuditService,
    IRealtimeReconciliationService realtimeReconciliationService,
    ICollectionRunRepository collectionRunRepository,
    IAttentionIssueRepository attentionIssueRepository)
{
    public async Task<DashboardOverview> LoadAsync(long? runId = null, CancellationToken cancellationToken = default)
    {
        var result = await repository.SearchAsync(
            new DeviceQuery(Limit: 1, Offset: 0, RunId: runId),
            cancellationToken).ConfigureAwait(false);
        var summary = await LoadSummaryAsync(result.Facets, runId, cancellationToken).ConfigureAwait(false);
        var riskContext = await LoadRiskContextAsync(cancellationToken).ConfigureAwait(false);

        var metrics = new[]
        {
            new OverviewMetric("总设备数", summary.Total.ToString("N0"), "SQLite + 实时纳管当前口径", OverviewMetricKind.Info),
            new OverviewMetric("开机", summary.Running.ToString("N0"), Percent(summary.RunningRate), OverviewMetricKind.Success),
            new OverviewMetric("关机", summary.Stopped.ToString("N0"), "在线但未运行", OverviewMetricKind.Neutral),
            new OverviewMetric("离线", summary.Offline.ToString("N0"), Percent(summary.OfflineRate), OverviewMetricKind.Warning),
            new OverviewMetric("未知", summary.Unknown.ToString("N0"), "需排查状态映射", summary.Unknown > 0 ? OverviewMetricKind.Warning : OverviewMetricKind.Success),
            new OverviewMetric("楼栋", summary.Buildings.Count.ToString("N0"), "当前采集范围", OverviewMetricKind.Info),
        };

        IReadOnlyList<DashboardRiskItem> risks = DashboardRiskBuilder.Build(
            summary,
            result.Facets,
            riskContext.QualityReport,
            riskContext.QualityError,
            riskContext.RealtimeReport,
            riskContext.RealtimeError,
            riskContext.Reconciliation,
            riskContext.ReconciliationError,
            riskContext.Runs,
            riskContext.RunsError);

        if (runId is null)
        {
            var observedAt = DateTimeOffset.UtcNow;
            var currentRunId = riskContext.QualityReport?.RunId ?? riskContext.Runs.FirstOrDefault()?.Id;
            var snapshot = new AttentionQueueSnapshot(
                risks
                    .Where(risk => risk.IsActionable)
                    .Select(risk => ToCandidate(risk, currentRunId))
                    .ToList(),
                ObservedSources(riskContext),
                observedAt);
            var synchronized = await attentionIssueRepository
                .SynchronizeAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
            risks = synchronized.Select(ToDashboardRisk).ToList();
        }

        return new DashboardOverview("SQLite + 实时详情", DateTimeOffset.Now, summary, metrics, risks);
    }

    private static string Percent(double value)
    {
        return value.ToString("P1");
    }

    private async Task<FleetSummary> LoadSummaryAsync(
        DeviceFacets facets,
        long? runId,
        CancellationToken cancellationToken)
    {
        var buildings = new List<BuildingSummary>();
        foreach (var building in new[] { "1号", "2号", "3号", "4号", "5号", "6号" })
        {
            var page = await repository.SearchAsync(
                new DeviceQuery(Building: building, Limit: 1, Offset: 0, RunId: runId),
                cancellationToken).ConfigureAwait(false);
            buildings.Add(new BuildingSummary(
                Building: building,
                Total: page.Facets.Total,
                Running: page.Facets.Running,
                Stopped: page.Facets.Stopped,
                Offline: page.Facets.Offline,
                Unknown: page.Facets.Unknown));
        }

        return new FleetSummary(
            Total: facets.Total,
            Running: facets.Running,
            Stopped: facets.Stopped,
            Offline: facets.Offline,
            Unknown: facets.Unknown,
            Buildings: buildings);
    }

    private async Task<DashboardRiskContext> LoadRiskContextAsync(CancellationToken cancellationToken)
    {
        QualityAuditReport? qualityReport = null;
        Exception? qualityError = null;
        RealtimeQualityAuditReport? realtimeReport = null;
        Exception? realtimeError = null;
        RealtimeReconciliationSummary? reconciliation = null;
        Exception? reconciliationError = null;
        IReadOnlyList<CollectionRunRecord> runs = [];
        Exception? runsError = null;

        try
        {
            qualityReport = await qualityAuditService.LoadLatestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            qualityError = ex;
        }

        try
        {
            realtimeReport = await realtimeQualityAuditService.LoadLatestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            realtimeError = ex;
        }

        try
        {
            var result = await realtimeReconciliationService.AnalyzeAsync(
                new RealtimeReconciliationQuery(Limit: 1),
                cancellationToken).ConfigureAwait(false);
            reconciliation = result.Summary;
        }
        catch (Exception ex)
        {
            reconciliationError = ex;
        }

        try
        {
            runs = await collectionRunRepository.ListAsync(20, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            runsError = ex;
        }

        return new DashboardRiskContext(
            qualityReport,
            qualityError,
            realtimeReport,
            realtimeError,
            reconciliation,
            reconciliationError,
            runs,
            runsError);
    }

    private sealed record DashboardRiskContext(
        QualityAuditReport? QualityReport,
        Exception? QualityError,
        RealtimeQualityAuditReport? RealtimeReport,
        Exception? RealtimeError,
        RealtimeReconciliationSummary? Reconciliation,
        Exception? ReconciliationError,
        IReadOnlyList<CollectionRunRecord> Runs,
        Exception? RunsError);

    private static AttentionIssueCandidate ToCandidate(DashboardRiskItem risk, long? currentRunId) => new(
        IssueId: risk.IssueId,
        SourceKey: risk.SourceKey,
        IssueType: risk.IssueType,
        Severity: risk.Kind,
        RunId: risk.RunId ?? currentRunId,
        Title: risk.Title,
        Detail: risk.Detail,
        Scope: risk.Scope,
        Count: risk.Count,
        Navigation: new AttentionNavigationTarget(
            string.IsNullOrWhiteSpace(risk.CommunicationState) ? "audit" : "devices",
            risk.CommunicationState,
            risk.RealtimeMatch,
            risk.RealtimePoints,
            risk.QuickFilter,
            risk.WatchState));

    private static IReadOnlySet<string> ObservedSources(DashboardRiskContext context)
    {
        var sources = new HashSet<string>(["inventory", "watch"], StringComparer.Ordinal);
        if (context.QualityError is null)
        {
            sources.Add("quality");
        }

        if (context.RealtimeError is null)
        {
            sources.Add("realtime");
        }

        if (context.ReconciliationError is null)
        {
            sources.Add("reconciliation");
        }

        if (context.RunsError is null)
        {
            sources.Add("runs");
        }

        return sources;
    }

    private static DashboardRiskItem ToDashboardRisk(AttentionIssueRecord issue) => new(
        Title: issue.Title,
        Detail: issue.Detail,
        Source: SourceLabel(issue.SourceKey),
        Kind: issue.Severity,
        Count: issue.Count,
        ActionLabel: issue.Navigation.Destination == "devices" ? "定位设备" : "查看审计",
        CommunicationState: issue.Navigation.CommunicationState,
        RealtimeMatch: issue.Navigation.RealtimeMatch,
        RealtimePoints: issue.Navigation.RealtimePoints,
        QuickFilter: issue.Navigation.QuickFilter,
        WatchState: issue.Navigation.WatchState,
        IssueId: issue.IssueId,
        SourceKey: issue.SourceKey,
        IssueType: issue.IssueType,
        Scope: issue.Scope,
        RunId: issue.RunId,
        Status: issue.Status,
        IgnoreReason: issue.IgnoreReason,
        LastSeenAt: issue.LastSeenAt);

    private static string SourceLabel(string sourceKey) => sourceKey switch
    {
        "inventory" => "当前数据",
        "watch" => "关注设备",
        "quality" => "基础质量审计",
        "realtime" => "实时点位审计",
        "reconciliation" => "实时对账",
        "runs" => "采集批次",
        _ => sourceKey,
    };
}
