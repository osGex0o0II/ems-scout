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
            .Where(group => group.Enabled && group.GroupKind.Equals("custom", StringComparison.OrdinalIgnoreCase))
            .OrderBy(group => PriorityRank(group.Priority))
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
        var matches = devices
            .Where(device => AreaGroupMembership.MatchesAny(device, items))
            .ToArray();
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
                .Count());
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
