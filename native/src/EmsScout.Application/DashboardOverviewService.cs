using EmsScout.Application.Devices;
using EmsScout.Application.Collection;
using EmsScout.Application.Groups;
using EmsScout.Application.Settings;
using EmsScout.Domain;
using System.Collections.Concurrent;

namespace EmsScout.Application;

public sealed class DashboardOverviewService(
    IDeviceReadRepository repository,
    IAreaGroupRepository areaGroupRepository,
    AppSettingsService? settingsService = null,
    ICollectionRunRepository? collectionRunRepository = null,
    IDashboardSummaryRepository? summaryRepository = null,
    IDeviceReadRevisionSource? revisionSource = null)
{
    private const int CacheLimit = 4;
    private const int MaxRevisionBuildAttempts = 3;
    private static readonly string[] Buildings = ["1号", "2号", "3号", "4号", "5号", "6号"];
    private readonly ConcurrentDictionary<OverviewCacheKey, Lazy<Task<DashboardOverview>>> _cache = new();

    public async Task<DashboardOverview> LoadAsync(
        long? runId = null,
        CancellationToken cancellationToken = default)
    {
        return await LoadAsync(runId, buildAttempt: 1, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DashboardOverview> LoadAsync(
        long? runId,
        int buildAttempt,
        CancellationToken cancellationToken)
    {
        var configuredSettings = settingsService?.Current;
        var anomalySettings = configuredSettings is null
            ? DashboardAnomalySettings.Default
            : new DashboardAnomalySettings(
                configuredSettings.DashboardNormalMode,
                configuredSettings.DashboardTemperatureMin,
                configuredSettings.DashboardTemperatureMax);
        var revision = revisionSource is null
            ? null
            : await revisionSource.GetRevisionAsync(cancellationToken).ConfigureAwait(false);
        if (revision is null)
        {
            var uncachedSummary = summaryRepository is null
                ? null
                : await summaryRepository.LoadAsync(runId, cancellationToken).ConfigureAwait(false);
            return await BuildOverviewAsync(runId, anomalySettings, uncachedSummary, cancellationToken)
                .ConfigureAwait(false);
        }

        var cacheKey = new OverviewCacheKey(runId, revision, anomalySettings);
        var pending = _cache.GetOrAdd(
            cacheKey,
            key => new Lazy<Task<DashboardOverview>>(
                () => BuildRevisionAsync(runId, anomalySettings),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var overview = await pending.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            var completedRevision = await revisionSource!
                .GetRevisionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(revision, completedRevision, StringComparison.Ordinal))
            {
                _cache.TryRemove(new KeyValuePair<OverviewCacheKey, Lazy<Task<DashboardOverview>>>(cacheKey, pending));
                if (buildAttempt >= MaxRevisionBuildAttempts)
                {
                    throw new InvalidOperationException("总览数据版本在读取期间持续变化，请稍后重试。");
                }

                return await LoadAsync(runId, buildAttempt + 1, cancellationToken).ConfigureAwait(false);
            }

            if (!overview.IsCacheable)
            {
                _cache.TryRemove(new KeyValuePair<OverviewCacheKey, Lazy<Task<DashboardOverview>>>(cacheKey, pending));
                return overview;
            }

            if (!string.IsNullOrWhiteSpace(overview.AreaGroupsError))
            {
                _cache.TryRemove(new KeyValuePair<OverviewCacheKey, Lazy<Task<DashboardOverview>>>(cacheKey, pending));
                return overview;
            }

            TrimCache(cacheKey);
            return overview;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _cache.TryRemove(new KeyValuePair<OverviewCacheKey, Lazy<Task<DashboardOverview>>>(cacheKey, pending));
            throw;
        }
    }

    private async Task<DashboardOverview> BuildRevisionAsync(
        long? runId,
        DashboardAnomalySettings anomalySettings)
    {
        var summaryResult = summaryRepository is null
            ? null
            : await summaryRepository.LoadAsync(runId, CancellationToken.None).ConfigureAwait(false);
        return await BuildOverviewAsync(runId, anomalySettings, summaryResult, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<DashboardOverview> BuildOverviewAsync(
        long? runId,
        DashboardAnomalySettings anomalySettings,
        DashboardSummaryResult? summaryResult,
        CancellationToken cancellationToken)
    {
        var result = await repository.SearchAsync(
            new DeviceQuery(Limit: 50_000, Offset: 0, RunId: runId),
            cancellationToken).ConfigureAwait(false);
        var inventoryRows = result.Rows
            .Where(device => !device.IsVirtual)
            .ToArray();
        var summary = summaryResult?.Summary ?? BuildSummary(inventoryRows);
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
        var areaGroupsTask = LoadAreaGroupsAsync(inventoryRows, anomalySettings, cancellationToken);
        var areaGroupContext = await areaGroupsTask.ConfigureAwait(false);
        var collectedAt = inventoryRows
            .Where(device => device.CollectedAt is not null)
            .Select(device => device.CollectedAt!.Value)
            .ToArray();
        var sourceUpdatedAt = summaryResult?.SourceUpdatedAt ??
                              (collectedAt.Length == 0 ? null as DateTimeOffset? : collectedAt.Max());
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
            realtimeStatus.StatusText)
        {
            IsCacheable = result.IsCacheable,
        };
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
            var groupSet = await areaGroupRepository.LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
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

    private void TrimCache(OverviewCacheKey currentKey)
    {
        if (_cache.Count <= CacheLimit)
        {
            return;
        }

        foreach (var candidate in _cache)
        {
            if (_cache.Count <= CacheLimit)
            {
                break;
            }

            if (candidate.Key != currentKey &&
                candidate.Value.IsValueCreated &&
                candidate.Value.Value.IsCompleted)
            {
                _cache.TryRemove(candidate);
            }
        }
    }

    private sealed record OverviewCacheKey(
        long? RunId,
        string Revision,
        DashboardAnomalySettings AnomalySettings);
}
