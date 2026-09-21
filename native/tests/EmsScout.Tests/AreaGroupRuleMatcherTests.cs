using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Domain;

namespace EmsScout.Tests;

public sealed class AreaGroupRuleMatcherTests
{
    [Fact]
    public void RequiresEverySelectedScopeFieldAndAnyKeywordWithinOneRule()
    {
        var rule = Rule("1号", "-", "1F", "include", "GQ | WSJ");

        Assert.True(AreaGroupRuleMatcher.MatchesAny(Device("1号", "1F", "GQ-0101-KT", "-"), [rule]));
        Assert.False(AreaGroupRuleMatcher.MatchesAny(Device("1号", "2F", "GQ-0101-KT", "-"), [rule]));
        Assert.False(AreaGroupRuleMatcher.MatchesAny(Device("1号", "1F", "ROOM-0101-KT", "-"), [rule]));
    }

    [Fact]
    public void UsesOrAcrossRulesInTheSameGroup()
    {
        var rules = new[]
        {
            Rule("1号", "-", "1F", "include", "GQ"),
            Rule("2号", "-", "2F", "include", "WSJ"),
        };

        Assert.True(AreaGroupRuleMatcher.MatchesAny(Device("1号", "1F", "GQ-0101-KT", "-"), rules));
        Assert.True(AreaGroupRuleMatcher.MatchesAny(Device("2号", "2F", "WSJ-0201-KT", "-"), rules));
        Assert.False(AreaGroupRuleMatcher.MatchesAny(Device("2号", "1F", "WSJ-0201-KT", "-"), rules));
    }

    [Fact]
    public void ExcludeRulesRemoveMatchesFromIncludeCandidates()
    {
        var rules = new[]
        {
            Rule("1号", "-", "-", "include", "KT"),
            Rule("1号", "-", "1F", "exclude", "TEMP"),
        };

        Assert.True(AreaGroupRuleMatcher.MatchesAny(Device("1号", "2F", "ROOM-KT", "-"), rules));
        Assert.False(AreaGroupRuleMatcher.MatchesAny(Device("1号", "1F", "ROOM-KT-TEMP", "-"), rules));
    }

    [Fact]
    public void ExcludeOnlyRulesUseTheirScopeAsTheCandidateUniverse()
    {
        var rule = Rule("5号", "C座", "BM", "exclude", "TEMP");

        Assert.True(AreaGroupRuleMatcher.MatchesAny(Device("5号", "BM", "ROOM-KT", "C座"), [rule]));
        Assert.False(AreaGroupRuleMatcher.MatchesAny(Device("5号", "BM", "ROOM-TEMP-KT", "C座"), [rule]));
        Assert.False(AreaGroupRuleMatcher.MatchesAny(Device("5号", "BM", "ROOM-KT", "B座"), [rule]));
    }

    [Fact]
    public void NormalizesPipeSeparatedKeywordsAndFloorLabels()
    {
        var keywords = AreaGroupRuleNormalizer.NormalizeKeywords(" GQ | wsj || GQ ");

        Assert.Equal(["GQ", "WSJ"], keywords);
        Assert.Equal("B1F", AreaGroupRuleNormalizer.NormalizeFloorLabel(" b1f "));
        Assert.Equal("-", AreaGroupRuleNormalizer.NormalizeZuo(string.Empty));
    }

    [Fact]
    public void KeepsThePreviousBuildingWhenComboBoxTemporarilyClearsSelection()
    {
        Assert.Equal("1号", AreaGroupRuleNormalizer.NormalizeBuildingSelection(null, "1号"));
        Assert.Equal("1号", AreaGroupRuleNormalizer.NormalizeBuildingSelection("", "1号"));
        Assert.Equal("2号", AreaGroupRuleNormalizer.NormalizeBuildingSelection(" 2号 ", "1号"));
    }

    [Fact]
    public void UsesOneFloorSortOrderForAreaAndDataPages()
    {
        var labels = new[] { "3F", "BM", "1F", "2.5F", "B1F", "1.5F", "2F" };

        var sorted = labels
            .OrderBy(DeviceFloorLabelFormatter.SortValue)
            .ToArray();

        Assert.Equal(["B1F", "BM", "1F", "1.5F", "2F", "2.5F", "3F"], sorted);
    }

    [Fact]
    public void MatchesEquivalentNumericFloorLabels()
    {
        var device = Device("1号", "1F", "GQ-0101-KT", "-");
        var basement = Device("1号", "B1F", "GQ-B001-KT", "-");

        Assert.True(AreaGroupRuleMatcher.MatchesAny(device, [Rule("1号", "-", "1.0F", "include", "GQ")]));
        Assert.True(AreaGroupRuleMatcher.MatchesAny(basement, [Rule("1号", "-", "B01F", "include", "GQ")]));
    }

