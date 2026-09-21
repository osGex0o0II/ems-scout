using System.Globalization;
using EmsScout.Application;
using EmsScout.Domain;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteDashboardSummaryRepository : IDashboardSummaryRepository
{
    private readonly Func<string> _databasePathResolver;

    public SqliteDashboardSummaryRepository(string databasePath)
        : this(() => databasePath)
    {
    }

    public SqliteDashboardSummaryRepository(Func<string> databasePathResolver)
    {
        _databasePathResolver = databasePathResolver;
    }

    public async Task<DashboardSummaryResult> LoadAsync(
        long? runId,
        CancellationToken cancellationToken = default)
    {
        var databasePath = _databasePathResolver();
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Cannot find EMS SQLite database.", databasePath);
        }

        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var source = runId is null
            ? (Pages: "pages", Joins: "JOIN pages p ON p.id = c.page_id JOIN sub_areas s ON s.id = p.sub_area_id", RunPredicate: "")
            : (Pages: "run_pages", Joins: "JOIN run_pages p ON p.id = c.run_page_id AND p.run_id = c.run_id JOIN run_sub_areas s ON s.id = p.run_sub_area_id AND s.run_id = p.run_id", RunPredicate: "WHERE c.run_id = $run_id");
        var cardsTable = runId is null ? "cards" : "run_cards";
        var collectedAtSql = await ColumnExistsAsync(connection, source.Pages, "collected_at", cancellationToken).ConfigureAwait(false)
            ? "MAX(p.collected_at)"
            : "NULL";
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.building, c.comm, COUNT(*) AS device_count,
                   {collectedAtSql} AS source_updated_at
            FROM {cardsTable} c
            {source.Joins}
            {source.RunPredicate}
            GROUP BY s.building, c.comm
            """;
        if (runId is not null)
        {
            command.Parameters.AddWithValue("$run_id", runId.Value);
        }

        var counts = new Dictionary<string, StateCounts>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? sourceUpdatedAt = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var building = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var state = DeviceCommunicationStateParser.Parse(reader.IsDBNull(1) ? null : reader.GetString(1));
            var count = Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture);
            var bucket = counts.GetValueOrDefault(building);
            counts[building] = bucket.Add(state, count);
            if (!reader.IsDBNull(3) && StoredTimestamp.TryParse(reader.GetString(3), out var collectedAt) &&
                (sourceUpdatedAt is null || collectedAt > sourceUpdatedAt))
            {
                sourceUpdatedAt = collectedAt;
            }
        }

        var buildings = new[] { "1号", "2号", "3号", "4号", "5号", "6号" }
            .Select(building => counts.GetValueOrDefault(building).ToBuildingSummary(building))
            .ToArray();
        var summary = new FleetSummary(
            buildings.Sum(item => item.Total),
            buildings.Sum(item => item.Running),
            buildings.Sum(item => item.Stopped),
            buildings.Sum(item => item.Offline),
            buildings.Sum(item => item.Unknown),
            buildings);
        var revision = $"{File.GetLastWriteTimeUtc(databasePath).Ticks}:{new FileInfo(databasePath).Length}:{runId?.ToString(CultureInfo.InvariantCulture) ?? "current"}";
        return new DashboardSummaryResult(summary, sourceUpdatedAt, revision);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct StateCounts(int Running, int Stopped, int Offline, int Unknown)
    {
        public StateCounts Add(DeviceCommunicationState state, int count) => state switch
        {
            DeviceCommunicationState.Running => this with { Running = Running + count },
            DeviceCommunicationState.Stopped => this with { Stopped = Stopped + count },
            DeviceCommunicationState.Offline => this with { Offline = Offline + count },
            _ => this with { Unknown = Unknown + count },
        };

        public BuildingSummary ToBuildingSummary(string building) =>
            new(building, Running + Stopped + Offline + Unknown, Running, Stopped, Offline, Unknown);
    }
}
