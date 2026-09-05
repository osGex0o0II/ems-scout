using EmsScout.Application.Devices;

namespace EmsScout.Application.Groups;

public static class AreaGroupMembership
{
    public static bool MatchesAny(
        DeviceRecord device,
        IEnumerable<AreaGroupItemRecord> items)
    {
        var materialized = items.ToArray();
        var exclusions = materialized
            .Where(item => IsNameExclusion(item.TargetType))
            .ToArray();
        if (exclusions.Any(item => MatchesName(device, item, include: true)))
        {
            return false;
        }

        var inclusions = materialized
            .Where(item => !IsNameExclusion(item.TargetType))
            .ToArray();
        return inclusions.Length > 0
            ? inclusions.Any(item => Matches(device, item))
            : exclusions.Any(item => string.Equals(device.Building, item.Building, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Matches(DeviceRecord device, AreaGroupItemRecord item)
    {
        if (!string.Equals(device.Building, item.Building, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return item.TargetType.Trim().ToLowerInvariant() switch
        {
            "device" => MatchesDevice(device, item),
            "name_contains" => MatchesName(device, item, include: true),
            "name_excludes" => MatchesName(device, item, include: false),
            "sub_area" => item.FloorValue is not null &&
                          SameFloor(device.Floor, item.FloorValue) &&
                          string.Equals(device.SubArea, item.SubAreaText, StringComparison.OrdinalIgnoreCase),
            "floor" => item.FloorValue is not null && SameFloor(device.Floor, item.FloorValue),
            _ => false,
        };
    }

    public static IReadOnlyList<string> Buildings(IEnumerable<AreaGroupItemRecord> items)
    {
        return items
            .Select(item => item.Building.Trim())
            .Where(building => !string.IsNullOrWhiteSpace(building))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(building => building, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MatchesDevice(DeviceRecord device, AreaGroupItemRecord item)
    {
        if (string.IsNullOrWhiteSpace(item.CardName))
        {
            return false;
        }

        var nameMatches = string.Equals(device.Name, item.CardName, StringComparison.OrdinalIgnoreCase) ||
                          device.Name.StartsWith(item.CardName + "#", StringComparison.OrdinalIgnoreCase);
        if (!nameMatches)
        {
            return false;
        }

        if (item.FloorValue is null && string.IsNullOrWhiteSpace(item.SubAreaText))
        {
            return true;
        }

        var floorMatches = item.FloorValue is null || SameFloor(device.Floor, item.FloorValue);
        var subAreaMatches = string.IsNullOrWhiteSpace(item.SubAreaText) ||
                             string.Equals(device.SubArea, item.SubAreaText, StringComparison.OrdinalIgnoreCase);
        return floorMatches && subAreaMatches;
    }

    private static bool MatchesName(DeviceRecord device, AreaGroupItemRecord item, bool include)
    {
        if (!string.Equals(device.Building, item.Building, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(item.CardName))
        {
            return false;
        }

        var floorMatches = item.FloorValue is null || SameFloor(device.Floor, item.FloorValue);
        var subAreaMatches = string.IsNullOrWhiteSpace(item.SubAreaText) ||
                             string.Equals(device.SubArea, item.SubAreaText, StringComparison.OrdinalIgnoreCase);
        if (!floorMatches || !subAreaMatches)
        {
            return false;
        }

        var nameMatches = device.Name.Contains(item.CardName, StringComparison.OrdinalIgnoreCase);
        return include ? nameMatches : !nameMatches;
    }

    private static bool IsNameExclusion(string targetType)
    {
        return string.Equals(targetType.Trim(), "name_excludes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameFloor(double? left, double? right)
    {
        return left is not null && right is not null && Math.Abs(left.Value - right.Value) < 0.001;
    }
}