    [Fact]
    public void KeepsHistoricalSavedZuoInsteadOfRecalculatingFromCoordinates()
    {
        var rule = Rule("5号", "C座", "1F", "include", "KT");
        var device = Device("5号", "1F", "ROOM-KT", "C座") with { X = 1200 };

        Assert.True(AreaGroupRuleMatcher.MatchesAny(device, [rule]));
    }

    [Fact]
    public void CountsDevicesMatchedByOneRuleUsingAllRuleFields()
    {
        var rule = Rule("5号", "C座", "1F", "include", "GQ");
        var devices = new[]
        {
            Device("5号", "1F", "GQ-0101-KT", "C座"),
            Device("5号", "1F", "ROOM-KT", "C座"),
            Device("5号", "1F", "GQ-0102-KT", "B座"),
            Device("5号", "2F", "GQ-0201-KT", "C座"),
        };

        Assert.Equal(1, AreaGroupRuleMatcher.CountMatches(devices, rule));
    }

    [Fact]
    public void CountsExcludedDevicesWithTheSameScopeAndKeywordSemantics()
    {
        var rule = Rule("1号", "-", "-", "exclude", "TEMP");
        var devices = new[]
        {
            Device("1号", "1F", "ROOM-TEMP-KT", "-"),
            Device("1号", "2F", "ROOM-KT", "-"),
            Device("2号", "1F", "ROOM-TEMP-KT", "-"),
        };

        Assert.Equal(1, AreaGroupRuleMatcher.CountMatches(devices, rule));
    }

    [Fact]
    public void RejectsInvalidScopeValues()
    {
        Assert.Throws<ArgumentException>(() => AreaGroupRuleNormalizer.Validate(
            Rule("9号", "-", "1F", "include", "KT")));
        Assert.Throws<ArgumentException>(() => AreaGroupRuleNormalizer.Validate(
            Rule("1号", "A座", "1F", "include", "KT")));
    }

    [Fact]
    public void EmptyKeywordsMatchEveryDeviceInTheSelectedScope()
    {
        var rule = Rule("3号", "-", "-", "-", "");

        Assert.True(AreaGroupRuleMatcher.MatchesAny(Device("3号", "1F", "ROOM-001-KT", "-"), [rule]));
        Assert.True(AreaGroupRuleMatcher.MatchesAny(Device("3号", "14F", "ROOM-014-KT", "-"), [rule]));
        Assert.False(AreaGroupRuleMatcher.MatchesAny(Device("2号", "1F", "ROOM-001-KT", "-"), [rule]));
        Assert.Null(Record.Exception(() => AreaGroupRuleNormalizer.Validate(rule)));
    }

    [Fact]
    public void PreparedRulesPreserveIncludeAndExcludeSemantics()
    {
        var prepared = AreaGroupRuleMatcher.Prepare([
            RawRule("1号", "-", "-", "包含", [" gq "]),
            RawRule("1号", "-", "1F", "不含", ["TEMP"]),
        ]);

        Assert.True(AreaGroupRuleMatcher.MatchesAny(
            Device("1号", "2F", "GQ-0101-KT", "-"), prepared));
        Assert.False(AreaGroupRuleMatcher.MatchesAny(
            Device("1号", "1F", "GQ-0101-TEMP", "-"), prepared));
    }

    private static AreaGroupRuleRecord Rule(
        string building,
        string zuo,
        string floor,
        string mode,
        string keywords)
    {
        return new AreaGroupRuleRecord(
            Id: 1,
            GroupId: 10,
            RuleOrder: 1,
            Building: building,
            Zuo: zuo,
            FloorLabel: floor,
            FloorValue: null,
            MatchMode: mode,
            Keywords: AreaGroupRuleNormalizer.NormalizeKeywords(keywords),
            Note: string.Empty);
    }

    private static AreaGroupRuleRecord RawRule(
        string building,
        string zuo,
        string floor,
        string mode,
        IReadOnlyList<string> keywords)
    {
        return new AreaGroupRuleRecord(
            Id: 1,
            GroupId: 10,
            RuleOrder: 1,
            Building: building,
            Zuo: zuo,
            FloorLabel: floor,
            FloorValue: null,
            MatchMode: mode,
            Keywords: keywords,
            Note: string.Empty);
    }

    private static DeviceRecord Device(string building, string floor, string name, string zuo)
    {
        return new DeviceRecord(
            Id: 1,
            Building: building,
            Floor: null,
            FloorLabel: floor,
            SubArea: floor,
            X: null,
            Y: null,
            PageName: "default",
            Name: name,
            Layout: "grid",
            SwitchState: "OFF",
            Mode: "制冷",
            IndoorTemperature: "25",
            SetTemperature: "25",
            Fan: "中",
            Indicator: string.Empty,
            CommunicationText: "关机",
            CommunicationState: DeviceCommunicationState.Stopped,
            Zuo: zuo);
    }
}
