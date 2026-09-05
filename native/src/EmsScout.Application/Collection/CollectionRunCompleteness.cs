namespace EmsScout.Application.Collection;

public static class CollectionRunCompleteness
{
    public static readonly IReadOnlySet<string> RequiredBuildings =
        new HashSet<string>(["1号", "2号", "3号", "4号", "5号", "6号"], StringComparer.OrdinalIgnoreCase);

    public static bool IsCompleteFleetSnapshot(CollectionRunRecord run)
    {
        var buildings = run.Buildings
            .Where(building => !string.IsNullOrWhiteSpace(building))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) &&
            run.Scope.Equals("full", StringComparison.OrdinalIgnoreCase) &&
            buildings.SetEquals(RequiredBuildings) &&
            run.CardCount > 0 &&
            run.SnapshotCardCount == run.CardCount;
    }
}
