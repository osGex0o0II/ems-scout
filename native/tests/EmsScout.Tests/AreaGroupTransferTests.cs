using System.Text.Json;
using EmsScout.Application.Groups;

namespace EmsScout.Tests;

public sealed class AreaGroupTransferTests
{
    [Fact]
    public void SerializesOnlyPortableAreaGroupConfiguration()
    {
        var document = new AreaGroupTransferDocument(
            SchemaVersion: 1,
            Groups:
            [
                new AreaGroupTransferGroup(
                    GroupKey: "public-area",
                    Name: "公区",
                    Note: "公共区域设备",
                    Enabled: true,
                    Rules:
                    [
                        new AreaGroupTransferRule(0, "1号", "-", "", "include", ["GQ", "WSJ"], "")
                    ])
            ]);

        var json = AreaGroupTransferCodec.Serialize(document);

        Assert.Contains("groupKey", json);
        Assert.Contains("keywords", json);
        Assert.DoesNotContain("cards", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("realtime", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("collection_runs", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quality", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoundTripsRuleOrderAndKeywords()
    {
        var source = new AreaGroupTransferDocument(
            1,
            [new AreaGroupTransferGroup(
                "review-zone", "复核区", "备注", false,
                [
                    new AreaGroupTransferRule(3, "6号", "B座", "2.5F", "exclude", ["TEMP"], "排除")
                ])]);

        var parsed = AreaGroupTransferCodec.Deserialize(AreaGroupTransferCodec.Serialize(source));

        Assert.Equal(source.SchemaVersion, parsed.SchemaVersion);
        Assert.Equal(source.Groups[0].GroupKey, parsed.Groups[0].GroupKey);
        Assert.Equal(source.Groups[0].Name, parsed.Groups[0].Name);
        Assert.Equal(source.Groups[0].Rules[0].RuleOrder, parsed.Groups[0].Rules[0].RuleOrder);
        Assert.Equal(source.Groups[0].Rules[0].Building, parsed.Groups[0].Rules[0].Building);
        Assert.Equal(source.Groups[0].Rules[0].Keywords, parsed.Groups[0].Rules[0].Keywords);
        Assert.Equal(3, parsed.Groups[0].Rules[0].RuleOrder);
    }

    [Fact]
    public void RejectsUnknownSchemaAndEmptyOrDuplicateGroupKeys()
    {
        Assert.Throws<JsonException>(() => AreaGroupTransferCodec.Deserialize("{\"schemaVersion\":99,\"groups\":[]}"));
        Assert.Throws<ArgumentException>(() => AreaGroupTransferCodec.Validate(
            new AreaGroupTransferDocument(1,
            [new AreaGroupTransferGroup("", "空", "", true, [])])));
        Assert.Throws<ArgumentException>(() => AreaGroupTransferCodec.Validate(
            new AreaGroupTransferDocument(1,
            [
                new AreaGroupTransferGroup("same", "一", "", true, []),
                new AreaGroupTransferGroup("SAME", "二", "", true, [])
            ])));
    }
}
