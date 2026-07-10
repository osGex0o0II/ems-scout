using EmsScout.Domain;

namespace EmsScout.Application;

public sealed record DashboardOverview(
    string SourcePath,
    DateTimeOffset SourceUpdatedAt,
    FleetSummary Summary,
    IReadOnlyList<OverviewMetric> Metrics,
    IReadOnlyList<DashboardRiskItem> Risks);

public sealed record OverviewMetric(
    string Label,
    string Value,
    string Detail,
    OverviewMetricKind Kind);

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
