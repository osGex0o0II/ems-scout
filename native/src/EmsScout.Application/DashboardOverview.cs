using EmsScout.Domain;
using EmsScout.Application.Attention;

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
    string WatchState = "",
    string IssueId = "",
    string SourceKey = "",
    string IssueType = "",
    string Scope = "全部楼栋",
    long? RunId = null,
    string Status = AttentionIssueStatuses.Unprocessed,
    string IgnoreReason = "",
    DateTimeOffset? LastSeenAt = null)
{
    public bool IsActionable => !string.IsNullOrWhiteSpace(IssueId);

    public bool CanNavigate =>
        !string.IsNullOrWhiteSpace(CommunicationState) ||
        !string.IsNullOrWhiteSpace(RealtimeMatch) ||
        !string.IsNullOrWhiteSpace(RealtimePoints) ||
        !string.IsNullOrWhiteSpace(QuickFilter) ||
        !string.IsNullOrWhiteSpace(WatchState);
}
