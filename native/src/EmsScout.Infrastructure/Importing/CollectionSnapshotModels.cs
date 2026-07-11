using System.Text.Json;

namespace EmsScout.Infrastructure.Importing;

public static class CollectionSnapshotContractV1
{
    public const string Version = "ems.collection-snapshot/v1";
    public const string ArtifactHashScope = "canonical-buildings-payload";
}

public sealed class CollectionSnapshotV1
{
    public required string ContractVersion { get; init; }
    public required string WorkflowId { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required SnapshotScope Scope { get; init; }
    public required SnapshotLineage Lineage { get; init; }
    public required SnapshotVersions Versions { get; init; }
    public required SnapshotCounts Counts { get; init; }
    public required SnapshotQuality Quality { get; init; }
    public required SnapshotArtifact Artifact { get; init; }
    public required List<SnapshotBuilding> Buildings { get; init; }
}

public sealed class SnapshotScope
{
    public required string Mode { get; init; }
    public required List<string> Buildings { get; init; }
    public required List<string> Targets { get; init; }
}

public sealed class SnapshotLineage
{
    public required string? BaseArtifactSha256 { get; init; }
    public required string? ParentWorkflowId { get; init; }
}

public sealed class SnapshotVersions
{
    public required string Collector { get; init; }
    public required string Playwright { get; init; }
    public required string Rules { get; init; }
    public required string DatabaseSchema { get; init; }
    public required string SourceRevision { get; init; }
}

public sealed class SnapshotCounts
{
    public required int BuildingCount { get; init; }
    public required int SubAreaCount { get; init; }
    public required int PageCount { get; init; }
    public required int RawCardCount { get; init; }
    public required int UniqueCardCount { get; init; }
}

public sealed class SnapshotQuality
{
    public required string Decision { get; init; }
    public required List<SnapshotFinding> Findings { get; init; }
    public required List<SnapshotRetryEvidence> Retries { get; init; }
}

public sealed class SnapshotFinding
{
    public required string Code { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public required string? SourceKey { get; init; }
}

public sealed class SnapshotRetryEvidence
{
    public required string SourceKey { get; init; }
    public required int Attempt { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required string Outcome { get; init; }
}

public sealed class SnapshotArtifact
{
    public required string HashScope { get; init; }
    public required string Sha256 { get; init; }
    public required long Bytes { get; init; }
}

public sealed class SnapshotBuilding
{
    public required string SourceKey { get; init; }
    public required string Building { get; init; }
    public required string? MenuClicked { get; init; }
    public required int SubAreaCount { get; init; }
    public required List<SnapshotSubArea> SubAreas { get; init; }
}

public sealed class SnapshotSubArea
{
    public required string SourceKey { get; init; }
    public required int Idx { get; init; }
    public required double? Floor { get; init; }
    public required string? FloorLabel { get; init; }
    public required string Text { get; init; }
    public required double? X { get; init; }
    public required double? Y { get; init; }
    public required SnapshotSubAreaSourceEvidence SourceEvidence { get; init; }
    public required List<SnapshotPage> Pages { get; init; }
}

public sealed class SnapshotSubAreaSourceEvidence
{
    public required JsonElement Err { get; init; }
}

public sealed class SnapshotPage
{
    public required string SourceKey { get; init; }
    public required string Page { get; init; }
    public required int RawCount { get; init; }
    public required int UniqueCount { get; init; }
    public required List<SnapshotDuplicateEvidence> Duplicates { get; init; }
    public required string? Layout { get; init; }
    public required SnapshotPageQuality Quality { get; init; }
    public required SnapshotPageSourceEvidence SourceEvidence { get; init; }
    public required List<SnapshotCard> Cards { get; init; }
}

public sealed class SnapshotDuplicateEvidence
{
    public required string Name { get; init; }
    public required int Copies { get; init; }
    public required List<string> SourceKeys { get; init; }
}

public sealed class SnapshotPageQuality
{
    public required string Decision { get; init; }
    public required string Reason { get; init; }
    public required int Attempts { get; init; }
}

public sealed class SnapshotPageSourceEvidence
{
    public required JsonElement Count { get; init; }
    public required JsonElement OnHref { get; init; }
    public required JsonElement OffHref { get; init; }
    public required JsonElement QualityReason { get; init; }
    public required JsonElement DuplicateNames { get; init; }
    public required JsonElement Err { get; init; }
}

public sealed class SnapshotCard
{
    public required string SourceKey { get; init; }
    public required string? DeviceUid { get; init; }
    public required string Name { get; init; }
    public required string? Switch { get; init; }
    public required string? Mode { get; init; }
    public required double? Indoor { get; init; }
    public required double? SetTemp { get; init; }
    public required string? Fan { get; init; }
    public required string? Indicator { get; init; }
    public required string Comm { get; init; }
    public required SnapshotCardSourceEvidence SourceEvidence { get; init; }
}

public sealed class SnapshotCardSourceEvidence
{
    public required SnapshotRawCardEvidence Raw { get; init; }
    public required int? NameFloor { get; init; }
}

public sealed class SnapshotRawCardEvidence
{
    public required JsonElement Name { get; init; }
    public required JsonElement Switch { get; init; }
    public required JsonElement Mode { get; init; }
    public required JsonElement Indoor { get; init; }
    public required JsonElement SetTemp { get; init; }
    public required JsonElement Fan { get; init; }
    public required JsonElement Indicator { get; init; }
    public required JsonElement Comm { get; init; }
}

public sealed record SnapshotArtifactVerification(
    string HashScope,
    string DeclaredSha256,
    string ComputedSha256,
    long DeclaredBytes,
    long ComputedBytes,
    bool IsValid);

public sealed record CollectionSnapshotReadResult(
    string SourcePath,
    CollectionSnapshotV1 Snapshot,
    SnapshotArtifactVerification ArtifactVerification);

public sealed class CollectionSnapshotContractException : IOException
{
    public CollectionSnapshotContractException(string message)
        : base(message)
    {
    }

    public CollectionSnapshotContractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
