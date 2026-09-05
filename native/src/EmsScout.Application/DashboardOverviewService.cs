using EmsScout.Application.Devices;
using EmsScout.Application.Collection;
using EmsScout.Application.Groups;
using EmsScout.Application.Quality;
using EmsScout.Domain;

namespace EmsScout.Application;

public sealed class DashboardOverviewService(
    IDeviceReadRepository repository,
    IQualityAuditService qualityAuditService,
    IRealtimeQualityAuditService realtimeQualityAuditService,
    IRealtimeReconciliationService realtimeReconciliationService,
    ICollectionRunRepository collectionRunRepository,
    IAreaGroupRepository areaGroupRepository)
{
    private static readonly string[] Buildings = ["1号", "2号", "3号", "4号", "5号", "6号"];

    public async Task<DashboardOverview> LoadAsync(
        long? runId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await repository.SearchAsync(
            new DeviceQuery(Limit: 50_000, Offset: 0, RunId: runId),
            cancellationToken).ConfigureAwait(false);
        var summary = BuildSummary(result.Rows);
        var riskTask = LoadRiskContextAsync(runId, cancellationToken);
        var areaGroupsTask = LoadAreaGroupsAsync(result.Rows, cancellationToken);
        await Task.WhenAll(riskTask, areaGroupsTask).ConfigureAwait(false);
        var riskContext = riskTask.Result;
        var areaGroupContext = areaGroupsTask.Result;
        var publicDevices = result.Rows.Count(device => string.Equals(
            device.AreaType,
            DeviceAreaClassifier.PublicArea,
            StringComparison.OrdinalIgnoreCase));

        var metrics = new[]
        {
            new OverviewMetric("总设备数", summary.Total.ToString("N0"), "SQLite + 实时纳管当前口径", OverviewMetricKind.Info),
            new OverviewMetric("开机", summary.Running.ToString("N0"), Percent(summary.RunningRate), OverviewMetricKind.Success, CommunicationState: "开机"),
            new OverviewMetric("关机", summary.Stopped.ToString("N0"), "在线但未运行", OverviewMetricKind.Neutral, CommunicationState: "关机"),
            new OverviewMetric("离线", summary.Offline.ToString("N0"), Percent(summary.OfflineRate), OverviewMetricKind.Warning, CommunicationState: "离线"),
            new OverviewMetric("未知", summary.Unknown.ToString("N0"), "需排查状态映射", summary.Unknown > 0 ? OverviewMetricKind.Warning : OverviewMetricKind.Success, CommunicationState: "未知"),
            new OverviewMetric("公区空调", publicDevices.ToString("N0"), "按页面布局、命名规则及人工分类", OverviewMetricKind.Info, AreaType: DeviceAreaClassifier.PublicArea),
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

        var collectedAt = result.Rows
            .Where(device => !device.IsVirtual && device.CollectedAt is not null)
            .Select(device => device.CollectedAt!.Value)
            .ToArray();
        var sourceUpdatedAt = collectedAt.Length == 0 ? null as DateTimeOffset? : collectedAt.Max();
        return new DashboardOverview(
            "SQLite 采集库 + 实时详情",
            sourceUpdatedAt,
            summary,
            metrics,
            risks,
            areaGroupContext.Groups,
            areaGroupContext.Error);
    }

    private static string Percent(double value)
    {
        return value.ToString("P1");
    }

    private static FleetSummary BuildSummary(IReadOnlyList<DeviceRecord> devices)
    {
        var buildings = Buildings
            .Select(building => BuildBuildingSummary(
                building,
                devices.Where(device => string.Equals(
                    device.Building,
                    building,
                    StringComparison.OrdinalIgnoreCase))))
            .ToArray();

        return new FleetSummary(
            Total: devices.Count,
            Running: devices.Count(device => device.CommunicationState == DeviceCommunicationState.Running),
            Stopped: devices.Count(device => device.CommunicationState == DeviceCommunicationState.Stopped),
            Offline: devices.Count(device => device.CommunicationState == DeviceCommunicationState.Offline),
            Unknown: devices.Count(device => device.CommunicationState == DeviceCommunicationState.Unknown),
            Buildings: buildings);
    }

    private static BuildingSummary BuildBuildingSummary(string building, IEnumerable<DeviceRecord> source)
    {
        var devices = source.ToArray();
        return new BuildingSummary(
            Building: building,
            Total: devices.Length,
            Running: devices.Count(device => device.CommunicationState == DeviceCommunicationState.Running),
            Stopped: devices.Count(device => device.CommunicationState == DeviceCommunicationState.Stopped),
            Offline: devices.Count(device => device.CommunicationState == DeviceCommunicationState.Offline),
            Unknown: devices.Count(device => device.CommunicationState == DeviceCommunicationState.Unknown));
    }

    private async Task<DashboardAreaGroupContext> LoadAreaGroupsAsync(
        IReadOnlyList<DeviceRecord> devices,
        CancellationToken cancellationToken)
    {
        try
        {
            var groupSet = await areaGroupRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
            return new DashboardAreaGroupContext(
                DashboardAreaGroupBuilder.Build(devices, groupSet),
                string.Empty);
        }
        catch (Exception ex)
        {
            return new DashboardAreaGroupContext([], ex.Message);
        }
    }

    private async Task<DashboardRiskContext> LoadRiskContextAsync(
        long? runId,
        CancellationToken cancellationToken)
    {
        if (runId is not null)
        {
            return new DashboardRiskContext(null, null, null, null, null, null, [], null);
        }

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

    private sealed record DashboardAreaGroupContext(
        IReadOnlyList<DashboardAreaGroupSummary> Groups,
        string Error);
}
