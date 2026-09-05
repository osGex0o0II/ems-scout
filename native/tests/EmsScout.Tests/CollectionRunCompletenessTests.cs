using EmsScout.Application.Collection;

namespace EmsScout.Tests;

public sealed class CollectionRunCompletenessTests
{
    [Fact]
    public void AcceptsOnlyCompletedFullSnapshotsCoveringAllBuildings()
    {
        var run = CreateRun(
            status: "completed",
            scope: "full",
            buildings: ["1号", "2号", "3号", "4号", "5号", "6号"],
            cardCount: 6471,
            snapshotCardCount: 6471);

        Assert.True(CollectionRunCompleteness.IsCompleteFleetSnapshot(run));
    }

    [Theory]
    [InlineData("partial", "completed")]
    [InlineData("full", "failed")]
    public void RejectsNonFullOrIncompleteRuns(string scope, string status)
    {
        var run = CreateRun(
            status,
            scope,
            ["1号", "2号", "3号", "4号", "5号", "6号"],
            6471,
            6471);

        Assert.False(CollectionRunCompleteness.IsCompleteFleetSnapshot(run));
    }

    [Fact]
    public void RejectsFullRunWithMissingBuildingOrSnapshotCards()
    {
        var missingBuilding = CreateRun(
            "completed",
            "full",
            ["1号", "2号", "3号", "4号", "5号"],
            6471,
            6471);
        var incompleteSnapshot = CreateRun(
            "completed",
            "full",
            ["1号", "2号", "3号", "4号", "5号", "6号"],
            6471,
            3096);

        Assert.False(CollectionRunCompleteness.IsCompleteFleetSnapshot(missingBuilding));
        Assert.False(CollectionRunCompleteness.IsCompleteFleetSnapshot(incompleteSnapshot));
    }

    private static CollectionRunRecord CreateRun(
        string status,
        string scope,
        IReadOnlyList<string> buildings,
        int cardCount,
        int snapshotCardCount) => new(
        Id: 1,
        RunKey: "test",
        StartedAt: "2026-08-31T00:00:00Z",
        CompletedAt: "2026-08-31T00:00:00Z",
        ImportedAt: "2026-08-31T00:00:00Z",
        Status: status,
        Scope: scope,
        Buildings: buildings,
        JsonPath: string.Empty,
        DbSnapshotPath: string.Empty,
        CardCount: cardCount,
        OnCount: 0,
        OffCount: 0,
        OfflineCount: 0,
        UnknownCount: 0,
        QualitySummary: "{}",
        IsAnomaly: false,
        Note: string.Empty,
        SnapshotCardCount: snapshotCardCount);
}
