namespace EmsScout.Application.Attention;

public static class AttentionIssueStatuses
{
    public const string Unprocessed = "unprocessed";
    public const string Acknowledged = "acknowledged";
    public const string Ignored = "ignored";
    public const string Resolved = "resolved";

    public static bool IsKnown(string value) =>
        value is Unprocessed or Acknowledged or Ignored or Resolved;
}

public sealed record AttentionNavigationTarget(
    string Destination,
    string CommunicationState = "",
    string RealtimeMatch = "",
    string RealtimePoints = "",
    string QuickFilter = "",
    string WatchState = "");

public sealed record AttentionIssueCandidate(
    string IssueId,
    string SourceKey,
    string IssueType,
    OverviewMetricKind Severity,
    long? RunId,
    string Title,
    string Detail,
    string Scope,
    int Count,
    AttentionNavigationTarget Navigation);

public sealed record AttentionQueueSnapshot(
    IReadOnlyList<AttentionIssueCandidate> Candidates,
    IReadOnlySet<string> ObservedSources,
    DateTimeOffset ObservedAt);

public sealed record AttentionIssueRecord(
    string IssueId,
    string SourceKey,
    string IssueType,
    OverviewMetricKind Severity,
    long? RunId,
    string Title,
    string Detail,
    string Scope,
    int Count,
    AttentionNavigationTarget Navigation,
    string Status,
    string IgnoreReason,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ResolvedAt);

public interface IAttentionIssueRepository
{
    Task<IReadOnlyList<AttentionIssueRecord>> SynchronizeAsync(
        AttentionQueueSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task<AttentionIssueRecord> SetStatusAsync(
        string issueId,
        string targetStatus,
        string? reason = null,
        CancellationToken cancellationToken = default);
}

public sealed record AttentionIssueTransition(string Status, string Reason);

public static class AttentionIssuePolicy
{
    public static AttentionIssueTransition ValidateTransition(
        string currentStatus,
        string targetStatus,
        string? reason)
    {
        ValidateKnownStatus(currentStatus, nameof(currentStatus));
        ValidateKnownStatus(targetStatus, nameof(targetStatus));

        if (currentStatus == AttentionIssueStatuses.Resolved &&
            targetStatus is not AttentionIssueStatuses.Resolved and not AttentionIssueStatuses.Unprocessed)
        {
            throw new InvalidOperationException("A resolved issue must be reopened before another manual state is applied.");
        }

        var normalizedReason = reason?.Trim() ?? string.Empty;
        if (targetStatus == AttentionIssueStatuses.Ignored && normalizedReason.Length == 0)
        {
            throw new ArgumentException("Ignoring an attention issue requires a reason.", nameof(reason));
        }

        return new AttentionIssueTransition(targetStatus, normalizedReason);
    }

    private static void ValidateKnownStatus(string status, string parameterName)
    {
        if (!AttentionIssueStatuses.IsKnown(status))
        {
            throw new ArgumentOutOfRangeException(parameterName, status, "Unknown attention issue status.");
        }
    }
}
