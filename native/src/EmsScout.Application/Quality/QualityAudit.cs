namespace EmsScout.Application.Quality;

public interface IQualityAuditService
{
    Task<QualityAuditReport?> LoadLatestAsync(CancellationToken cancellationToken = default);
}

public sealed record QualityAuditReport(
    string SourcePath,
    string GeneratedAt,
    string GeneratedAtLocal,
    long? RunId,
    QualityAuditSummary Summary,
    IReadOnlyList<QualityAuditIssue> Issues,
    bool IsStale,
    string StaleReason);

public sealed record QualityAuditSummary(
    int TotalCards,
    int IssueCount,
    int PlaceholderCards,
    int StateMismatch,
    int UnknownCommunication,
    int MissingIndicator,
    int UnknownSwitch,
    int DuplicateCardsSamePage,
    int DuplicateRenderedPages,
    int EmptySubAreas,
    int InlineSubAreas,
    int SuspiciousUniformPages,
    int UniformResolvedPages);

public sealed record QualityAuditIssue(
    string Severity,
    string Code,
    int Count,
    string Message)
{
    public string Label => string.IsNullOrWhiteSpace(Code)
        ? Severity
        : $"{Severity} · {Code}";
}
