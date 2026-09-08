using EmsScout.Application.Collection;

namespace EmsScout.Tests;

public sealed class CollectionRunComparisonTests
{
    [Fact]
    public void BuildsPerBuildingAddedAndMissingCounts()
    {
        var run = CreateRun();
        var comparison = CollectionRunComparison.Create(
            run,
            snapshotByBuilding: new Dictionary<string, int>
            {
                ["1号"] = 4,
                ["2号"] = 2,
            },
            currentByBuilding: new Dictionary<string, int>
            {
                ["1号"] = 5,
                ["2号"] = 1,
                ["3号"] = 3,
            },
            changedCount: 2,
            isRestorable: true,
            blockingReason: null);

        Assert.Equal(6, comparison.SnapshotCardCount);
        Assert.Equal(9, comparison.CurrentCardCount);
        Assert.Equal(4, comparison.AddedCount);
        Assert.Equal(1, comparison.MissingCount);
        Assert.Equal(2, comparison.ChangedCount);
        Assert.Contains(comparison.BuildingDifferences, row => row.Building == "3号" && row.AddedCount == 3);
    }

    [Fact]
    public void KeepsRestoreBlockedReasonExplicit()
    {
        var comparison = CollectionRunComparison.Create(
            CreateRun(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            changedCount: 0,
            isRestorable: false,
            blockingReason: "全量批次未覆盖 1-6 号楼");

        Assert.False(comparison.IsRestorable);
        Assert.Equal("全量批次未覆盖 1-6 号楼", comparison.BlockingReason);
    }

    private static CollectionRunRecord CreateRun() =>
        new(
            1,
            "run_1",
            "2026-09-08T10:00:00Z",
            "2026-09-08T10:05:00Z",
            "2026-09-08T10:06:00Z",
            "completed",
            "partial",
            ["1号"],
            string.Empty,
            string.Empty,
            6,
            0,
            6,
            0,
            0,
            "{}",
            false,
            string.Empty,
            6);
}
