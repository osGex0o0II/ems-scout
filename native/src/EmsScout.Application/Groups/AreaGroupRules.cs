using System.Globalization;
using System.Text.RegularExpressions;
using EmsScout.Application.Devices;

namespace EmsScout.Application.Groups;

public sealed record AreaGroupRuleRecord(
    long Id,
    long GroupId,
    int RuleOrder,
    string Building,
    string Zuo,
    string FloorLabel,
    double? FloorValue,
    string MatchMode,
    IReadOnlyList<string> Keywords,
    string Note)
{
    public string KeywordText => string.Join(" | ", Keywords);

    public bool IsInclude => string.Equals(MatchMode, AreaGroupRuleNormalizer.Include, StringComparison.OrdinalIgnoreCase);

    public bool IsExclude => string.Equals(MatchMode, AreaGroupRuleNormalizer.Exclude, StringComparison.OrdinalIgnoreCase);
}

public sealed record AreaGroupRuleEdit(
    long GroupId,
    string Building,
    string Zuo,
    string FloorLabel,
    string MatchMode,
    string Keywords,
    string Note,
    long? Id = null,
    int? RuleOrder = null);

public static class AreaGroupRuleNormalizer
{
    public const string Include = "include";
    public const string Exclude = "exclude";

    private static readonly string[] Buildings = ["1号", "2号", "3号", "4号", "5号", "6号"];
    private static readonly string[] Building5Zuos = ["-", "A座", "B座", "C座", "D座", "E座", "F座"];
    private static readonly string[] Building6Zuos = ["-", "A座", "B座", "C座"];
    private static readonly Regex FloorPattern = new(
        "^(?:BM|B\\d+(?:\\.\\d+)?F|\\d+(?:\\.\\d+)?F)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static IReadOnlyList<string> NormalizeKeywords(string? value)
    {
        return (value ?? string.Empty)
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(keyword => keyword.Trim().ToUpperInvariant())
            .Where(keyword => keyword.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string NormalizeBuilding(string? value) => (value ?? string.Empty).Trim();

    public static string NormalizeBuildingSelection(string? value, string? fallback)
    {
        var normalized = NormalizeBuilding(value);
        return normalized.Length == 0 ? NormalizeBuilding(fallback) : normalized;
    }

    public static string NormalizeZuo(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "-" : normalized;
    }

    public static string NormalizeFloorLabel(string? value)
    {
        var normalized = DeviceFloorLabelFormatter.Normalize(value);
        if (normalized is "" or "-")
        {
            return string.Empty;
        }

        if (normalized.Equals("BM", StringComparison.OrdinalIgnoreCase))
        {
            return "BM";
        }

        var numeric = normalized.EndsWith('F') ? normalized[..^1] : normalized;
        var basement = numeric.StartsWith("B", StringComparison.OrdinalIgnoreCase);
        if (basement)
        {
            numeric = numeric[1..];
        }

        if (!double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return normalized;
        }

        var formatted = number.ToString("0.################", CultureInfo.InvariantCulture);
        return basement ? $"B{formatted}F" : $"{formatted}F";
    }

    public static AreaGroupRuleRecord Normalize(AreaGroupRuleRecord rule)
    {
        var normalized = rule with
        {
            Building = NormalizeBuilding(rule.Building),
            Zuo = NormalizeZuo(rule.Zuo),
            FloorLabel = NormalizeFloorLabel(rule.FloorLabel),
            MatchMode = NormalizeMatchMode(rule.MatchMode),
            Keywords = NormalizeKeywords(string.Join("|", rule.Keywords)),
            Note = (rule.Note ?? string.Empty).Trim(),
        };
        Validate(normalized);
        return normalized;
    }

    public static void Validate(AreaGroupRuleRecord rule)
    {
        var building = NormalizeBuilding(rule.Building);
        if (!Buildings.Contains(building, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"不支持的楼栋：{rule.Building}", nameof(rule));
        }

        var zuo = NormalizeZuo(rule.Zuo);
        var allowedZuos = building switch
        {
            "5号" => Building5Zuos,
            "6号" => Building6Zuos,
            _ => ["-"],
        };
        if (!allowedZuos.Contains(zuo, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{building}不支持座号：{rule.Zuo}", nameof(rule));
        }

        var floor = NormalizeFloorLabel(rule.FloorLabel);
        if (!string.IsNullOrWhiteSpace(floor) && !FloorPattern.IsMatch(floor))
        {
            throw new ArgumentException($"不支持的楼层：{rule.FloorLabel}", nameof(rule));
        }

        var mode = NormalizeMatchMode(rule.MatchMode);
        if (mode is not (Include or Exclude))
        {
            throw new ArgumentException($"不支持的匹配方式：{rule.MatchMode}", nameof(rule));
        }

    }

    public static string NormalizeMatchMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "-" or "include" or "包含" => Include,
            "exclude" or "不含" or "不包含" => Exclude,
            _ => normalized,
        };
    }

    public static double? TryParseFloorValue(string? floorLabel)
    {
        var normalized = NormalizeFloorLabel(floorLabel);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Equals("BM", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var numeric = normalized.EndsWith('F') ? normalized[..^1] : normalized;
        if (numeric.StartsWith('B'))
        {
            numeric = "-" + numeric[1..];
        }

        return double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
