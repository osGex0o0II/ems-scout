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
    string StaleReason)
{
    public IReadOnlyList<QualityAuditKnownFindingAnnotation> KnownFindingAnnotations { get; init; } = [];
}

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
    int UniformResolvedPages)
{
    public int KnownFindings { get; init; }

    public int BlockingKnownFindings { get; init; }

    public int NonBlockingKnownFindings { get; init; }

    public int OfflineTemplateWithoutStability { get; init; }

    public int OfflineTemplateStable { get; init; }

    public int InvalidCardFields { get; init; }

    public int ActiveFieldIncompletePages { get; init; }

    public int DetectedOfflineTemplateWithoutStability { get; init; }

    public int DetectedOfflineTemplateStable { get; init; }

    public int DetectedInvalidCardFields { get; init; }

    public int DetectedActiveFieldIncompletePages { get; init; }
}

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
