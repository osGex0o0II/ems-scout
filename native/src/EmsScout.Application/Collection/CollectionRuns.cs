namespace EmsScout.Application.Collection;

public interface ICollectionRunRepository
{
    Task<IReadOnlyList<CollectionRunRecord>> ListAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<CollectionRunRecord> SetAnomalyAsync(
        long runId,
        bool isAnomaly,
        string note,
        CancellationToken cancellationToken = default);

    Task<CollectionRunRestoreResult> RestoreCurrentAsync(
        long runId,
        CancellationToken cancellationToken = default);

    Task<CollectionRunDeleteResult> DeleteAsync(
        long runId,
        CancellationToken cancellationToken = default);
}

public sealed record CollectionRunRecord(
    long Id,
    string RunKey,
    string StartedAt,
    string CompletedAt,
    string ImportedAt,
    string Status,
    string Scope,
    IReadOnlyList<string> Buildings,
    string JsonPath,
    string DbSnapshotPath,
    int CardCount,
    int OnCount,
    int OffCount,
    int OfflineCount,
    int UnknownCount,
    string QualitySummary,
    bool IsAnomaly,
    string Note)
{
    public string ScopeLabel => Scope.Equals("partial", StringComparison.OrdinalIgnoreCase)
        ? string.Join("、", Buildings)
        : "全量";

    public string CountLabel => $"{CardCount:N0} 张";

    public string StatusLabel => IsAnomaly
        ? "异常隔离"
        : Status.ToLowerInvariant() switch
        {
            "completed" => "已完成",
            "backup" => "恢复前备份",
            "failed" => "失败",
            "stopped" => "已停止",
            _ => string.IsNullOrWhiteSpace(Status) ? "未知" : Status,
        };
}

public sealed record CollectionRunRestoreResult(
    long RunId,
    string RunKey,
    string CompletedAt,
    IReadOnlyList<string> Buildings,
    int RestoredCards,
    long? BackupRunId,
    bool IsPartial);

public sealed record CollectionRunDeleteResult(
    long RunId,
    string RunKey,
    string CompletedAt,
    int DeletedCards,
    int DeletedPages,
    int DeletedSubAreas,
    int DeletedBuildings);
