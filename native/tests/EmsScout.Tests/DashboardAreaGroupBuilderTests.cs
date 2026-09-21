using EmsScout.Application;
using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Domain;

namespace EmsScout.Tests;

public sealed class DashboardAreaGroupBuilderTests
{
    [Fact]
    public void BuildsAreaGroupsFromPreparedRules()
    {
        var source = File.ReadAllText(Path.Combine(LocateRepositoryRoot(), "native", "src", "EmsScout.Application", "DashboardAreaGroupBuilder.cs"));

        Assert.Contains("AreaGroupRuleMatcher.Prepare", source);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
    [Fact]
    public void AggregatesRuleMatchedDevicesForEnabledCustomGroups()
    {
        var enabled = Group(10, "未开放", enabled: true);
        var disabled = Group(11, "已停用", enabled: false);
        var set = new AreaGroupSet(
            [enabled, disabled],
            [Rule(enabled.Id, "1号", "-", "-", "include", "GQ"),
             Rule(disabled.Id, "1号", "-", "-", "include", "GQ")]);
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
        Assert.Equal(4, summary.Total);
        Assert.Equal(2, summary.Online);
        Assert.Equal(1, summary.Offline);
        Assert.Equal(1, summary.Unknown);
        Assert.Equal(1, summary.Running);
        Assert.Equal(1, summary.Stopped);
        Assert.Equal(2, summary.CoveredAreas);
        Assert.Equal(string.Empty, summary.AreaType);
    }

    [Fact]
    public void EmptyRuleGroupsRemainVisibleWithoutImplicitMembers()
    {
        var publicGroup = Group(1, "公区", enabled: true);
        var privateGroup = Group(2, "非公区", enabled: true);
        var set = new AreaGroupSet([publicGroup, privateGroup], []);
        var devices = new[]
        {
            Device(1, "GQ-0101-KT", 1, "1F A", DeviceCommunicationState.Running),
            Device(2, "QL-0102-KT", 1, "1F A", DeviceCommunicationState.Stopped),
            Device(3, "GQ-0103-KT", 1, "1F B", DeviceCommunicationState.Offline),
        };

        var summaries = DashboardAreaGroupBuilder.Build(devices, set);

        Assert.Equal(2, summaries.Count);
        Assert.All(summaries, summary =>
        {
            Assert.Equal(0, summary.MemberCount);
            Assert.Equal(0, summary.Total);
        });
    }

    [Fact]
    public void CountsConfiguredOverviewAnomaliesWithoutTreatingOfflineDevicesAsRealtimeAnomalies()
    {
        var publicGroup = Group(1, "公区", enabled: true);
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
                new AreaGroupSet([publicGroup], [Rule(publicGroup.Id, "1号", "-", "-", "include", "GQ")]),
                new DashboardAnomalySettings("制冷", 22, 26)));

        Assert.Equal(2, summary.ModeAbnormal);
        Assert.Equal(3, summary.TemperatureAbnormal);
        Assert.Equal(1, summary.LockOn);
        Assert.Equal(3, summary.LockOff);
    }

    [Fact]
    public void CountsBaseAnomaliesWhenRealtimeSnapshotIsUnavailable()
    {
        var publicGroup = Group(1, "公区", enabled: true);
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
                new AreaGroupSet([publicGroup], [Rule(publicGroup.Id, "1号", "-", "-", "include", "GQ")]),
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
        var publicGroup = Group(1, "公区", enabled: true);
        var devices = new[]
        {
            Device(1, "GQ-0101-KT", 1, "1F A", DeviceCommunicationState.Running),
            Device(2, "GQ-0102-KT", 1, "1F A", DeviceCommunicationState.Stopped),
        };

        var summary = Assert.Single(
            DashboardAreaGroupBuilder.Build(
                devices,
                new AreaGroupSet(
                    [publicGroup],
                    [Rule(publicGroup.Id, "1号", "-", "-", "include", "GQ")] )));

        Assert.Equal(DashboardRealtimeAvailability.Unavailable, summary.RealtimeAvailability);
        Assert.Contains("不可用", summary.RealtimeStatusText);
    }

    [Fact]
    public void KeepsValidLockCountsVisibleWhenRealtimeDetailsArePartial()
    {
        var publicGroup = Group(1, "公区", enabled: true);
        var devices = new[]
        {
            DeviceWithValues(1, "GQ-0101-KT", DeviceCommunicationState.Running, "制冷", "25", "开启"),
            DeviceWithValues(2, "GQ-0102-KT", DeviceCommunicationState.Stopped, "制冷", "25", "关闭"),
            Device(3, "GQ-0103-KT", 1, "1F A", DeviceCommunicationState.Running),
        };

        var summary = Assert.Single(
            DashboardAreaGroupBuilder.Build(
                devices,
                new AreaGroupSet(
                    [publicGroup],
                    [Rule(publicGroup.Id, "1号", "-", "-", "include", "GQ")] )));

        Assert.Equal(DashboardRealtimeAvailability.Partial, summary.RealtimeAvailability);
        Assert.Equal(1, summary.LockOn);
        Assert.Equal(1, summary.LockOff);
        Assert.Contains("部分可用", summary.RealtimeStatusText);
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
            new AreaGroupSet([enabled, disabled], rules));

        var summary = Assert.Single(summaries);
        Assert.Equal(enabled.Id, summary.Id);
        Assert.Equal(1, summary.Total);
        Assert.Equal(string.Empty, summary.AreaType);
    }

    private static AreaGroupRecord Group(
        long id,
        string name,
        bool enabled)
    {
        return new AreaGroupRecord(
            Id: id,
            Name: name,
            AreaLabel: string.Empty,
            Description: string.Empty,
            Priority: "重点",
            Enabled: enabled,
            ItemCount: 0,
            Total: 0,
            OnCount: 0,
            OffCount: 0,
            OfflineCount: 0,
            UnknownCount: 0,
            CoveredAreas: 0,
            GroupKey: string.Empty);
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
