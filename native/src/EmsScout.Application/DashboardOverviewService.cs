using EmsScout.Application.Devices;
using EmsScout.Application.Collection;
using EmsScout.Application.Groups;
using EmsScout.Application.Settings;
using EmsScout.Domain;

namespace EmsScout.Application;

public sealed class DashboardOverviewService(
    IDeviceReadRepository repository,
    IAreaGroupRepository areaGroupRepository,
    AppSettingsService? settingsService = null,
    ICollectionRunRepository? collectionRunRepository = null)
{
    private static readonly string[] Buildings = ["1号", "2号", "3号", "4号", "5号", "6号"];

    public async Task<DashboardOverview> LoadAsync(
        long? runId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await repository.SearchAsync(
            new DeviceQuery(Limit: 50_000, Offset: 0, RunId: runId),
            cancellationToken).ConfigureAwait(false);
        var inventoryRows = result.Rows
            .Where(device => !device.IsVirtual)
            .ToArray();
        var summary = BuildSummary(inventoryRows);
        var onlineRows = inventoryRows
            .Where(device => device.CommunicationState is DeviceCommunicationState.Running or DeviceCommunicationState.Stopped)
            .ToArray();
        var realtimeRows = onlineRows.Where(device => device.Realtime is not null).ToArray();
        var realtimeStatus = DashboardRealtimeAvailabilityRules.Evaluate(
            onlineRows.Length,
            realtimeRows.Length,
            onlineRows
                .Select(device => device.RealtimeUnavailableReason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason)));
        var configuredSettings = settingsService?.Current;
        var anomalySettings = configuredSettings is null
            ? DashboardAnomalySettings.Default
            : new DashboardAnomalySettings(
                configuredSettings.DashboardNormalMode,
                configuredSettings.DashboardTemperatureMin,
                configuredSettings.DashboardTemperatureMax);
        var areaGroupsTask = LoadAreaGroupsAsync(inventoryRows, anomalySettings, cancellationToken);
        var areaGroupContext = await areaGroupsTask.ConfigureAwait(false);
        var collectedAt = inventoryRows
            .Where(device => device.CollectedAt is not null)
            .Select(device => device.CollectedAt!.Value)
            .ToArray();
        var sourceUpdatedAt = collectedAt.Length == 0 ? null as DateTimeOffset? : collectedAt.Max();
        var batchCompletedAt = await ResolveBatchCompletedAtAsync(runId, cancellationToken).ConfigureAwait(false);
        var totalMetricDetail = collectionRunRepository is not null
            ? batchCompletedAt.HasValue
                ? batchCompletedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "当前来源未确定"
            : sourceUpdatedAt.HasValue
                ? sourceUpdatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "暂无采集时间";

        var metrics = new[]
        {
            new OverviewMetric("总设备数", summary.Total.ToString("N0"), totalMetricDetail, OverviewMetricKind.Info),
            new OverviewMetric("开机", summary.Running.ToString("N0"), Percent(summary.RunningRate), OverviewMetricKind.Success, CommunicationState: "开机"),
            new OverviewMetric("关机", summary.Stopped.ToString("N0"), Percent(Rate(summary.Stopped, summary.Total)), OverviewMetricKind.Neutral, CommunicationState: "关机"),
            new OverviewMetric("离线", summary.Offline.ToString("N0"), Percent(summary.OfflineRate), OverviewMetricKind.Warning, CommunicationState: "离线"),
            new OverviewMetric("未知", summary.Unknown.ToString("N0"), Percent(Rate(summary.Unknown, summary.Total)), summary.Unknown > 0 ? OverviewMetricKind.Warning : OverviewMetricKind.Success, CommunicationState: "未知"),
        };

        return new DashboardOverview(
            "SQLite 采集库 + 实时详情",
            sourceUpdatedAt,
            summary,
            metrics,
            areaGroupContext.Groups,
            areaGroupContext.Error,
            realtimeStatus.Availability,
            realtimeStatus.StatusText);
    }

    private async Task<DateTimeOffset?> ResolveBatchCompletedAtAsync(
        long? runId,
        CancellationToken cancellationToken)
    {
        if (collectionRunRepository is null)
        {
            return null;
        }

        var runs = await collectionRunRepository.ListAsync(null, cancellationToken).ConfigureAwait(false);
        CollectionRunRecord? run;
        if (runId is > 0)
        {
            run = runs.FirstOrDefault(item => item.Id == runId.Value);
        }
        else
        {
            var binding = await collectionRunRepository
                .GetCurrentDataSourceAsync(cancellationToken)
                .ConfigureAwait(false);
            run = binding.IsBound && binding.RunId is > 0
                ? runs.FirstOrDefault(item => item.Id == binding.RunId.Value)
                : null;

            if (run is not null && !binding.Matches(run))
            {
                run = null;
            }
        }

        if (run is null)
        {
            return null;
        }

        return StoredTimestamp.TryParse(run.CompletedAt, out var completedAt)
            ? completedAt
            : StoredTimestamp.TryParse(run.ImportedAt, out var importedAt)
                ? importedAt
                : null;
    }

    private static string Percent(double value)
    {
        return value.ToString("P1");
    }

    private static double Rate(int count, int total)
    {
        return total == 0 ? 0 : count / (double)total;
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
        DashboardAnomalySettings anomalySettings,
        CancellationToken cancellationToken)
    {
        try
        {
            var groupSet = await areaGroupRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
            return new DashboardAreaGroupContext(
                DashboardAreaGroupBuilder.Build(devices, groupSet, anomalySettings),
                string.Empty);
        }
        catch (Exception ex)
        {
            return new DashboardAreaGroupContext([], ex.Message);
        }
    }

    private sealed record DashboardAreaGroupContext(
        IReadOnlyList<DashboardAreaGroupSummary> Groups,
        string Error);
}
