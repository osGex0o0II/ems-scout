using EmsScout.Application;
using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Domain;

namespace EmsScout.Tests;

public sealed class DashboardAreaGroupBuilderTests
{
    [Fact]
    public void AggregatesAllDevicesAndPublicStatesForEnabledCustomGroups()
    {
        var enabled = Group(10, "未开放", enabled: true);
        var disabled = Group(11, "已停用", enabled: false);
        var system = Group(1, "公区", enabled: true, groupKind: "system", systemKey: "public");
        var set = new AreaGroupSet(
            [enabled, disabled, system],
            [FloorItem(enabled.Id, 1), FloorItem(disabled.Id, 1)]);
        var devices = new[]
        {
            Device(1, "1-0101-KT", 1, "1F A", DeviceCommunicationState.Running, layout: "group"),
            Device(2, "GQ-0102-KT", 1, "1F A", DeviceCommunicationState.Stopped),
            Device(3, "GQ-0103-KT", 1, "1F A", DeviceCommunicationState.Offline),
            Device(4, "GQ-0104-KT", 1, "1F A", DeviceCommunicationState.Unknown),
            Device(5, "QL-100-KT", 1, "1F A", DeviceCommunicationState.Running),
            Device(6, "1-0106-KT", 1, "1F B", DeviceCommunicationState.Running, areaTypeOverride: "公区", isVirtual: true),
            Device(7, "GQ-0201-KT", 2, "2F A", DeviceCommunicationState.Running),
        };

        var summary = Assert.Single(DashboardAreaGroupBuilder.Build(devices, set));

        Assert.Equal(enabled.Id, summary.Id);
        Assert.Equal(6, summary.Total);
        Assert.Equal(4, summary.Online);
        Assert.Equal(1, summary.Offline);
        Assert.Equal(1, summary.Unknown);
        Assert.Equal(3, summary.Running);
        Assert.Equal(1, summary.Stopped);
        Assert.Equal(2, summary.CoveredAreas);
        Assert.Equal(5, summary.PublicTotal);
        Assert.Equal(2, summary.PublicRunning);
        Assert.Equal(1, summary.PublicStopped);
        Assert.Equal(1, summary.PublicOffline);
        Assert.Equal(1, summary.PublicUnknown);
        Assert.Equal(2, summary.PublicCoveredAreas);
        Assert.Equal(
            summary.PublicTotal,
            summary.PublicRunning + summary.PublicStopped + summary.PublicOffline + summary.PublicUnknown);
        Assert.Equal(1, summary.PrivateTotal);
        Assert.Equal(1, summary.PrivateRunning);
        Assert.Equal(0, summary.PrivateStopped);
    }

    [Fact]
    public void MatchesDuplicateDeviceSuffixWithinTheConfiguredLocation()
    {
        var item = new AreaGroupItemRecord(
            Id: 1,
            GroupId: 10,
            GroupName: "复核区",
            TargetType: "device",
            Building: "1号",
            FloorLabel: "1F",
            FloorValue: 1,
            SubAreaText: "1F A",
            CardName: "GQ-DUP-KT",
            Note: string.Empty);

        Assert.True(DashboardAreaGroupBuilder.Matches(
            Device(1, "GQ-DUP-KT#1", 1, "1F A", DeviceCommunicationState.Running),
            item));
        Assert.False(DashboardAreaGroupBuilder.Matches(
            Device(2, "GQ-DUP-KT#1", 1, "1F B", DeviceCommunicationState.Running),
            item));
        Assert.False(DashboardAreaGroupBuilder.Matches(
            Device(3, "GQ-DUP-KT#1", 2, "2F A", DeviceCommunicationState.Running),
            item));
    }

    [Fact]
    public void NameRulesIncludeMatchingDevicesAndExcludeExplicitNames()
    {
        var include = new AreaGroupItemRecord(
            1, 10, "名称筛选", "name_contains", "1号", string.Empty, null, string.Empty, "KT-", string.Empty);
        var exclude = new AreaGroupItemRecord(
            2, 10, "名称筛选", "name_excludes", "1号", string.Empty, null, string.Empty, "TEST", string.Empty);

        Assert.True(AreaGroupMembership.MatchesAny(
            Device(1, "ROOM-KT-01", 1, "1F A", DeviceCommunicationState.Running), [include]));
        Assert.False(AreaGroupMembership.MatchesAny(
            Device(2, "ROOM-01", 1, "1F A", DeviceCommunicationState.Running), [include]));
        Assert.True(AreaGroupMembership.MatchesAny(
            Device(3, "ROOM-01", 1, "1F A", DeviceCommunicationState.Running), [exclude]));
        Assert.False(AreaGroupMembership.MatchesAny(
            Device(4, "ROOM-TEST-01", 1, "1F A", DeviceCommunicationState.Running), [exclude]));
    }

    private static AreaGroupRecord Group(
        long id,
        string name,
        bool enabled,
        string groupKind = "custom",
        string systemKey = "")
    {
        return new AreaGroupRecord(
            Id: id,
            Name: name,
            AreaLabel: string.Empty,
            Description: string.Empty,
            Priority: "重点",
            GroupKind: groupKind,
            SystemKey: systemKey,
            Locked: groupKind == "system",
            Enabled: enabled,
            ItemCount: groupKind == "custom" ? 1 : 0,
            Total: 0,
            OnCount: 0,
            OffCount: 0,
            OfflineCount: 0,
            UnknownCount: 0,
            CoveredAreas: 0,
            PublicTotal: 0,
            PublicOnCount: 0,
            PublicOffCount: 0,
            PublicOfflineCount: 0,
            PublicUnknownCount: 0,
            PublicCoveredAreas: 0);
    }

    private static AreaGroupItemRecord FloorItem(long groupId, double floor)
    {
        return new AreaGroupItemRecord(
            Id: groupId,
            GroupId: groupId,
            GroupName: string.Empty,
            TargetType: "floor",
            Building: "1号",
            FloorLabel: $"{floor:0.#}F",
            FloorValue: floor,
            SubAreaText: string.Empty,
            CardName: string.Empty,
            Note: string.Empty);
    }

    private static DeviceRecord Device(
        long id,
        string name,
        double floor,
        string subArea,
        DeviceCommunicationState state,
        string layout = "grid",
        string? areaTypeOverride = null,
        bool isVirtual = false)
    {
        var communication = state switch
        {
            DeviceCommunicationState.Running => "开机",
            DeviceCommunicationState.Stopped => "关机",
            DeviceCommunicationState.Offline => "离线",
            _ => string.Empty,
        };
        return new DeviceRecord(
            Id: id,
            Building: "1号",
            Floor: floor,
            FloorLabel: $"{floor:0.#}F",
            SubArea: subArea,
            X: null,
            Y: null,
            PageName: "default",
            Name: name,
            Layout: layout,
            SwitchState: string.Empty,
            Mode: string.Empty,
            IndoorTemperature: string.Empty,
            SetTemperature: string.Empty,
            Fan: string.Empty,
            Indicator: string.Empty,
            CommunicationText: communication,
            CommunicationState: state,
            AreaTypeOverride: areaTypeOverride,
            IsVirtual: isVirtual);
    }
}
