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
        var rulesByGroup = groupSet.RuleRecords
            .GroupBy(rule => rule.GroupId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AreaGroupRuleRecord>)group.ToArray());
        var preparedRulesByGroup = rulesByGroup.ToDictionary(
            pair => pair.Key,
            pair => AreaGroupRuleMatcher.Prepare(pair.Value));

        return groupSet.Groups
            .Where(group => group.Enabled)
            .OrderBy(group => PriorityRank(group.Priority))
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildSummary(
                group,
                devices,
                rulesByGroup.GetValueOrDefault(group.Id, []),
                preparedRulesByGroup.GetValueOrDefault(group.Id, AreaGroupRuleMatcher.Prepare([])),
                anomalySettings))
            .ToArray();
    }

    private static DashboardAreaGroupSummary BuildSummary(
        AreaGroupRecord group,
        IReadOnlyList<DeviceRecord> devices,
        IReadOnlyList<AreaGroupRuleRecord> rules,
        PreparedAreaGroupRules preparedRules,
        DashboardAnomalySettings anomalySettings)
    {
        var matches = devices.Where(device => AreaGroupRuleMatcher.MatchesAny(device, preparedRules)).ToArray();
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
            MemberCount: rules.Count,
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
            ModeAbnormal: onlineMatches.Count(device =>
                !string.IsNullOrWhiteSpace(device.Mode) &&
                !string.Equals(device.Mode.Trim(), anomalySettings.NormalMode, StringComparison.OrdinalIgnoreCase)),
            TemperatureAbnormal: onlineMatches.Count(device =>
                DeviceTemperatureRules.TryRead(device.SetTemperature, out var temperature) &&
                (temperature < anomalySettings.TemperatureMin || temperature > anomalySettings.TemperatureMax)),
            LockOn: realtimeDevices.Count(device => device.Realtime?.LockStateValid == true && device.Realtime.LockState == "开启"),
            LockOff: realtimeDevices.Count(device => device.Realtime?.LockStateValid == true && device.Realtime.LockState == "关闭"),
            AreaType: string.Empty,
            RealtimeAvailability: realtimeStatus.Availability,
            RealtimeStatusText: realtimeStatus.StatusText);
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
