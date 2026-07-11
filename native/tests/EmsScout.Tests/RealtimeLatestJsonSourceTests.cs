using EmsScout.Infrastructure.Sqlite;
using EmsScout.Infrastructure.Realtime;

namespace EmsScout.Tests;

public sealed class RealtimeLatestJsonSourceTests
{
    private static readonly string[] Buildings = ["1号", "2号", "3号", "4号", "5号", "6号"];

    [Fact]
    public async Task LoadsRealtimeLatestFilesWithBaselineCounts()
    {
        var source = CurrentRealtimeSource();

        var details = await source.LoadAsync(Buildings);

        Assert.Equal(6575, details.Rows.Count);
        Assert.Equal(1481, details.Rows.Count(row => row.LockState == "开启"));
        Assert.Equal(5031, details.Rows.Count(row => row.PointsComplete));
        Assert.Equal(1544, details.Rows.Count(row => !row.PointsComplete));
        Assert.Equal(1544, details.Rows.Count(row => row.IsInvalid));
        Assert.Equal(1493, details.Rows.Count(row => row.Building == "1号"));
        Assert.Equal(2482, details.Rows.Count(row => row.Building == "6号"));
    }

    [Fact]
    public async Task AttachesRealtimeDetailsToCurrentDatabaseRows()
    {
        var root = ProductionDataSnapshot.RepositoryRoot;
        var repository = new SqliteDeviceReadRepository(
            ProductionDataSnapshot.DatabasePath,
            CurrentRealtimeSource());

        var result = await repository.SearchAsync(new(Limit: 10));

        Assert.Equal(6570, result.Total);
        Assert.Equal(6575, result.Facets.RealtimeRows);
        Assert.Equal(6565, result.Facets.RealtimeMatched);
        Assert.Equal(5, result.Facets.RealtimeMissing);
        Assert.Equal(10, result.Facets.RealtimeUnmatched);
        Assert.Equal(1477, result.Facets.RealtimeLocked);
        Assert.Equal(5023, result.Facets.RealtimePointsComplete);
        Assert.Equal(1547, result.Facets.RealtimePointsIncomplete);
        Assert.Equal(1542, result.Facets.RealtimeInvalid);
        Assert.Equal(2, result.Facets.VirtualManaged);
        Assert.Equal(7, result.Facets.ManualOverrides);
        Assert.Equal("已匹配", result.Rows[0].RealtimeMatchLabel);
        Assert.Equal("开启", result.Rows[0].Realtime?.LockState);
    }

    [Fact]
    public async Task AppliesRealtimeMatchOverridesAndVirtualManagedDevices()
    {
        var root = ProductionDataSnapshot.RepositoryRoot;
        var repository = new SqliteDeviceReadRepository(
            ProductionDataSnapshot.DatabasePath,
            CurrentRealtimeSource());

        var virtualDevice = await repository.SearchAsync(new(SearchText: "2F-HTDTT-KT-2", Limit: 5));
        var manualDevice = await repository.SearchAsync(new(SearchText: "20009772", Limit: 5));

        Assert.Equal(1, virtualDevice.Total);
        Assert.Equal(-10, virtualDevice.Rows[0].Id);
        Assert.True(virtualDevice.Rows[0].IsVirtual);
        Assert.Equal("虚拟纳管", virtualDevice.Rows[0].RealtimeMatchLabel);
        Assert.Equal("create_virtual", virtualDevice.Rows[0].MatchOverrideAction);
        Assert.Equal("公区", virtualDevice.Rows[0].AreaType);
        Assert.Equal("20008942", virtualDevice.Rows[0].Realtime?.DevId);

        Assert.Equal(1, manualDevice.Total);
        Assert.False(manualDevice.Rows[0].IsVirtual);
        Assert.Equal("手动匹配", manualDevice.Rows[0].RealtimeMatchLabel);
        Assert.Equal("map_to_db", manualDevice.Rows[0].MatchOverrideAction);
        Assert.Equal("20009772", manualDevice.Rows[0].Realtime?.DevId);
        Assert.Contains("按同名同楼层映射", manualDevice.Rows[0].MatchOverrideNote);
    }

    [Fact]
    public async Task AppliesNativeDataWorkbenchFilters()
    {
        var root = ProductionDataSnapshot.RepositoryRoot;
        var repository = new SqliteDeviceReadRepository(
            ProductionDataSnapshot.DatabasePath,
            CurrentRealtimeSource());

        Assert.Equal(7, (await repository.SearchAsync(new(Floor: "2.5F", Limit: 1))).Total);
        Assert.Equal(24, (await repository.SearchAsync(new(Floor: "B1F", Limit: 1))).Total);
        Assert.Equal(23, (await repository.SearchAsync(new(Building: "5号", Zuo: "A座", Limit: 1))).Total);
        Assert.Equal(889, (await repository.SearchAsync(new(Building: "6号", Zuo: "C座", Limit: 1))).Total);

        Assert.Equal(6565, (await repository.SearchAsync(new(RealtimeMatch: "matched", Limit: 1))).Total);
        Assert.Equal(5, (await repository.SearchAsync(new(RealtimeMatch: "missing", Limit: 1))).Total);
        Assert.Equal(1542, (await repository.SearchAsync(new(RealtimeMatch: "invalid", Limit: 1))).Total);
        Assert.Equal(7, (await repository.SearchAsync(new(RealtimeMatch: "manual", Limit: 1))).Total);
        Assert.Equal(2, (await repository.SearchAsync(new(RealtimeMatch: "virtual", Limit: 1))).Total);

        Assert.Equal(5023, (await repository.SearchAsync(new(RealtimePoints: "complete", Limit: 1))).Total);
        Assert.Equal(1547, (await repository.SearchAsync(new(RealtimePoints: "incomplete", Limit: 1))).Total);
        Assert.Equal(5, (await repository.SearchAsync(new(RealtimePoints: "missing", Limit: 1))).Total);
        Assert.Equal(1, (await repository.SearchAsync(new(SearchText: "20009772", Limit: 1))).Total);
    }

    private static RealtimeLatestJsonSource CurrentRealtimeSource()
    {
        var root = ProductionDataSnapshot.RepositoryRoot;
        return new RealtimeLatestJsonSource(root, Path.Combine(root, "out"));
    }
}
