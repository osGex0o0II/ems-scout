using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Domain;

namespace EmsScout.Application;

public static class DashboardAreaGroupBuilder
{
    public static IReadOnlyList<DashboardAreaGroupSummary> Build(
        IReadOnlyList<DeviceRecord> devices,
        AreaGroupSet groupSet)
    {
        var itemsByGroup = groupSet.Items
            .GroupBy(item => item.GroupId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AreaGroupItemRecord>)group.ToArray());

        return groupSet.Groups
            .Where(group => group.Enabled && IsDashboardGroup(group))
            .OrderBy(group => group.GroupKind.Equals("system", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(group => SystemGroupRank(group.SystemKey))
            .ThenBy(group => PriorityRank(group.Priority))
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildSummary(
                group,
                devices,
                itemsByGroup.GetValueOrDefault(group.Id, [])))
            .ToArray();
    }

    public static bool Matches(DeviceRecord device, AreaGroupItemRecord item)
    {
        return AreaGroupMembership.Matches(device, item);
    }

    private static DashboardAreaGroupSummary BuildSummary(
        AreaGroupRecord group,
        IReadOnlyList<DeviceRecord> devices,
        IReadOnlyList<AreaGroupItemRecord> items)
    {
        var matches = IsSystemGroup(group)
            ? devices.Where(device => MatchesSystemGroup(device, group.SystemKey)).ToArray()
            : devices.Where(device => AreaGroupMembership.MatchesAny(device, items)).ToArray();
        var publicMatches = matches
            .Where(device => string.Equals(
                device.AreaType,
                DeviceAreaClassifier.PublicArea,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new DashboardAreaGroupSummary(
            Id: group.Id,
            Name: group.Name,
            AreaLabel: group.AreaLabel,
            Description: group.Description,
            Priority: group.Priority,
            MemberCount: group.ItemCount,
            Total: matches.Length,
            Online: matches.Count(device => device.CommunicationState is DeviceCommunicationState.Running or DeviceCommunicationState.Stopped),
            Offline: matches.Count(device => device.CommunicationState == DeviceCommunicationState.Offline),
            Unknown: matches.Count(device => device.CommunicationState == DeviceCommunicationState.Unknown),
            Running: matches.Count(device => device.CommunicationState == DeviceCommunicationState.Running),
            Stopped: matches.Count(device => device.CommunicationState == DeviceCommunicationState.Stopped),
            CoveredAreas: matches
                .Select(device => (device.Building, device.Floor, device.SubArea))
                .Distinct()
                .Count(),
            PublicTotal: publicMatches.Length,
            PublicRunning: publicMatches.Count(device => device.CommunicationState == DeviceCommunicationState.Running),
            PublicStopped: publicMatches.Count(device => device.CommunicationState == DeviceCommunicationState.Stopped),
            PublicOffline: publicMatches.Count(device => device.CommunicationState == DeviceCommunicationState.Offline),
            PublicUnknown: publicMatches.Count(device => device.CommunicationState == DeviceCommunicationState.Unknown),
            PublicCoveredAreas: publicMatches
                .Select(device => (device.Building, device.Floor, device.SubArea))
                .Distinct()
                .Count(),
            AreaType: group.SystemKey switch
            {
                "public" => DeviceAreaClassifier.PublicArea,
                "non_public" => DeviceAreaClassifier.PrivateArea,
                _ => string.Empty,
            });
    }

    private static bool IsDashboardGroup(AreaGroupRecord group)
    {
        return group.GroupKind.Equals("custom", StringComparison.OrdinalIgnoreCase) ||
            IsSystemGroup(group);
    }

    private static bool IsSystemGroup(AreaGroupRecord group)
    {
        return group.GroupKind.Equals("system", StringComparison.OrdinalIgnoreCase) &&
            group.SystemKey is "public" or "non_public";
    }

    private static bool MatchesSystemGroup(DeviceRecord device, string systemKey)
    {
        return systemKey switch
        {
            "public" => string.Equals(device.AreaType, DeviceAreaClassifier.PublicArea, StringComparison.OrdinalIgnoreCase),
            "non_public" => string.Equals(device.AreaType, DeviceAreaClassifier.PrivateArea, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static int SystemGroupRank(string systemKey)
    {
        return systemKey switch
        {
            "public" => 0,
            "non_public" => 1,
            _ => 2,
        };
    }

    private static int PriorityRank(string priority)
    {
        return priority switch
        {
            "紧急" => 0,
            "重点" => 1,
            _ => 2,
        };
    }
}
