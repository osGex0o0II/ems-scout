namespace EmsScout.Infrastructure.Importing;

public static class CollectionImportReportContract
{
    public const string Version = "ems.import-parity/v1";
}

public sealed record CollectionImportRequest(
    string SnapshotPath,
    string? DatabasePath = null,
    IReadOnlyList<string>? Buildings = null,
    bool Apply = false,
    string? MigrationBackupPath = null);

public sealed record CollectionImportParityReport(
    string ContractVersion,
    string Operation,
    bool ReadOnly,
    bool ApplyReady,
    string WorkflowId,
    string SnapshotPath,
    string? DatabasePath,
    string ScopeMode,
    string ReplacementMode,
    IReadOnlyList<string> ImportedBuildings,
    SnapshotCounts DeclaredCounts,
    SnapshotArtifactVerification Artifact,
    ImportInventorySummary SnapshotSelected,
    ImportInventorySummary? DatabaseBefore,
    ImportInventorySummary? DatabaseAfter,
    IReadOnlyList<BuildingParity> Buildings,
    IReadOnlyList<UserDataTableState> UserDataBefore,
    IReadOnlyList<UserDataTableState> UserDataAfter,
    IReadOnlyList<string> Issues,
    long? RunId,
    string? MigrationBackupPath);

public sealed record ImportInventorySummary(
    int BuildingCount,
    int SubAreaCount,
    int PageCount,
    int RawCardCount,
    int UniqueCardCount,
    int DeduplicatedObservationCount,
    ImportStatusCounts Statuses,
    IReadOnlyList<BuildingInventory> Buildings);

public sealed record BuildingInventory(
    string Building,
    bool Exists,
    int SubAreaCount,
    int PageCount,
    int RawCardCount,
    int UniqueCardCount,
    int DeduplicatedObservationCount,
    ImportStatusCounts Statuses);

public sealed record ImportStatusCounts(
    int Running,
    int Stopped,
    int Offline,
    int Unknown)
{
    public int Total => Running + Stopped + Offline + Unknown;
}

public sealed record BuildingParity(
    string Building,
    BuildingInventory Snapshot,
    BuildingInventory? DatabaseBefore,
    BuildingInventory? DatabaseAfter,
    bool BeforeMatches,
    bool AfterMatches);

public sealed record UserDataTableState(
    string Table,
    bool Exists,
    long RowCount,
    string? Sha256);

internal sealed record ImportSchemaState(
    int UserVersion,
    string QuickCheck,
    bool CoreReady,
    bool HistoryReady,
    bool IdentityReady,
    long NullCurrentIdentityRows,
    long NullRunIdentityRows,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<string> Issues => BlockingIssues.Concat(Warnings).ToArray();

    public bool ApplyReady =>
        UserVersion >= 2 &&
        QuickCheck.Equals("ok", StringComparison.OrdinalIgnoreCase) &&
        CoreReady &&
        HistoryReady &&
        IdentityReady &&
        NullCurrentIdentityRows == 0 &&
        BlockingIssues.Count == 0;
}
