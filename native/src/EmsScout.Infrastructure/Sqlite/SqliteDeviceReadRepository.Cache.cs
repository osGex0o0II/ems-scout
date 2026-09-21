using System.Collections.Concurrent;
using System.Collections.Immutable;
using EmsScout.Application.Devices;

namespace EmsScout.Infrastructure.Sqlite;

public sealed partial class SqliteDeviceReadRepository
{
    private readonly DeviceDataRevisionMonitor _revisionMonitor = new();
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly ConcurrentDictionary<SnapshotKey, Lazy<Task<FilterDeviceSnapshot>>> _snapshots = new();
    private readonly ConcurrentDictionary<(SnapshotKey Key, DeviceQuery Query), DeviceListResult> _pages = new();

    private readonly SemaphoreSlim _ruleGate = new(1, 1);
    private ImmutableArray<DeviceRecord>? _ruleRows;
    private string? _ruleRevision;

    private async Task<T> ReadConsistentAsync<T>(DeviceQuery query,
        Func<FilterDeviceSnapshot, Task<T>> read, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var snapshot = await GetSnapshotAsync(query, token).ConfigureAwait(false);
            var result = await read(snapshot).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (realtimeDetailSource is not null and not IDeviceReadRevisionSource ||
                snapshot.Key!.Revision == await GetRevisionAsync(token).ConfigureAwait(false)) return result;
        }
        throw new InvalidOperationException("数据正在更新，请稍后重试。");
    }

    public Task<string> GetRevisionAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(DatabasePathResolver());
        var databaseRevision = _revisionMonitor.GetRevision(path);
        var realtimeRevision = realtimeDetailSource is IDeviceReadRevisionSource revisionSource
            ? await revisionSource.GetRevisionAsync(cancellationToken).ConfigureAwait(false)
            : realtimeDetailSource is null ? string.Empty : Guid.NewGuid().ToString("N");
        return $"{path}|{databaseRevision}|{realtimeRevision}";
    }, cancellationToken);

    private async Task<FilterDeviceSnapshot> GetSnapshotAsync(DeviceQuery query, CancellationToken token)
    {
        var scope = query with { Limit = 0, Offset = 0, SortBy = null, SortDescending = false };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var revision = await GetRevisionAsync(token).ConfigureAwait(false);
            var key = new SnapshotKey(revision, scope);
            var pending = _snapshots.GetOrAdd(key, _ => new Lazy<Task<FilterDeviceSnapshot>>(
                () => Task.Run(async () =>
                {
                    await _snapshotGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var result = await LoadFilterRowsAsync(scope, CancellationToken.None).ConfigureAwait(false);
                        return result with { Key = key };
                    }
                    finally { _snapshotGate.Release(); }
                }), LazyThreadSafetyMode.ExecutionAndPublication));
            FilterDeviceSnapshot snapshot;
            try { snapshot = await pending.Value.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                _snapshots.TryRemove(new KeyValuePair<SnapshotKey, Lazy<Task<FilterDeviceSnapshot>>>(key, pending));
                throw;
            }
            // Unknown custom sources cannot safely be cached. Real JSON sources expose revisions.
            var cacheable = realtimeDetailSource is null or IDeviceReadRevisionSource;
            if (cacheable && revision != await GetRevisionAsync(token).ConfigureAwait(false))
            {
                _snapshots.TryRemove(key, out _);
                continue;
            }
            if (!cacheable || !snapshot.IsCacheable) _snapshots.TryRemove(key, out _);
            foreach (var old in _snapshots.Keys.Where(k => k.Revision != revision)) _snapshots.TryRemove(old, out _);
            foreach (var old in _filterOptions.Keys.Where(k => k.Key.Revision != revision)) _filterOptions.TryRemove(old, out _);
            foreach (var old in _pages.Keys.Where(k => k.Key.Revision != revision)) _pages.TryRemove(old, out _);
            foreach (var old in _snapshots.Keys.Where(k => k != key).Take(Math.Max(0, _snapshots.Count - 3))) _snapshots.TryRemove(old, out _);
            return snapshot;
        }
        throw new InvalidOperationException("数据正在更新，请稍后重试。");
    }

    private DeviceListResult BuildPage(FilterDeviceSnapshot snapshot, DeviceQuery query, CancellationToken token)
    {
        var normalized = query with { Limit = 0, Offset = 0 };
        var cacheKey = (snapshot.Key!, normalized);
        if (!_pages.TryGetValue(cacheKey, out var all))
        {
            token.ThrowIfCancellationRequested();
            // The snapshot preserves SQL visibility and source provenance, with
            // realtime ownership resolved against complete building identities.
            var baseRows = snapshot.Rows.Where(row => DeviceQuerySpecification.MatchesScope(row, query)).ToArray();
            var filtered = SortRows(baseRows.Where(row => DeviceQuerySpecification.MatchesResult(row, query)).ToArray(), query)
                .ToImmutableArray();
            all = new DeviceListResult(filtered.Length, filtered,
                DeviceFacets.From(baseRows, realtimeRows: snapshot.RealtimeRows, realtimeUnmatched: snapshot.UnmatchedRealtimeRows), snapshot.StatusText)
            { IsCacheable = snapshot.IsCacheable };
            token.ThrowIfCancellationRequested();
            if (_pages.Count >= 12) _pages.Clear();
            if (snapshot.IsCacheable) _pages[cacheKey] = all;
        }
        return all with { Rows = all.Rows.Skip(Math.Max(0, query.Offset)).Take(Math.Clamp(query.Limit, 1, 50000)).ToImmutableArray() };
    }

    private readonly ConcurrentDictionary<(SnapshotKey Key, DeviceQuery Query), DeviceFilterOptions> _filterOptions = new();

    private DeviceFilterOptions GetFilterOptions(FilterDeviceSnapshot snapshot, DeviceQuery query)
    {
        var key = (snapshot.Key!, query with { Limit = 0, Offset = 0, SortBy = null, SortDescending = false });
        if (_filterOptions.TryGetValue(key, out var cached)) return cached;
        var result = BuildFilterOptions(snapshot.Rows, query);
        result = result with
        {
            AreaGroups = snapshot.AreaGroups,
            Buildings = result.Buildings.ToImmutableArray(),
            CommunicationStates = result.CommunicationStates.ToImmutableArray(),
            Floors = result.Floors.ToImmutableArray(),
            SubAreas = result.SubAreas.ToImmutableArray(),
            PageNames = result.PageNames.ToImmutableArray(),
            DeviceNames = result.DeviceNames.ToImmutableArray(),
            Zuos = result.Zuos.ToImmutableArray(),
            Modes = result.Modes.ToImmutableArray(),
            Fans = result.Fans.ToImmutableArray(),
            SetTemperatures = result.SetTemperatures.ToImmutableArray(),
            IndoorTemperatures = result.IndoorTemperatures.ToImmutableArray(),
            Tags = result.Tags.ToImmutableArray(),
            RealtimePowers = result.RealtimePowers?.ToImmutableArray(),
            RealtimeModes = result.RealtimeModes?.ToImmutableArray(),
            RealtimeFans = result.RealtimeFans?.ToImmutableArray(),
            RealtimeLocks = result.RealtimeLocks?.ToImmutableArray(),
            RealtimeSystemTypes = result.RealtimeSystemTypes?.ToImmutableArray(),
            AreaGroupCounts = result.AreaGroupCounts?.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)
        };
        if (_filterOptions.Count >= 12) _filterOptions.Clear();
        return snapshot.IsCacheable ? _filterOptions.GetOrAdd(key, result) : result;
    }

    private static DeviceRecord FreezeRow(DeviceRecord row) => row with
    {
        Tags = row.Tags?.ToImmutableArray(),
        AreaGroups = row.AreaGroups?.ToImmutableArray(),
        Realtime = row.Realtime is null ? null : row.Realtime with
        {
            Fields = row.Realtime.Fields.ToImmutableDictionary(),
            ValidFields = row.Realtime.ValidFields.ToImmutableDictionary(),
            RawFields = row.Realtime.RawFields?.ToImmutableDictionary()
        }
    };

    public void Dispose() => _revisionMonitor.Dispose();

    private sealed record SnapshotKey(string Revision, DeviceQuery Scope);
}
