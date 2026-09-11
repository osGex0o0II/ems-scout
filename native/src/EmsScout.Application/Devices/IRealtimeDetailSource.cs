namespace EmsScout.Application.Devices;

public interface IRealtimeDetailSource
{
    Task<RealtimeDetailSet> LoadAsync(IReadOnlyList<string> buildings, CancellationToken cancellationToken = default);

    Task<RealtimeDetailSet> LoadAsync(
        IReadOnlyList<string> buildings,
        long? expectedRunId,
        CancellationToken cancellationToken = default)
        => LoadAsync(buildings, cancellationToken);
}
