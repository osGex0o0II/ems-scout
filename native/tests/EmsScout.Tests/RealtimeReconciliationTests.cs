using EmsScout.Application.Devices;
using EmsScout.Infrastructure.Realtime;
using EmsScout.Infrastructure.Sqlite;
using System.IO.Compression;

namespace EmsScout.Tests;

public sealed class RealtimeReconciliationTests
{
    [Fact]
    public async Task BuildsRealtimeSourceParitySummary()
    {
        var service = CurrentService();

        var result = await service.AnalyzeAsync(new(Limit: 10));

        Assert.True(result.Summary.DbCount > 0);
        Assert.True(result.Summary.RealtimeCount > 0);
        Assert.Equal(result.Summary.RealtimeCount - result.Summary.DbCount, result.Summary.Difference);
        Assert.Equal(result.Summary.ByType.Values.Sum(), result.Summary.DiffItemCount);
        Assert.True(result.Summary.ExactMatches > 0);
        Assert.True(result.Summary.ManualMatches > 0);
        Assert.True(result.Summary.RelaxedMatches > 0);
        Assert.True(result.Summary.OverrideCount > 0);
        Assert.True(result.Summary.ByType[RealtimeReconciliationTypes.NewDevice] > 0);
        Assert.True(result.Summary.ByType[RealtimeReconciliationTypes.MissingInRealtime] > 0);
        Assert.True(result.Summary.ByType[RealtimeReconciliationTypes.MatchFailed] > 0);
        Assert.True(result.Summary.ByType[RealtimeReconciliationTypes.VirtualOverride] > 0);
        Assert.True(result.Summary.ByType[RealtimeReconciliationTypes.DataNoise] > 0);
        Assert.NotNull(result.Summary.SourceUpdatedAt);
        Assert.True(result.Summary.SourceUpdatedAt <= result.Summary.GeneratedAt);
        Assert.Equal(10, result.Items.Count);
        Assert.Equal(RealtimeReconciliationTypes.NewDevice, result.Items[0].Type);
        Assert.False(string.IsNullOrWhiteSpace(result.Items[0].Name));
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

        var manual = Assert.Single(search.Items, item => item.Type == RealtimeReconciliationTypes.MatchFailed);
        Assert.Equal("6F-619E-KT", manual.Name);
        Assert.Equal("20009772", manual.DevId);
        Assert.Contains("override", manual.EvidenceSummary);
        Assert.Contains(manual.DecisionPath, step => step.Contains("realtime_match_overrides"));
    }

    [Fact]
    public async Task BuildsDataNavigationTargetFromSelectedDiff()
    {
        var search = await CurrentService().AnalyzeAsync(new(SearchText: "20009772", Limit: 10));

        var target = DeviceNavigationTargetFactory.FromReconciliationItem(
            search.Items.Single(item => item.Type == RealtimeReconciliationTypes.MatchFailed));

        Assert.Equal("6F-619E-KT", target.SearchText);
        Assert.Equal("6号", target.Building);
        Assert.Equal("manual", target.RealtimeMatch);
    }

    [Fact]
    public async Task NavigationTargetFindsValidVirtualDeviceInDataWorkbench()
    {
        var root = LocateRepositoryRoot();
        var repository = new SqliteDeviceReadRepository(
            Path.Combine(root, "out", "ac.db"),
            new RealtimeLatestJsonSource(root, Path.Combine(root, "out")));
        var search = await CurrentService().AnalyzeAsync(new(
            DiffType: RealtimeReconciliationTypes.VirtualOverride,
            Limit: 10));
        var target = DeviceNavigationTargetFactory.FromReconciliationItem(search.Items[0]);

        var result = await repository.SearchAsync(new DeviceQuery(
            DeviceName: target.SearchText,
            Building: target.Building,
            RealtimeMatch: target.RealtimeMatch,
            Limit: 10));

        Assert.NotEmpty(result.Rows);
        Assert.All(result.Rows, row => Assert.Equal(target.SearchText, row.Name));
    }

    private static SqliteRealtimeReconciliationService CurrentService()
    {
        var root = LocateRepositoryRoot();
        return new SqliteRealtimeReconciliationService(
            Path.Combine(root, "out", "ac.db"),
            new RealtimeLatestJsonSource(root, Path.Combine(root, "out")));
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "out")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }

}
