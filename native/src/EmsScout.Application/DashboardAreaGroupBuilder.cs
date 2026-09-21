using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Domain;

namespace EmsScout.Application;

public static class DashboardAreaGroupBuilder
{
    public static IReadOnlyList<DashboardAreaGroupSummary> Build(
        IReadOnlyList<DeviceRecord> devices,
        AreaGroupSet groupSet,
        DashboardAnomalySettings? anomalySettings = null)
    {
        anomalySettings ??= DashboardAnomalySettings.Default;
        var legacyItemsEnabled = groupSet.Rules is null;
        var itemsByGroup = groupSet.Items
            .GroupBy(item => item.GroupId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AreaGroupItemRecord>)group.ToArray());
        var rulesByGroup = groupSet.RuleRecords
            .GroupBy(rule => rule.GroupId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AreaGroupRuleRecord>)group.ToArray());

        return groupSet.Groups
            .Where(group => group.Enabled)
            .OrderBy(group => PriorityRank(group.Priority))
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildSummary(
                group,
                devices,
                itemsByGroup.GetValueOrDefault(group.Id, []),
                rulesByGroup.GetValueOrDefault(group.Id, []),
                legacyItemsEnabled,
                anomalySettings))
            .ToArray();
    }

    public static bool Matches(DeviceRecord device, AreaGroupItemRecord item)
    {
        return AreaGroupMembership.Matches(device, item);
    }

    private static DashboardAreaGroupSummary BuildSummary(
        AreaGroupRecord group,
        IReadOnlyList<DeviceRecord> devices,
        IReadOnlyList<AreaGroupItemRecord> items,
        IReadOnlyList<AreaGroupRuleRecord> rules,
        bool legacyItemsEnabled,
        DashboardAnomalySettings anomalySettings)
    {
        var matches = rules.Count > 0
            ? devices.Where(device => AreaGroupRuleMatcher.MatchesAny(device, rules)).ToArray()
            : legacyItemsEnabled
                ? IsLegacySystemGroup(group)
                    ? devices.Where(device => MatchesLegacySystemGroup(device, group.SystemKey)).ToArray()
                    : devices.Where(device => AreaGroupMembership.MatchesAny(device, items)).ToArray()
            : [];
        IReadOnlyList<DeviceRecord> publicMatches = legacyItemsEnabled
            ? matches.Where(device => string.Equals(device.AreaType, DeviceAreaClassifier.PublicArea, StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
        var onlineMatches = matches
            .Where(device => device.CommunicationState is DeviceCommunicationState.Running or DeviceCommunicationState.Stopped)
            .ToArray();
        var realtimeDevices = matches
            .Where(device =>
                (device.CommunicationState is DeviceCommunicationState.Running or DeviceCommunicationState.Stopped) &&
                device.Realtime is not null)
            .ToArray();
        var realtimeStatus = DashboardRealtimeAvailabilityRules.Evaluate(
            onlineMatches.Length,
            realtimeDevices.Length,
            onlineMatches
                .Select(device => device.RealtimeUnavailableReason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason)));

        return new DashboardAreaGroupSummary(
            Id: group.Id,
            Name: group.Name,
            AreaLabel: group.AreaLabel,
            Description: group.Description,
            Priority: group.Priority,
            MemberCount: rules.Count > 0 ? rules.Count : group.ItemCount,
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
            PublicTotal: publicMatches.Count,
            PublicRunning: publicMatches.Count(device => device.CommunicationState == DeviceCommunicationState.Running),
            PublicStopped: publicMatches.Count(device => device.CommunicationState == DeviceCommunicationState.Stopped),
            PublicOffline: publicMatches.Count(device => device.CommunicationState == DeviceCommunicationState.Offline),
            PublicUnknown: publicMatches.Count(device => device.CommunicationState == DeviceCommunicationState.Unknown),
            PublicCoveredAreas: publicMatches
                .Select(device => (device.Building, device.Floor, device.SubArea))
                .Distinct()
                .Count(),
            ModeAbnormal: onlineMatches.Count(device =>
                !string.IsNullOrWhiteSpace(device.Mode) &&
                !string.Equals(device.Mode.Trim(), anomalySettings.NormalMode, StringComparison.OrdinalIgnoreCase)),
            TemperatureAbnormal: onlineMatches.Count(device =>
                DeviceTemperatureRules.TryRead(device.SetTemperature, out var temperature) &&
                (temperature < anomalySettings.TemperatureMin || temperature > anomalySettings.TemperatureMax)),
            LockOn: realtimeDevices.Count(device => device.Realtime?.LockStateValid == true && device.Realtime.LockState == "开启"),
            LockOff: realtimeDevices.Count(device => device.Realtime?.LockStateValid == true && device.Realtime.LockState == "关闭"),
            AreaType: legacyItemsEnabled
                ? group.SystemKey switch
                {
                    "public" => DeviceAreaClassifier.PublicArea,
                    "non_public" => DeviceAreaClassifier.PrivateArea,
                    _ => string.Empty,
                }
                : string.Empty,
            RealtimeAvailability: realtimeStatus.Availability,
            RealtimeStatusText: realtimeStatus.StatusText);
    }

    private static bool IsLegacySystemGroup(AreaGroupRecord group)
    {
        return group.GroupKind.Equals("system", StringComparison.OrdinalIgnoreCase) &&
            group.SystemKey is "public" or "non_public";
    }

    private static bool MatchesLegacySystemGroup(DeviceRecord device, string systemKey)
    {
        return systemKey switch
        {
            "public" => string.Equals(device.AreaType, DeviceAreaClassifier.PublicArea, StringComparison.OrdinalIgnoreCase),
            "non_public" => string.Equals(device.AreaType, DeviceAreaClassifier.PrivateArea, StringComparison.OrdinalIgnoreCase),
            _ => false,
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
