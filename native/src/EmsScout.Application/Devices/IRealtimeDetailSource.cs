namespace EmsScout.Application.Devices;

public interface IRealtimeDetailSource
{
    Task<RealtimeDetailSet> LoadAsync(IReadOnlyList<string> buildings, CancellationToken cancellationToken = default);
}
