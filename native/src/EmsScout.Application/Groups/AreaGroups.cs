namespace EmsScout.Application.Groups;

public interface IAreaGroupRepository
{
    Task<AreaGroupSet> LoadAsync(CancellationToken cancellationToken = default);

    // Compatibility default for repositories that have not separated startup migration
    // from normal reads. SQLite overrides this and preserves statistics without writes.
    Task<AreaGroupSet> LoadReadOnlyAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(cancellationToken);

    // Compatibility default for repositories that have not yet split configuration from statistics.
    // Implementations backed by SQLite should override this with a configuration-only read.
    Task<AreaGroupSet> LoadConfigurationAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(cancellationToken);

    Task<AreaGroupRecord> SaveGroupAsync(
        AreaGroupEdit edit,
        CancellationToken cancellationToken = default);

    Task<AreaGroupRecord> SaveConfigurationAsync(
        AreaGroupEdit edit,
        IReadOnlyList<AreaGroupRuleEdit> rules,
        CancellationToken cancellationToken = default);

    Task DeleteGroupAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FloorCatalogRecord>> LoadFloorsAsync(
        string building,
        bool includeDisabled = false,
        CancellationToken cancellationToken = default);

    Task<FloorCatalogRecord> SaveFloorAsync(
        FloorCatalogEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteFloorAsync(long id, CancellationToken cancellationToken = default);

    Task<AreaGroupRuleRecord> SaveRuleAsync(
        AreaGroupRuleEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteRuleAsync(long id, CancellationToken cancellationToken = default);

    Task<AreaGroupTransferDocument> ExportAsync(CancellationToken cancellationToken = default);

    Task ImportAsync(
        AreaGroupTransferDocument document,
        CancellationToken cancellationToken = default);
}

public sealed record AreaGroupSet(
    IReadOnlyList<AreaGroupRecord> Groups,
    IReadOnlyList<AreaGroupRuleRecord> RuleRecords);

public sealed record AreaGroupRecord(
    long Id,
    string Name,
    string AreaLabel,
    string Description,
    string Priority,
    bool Enabled,
    int ItemCount,
    int Total,
    int OnCount,
    int OffCount,
    int OfflineCount,
    int UnknownCount,
    int CoveredAreas,
    string GroupKey = "");

public sealed record AreaGroupEdit(
    long? Id,
    string Name,
    string AreaLabel,
    string Description,
    string Priority,
    bool Enabled,
    string GroupKey = "");

public sealed record FloorCatalogRecord(
    long Id,
    string Building,
    string FloorLabel,
    double FloorValue,
    string Source,
    bool Enabled,
    string Note);

public sealed record FloorCatalogEdit(
    long? Id,
    string Building,
    string FloorLabel,
    bool Enabled,
    string Note);
