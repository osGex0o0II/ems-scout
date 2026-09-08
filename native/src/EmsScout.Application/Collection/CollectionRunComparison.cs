namespace EmsScout.Application.Collection;

public sealed record CollectionRunBuildingDifference(
    string Building,
    int SnapshotCount,
    int CurrentCount,
    int AddedCount,
    int MissingCount)
{
    public int Delta => CurrentCount - SnapshotCount;

    public string Summary => $"历史 {SnapshotCount:N0} / 当前 {CurrentCount:N0}";
}

public sealed record CollectionRunComparison(
    long RunId,
    int SnapshotCardCount,
    int CurrentCardCount,
    int AddedCount,
    int MissingCount,
    int ChangedCount,
    bool IsRestorable,
    string? BlockingReason,
    IReadOnlyList<CollectionRunBuildingDifference> BuildingDifferences)
{
    public static CollectionRunComparison Create(
        CollectionRunRecord run,
        IReadOnlyDictionary<string, int> snapshotByBuilding,
        IReadOnlyDictionary<string, int> currentByBuilding,
        int changedCount,
        bool isRestorable,
        string? blockingReason)
    {
        var buildings = snapshotByBuilding.Keys
            .Concat(currentByBuilding.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var differences = buildings
            .Select(building =>
            {
                var snapshotCount = GetCount(snapshotByBuilding, building);
                var currentCount = GetCount(currentByBuilding, building);
                return new CollectionRunBuildingDifference(
                    building,
                    snapshotCount,
                    currentCount,
                    Math.Max(currentCount - snapshotCount, 0),
                    Math.Max(snapshotCount - currentCount, 0));
            })
            .ToArray();

        return new CollectionRunComparison(
            run.Id,
            snapshotByBuilding.Values.Sum(),
            currentByBuilding.Values.Sum(),
            differences.Sum(row => row.AddedCount),
            differences.Sum(row => row.MissingCount),
            Math.Max(changedCount, 0),
            isRestorable,
            string.IsNullOrWhiteSpace(blockingReason) ? null : blockingReason.Trim(),
            differences);
    }

    private static int GetCount(IReadOnlyDictionary<string, int> counts, string building) =>
        counts.TryGetValue(building, out var count) ? Math.Max(count, 0) : 0;
}
