using EmsScout.Application.Collection;

namespace EmsScout.Tests;

public sealed class CollectionRunMetadataTests
{
    [Fact]
    public void LegacyRunUsesStableMetadataDefaults()
    {
        var run = CreateRun();

        var metadata = CollectionRunMetadata.From(run, isCurrent: true);

        Assert.Equal("采集导入", metadata.Source);
        Assert.Equal("v1.0.0", metadata.DataVersion);
        Assert.Equal("本机", metadata.Operator);
        Assert.True(metadata.IsCurrent);
    }

    [Fact]
    public void FilterMatchesBuildingAndKeywordWithoutChangingTheSourceCollection()
    {
        var run = CreateRun(buildings: ["1号", "6号"], note: "夜间复核");
        var filter = new CollectionRunFilter(
            Building: "6号",
            Keyword: "夜间");

        Assert.True(filter.Matches(run));
        Assert.False((filter with { Building = "2号" }).Matches(run));
        Assert.False((filter with { Keyword = "不存在" }).Matches(run));
    }

    [Fact]
    public void ShowsNeedsReviewWhenCompletedRunContainsBlockingQualityFindings()
    {
        var run = CreateRun() with
        {
            QualitySummary = "{\"summary\":{\"invalid_card_fields\":1472}}"
        };

        Assert.Equal("需复核", run.StatusLabel);
    }

    private static CollectionRunRecord CreateRun(
        IReadOnlyList<string>? buildings = null,
        string note = "") =>
        new(
            1,
            "run_1",
            "2026-09-08T10:00:00Z",
            "2026-09-08T10:05:00Z",
            "2026-09-08T10:06:00Z",
            "completed",
            "partial",
            buildings ?? ["1号"],
            string.Empty,
            string.Empty,
            10,
            1,
            8,
            1,
            0,
            "{}",
            false,
            note,
            10);
}
