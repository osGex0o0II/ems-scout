using EmsScout.Application.Devices;
using EmsScout.Domain;

namespace EmsScout.Tests;

public sealed class AreaGroupDisplayTests
{
    [Fact]
    public void UnmatchedDeviceUsesDashAndOverlappingGroupsUseStableNames()
    {
        var unmatched = TestDevice() with { AreaGroups = null };
        var overlapping = TestDevice() with { AreaGroups = ["公区", "重点巡检"] };

        Assert.Equal("-", unmatched.AreaGroupText);
        Assert.Equal("公区 / 重点巡检", overlapping.AreaGroupText);
    }

    private static DeviceRecord TestDevice()
    {
        return new DeviceRecord(
            Id: 1,
            Building: "1号",
            Floor: 1,
            FloorLabel: "1F",
            SubArea: "1F A",
            X: 10,
            Y: 20,
            PageName: "default",
            Name: "GQ-0101-KT",
            Layout: "grid",
            SwitchState: "OFF",
            Mode: "制冷",
            IndoorTemperature: "26",
            SetTemperature: "24",
            Fan: "中",
            Indicator: string.Empty,
            CommunicationText: "关机",
            CommunicationState: DeviceCommunicationState.Stopped);
    }
}
