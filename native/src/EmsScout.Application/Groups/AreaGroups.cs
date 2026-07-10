namespace EmsScout.Application.Groups;

public interface IAreaGroupRepository
{
    Task<AreaGroupSet> LoadAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AreaGroupTargetOption>> LoadTargetOptionsAsync(
        string building,
        string floorLabel,
        CancellationToken cancellationToken = default);

    Task<AreaGroupRecord> SaveGroupAsync(
        AreaGroupEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteGroupAsync(long id, CancellationToken cancellationToken = default);

    Task<AreaGroupItemRecord> SaveItemAsync(
        AreaGroupItemEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteItemAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FloorCatalogRecord>> LoadFloorsAsync(
        string building,
        bool includeDisabled = false,
        CancellationToken cancellationToken = default);

    Task<FloorCatalogRecord> SaveFloorAsync(
        FloorCatalogEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteFloorAsync(long id, CancellationToken cancellationToken = default);
}

public sealed record AreaGroupSet(
    IReadOnlyList<AreaGroupRecord> Groups,
    IReadOnlyList<AreaGroupItemRecord> Items);

public sealed record AreaGroupRecord(
    long Id,
    string Name,
    string AreaLabel,
    string Description,
    string Priority,
    string GroupKind,
    string SystemKey,
    bool Locked,
    bool Enabled,
    int ItemCount,
    int Total,
    int OnCount,
    int OffCount,
    int OfflineCount,
    int UnknownCount,
    int CoveredAreas);

public sealed record AreaGroupItemRecord(
    long Id,
    long GroupId,
    string GroupName,
    string TargetType,
    string Building,
    string FloorLabel,
    double? FloorValue,
    string SubAreaText,
    string CardName,
    string Note);

public sealed record AreaGroupEdit(
    long? Id,
    string Name,
    string AreaLabel,
    string Description,
    string Priority,
    bool Enabled);

public sealed record AreaGroupItemEdit(
    long GroupId,
    string TargetType,
    string Building,
    string FloorLabel,
    string SubAreaText,
    string CardName,
    string Note,
    long? Id = null);

public sealed record AreaGroupTargetOption(
    string Type,
    string Building,
    string FloorLabel,
    double? FloorValue,
    string SubAreaText,
    string CardName,
    int Count);

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
