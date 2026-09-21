using EmsScout.Application.Devices;
using EmsScout.Domain;

namespace EmsScout.Tests;

public sealed class DashboardAreaGroupMetricRulesTests
{
    [Fact]
    public void MetricRulesMatchTheSameBucketsShownByAreaGroupSummary()
    {
        var running = Device("开机", DeviceCommunicationState.Running, "制冷", "25", "开启");
        var stopped = Device("关机", DeviceCommunicationState.Stopped, "制冷", "25", "关闭");
        var offline = Device("离线", DeviceCommunicationState.Offline, "制热", "10", "");
        var modeAbnormal = Device("开机", DeviceCommunicationState.Running, "制热", "25", "关闭");
        var tempAbnormal = Device("关机", DeviceCommunicationState.Stopped, "制冷", "27", "关闭");

        Assert.True(DashboardAreaGroupMetricRules.Matches(running, "online", "制冷", 22, 26));
        Assert.True(DashboardAreaGroupMetricRules.Matches(stopped, "online", "制冷", 22, 26));
        Assert.False(DashboardAreaGroupMetricRules.Matches(offline, "online", "制冷", 22, 26));
        Assert.True(DashboardAreaGroupMetricRules.Matches(offline, "offline", "制冷", 22, 26));
        Assert.True(DashboardAreaGroupMetricRules.Matches(modeAbnormal, "mode_abnormal", "制冷", 22, 26));
        Assert.True(DashboardAreaGroupMetricRules.Matches(tempAbnormal, "temperature_abnormal", "制冷", 22, 26));
        Assert.True(DashboardAreaGroupMetricRules.Matches(running, "lock_on", "制冷", 22, 26));
        Assert.True(DashboardAreaGroupMetricRules.Matches(stopped, "lock_off", "制冷", 22, 26));
        Assert.False(DashboardAreaGroupMetricRules.Matches(offline, "temperature_abnormal", "制冷", 22, 26));
    }

    [Fact]
    public void DashboardMetricKeepsTheOtherDataFiltersInTheQuery()
    {
        var record = Device("开机", DeviceCommunicationState.Running, "制冷", "25", "开启");

        Assert.False(DeviceQuerySpecification.MatchesResult(
            record,
            new DeviceQuery(Building: "2号", QuickFilter: "online")));
        Assert.True(DeviceQuerySpecification.MatchesResult(
            record,
            new DeviceQuery(Building: "1号", QuickFilter: "online")));
    }

    private static DeviceRecord Device(
        string communicationText,
        DeviceCommunicationState communicationState,
        string mode,
        string setTemperature,
        string lockState)
    {
        return new DeviceRecord(
            Id: Random.Shared.NextInt64(),
            Building: "1号",
            Floor: 1,
            FloorLabel: "1F",
            SubArea: "1F A",
            X: null,
            Y: null,
            PageName: "default",
            Name: "GQ-TEST-KT",
            Layout: "grid",
            SwitchState: communicationState == DeviceCommunicationState.Running ? "ON" : "OFF",
            Mode: mode,
            IndoorTemperature: "25",
            SetTemperature: setTemperature,
            Fan: "中",
            Indicator: "indicator",
            CommunicationText: communicationText,
            CommunicationState: communicationState,
            Realtime: communicationState == DeviceCommunicationState.Offline
                ? null
                : new RealtimeDetailRecord(
                    RowId: "row",
                    SourceFile: "fixture",
                    SourceUpdatedAt: DateTimeOffset.UtcNow,
                    Building: "1号",
                    Floor: 1,
                    SubArea: "1F A",
                    PageName: "default",
                    Name: "GQ-TEST-KT",
                    DevId: "dev",
                    MeterId: "meter",
                    RtuId: "rtu",
                    FieldCount: 1,
                    RealtimeTagCount: 1,
                    RealtimeValidTagCount: 1,
                    DefaultLike: false,
                    Error: string.Empty,
                    CardComm: communicationText,
                    CardSwitch: "ON",
                    CardIndicator: "indicator",
                    Fields: new Dictionary<string, string> { ["集控锁定"] = lockState },
                    ValidFields: new Dictionary<string, bool> { ["集控锁定"] = true }));
    }
}
