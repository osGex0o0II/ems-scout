namespace EmsScout.Application.Devices;

public interface IRealtimeSnapshotStore
{
    Task SaveAsync(
        long runId,
        string dataDirectory,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        long runId,
        string batchUid,
        string dataDirectory,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken = default);

    Task<RealtimeDetailSet> LoadAsync(
        long runId,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken = default);

    Task<RealtimeDetailSet> LoadAsync(
        long runId,
        string batchUid,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken = default);
}
