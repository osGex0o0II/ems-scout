namespace EmsScout.Application.Quality;

public interface INativeQualityAuditService : IQualityAuditService
{
    Task<QualityAuditReport?> AuditAsync(
        NativeQualityAuditRequest request,
        CancellationToken cancellationToken = default);
}

public enum QualityAuditSourceKind
{
    LatestCompletedRun,
    Current,
    SpecificRun,
}

public sealed record NativeQualityAuditRequest(
    QualityAuditSourceKind SourceKind,
    string? DatabasePath = null,
    long? RunId = null)
{
    public static NativeQualityAuditRequest LatestCompletedRun { get; } =
        new(QualityAuditSourceKind.LatestCompletedRun);

    public static NativeQualityAuditRequest Current { get; } =
        new(QualityAuditSourceKind.Current);

    public static NativeQualityAuditRequest ForRun(long runId) =>
        new(QualityAuditSourceKind.SpecificRun, RunId: runId);
}

public sealed record QualityAuditKnownFindingAnnotation(
    string IssueCode,
    string Building,
    double? Floor,
    string SubArea,
    string PageName,
    string DeviceName,
    bool IsBlocking,
    IReadOnlyList<QualityAuditKnownFindingReference> Findings);

public sealed record QualityAuditKnownFindingReference(
    string Id,
    string Type,
    string Status,
    bool IsBlocking,
    string Reason,
    IReadOnlyList<string> Evidence);
