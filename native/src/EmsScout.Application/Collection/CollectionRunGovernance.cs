namespace EmsScout.Application.Collection;

public interface ICollectionRunActivity
{
    bool IsActive { get; }

    IDisposable Begin();
}

public sealed class CollectionRunActivityRegistry : ICollectionRunActivity
{
    private int _activeCount;

    public bool IsActive => Volatile.Read(ref _activeCount) > 0;

    public IDisposable Begin()
    {
        Interlocked.Increment(ref _activeCount);
        return new Lease(this);
    }

    private void Release() => Interlocked.Decrement(ref _activeCount);

    private sealed class Lease(CollectionRunActivityRegistry owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}

public sealed record RunEvidenceStatus(
    string Kind,
    string State,
    string Reason,
    int? ExpectedCount = null,
    int? ActualCount = null,
    string? SourcePath = null);

public static class CurrentDataSourceStates
{
    public const string Bound = "bound";
    public const string Unresolved = "unresolved";
    public const string Stale = "stale";
}

public sealed record RunArtifactCandidate(
    string RelativePath,
    string Kind,
    bool IsShared,
    bool IdentityVerified);

public sealed record ArtifactCleanupResult(
    int DeletedCount,
    IReadOnlyList<string> PendingPaths,
    IReadOnlyList<string> PendingReasons)
{
    public bool IsComplete => PendingPaths.Count == 0;
}

public sealed record RunDeleteImpact(
    long RunId,
    string RunKey,
    bool IsCurrent,
    int SnapshotCards,
    int RealtimeRows,
    int Pages,
    int SubAreas,
    int Buildings,
    IReadOnlyList<RunArtifactCandidate> Artifacts,
    IReadOnlyList<string> BlockingReasons)
{
    public bool CanDelete => BlockingReasons.Count == 0;
}

public sealed record RunOperationRecord(
    Guid OperationId,
    string OperationType,
    long RunId,
    string RunKey,
    string BatchUid,
    string OccurredAt,
    string Result,
    string Summary,
    int DeletedCards,
    int DeletedPages,
    int DeletedSubAreas,
    int DeletedBuildings,
    int PendingArtifacts)
{
    public static RunOperationRecord Delete(string runKey, string batchUid, long runId, int deletedCards, string result) =>
        new(
            Guid.NewGuid(),
            "delete",
            runId,
            runKey,
            batchUid,
            StoredTimestamp.FormatLocal(DateTimeOffset.Now),
            result,
            $"删除历史快照 {runKey}",
            deletedCards,
            0,
            0,
            0,
            0);
}
