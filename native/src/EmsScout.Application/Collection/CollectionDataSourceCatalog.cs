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
        var historical = current is null
            ? ordered
            : ordered.Where(run => run.Id != current.Id).ToArray();
        return new CollectionDataSourceCatalogResult(current, historical);
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
