using EmsScout.Domain;

namespace EmsScout.Application;

public sealed record DashboardOverview(
    string SourcePath,
    DateTimeOffset? SourceUpdatedAt,
    FleetSummary Summary,
    IReadOnlyList<OverviewMetric> Metrics,
    IReadOnlyList<DashboardAreaGroupSummary> AreaGroups,
    string AreaGroupsError,
    DashboardRealtimeAvailability RealtimeAvailability = DashboardRealtimeAvailability.NotApplicable,
    string RealtimeStatusText = "");

public sealed record OverviewMetric(
    string Label,
    string Value,
    string Detail,
    OverviewMetricKind Kind,
    string CommunicationState = "",
    string AreaType = "");

public sealed record DashboardAreaGroupSummary(
    long Id,
    string Name,
    string AreaLabel,
    string Description,
    string Priority,
    int MemberCount,
    int Total,
    int Online,
    int Offline,
    int Unknown,
    int Running,
    int Stopped,
    int CoveredAreas,
    int PublicTotal,
    int PublicRunning,
    int PublicStopped,
    int PublicOffline,
    int PublicUnknown,
    int PublicCoveredAreas,
    int ModeAbnormal,
    int TemperatureAbnormal,
    int LockOn,
    int LockOff,
    string AreaType = "",
    DashboardRealtimeAvailability RealtimeAvailability = DashboardRealtimeAvailability.NotApplicable,
    string RealtimeStatusText = "")
{
    public int Attention => Offline + Unknown;

    public int PublicAttention => PublicOffline + PublicUnknown;

    public double PublicRunningRate => PublicTotal == 0 ? 0 : PublicRunning / (double)PublicTotal;

    public int PrivateTotal => Math.Max(0, Total - PublicTotal);

    public int PrivateRunning => Math.Max(0, Running - PublicRunning);

    public int PrivateStopped => Math.Max(0, Stopped - PublicStopped);

    public int PrivateOffline => Math.Max(0, Offline - PublicOffline);

    public int PrivateUnknown => Math.Max(0, Unknown - PublicUnknown);
}

public enum DashboardRealtimeAvailability
{
    NotApplicable,
    Available,
    Partial,
    Unavailable,
}

public static class DashboardRealtimeAvailabilityRules
{
    public static (DashboardRealtimeAvailability Availability, string StatusText) Evaluate(
        int expectedOnline,
        int matchedOnline,
        string? unavailableReason = null)
    {
        if (expectedOnline <= 0)
        {
            return (DashboardRealtimeAvailability.NotApplicable, "当前没有在线设备可核对实时详情");
        }

        if (matchedOnline <= 0)
        {
            return (
                DashboardRealtimeAvailability.Unavailable,
                string.IsNullOrWhiteSpace(unavailableReason) ? "实时详情不可用" : unavailableReason);
        }

        if (matchedOnline < expectedOnline)
        {
            var missingCount = expectedOnline - matchedOnline;
            var suffix = string.IsNullOrWhiteSpace(unavailableReason)
                ? $"仍有 {missingCount:N0} 台在线设备未匹配"
                : unavailableReason;
            return (DashboardRealtimeAvailability.Partial, $"实时详情部分可用：{suffix}");
        }

        return (DashboardRealtimeAvailability.Available, "实时详情已完整匹配");
    }
}

public sealed record DashboardAnomalySettings(
    string NormalMode,
    double TemperatureMin,
    double TemperatureMax)
{
    public static DashboardAnomalySettings Default { get; } = new("制冷", 22, 26);
}

public enum OverviewMetricKind
{
    Neutral,
    Info,
    Success,
    Warning,
    Danger
}

public sealed record DashboardRiskItem(
    string Title,
    string Detail,
    string Source,
    OverviewMetricKind Kind,
    int Count = 0,
    string ActionLabel = "",
    string CommunicationState = "",
    string RealtimeMatch = "",
    string RealtimePoints = "",
    string QuickFilter = "",
    string WatchState = "")
{
    public bool CanNavigate =>
        !string.IsNullOrWhiteSpace(CommunicationState) ||
        !string.IsNullOrWhiteSpace(RealtimeMatch) ||
        !string.IsNullOrWhiteSpace(RealtimePoints) ||
        !string.IsNullOrWhiteSpace(QuickFilter) ||
        !string.IsNullOrWhiteSpace(WatchState);
}
