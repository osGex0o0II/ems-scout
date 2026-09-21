namespace EmsScout.Application.Groups;

public interface IAreaGroupSnapshotSource
{
    Task<AreaGroupSet> LoadHistoricalAreaConfigurationAsync(long runId, CancellationToken cancellationToken = default);
}
