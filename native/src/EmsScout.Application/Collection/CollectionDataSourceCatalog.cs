namespace EmsScout.Application.Collection;

public sealed record CollectionDataSourceCatalogResult(
    CollectionRunRecord? CurrentRun,
    IReadOnlyList<CollectionRunRecord> HistoricalRuns);

public static class CollectionDataSourceCatalog
{
    public static CollectionDataSourceCatalogResult Build(IEnumerable<CollectionRunRecord> runs)
    {
        var ordered = runs
            .Where(run => run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                          run.Status.Equals("needs_review", StringComparison.OrdinalIgnoreCase))
            // Completion time is the batch timeline. Import time is separate
            // metadata and must not make an older batch appear newer.
            .OrderByDescending(run => ParseTimestamp(run.CompletedAt, run.ImportedAt))
            .ThenByDescending(run => run.Id)
            .ToArray();
        var current = ordered.FirstOrDefault();
        // Current cards may be restored, mixed, or unbound. Chronology cannot
        // identify that source, so every available snapshot remains selectable.
        return new CollectionDataSourceCatalogResult(current, ordered);
    }

    public static void EnsureSnapshotAvailable(CollectionDataSourceCatalogResult catalog, long? runId)
    {
        if (runId is not null && catalog.HistoricalRuns.All(run => run.Id != runId))
            throw new InvalidOperationException($"历史批次 {runId} 不可用，请重新选择数据来源。");
    }

    private static DateTimeOffset ParseTimestamp(string primary, string fallback)
    {
        return StoredTimestamp.TryParse(primary, out var parsed)
            ? parsed
            : StoredTimestamp.TryParse(fallback, out parsed)
                ? parsed
                : DateTimeOffset.MinValue;
    }
}
