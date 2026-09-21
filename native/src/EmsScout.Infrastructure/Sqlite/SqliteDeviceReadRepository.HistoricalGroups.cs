using EmsScout.Application.Groups;

namespace EmsScout.Infrastructure.Sqlite;

public sealed partial class SqliteDeviceReadRepository : IAreaGroupSnapshotSource
{
    public Task<AreaGroupSet> LoadHistoricalAreaConfigurationAsync(long runId, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            EnsureDatabaseExists();
            await using var connection = OpenConnection();
            var groups = await LoadEnabledAreaGroupRulesAsync(connection, null, runId, cancellationToken).ConfigureAwait(false);
            return new AreaGroupSet(groups.Select(group => new AreaGroupRecord(
                group.GroupId, group.Name, string.Empty, string.Empty, string.Empty, true,
                group.Rules.Count, 0, 0, 0, 0, 0, 0)).ToArray(), groups.SelectMany(group => group.Rules).ToArray());
        }, cancellationToken);
}
