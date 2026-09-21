using EmsScout.Application.Devices;

namespace EmsScout.Application.Groups;

public sealed record PreparedAreaGroupRules(
    IReadOnlyList<AreaGroupRuleRecord> Includes,
    IReadOnlyList<AreaGroupRuleRecord> Excludes)
{
    public bool IsEmpty => Includes.Count == 0 && Excludes.Count == 0;
}

public static class AreaGroupRuleMatcher
{
    public static PreparedAreaGroupRules Prepare(IEnumerable<AreaGroupRuleRecord> rules)
    {
        var materialized = rules
            .Select(AreaGroupRuleNormalizer.Normalize)
            .ToArray();
        return new(
            materialized.Where(rule => rule.IsInclude).ToArray(),
            materialized.Where(rule => rule.IsExclude).ToArray());
    }

    public static int CountMatches(
        IEnumerable<DeviceRecord> devices,
        AreaGroupRuleRecord rule)
    {
        var normalized = AreaGroupRuleNormalizer.Normalize(rule);
        return devices.Count(device => MatchesRule(device, normalized));
    }

    public static bool MatchesAny(
        DeviceRecord device,
        IEnumerable<AreaGroupRuleRecord> rules)
    {
        return MatchesAny(device, Prepare(rules));
    }

    public static bool MatchesAny(
        DeviceRecord device,
        PreparedAreaGroupRules rules)
    {
        if (rules.IsEmpty)
        {
            return false;
        }

        var candidate = rules.Includes.Count > 0
            ? rules.Includes.Any(rule => MatchesRule(device, rule, includeKeywords: true))
            : rules.Excludes.Any(rule => MatchesScope(device, rule));

        return candidate && !rules.Excludes.Any(rule => MatchesRule(device, rule, includeKeywords: true));
    }

    public static bool MatchesRule(
        DeviceRecord device,
        AreaGroupRuleRecord rule,
        bool includeKeywords = true)
    {
        return MatchesScope(device, rule) &&
               (!includeKeywords || MatchesKeywords(device.Name, rule.Keywords));
    }

    public static bool MatchesScope(DeviceRecord device, AreaGroupRuleRecord rule)
    {
        if (!string.Equals(device.Building, rule.Building, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var zuo = AreaGroupRuleNormalizer.NormalizeZuo(rule.Zuo);
        if (zuo != "-" && !string.Equals(device.Zuo, zuo, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var floor = AreaGroupRuleNormalizer.NormalizeFloorLabel(rule.FloorLabel);
        if (string.IsNullOrWhiteSpace(floor))
        {
            return true;
        }

        var deviceFloor = AreaGroupRuleNormalizer.NormalizeFloorLabel(
            string.IsNullOrWhiteSpace(device.FloorLabel)
                ? DeviceFloorLabelFormatter.Format(device.Floor, device.SubArea)
                : device.FloorLabel);
        return string.Equals(deviceFloor, floor, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesKeywords(string deviceName, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0)
        {
            return true;
        }

        return keywords.Any(keyword =>
            !string.IsNullOrWhiteSpace(keyword) &&
            (keyword == "*" || deviceName.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
    }
}
