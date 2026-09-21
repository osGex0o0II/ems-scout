using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmsScout.Application.Groups;

public sealed record AreaGroupTransferDocument(
    int SchemaVersion,
    IReadOnlyList<AreaGroupTransferGroup> Groups);

public sealed record AreaGroupTransferGroup(
    string GroupKey,
    string Name,
    string Note,
    bool Enabled,
    IReadOnlyList<AreaGroupTransferRule> Rules,
    string AreaLabel = "",
    string Priority = "重点");

public sealed record AreaGroupTransferRule(
    int RuleOrder,
    string Building,
    string Zuo,
    string Floor,
    string Mode,
    IReadOnlyList<string> Keywords,
    string Note);

public static class AreaGroupTransferCodec
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(AreaGroupTransferDocument document)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static AreaGroupTransferDocument Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<AreaGroupTransferDocument>(json, JsonOptions)
                       ?? throw new JsonException("区域组文件为空。");
        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new JsonException($"不支持的区域组文件版本：{document.SchemaVersion}。");
        }

        Validate(document);
        return document;
    }

    public static void Validate(AreaGroupTransferDocument document)
    {
        if (document.Groups is null)
        {
            throw new JsonException("区域组文件缺少 groups 数组。", null, 0, 0);
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new JsonException($"不支持的区域组文件版本：{document.SchemaVersion}。");
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in document.Groups ?? [])
        {
            var key = (group.GroupKey ?? string.Empty).Trim();
            if (key.Length == 0 || key.Length > 128 || key.Any(char.IsWhiteSpace) || !keys.Add(key))
            {
                throw new ArgumentException("区域组 groupKey 必须唯一且为 1-128 个不含空格的字符。", nameof(document));
            }

            if (string.IsNullOrWhiteSpace(group.Name))
            {
                throw new ArgumentException("区域组名称不能为空。", nameof(document));
            }

            if (group.Rules is null)
            {
                throw new JsonException($"区域组 {key} 缺少 rules 数组。", null, 0, 0);
            }

            foreach (var transferRule in group.Rules)
            {
                var rule = new AreaGroupRuleRecord(
                    Id: 0,
                    GroupId: 0,
                    RuleOrder: transferRule.RuleOrder,
                    Building: transferRule.Building,
                    Zuo: transferRule.Zuo,
                    FloorLabel: transferRule.Floor,
                    FloorValue: AreaGroupRuleNormalizer.TryParseFloorValue(transferRule.Floor),
                    MatchMode: transferRule.Mode,
                    Keywords: transferRule.Keywords ?? [],
                    Note: transferRule.Note);
                AreaGroupRuleNormalizer.Validate(AreaGroupRuleNormalizer.Normalize(rule));
            }
        }
    }
}
