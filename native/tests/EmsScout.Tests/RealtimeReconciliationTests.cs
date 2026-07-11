using EmsScout.Application.Devices;
using EmsScout.Infrastructure.Sqlite;
using EmsScout.Infrastructure.Realtime;
using System.IO.Compression;

namespace EmsScout.Tests;

[Trait("Fixture", "ProductionEvidence")]
public sealed class RealtimeReconciliationTests
{
    [Fact]
    public async Task BuildsRealtimeSourceParitySummary()
    {
        var service = CurrentService();

        var result = await service.AnalyzeAsync(new(Limit: 10));

        Assert.Equal(6568, result.Summary.DbCount);
        Assert.Equal(6575, result.Summary.RealtimeCount);
        Assert.Equal(7, result.Summary.Difference);
        Assert.Equal(43, result.Summary.DiffItemCount);
        Assert.Equal(6439, result.Summary.ExactMatches);
        Assert.Equal(5, result.Summary.ManualMatches);
        Assert.Equal(111, result.Summary.RelaxedMatches);
        Assert.Equal(7, result.Summary.OverrideCount);
        Assert.Equal(16, result.Summary.ByType[RealtimeReconciliationTypes.NewDevice]);
        Assert.Equal(18, result.Summary.ByType[RealtimeReconciliationTypes.MissingInRealtime]);
        Assert.Equal(5, result.Summary.ByType[RealtimeReconciliationTypes.MatchFailed]);
        Assert.Equal(2, result.Summary.ByType[RealtimeReconciliationTypes.VirtualOverride]);
        Assert.Equal(2, result.Summary.ByType[RealtimeReconciliationTypes.DataNoise]);
        Assert.DoesNotContain(RealtimeReconciliationTypes.DuplicateRender, result.Summary.ByType.Keys);
        Assert.NotNull(result.Summary.SourceUpdatedAt);
        Assert.True(result.Summary.SourceUpdatedAt <= result.Summary.GeneratedAt);
        Assert.Equal(10, result.Items.Count);
        Assert.Equal(RealtimeReconciliationTypes.NewDevice, result.Items[0].Type);
        Assert.Equal("BM-GQ-KT-1", result.Items[0].Name);
        Assert.All(result.Items, item =>
        {
            Assert.Equal(RealtimeReconciliationTypes.RuleVersion, item.RuleVersion);
            Assert.InRange(item.Confidence, 0.35, 0.95);
            Assert.False(string.IsNullOrWhiteSpace(item.RuleDescription));
            Assert.False(string.IsNullOrWhiteSpace(item.EvidenceSummary));
            Assert.Contains("归因结果", item.DecisionPath.Last());
        });
    }

    [Fact]
    public async Task FiltersRealtimeSourceParityItems()
    {
        var service = CurrentService();

        var virtualRows = await service.AnalyzeAsync(new(
            DiffType: RealtimeReconciliationTypes.VirtualOverride,
            Limit: 10));
        var search = await service.AnalyzeAsync(new(
            SearchText: "20009772",
            Limit: 10));

        Assert.Equal(2, virtualRows.Items.Count);
        Assert.Contains(virtualRows.Items, item => item.Name == "2F-HTDTT-KT-2");
        Assert.All(virtualRows.Items, item => Assert.Equal(RealtimeReconciliationTypes.VirtualOverride, item.Type));

        Assert.Single(search.Items);
        Assert.Equal(RealtimeReconciliationTypes.MatchFailed, search.Items[0].Type);
        Assert.Equal("6F-619E-KT", search.Items[0].Name);
        Assert.Equal("20009772", search.Items[0].DevId);
        Assert.Contains("override", search.Items[0].EvidenceSummary);
        Assert.Contains(search.Items[0].DecisionPath, step => step.Contains("realtime_match_overrides"));
    }

    [Fact]
    public async Task BuildsDataNavigationTargetFromSelectedDiff()
    {
        var search = await CurrentService().AnalyzeAsync(new(SearchText: "20009772", Limit: 10));

        var target = DeviceNavigationTargetFactory.FromReconciliationItem(search.Items.Single());

        Assert.Equal("6F-619E-KT", target.SearchText);
        Assert.Equal("6号", target.Building);
        Assert.Equal("manual", target.RealtimeMatch);
    }

    [Fact]
    public async Task NavigationTargetFindsDeviceInDataWorkbench()
    {
        var root = ProductionDataSnapshot.RepositoryRoot;
        var repository = new SqliteDeviceReadRepository(
            ProductionDataSnapshot.DatabasePath,
            new RealtimeLatestJsonSource(root, Path.Combine(root, "out")));
        var search = await CurrentService().AnalyzeAsync(new(SearchText: "20009772", Limit: 10));
        var target = DeviceNavigationTargetFactory.FromReconciliationItem(search.Items.Single());

        var result = await repository.SearchAsync(new DeviceQuery(
            DeviceName: target.SearchText,
            Building: target.Building,
            Limit: 10));

        Assert.Single(result.Rows);
        Assert.Equal("6F-619E-KT", result.Rows[0].Name);
    }

    private static SqliteRealtimeReconciliationService CurrentService()
    {
        var root = ProductionDataSnapshot.RepositoryRoot;
        return new SqliteRealtimeReconciliationService(
            ProductionDataSnapshot.DatabasePath,
            new RealtimeLatestJsonSource(root, Path.Combine(root, "out")));
    }

}
