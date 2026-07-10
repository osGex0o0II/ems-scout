using EmsScout.Application.Devices;
using EmsScout.Application.Collection;
using EmsScout.Application.Quality;
using EmsScout.Domain;

namespace EmsScout.Application;

public sealed class DashboardOverviewService(
    IDeviceReadRepository repository,
    IQualityAuditService qualityAuditService,
    IRealtimeQualityAuditService realtimeQualityAuditService,
    IRealtimeReconciliationService realtimeReconciliationService,
    ICollectionRunRepository collectionRunRepository)
{
    public async Task<DashboardOverview> LoadAsync(CancellationToken cancellationToken = default)
    {
        var result = await repository.SearchAsync(
            new DeviceQuery(Limit: 1, Offset: 0),
            cancellationToken).ConfigureAwait(false);
        var summary = await LoadSummaryAsync(result.Facets, cancellationToken).ConfigureAwait(false);
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

        var risks = DashboardRiskBuilder.Build(
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

        return new DashboardOverview("SQLite + 实时详情", DateTimeOffset.Now, summary, metrics, risks);
    }

    private static string Percent(double value)
    {
        return value.ToString("P1");
    }

    private async Task<FleetSummary> LoadSummaryAsync(
        DeviceFacets facets,
        CancellationToken cancellationToken)
    {
        var buildings = new List<BuildingSummary>();
        foreach (var building in new[] { "1号", "2号", "3号", "4号", "5号", "6号" })
        {
            var page = await repository.SearchAsync(
                new DeviceQuery(Building: building, Limit: 1, Offset: 0),
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
}
