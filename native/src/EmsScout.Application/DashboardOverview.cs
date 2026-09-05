using EmsScout.Domain;

namespace EmsScout.Application;

public sealed record DashboardOverview(
    string SourcePath,
    DateTimeOffset? SourceUpdatedAt,
    FleetSummary Summary,
    IReadOnlyList<OverviewMetric> Metrics,
    IReadOnlyList<DashboardRiskItem> Risks,
    IReadOnlyList<DashboardAreaGroupSummary> AreaGroups,
    string AreaGroupsError);

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
    int PublicCoveredAreas)
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
