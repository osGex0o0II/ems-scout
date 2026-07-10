namespace EmsScout.Application.Devices;

public sealed class RealtimeDetailSet(IReadOnlyList<RealtimeDetailRecord> rows)
{
    private readonly Dictionary<string, int> _exactUsage = [];

    public IReadOnlyList<RealtimeDetailRecord> Rows { get; } = rows;

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
