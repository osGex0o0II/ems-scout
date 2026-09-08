namespace EmsScout.Application.Collection;

public sealed record CollectionDataSourceCatalogResult(
    CollectionRunRecord? CurrentRun,
    IReadOnlyList<CollectionRunRecord> HistoricalRuns);

public static class CollectionDataSourceCatalog
{
    public static CollectionDataSourceCatalogResult Build(IEnumerable<CollectionRunRecord> runs)
    {
        var ordered = runs
            .Where(run => run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(run => ParseTimestamp(run.CompletedAt))
            .ThenByDescending(run => run.Id)
            .ToArray();
        var current = ordered.FirstOrDefault();
        var historical = current is null
            ? ordered
            : ordered.Where(run => run.Id != current.Id).ToArray();
        return new CollectionDataSourceCatalogResult(current, historical);
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }
}
