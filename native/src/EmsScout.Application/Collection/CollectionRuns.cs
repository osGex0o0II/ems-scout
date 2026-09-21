namespace EmsScout.Application.Collection;

public interface ICollectionRunRepository
{
    Task<IReadOnlyList<CollectionRunRecord>> ListAsync(
        int? limit = 50,
        CancellationToken cancellationToken = default);

    Task<CurrentDataSourceBinding> GetCurrentDataSourceAsync(
        CancellationToken cancellationToken = default);

    Task<RunDeleteImpact> GetDeleteImpactAsync(
        long runId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollectionRunDeleteResult>> DeleteManyAsync(
        IReadOnlyList<long> runIds,
        CancellationToken cancellationToken = default);

    Task<CollectionRunComparison> CompareCurrentAsync(
        long runId,
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

public sealed record CurrentDataSourceBinding(
    string State,
    long? RunId,
    int CardCount,
    string Reason)
{
    public bool IsBound =>
        State.Equals(CurrentDataSourceStates.Bound, StringComparison.OrdinalIgnoreCase) &&
        RunId is > 0;

    public bool Matches(CollectionRunRecord run) =>
        IsBound &&
        RunId == run.Id &&
        CardCount == run.CardCount;

    public static CurrentDataSourceBinding Unresolved(string reason, int cardCount = 0) =>
        new(CurrentDataSourceStates.Unresolved, null, cardCount, reason);
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
    string Note,
    int SnapshotCardCount = 0,
    string Source = "采集导入",
    string DataVersion = "v1.0.0",
    string Operator = "本机",
    long? RestoredFromRunId = null,
    string BatchUid = "",
    string LifecycleState = "completed",
    string? CurrentRevisionUid = null,
    string? RestoredFromBatchUid = null,
    long RunNumber = 0,
    string CollectionMode = "",
    long? DurationMs = null)
{
    public IReadOnlyDictionary<string, int> BuildingCardCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public bool RequiresReview =>
        Status.Equals("needs_review", StringComparison.OrdinalIgnoreCase) ||
        (Status.Equals("completed", StringComparison.OrdinalIgnoreCase) &&
         CollectionRunCompleteness.HasBlockingQualityFailure(QualitySummary));

    public string ScopeLabel => Scope.Equals("partial", StringComparison.OrdinalIgnoreCase)
        ? string.Join("、", Buildings)
        : "全量";

    public string CountLabel => $"{CardCount:N0} 张";

    public string StatusLabel => IsAnomaly
        ? "异常隔离"
        : RequiresReview
            ? "需复核"
        : Status.ToLowerInvariant() switch
        {
            "completed" => "已完成",
            "needs_review" => "需复核",
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
    int DeletedBuildings,
    Guid OperationId = default,
    ArtifactCleanupResult? ArtifactCleanup = null);
