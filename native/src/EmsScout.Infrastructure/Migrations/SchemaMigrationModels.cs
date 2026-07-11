namespace EmsScout.Infrastructure.Migrations;

public enum SchemaIssueSeverity
{
    Info,
    Warning,
    Error,
}

public enum SchemaChangeKind
{
    CreateTable,
    AddColumn,
    CreateIndex,
    RecordMigration,
    SetUserVersion,
}

public sealed record SchemaAuditIssue(
    SchemaIssueSeverity Severity,
    string Code,
    string Message);

public sealed record SchemaPendingChange(
    SchemaChangeKind Kind,
    string ObjectName,
    string Description);

public sealed record SchemaAuditReport(
    string DatabasePath,
    DateTimeOffset AuditedAt,
    string DatabaseShape,
    int UserVersion,
    int LatestSupportedVersion,
    string JournalMode,
    string QuickCheck,
    bool UsedImmutableReadOnlyFallback,
    bool CanMigrate,
    bool IsCurrent,
    IReadOnlyList<string> Tables,
    IReadOnlyList<SchemaAuditIssue> Issues,
    IReadOnlyList<SchemaPendingChange> PendingChanges)
{
    public bool HasErrors => Issues.Any(issue => issue.Severity == SchemaIssueSeverity.Error);
    public int UnresolvedIdentityAmbiguities { get; init; }
    public int AutoResolvedIdentityAmbiguities { get; init; }
}

public sealed record SchemaMigrationResult(
    string DatabasePath,
    string? BackupPath,
    IReadOnlyList<int> AppliedVersions,
    IReadOnlyList<SchemaPendingChange> AppliedChanges,
    SchemaAuditReport Before,
    SchemaAuditReport After)
{
    public DeviceIdentityMigrationReport? IdentityReport { get; init; }
}

public sealed record DeviceIdentityAmbiguityRecord(
    long Id,
    string EntityTable,
    string EntityKey,
    string ReasonCode,
    string Status,
    string? SourceKey,
    string IdentityJson,
    IReadOnlyList<string> CandidateDeviceUids,
    string? ResolvedDeviceUid,
    string ResolutionNote);

public sealed record DeviceIdentityMigrationReport(
    int CurrentCardCount,
    int CurrentSourceKeyCount,
    int CurrentDeviceUidCount,
    int RunCardCount,
    int RunSourceKeyCount,
    int RunDeviceUidCount,
    int RegistryCount,
    int SourceAliasCount,
    int UserReferenceCount,
    int UserReferenceResolvedCount,
    IReadOnlyList<DeviceIdentityAmbiguityRecord> Ambiguities)
{
    public int UnresolvedCount => Ambiguities.Count(item => item.Status == "unresolved");
    public int AutoResolvedCount => Ambiguities.Count(item => item.Status == "auto_resolved");
}

public sealed class SchemaMigrationException : Exception
{
    public SchemaMigrationException(string message, string? backupPath = null, Exception? innerException = null)
        : base(message, innerException)
    {
        BackupPath = backupPath;
    }

    public string? BackupPath { get; }
}
