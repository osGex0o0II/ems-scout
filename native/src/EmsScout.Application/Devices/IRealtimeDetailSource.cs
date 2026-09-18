namespace EmsScout.Application.Devices;

public interface IRealtimeDetailSource
{
    Task<RealtimeDetailSet> LoadAsync(IReadOnlyList<string> buildings, CancellationToken cancellationToken = default);

    Task<RealtimeDetailSet> LoadAsync(
        IReadOnlyList<string> buildings,
        long? expectedRunId,
        CancellationToken cancellationToken = default)
        => LoadAsync(buildings, cancellationToken);

    Task<RealtimeDetailSet> LoadAsync(
        IReadOnlyList<string> buildings,
        string? expectedBatchUid,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new RealtimeDetailSet(
            [],
            RealtimeDetailAvailability.MissingSnapshot,
            "实时详情源未实现批次 UID 校验，已拒绝自动绑定。"));

    Task<RealtimeDetailSet> LoadAsync(
        IReadOnlyList<string> buildings,
        long expectedRunId,
        string expectedBatchUid,
        string expectedRunKey,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new RealtimeDetailSet(
            [],
            RealtimeDetailAvailability.MissingSnapshot,
            "实时详情源未实现完整批次身份校验，已拒绝自动绑定。"));
}
