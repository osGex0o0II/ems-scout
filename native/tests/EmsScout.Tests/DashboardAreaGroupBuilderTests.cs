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

        var summary = Assert.Single(
            DashboardAreaGroupBuilder.Build(devices, set),
            summary => summary.Id == enabled.Id);

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
    public void IncludesPublicAndPrivateSystemGroupsAsStandaloneRows()
    {
        var publicGroup = Group(1, "公区", enabled: true, groupKind: "system", systemKey: "public");
        var privateGroup = Group(2, "非公区", enabled: true, groupKind: "system", systemKey: "non_public");
        var set = new AreaGroupSet([publicGroup, privateGroup], []);
        var devices = new[]
        {
            Device(1, "GQ-0101-KT", 1, "1F A", DeviceCommunicationState.Running),
            Device(2, "QL-0102-KT", 1, "1F A", DeviceCommunicationState.Stopped),
            Device(3, "GQ-0103-KT", 1, "1F B", DeviceCommunicationState.Offline),
        };

        var summaries = DashboardAreaGroupBuilder.Build(devices, set);

        var publicSummary = Assert.Single(summaries, summary => summary.Id == publicGroup.Id);
        Assert.Equal(2, publicSummary.Total);
        Assert.Equal(DeviceAreaClassifier.PublicArea, publicSummary.AreaType);
        Assert.Equal(2, publicSummary.PublicTotal);
        Assert.Equal(1, publicSummary.Running);
        Assert.Equal(1, publicSummary.Offline);

        var privateSummary = Assert.Single(summaries, summary => summary.Id == privateGroup.Id);
        Assert.Equal(1, privateSummary.Total);
        Assert.Equal(DeviceAreaClassifier.PrivateArea, privateSummary.AreaType);
        Assert.Equal(0, privateSummary.PublicTotal);
        Assert.Equal(1, privateSummary.Stopped);
        Assert.Equal(1, privateSummary.PrivateTotal);
    }

    [Fact]
    public void CountsConfiguredOverviewAnomaliesWithoutTreatingOfflineDevicesAsRealtimeAnomalies()
    {
        var publicGroup = Group(1, "公区", enabled: true, groupKind: "system", systemKey: "public");
        var set = new AreaGroupSet([publicGroup], []);
        var devices = new[]
        {
            DeviceWithValues(1, "GQ-MODE-KT", DeviceCommunicationState.Running, "制热", "25", "开启"),
            DeviceWithValues(2, "GQ-TEMP-LOW-KT", DeviceCommunicationState.Stopped, "制冷", "21", "关闭"),
            DeviceWithValues(3, "GQ-TEMP-HIGH-KT", DeviceCommunicationState.Stopped, "制冷", "27", "关闭"),
            DeviceWithValues(4, "GQ-OK-KT", DeviceCommunicationState.Running, "制冷", "22", "关闭"),
            DeviceWithValues(5, "GQ-OFFLINE-KT", DeviceCommunicationState.Offline, "制热", "18", string.Empty),
            DeviceWithValues(6, "GQ-NO-SNAPSHOT-KT", DeviceCommunicationState.Running, "制热", "18", string.Empty),
        };

        var summary = Assert.Single(
            DashboardAreaGroupBuilder.Build(
                devices,
                set,
                new DashboardAnomalySettings("制冷", 22, 26)));

        Assert.Equal(2, summary.ModeAbnormal);
        Assert.Equal(3, summary.TemperatureAbnormal);
        Assert.Equal(1, summary.LockOn);
        Assert.Equal(3, summary.LockOff);
    }

    [Fact]
    public void CountsBaseAnomaliesWhenRealtimeSnapshotIsUnavailable()
    {
        var publicGroup = Group(1, "公区", enabled: true, groupKind: "system", systemKey: "public");
        var devices = new[]
        {
            DeviceWithValues(1, "GQ-MODE-KT", DeviceCommunicationState.Running, "制热", "25", string.Empty),
            DeviceWithValues(2, "GQ-TEMP-LOW-KT", DeviceCommunicationState.Stopped, "制冷", "21", string.Empty),
            DeviceWithValues(3, "GQ-TEMP-HIGH-KT", DeviceCommunicationState.Stopped, "制冷", "27", string.Empty),
            DeviceWithValues(4, "GQ-OK-KT", DeviceCommunicationState.Running, "制冷", "22", string.Empty),
            DeviceWithValues(5, "GQ-OFFLINE-KT", DeviceCommunicationState.Offline, "制热", "18", string.Empty),
        };

        var summary = Assert.Single(
            DashboardAreaGroupBuilder.Build(
                devices,
                new AreaGroupSet([publicGroup], []),
                new DashboardAnomalySettings("制冷", 22, 26)));

        Assert.Equal(1, summary.ModeAbnormal);
        Assert.Equal(2, summary.TemperatureAbnormal);
        Assert.Equal(0, summary.LockOn);
        Assert.Equal(0, summary.LockOff);
        Assert.Equal(DashboardRealtimeAvailability.Unavailable, summary.RealtimeAvailability);
    }

    [Fact]
    public void MarksRealtimeAggregatesUnavailableWhenOnlineDevicesHaveNoDetails()
    {
        var publicGroup = Group(1, "公区", enabled: true, groupKind: "system", systemKey: "public");
        var devices = new[]
        {
            Device(1, "GQ-0101-KT", 1, "1F A", DeviceCommunicationState.Running),
            Device(2, "GQ-0102-KT", 1, "1F A", DeviceCommunicationState.Stopped),
        };

        var summary = Assert.Single(
            DashboardAreaGroupBuilder.Build(devices, new AreaGroupSet([publicGroup], [])));

        Assert.Equal(DashboardRealtimeAvailability.Unavailable, summary.RealtimeAvailability);
        Assert.Contains("不可用", summary.RealtimeStatusText);
    }

    [Fact]
    public void KeepsValidLockCountsVisibleWhenRealtimeDetailsArePartial()
    {
        var publicGroup = Group(1, "公区", enabled: true, groupKind: "system", systemKey: "public");
        var devices = new[]
        {
            DeviceWithValues(1, "GQ-0101-KT", DeviceCommunicationState.Running, "制冷", "25", "开启"),
            DeviceWithValues(2, "GQ-0102-KT", DeviceCommunicationState.Stopped, "制冷", "25", "关闭"),
            Device(3, "GQ-0103-KT", 1, "1F A", DeviceCommunicationState.Running),
        };

        var summary = Assert.Single(
            DashboardAreaGroupBuilder.Build(devices, new AreaGroupSet([publicGroup], [])));

        Assert.Equal(DashboardRealtimeAvailability.Partial, summary.RealtimeAvailability);
        Assert.Equal(1, summary.LockOn);
        Assert.Equal(1, summary.LockOff);
        Assert.Contains("部分可用", summary.RealtimeStatusText);
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
    public void BuildsGenericDashboardGroupsFromAreaRulesAndHidesDisabledGroups()
    {
        var enabled = Group(20, "公共区域设备", enabled: true);
        var disabled = Group(21, "停用组", enabled: false);
        var rules = new[]
        {
            Rule(enabled.Id, "1号", "-", "1F", "include", "GQ"),
            Rule(disabled.Id, "1号", "-", "1F", "include", "GQ"),
        };
        var devices = new[]
        {
            Device(1, "GQ-0101-KT", 1, "1F A", DeviceCommunicationState.Running),
            Device(2, "QL-0102-KT", 1, "1F A", DeviceCommunicationState.Stopped),
        };

        var summaries = DashboardAreaGroupBuilder.Build(
            devices,
            new AreaGroupSet([enabled, disabled], [], rules));

        var summary = Assert.Single(summaries);
        Assert.Equal(enabled.Id, summary.Id);
        Assert.Equal(1, summary.Total);
        Assert.Equal(string.Empty, summary.AreaType);
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

    private static AreaGroupRuleRecord Rule(
        long groupId,
        string building,
        string zuo,
        string floor,
        string mode,
        string keywords)
    {
        return new AreaGroupRuleRecord(
            Id: groupId,
            GroupId: groupId,
            RuleOrder: 0,
            Building: building,
            Zuo: zuo,
            FloorLabel: floor,
            FloorValue: null,
            MatchMode: mode,
            Keywords: AreaGroupRuleNormalizer.NormalizeKeywords(keywords),
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

    private static DeviceRecord DeviceWithValues(
        long id,
        string name,
        DeviceCommunicationState state,
        string mode,
        string setTemperature,
        string lockState)
    {
        var device = Device(id, name, 1, "1F A", state);
        return device with
        {
            Mode = mode,
            SetTemperature = setTemperature,
            Realtime = string.IsNullOrWhiteSpace(lockState)
                ? null
                : new RealtimeDetailRecord(
                    RowId: $"row-{id}",
                    SourceFile: "test.json",
                    SourceUpdatedAt: DateTimeOffset.UtcNow,
                    Building: "1号",
                    Floor: 1,
                    SubArea: "1F A",
                    PageName: "default",
                    Name: name,
                    DevId: $"dev-{id}",
                    MeterId: string.Empty,
                    RtuId: string.Empty,
                    FieldCount: 1,
                    RealtimeTagCount: 1,
                    RealtimeValidTagCount: 1,
                    DefaultLike: false,
                    Error: string.Empty,
                    CardComm: "在线",
                    CardSwitch: "ON",
                    CardIndicator: string.Empty,
                    Fields: new Dictionary<string, string> { ["集控锁定"] = lockState },
                    ValidFields: new Dictionary<string, bool> { ["集控锁定"] = true }),
        };
    }
}
