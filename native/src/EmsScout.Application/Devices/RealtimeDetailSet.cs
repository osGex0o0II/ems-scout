namespace EmsScout.Application.Devices;

public enum RealtimeDetailAvailability
{
    Available,
    MissingSnapshot,
    Unavailable,
}

public sealed class RealtimeDetailSet(
    IReadOnlyList<RealtimeDetailRecord> rows,
    RealtimeDetailAvailability availability = RealtimeDetailAvailability.Available,
    string? statusText = null,
    long? sourceRunId = null)
{
    private readonly Dictionary<string, int> _exactUsage = [];

    public IReadOnlyList<RealtimeDetailRecord> Rows { get; } = rows;

    public RealtimeDetailAvailability Availability { get; } = availability;

    public long? SourceRunId { get; } = sourceRunId;

    public string StatusText { get; } = statusText ?? (availability switch
    {
        RealtimeDetailAvailability.MissingSnapshot => "该批次未保存实时详情快照，集控锁定状态不可用",
        RealtimeDetailAvailability.Unavailable => "实时详情暂不可用",
        _ => string.Empty,
    });

    public bool IsAvailable => Availability == RealtimeDetailAvailability.Available;

    public Dictionary<string, List<RealtimeDetailRecord>> ByExactKey { get; } = BuildIndex(rows, row => RealtimeKeyBuilder.ExactKey(row));

    public Dictionary<string, List<RealtimeDetailRecord>> ByNameKey { get; } = BuildIndex(rows, row => RealtimeKeyBuilder.NameKey(row.Building, row.Name));

    public HashSet<string> UsedRowIds { get; } = [];

    public void MarkUsed(RealtimeDetailRecord record)
    {
        UsedRowIds.Add(record.RowId);
    }

    public RealtimeDetailRecord? TakeExact(string key)
    {
        if (!ByExactKey.TryGetValue(key, out var values) || values.Count == 0)
        {
            return null;
        }

        var index = _exactUsage.GetValueOrDefault(key);
        while (index < values.Count)
        {
            var row = values[index];
            index++;
            _exactUsage[key] = index;
            if (UsedRowIds.Add(row.RowId))
            {
                return row;
            }
        }

        return null;
    }

    public RealtimeDetailRecord? UniqueByName(string key)
    {
        if (!ByNameKey.TryGetValue(key, out var values) || values.Count != 1)
        {
            return null;
        }

        return UsedRowIds.Add(values[0].RowId) ? values[0] : null;
    }

    public int UnmatchedRealtimeRows => Rows.Count(row => !UsedRowIds.Contains(row.RowId));

    private static Dictionary<string, List<RealtimeDetailRecord>> BuildIndex(
        IEnumerable<RealtimeDetailRecord> rows,
        Func<RealtimeDetailRecord, string> keySelector)
    {
        var map = new Dictionary<string, List<RealtimeDetailRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = keySelector(row);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            list.Add(row);
        }

        return map;
    }
}
