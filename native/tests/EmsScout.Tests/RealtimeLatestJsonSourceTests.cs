using EmsScout.Infrastructure.Sqlite;
using EmsScout.Infrastructure.Realtime;

namespace EmsScout.Tests;

public sealed class RealtimeLatestJsonSourceTests
{
    private static readonly string[] Buildings = ["1号", "2号", "3号", "4号", "5号", "6号"];

    [Fact]
    public async Task LoadsRealtimeLatestFilesWithSelfConsistentCounts()
    {
        var source = CurrentRealtimeSource();

        var details = await source.LoadAsync(Buildings);

        Assert.True(details.Rows.Count > 0);
        Assert.Contains(details.Rows, row => !string.IsNullOrWhiteSpace(row.LockState) && !row.LockStateValid);
        Assert.Equal(details.Rows.Count, details.Rows.Count(row => row.PointsComplete) + details.Rows.Count(row => !row.PointsComplete));
        Assert.All(Buildings, building => Assert.Contains(details.Rows, row => row.Building == building));
    }

    [Fact]
    public async Task AttachesRealtimeDetailsToCurrentDatabaseRows()
    {
        var root = LocateRepositoryRoot();
        var repository = new SqliteDeviceReadRepository(
            Path.Combine(root, "out", "ac.db"),
            CurrentRealtimeSource());

        var result = await repository.SearchAsync(new(Limit: 50000));

        Assert.True(result.Total > 0);
        Assert.True(result.Facets.RealtimeRows > 0);
        Assert.Equal(result.Total, result.Facets.RealtimeMatched + result.Facets.RealtimeMissing);
        Assert.True(result.Facets.RealtimeUnmatched >= 0);
        Assert.Equal(result.Rows.Count(row => row.RealtimeLocked), result.Facets.RealtimeLocked);
        Assert.Equal(result.Total, result.Facets.RealtimePointsComplete + result.Facets.RealtimePointsIncomplete);
        Assert.Equal(result.Rows.Count(row => row.Realtime?.IsInvalid == true), result.Facets.RealtimeInvalid);
        Assert.Equal(2, result.Facets.VirtualManaged);
        Assert.True(result.Facets.ManualOverrides > 0);
        Assert.Equal("已匹配", result.Rows[0].RealtimeMatchLabel);
        Assert.NotNull(result.Rows[0].Realtime);
    }

    [Fact]
    public async Task AppliesRealtimeMatchOverridesAndVirtualManagedDevices()
    {
        var root = LocateRepositoryRoot();
        var repository = new SqliteDeviceReadRepository(
            Path.Combine(root, "out", "ac.db"),
            CurrentRealtimeSource());

        var virtualDevice = await repository.SearchAsync(new(SearchText: "2F-HTDTT-KT-2", Limit: 5));
        var manualDevices = await repository.SearchAsync(new(RealtimeMatch: "manual", Limit: 5));

        Assert.Equal(1, virtualDevice.Total);
        Assert.Equal(-10, virtualDevice.Rows[0].Id);
        Assert.True(virtualDevice.Rows[0].IsVirtual);
        Assert.Equal("虚拟纳管", virtualDevice.Rows[0].RealtimeMatchLabel);
        Assert.Equal("create_virtual", virtualDevice.Rows[0].MatchOverrideAction);
        Assert.Equal("公区", virtualDevice.Rows[0].AreaType);
        Assert.Equal("20008942", virtualDevice.Rows[0].Realtime?.DevId);

        Assert.True(manualDevices.Total > 0);
        Assert.Contains(manualDevices.Rows, row =>
            !row.IsVirtual &&
            row.RealtimeMatchLabel == "手动匹配" &&
            row.MatchOverrideAction == "map_to_db" &&
            row.Realtime is not null);
        Assert.All(manualDevices.Rows, row =>
        {
            Assert.True(row.HasManualOverride);
        });
    }

    [Fact]
    public async Task AppliesNativeDataWorkbenchFilters()
    {
        var root = LocateRepositoryRoot();
        var repository = new SqliteDeviceReadRepository(
            Path.Combine(root, "out", "ac.db"),
            CurrentRealtimeSource());

        Assert.Equal(10, (await repository.SearchAsync(new(Floor: "2.5F", Limit: 1))).Total);
        Assert.Equal(24, (await repository.SearchAsync(new(Floor: "B1F", Limit: 1))).Total);
        Assert.Equal(23, (await repository.SearchAsync(new(Building: "5号", Zuo: "A座", Limit: 1))).Total);
        Assert.Equal(889, (await repository.SearchAsync(new(Building: "6号", Zuo: "C座", Limit: 1))).Total);

        var matched = await repository.SearchAsync(new(RealtimeMatch: "matched", Limit: 1));
        var missing = await repository.SearchAsync(new(RealtimeMatch: "missing", Limit: 1));
        var all = await repository.SearchAsync(new(Limit: 50000));
        Assert.Equal(all.Total, matched.Total + missing.Total);
        Assert.True((await repository.SearchAsync(new(RealtimeMatch: "invalid", Limit: 1))).Total > 0);
        Assert.True((await repository.SearchAsync(new(RealtimeMatch: "manual", Limit: 1))).Total > 0);
        Assert.Equal(2, (await repository.SearchAsync(new(RealtimeMatch: "virtual", Limit: 1))).Total);

        var completePoints = await repository.SearchAsync(new(RealtimePoints: "complete", Limit: 1));
        var incompletePoints = await repository.SearchAsync(new(RealtimePoints: "incomplete", Limit: 1));
        var missingPoints = await repository.SearchAsync(new(RealtimePoints: "missing", Limit: 1));
        Assert.Equal(all.Total, completePoints.Total + incompletePoints.Total);
        Assert.True(missingPoints.Total <= incompletePoints.Total);
        Assert.Equal(1, (await repository.SearchAsync(new(SearchText: "2F-HTDTT-KT-2", Limit: 1))).Total);
    }

    private static RealtimeLatestJsonSource CurrentRealtimeSource()
    {
        var root = LocateRepositoryRoot();
        return new RealtimeLatestJsonSource(root, Path.Combine(root, "out"));
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
