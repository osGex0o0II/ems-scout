namespace EmsScout.Application.Quality;

public interface ICollectionIssueService
{
    Task<CollectionIssueReport> LoadForRunAsync(
        long runId,
        CancellationToken cancellationToken = default);
}

public sealed record CollectionIssueReport(
    long RunId,
    IReadOnlyList<CollectionIssueRecord> Records,
    IReadOnlyList<CollectionIssueCategory> Categories,
    IReadOnlyList<string> Warnings);

public sealed record CollectionIssueCategory(
    string Code,
    string Label,
    string Severity,
    int Count);

public sealed record CollectionIssueRecord(
    long BatchId,
    string BatchUid,
    string IssueType,
    string Severity,
    string Building,
    string Floor,
    string Zone,
    string PageName,
    string DeviceName,
    string DeviceId,
    string CollectedAt,
    string ObservedValue,
    string Evidence,
    string CollectorDecision,
    string Reason,
    string Attribution,
    string ResolutionState,
    string SourceArtifact,
    string SourcePath);
