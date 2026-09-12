using EmsScout.Application.Collection;

namespace EmsScout.Tests;

public sealed class CollectionDataSourceCatalogTests
{
    [Fact]
    public void KeepsCurrentDatabaseSeparateWhenNewestRunFailsQualityGate()
    {
        var failedNewest = Run(24, "2026-09-08T10:00:00Z", "failed", 6471);
        var olderComplete = Run(21, "2026-09-07T10:00:00Z", "completed", 6573);

        var result = CollectionDataSourceCatalog.Build([failedNewest, olderComplete]);

        Assert.Equal(21, result.CurrentRun!.Id);
        Assert.DoesNotContain(result.HistoricalRuns, run => run.Id == result.CurrentRun.Id);
    }

    [Fact]
    public void UsesNewestCompletedRunAsCurrentEvenWhenItIsNotACompleteFleetSnapshot()
    {
        var newestPartial = Run(24, "2026-09-08T10:00:00Z", "completed", 6471, scope: "partial");
        var olderFull = Run(21, "2026-09-07T10:00:00Z", "completed", 6573);

        var result = CollectionDataSourceCatalog.Build([olderFull, newestPartial]);

        Assert.Equal(24, result.CurrentRun!.Id);
        Assert.Single(result.HistoricalRuns);
        Assert.Equal(21, result.HistoricalRuns[0].Id);
    }

    [Fact]
    public void KeepsNeedsReviewRunAsCurrentDataSourceSoItMatchesTheDatabase()
    {
        var needsReview = Run(24, "2026-09-08T10:00:00Z", "needs_review", 6471);
        var olderComplete = Run(21, "2026-09-07T10:00:00Z", "completed", 6573);

        var result = CollectionDataSourceCatalog.Build([needsReview, olderComplete]);

        Assert.Equal(24, result.CurrentRun!.Id);
        Assert.Equal("需复核", result.CurrentRun.StatusLabel);
        Assert.Single(result.HistoricalRuns);
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
