namespace EmsScout.Application.Quality;

public interface IRealtimeQualityAuditService
{
    Task<RealtimeQualityAuditReport?> LoadLatestAsync(CancellationToken cancellationToken = default);

    Task<RealtimeQualityAuditReport?> LoadForRunAsync(
        long runId,
        CancellationToken cancellationToken = default)
        => LoadLatestAsync(cancellationToken);
}

public sealed record RealtimeQualityAuditReport(
    string SourcePath,
    string CreatedAt,
    string SummarySource,
    int TotalRows,
    int UniqueDevices,
    bool CollectionOk,
    int CollectionErrorCount,
    int DeviceAnomalyRows,
    int DeviceAnomalyEvents,
    IReadOnlyList<RealtimeQualityCategory> CollectionErrorCategories,
    IReadOnlyList<RealtimeQualityCategory> DeviceAnomalyCategories,
    IReadOnlyList<RealtimeQualityBuilding> Buildings,
    string Note,
    long? RunId = null,
    bool IsStale = false,
    string StaleReason = "",
    string? BatchUid = null,
    string? RunKey = null);

public sealed record RealtimeQualityCategory(
    string Code,
    string Label,
    int Count);

public sealed record RealtimeQualityBuilding(
    string Building,
    int Rows,
    int CollectionErrors,
    int DeviceAnomalyRows,
    int DeviceAnomalyEvents,
    int InvalidRealtimeTags,
    int InvalidEnum,
    int OutOfRange,
    int InvalidLock);
