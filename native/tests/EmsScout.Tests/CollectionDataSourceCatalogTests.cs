using EmsScout.Application.Collection;

namespace EmsScout.Tests;

public sealed class CollectionDataSourceCatalogTests
{
    [Fact]
    public void MissingSnapshotIsAnExplicitErrorWhileCurrentRemainsSelectable()
    {
        var catalog = CollectionDataSourceCatalog.Build([Run(1, "2026-09-01", "completed", 2), Run(2, "2026-09-02", "needs_review", 3)]);
        CollectionDataSourceCatalog.EnsureSnapshotAvailable(catalog, null);
        CollectionDataSourceCatalog.EnsureSnapshotAvailable(catalog, 1);
        CollectionDataSourceCatalog.EnsureSnapshotAvailable(catalog, 2);
        Assert.Contains("999", Assert.Throws<InvalidOperationException>(() => CollectionDataSourceCatalog.EnsureSnapshotAvailable(catalog, 999)).Message);
    }

    [Fact]
    public void KeepsCurrentDatabaseSeparateWhenNewestRunFailsQualityGate()
    {
        var failedNewest = Run(24, "2026-09-08T10:00:00Z", "failed", 6471);
        var olderComplete = Run(21, "2026-09-07T10:00:00Z", "completed", 6573);

        var result = CollectionDataSourceCatalog.Build([failedNewest, olderComplete]);

        Assert.Equal(21, result.CurrentRun!.Id);
        Assert.Equal(21, Assert.Single(result.HistoricalRuns).Id);
    }

    [Fact]
    public void UsesNewestCompletedRunAsCurrentEvenWhenItIsNotACompleteFleetSnapshot()
    {
        var newestPartial = Run(24, "2026-09-08T10:00:00Z", "completed", 6471, scope: "partial");
        var olderFull = Run(21, "2026-09-07T10:00:00Z", "completed", 6573);

        var result = CollectionDataSourceCatalog.Build([olderFull, newestPartial]);

        Assert.Equal(24, result.CurrentRun!.Id);
        Assert.Equal([24L, 21L], result.HistoricalRuns.Select(run => run.Id));
    }

    [Fact]
    public void KeepsNeedsReviewRunAsCurrentDataSourceSoItMatchesTheDatabase()
    {
        var needsReview = Run(24, "2026-09-08T10:00:00Z", "needs_review", 6471);
        var olderComplete = Run(21, "2026-09-07T10:00:00Z", "completed", 6573);

        var result = CollectionDataSourceCatalog.Build([needsReview, olderComplete]);

        Assert.Equal(24, result.CurrentRun!.Id);
        Assert.Equal("需复核", result.CurrentRun.StatusLabel);
        Assert.Equal([24L, 21L], result.HistoricalRuns.Select(run => run.Id));
    }

    [Fact]
    public void CompletionTimeWinsWhenImportHappensLater()
    {
        var importedLater = Run(30, "2026-09-16T15:23:16+08:00", "completed", 6573)
            with { ImportedAt = "2026-09-16T21:49:29+08:00" };
        var importedEarlier = Run(31, "2026-09-16T21:00:00+08:00", "completed", 6573)
            with { ImportedAt = "2026-09-16T21:10:00+08:00" };

        var result = CollectionDataSourceCatalog.Build([importedLater, importedEarlier]);

        Assert.Equal(31, result.CurrentRun!.Id);
    }

    private static CollectionRunRecord Run(long id, string completedAt, string status, int cards, string scope = "full") =>
        new(
            id,
            $"run_{id}",
            completedAt,
            completedAt,
            completedAt,
            status,
            scope,
            ["1号", "2号", "3号", "4号", "5号", "6号"],
            string.Empty,
            string.Empty,
            cards,
            0,
            cards,
            0,
            0,
            "{\"summary\":{}}",
            false,
            string.Empty,
            cards);
}
