using EmsScout.Application.Devices;

namespace EmsScout.Tests;

public sealed class DeviceDataQuerySnapshotTests
{
    [Fact]
    public void RejectsOlderResponsesAndKeepsTheNewestSuccessfulQuery()
    {
        var session = new DeviceDataQuerySession();
        var first = session.Begin(new DeviceQuery(Building: "1号", QuickFilter: "offline"));
        var second = session.Begin(new DeviceQuery(Building: "2号", QuickFilter: "unknown"));

        Assert.True(second.Version > first.Version);
        Assert.False(session.TryAccept(first, Result(total: 7)));
        Assert.Null(session.SuccessfulQuery);

        Assert.True(session.TryAccept(second, Result(total: 3)));
        Assert.Equal("2号", session.SuccessfulQuery?.Building);
        Assert.Equal("unknown", session.SuccessfulQuery?.QuickFilter);
        Assert.Equal(3, session.SuccessfulTotal);
    }

    [Fact]
    public void QuickFiltersExposeStableKeysLabelsAndFacetCounts()
    {
        var facets = new DeviceFacets(
            Total: 20,
            Running: 4,
            Stopped: 6,
            Offline: 5,
            Unknown: 2,
            PublicArea: 8,
            PrivateArea: 12,
            TemperatureIssues: 3,
            NeedsReview: 9,
            RealtimeMissing: 7);

        var filters = DeviceQuickFilterCatalog.Create(facets, activeKey: "temp_abnormal");

        Assert.Collection(
            filters,
            item => Assert.Equal(("offline", "离线", 5), (item.Key, item.Label, item.Count)),
            item => Assert.Equal(("unknown", "未知", 2), (item.Key, item.Label, item.Count)),
            item => Assert.Equal(("temp_abnormal", "温度异常", 3), (item.Key, item.Label, item.Count)),
            item => Assert.Equal(("realtime_missing", "无实时数据", 7), (item.Key, item.Label, item.Count)),
            item => Assert.Equal(("needs_review", "需关注", 9), (item.Key, item.Label, item.Count)));
        Assert.False(filters[0].IsActive);
        Assert.False(filters[1].IsActive);
        Assert.True(filters[2].IsActive);
        Assert.False(filters[3].IsActive);
        Assert.False(filters[4].IsActive);
    }

    private static DeviceListResult Result(int total)
    {
        return new DeviceListResult(total, [], DeviceFacets.From([]));
    }
}
