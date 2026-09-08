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

    [Fact]
    public void RejectsAnomalousRunAndStructuralQualityFailures()
    {
        var anomalous = CreateRun(
            "completed",
            "full",
            ["1号", "2号", "3号", "4号", "5号", "6号"],
            6471,
            6471,
            isAnomaly: true);
        var structurallyInvalid = CreateRun(
            "completed",
            "full",
            ["1号", "2号", "3号", "4号", "5号", "6号"],
            6471,
            6471,
            qualitySummary: "{\"summary\":{\"baseline_delta\":2,\"empty_sub_areas\":1}}");

        Assert.False(CollectionRunCompleteness.IsCompleteFleetSnapshot(anomalous));
        Assert.False(CollectionRunCompleteness.IsCompleteFleetSnapshot(structurallyInvalid));
    }

    [Fact]
    public void RejectsFullSnapshotWithBaselineDelta()
    {
        var run = CreateRun(
            status: "completed",
            scope: "full",
            buildings: ["1号", "2号", "3号", "4号", "5号", "6号"],
            cardCount: 6573,
            snapshotCardCount: 6573,
            qualitySummary: "{\"summary\":{\"baseline_delta\":1}}");

        Assert.False(CollectionRunCompleteness.IsCompleteFleetSnapshot(run));
    }

    [Theory]
    [InlineData("invalid_card_fields")]
    [InlineData("active_field_incomplete_pages")]
    [InlineData("offline_template_without_stability")]
    public void RejectsFullSnapshotWithBlockingQualityFindings(string qualityCode)
    {
        var run = CreateRun(
            status: "completed",
            scope: "full",
            buildings: ["1号", "2号", "3号", "4号", "5号", "6号"],
            cardCount: 6471,
            snapshotCardCount: 6471,
            qualitySummary: $"{{\"summary\":{{\"{qualityCode}\":1}}}}");

        Assert.False(CollectionRunCompleteness.IsCompleteFleetSnapshot(run));
    }

    [Fact]
    public void AcceptsKnownFindingWhenQualityReportHasRemovedItFromBlockingSummary()
    {
        var run = CreateRun(
            status: "completed",
            scope: "full",
            buildings: ["1号", "2号", "3号", "4号", "5号", "6号"],
            cardCount: 6471,
            snapshotCardCount: 6471,
            qualitySummary: "{\"summary\":{\"invalid_card_fields\":0,\"known_findings\":1}}");

        Assert.True(CollectionRunCompleteness.IsCompleteFleetSnapshot(run));
    }

    private static CollectionRunRecord CreateRun(
        string status,
        string scope,
        IReadOnlyList<string> buildings,
        int cardCount,
        int snapshotCardCount,
        string qualitySummary = "{}",
        bool isAnomaly = false) => new(
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
        QualitySummary: qualitySummary,
        IsAnomaly: isAnomaly,
        Note: string.Empty,
        SnapshotCardCount: snapshotCardCount);
}
