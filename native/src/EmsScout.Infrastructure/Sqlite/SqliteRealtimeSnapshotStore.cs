using System.Text.Json;
using EmsScout.Application.Devices;
using EmsScout.Infrastructure.Realtime;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteRealtimeSnapshotStore(Func<string> databasePathResolver) : IRealtimeSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task SaveAsync(
        long runId,
        string dataDirectory,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var selectedBuildings = buildings
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedBuildings.Length == 0)
        {
            return;
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            var buildingParameters = string.Join(", ", selectedBuildings.Select((_, index) => "$building" + index));
            delete.CommandText = $"DELETE FROM run_realtime_details WHERE run_id = $run_id AND building IN ({buildingParameters})";
            delete.Parameters.AddWithValue("$run_id", runId);
            for (var index = 0; index < selectedBuildings.Length; index++)
            {
                delete.Parameters.AddWithValue("$building" + index, selectedBuildings[index]);
            }
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO run_realtime_details
                (run_id, source_row_id, building, floor, sub_area, page_name, name,
                 source_file, source_updated_at, payload_json)
            VALUES
                ($run_id, $source_row_id, $building, $floor, $sub_area, $page_name, $name,
                 $source_file, $source_updated_at, $payload_json)
            """;
        var runIdParameter = insert.Parameters.Add("$run_id", SqliteType.Integer);
        var sourceRowIdParameter = insert.Parameters.Add("$source_row_id", SqliteType.Text);
        var buildingParameter = insert.Parameters.Add("$building", SqliteType.Text);
        var floorParameter = insert.Parameters.Add("$floor", SqliteType.Real);
        var subAreaParameter = insert.Parameters.Add("$sub_area", SqliteType.Text);
        var pageNameParameter = insert.Parameters.Add("$page_name", SqliteType.Text);
        var nameParameter = insert.Parameters.Add("$name", SqliteType.Text);
        var sourceFileParameter = insert.Parameters.Add("$source_file", SqliteType.Text);
        var sourceUpdatedAtParameter = insert.Parameters.Add("$source_updated_at", SqliteType.Text);
        var payloadParameter = insert.Parameters.Add("$payload_json", SqliteType.Text);

        foreach (var building in selectedBuildings)
        {
            var file = RealtimeLatestJsonSource.FindLatestFile(dataDirectory, building);
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            await using var stream = File.OpenRead(file);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var sourceFile = Path.GetRelativePath(dataDirectory, file);
            var sourceRunId = RealtimeLatestJsonSource.ReadRunId(document.RootElement);
            if (sourceRunId != runId)
            {
                var actual = sourceRunId is null ? "缺失" : $"#{sourceRunId}";
                throw new InvalidOperationException(
                    $"实时详情文件 {sourceFile} 批次不匹配：目标批次 #{runId}，文件批次 {actual}。请重新采集实时详情。");
            }
            var updatedAt = RealtimeLatestJsonSource.ReadSourceUpdatedAt(document.RootElement, file);
            var rows = RealtimeLatestJsonSource.ReadRows(document.RootElement, building, sourceFile, updatedAt);
            foreach (var row in rows)
            {
                runIdParameter.Value = runId;
                sourceRowIdParameter.Value = row.RowId;
                buildingParameter.Value = row.Building;
                floorParameter.Value = row.Floor.HasValue ? row.Floor.Value : DBNull.Value;
                subAreaParameter.Value = row.SubArea;
                pageNameParameter.Value = row.PageName;
                nameParameter.Value = row.Name;
                sourceFileParameter.Value = row.SourceFile;
                sourceUpdatedAtParameter.Value = row.SourceUpdatedAt.ToString("O");
                payloadParameter.Value = JsonSerializer.Serialize(row, JsonOptions);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RealtimeDetailSet> LoadAsync(
        long runId,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = await OpenConnectionAsync(cancellationToken, readOnly: true).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, "run_realtime_details", cancellationToken).ConfigureAwait(false))
        {
            return new RealtimeDetailSet([], RealtimeDetailAvailability.MissingSnapshot);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM run_realtime_details
            WHERE run_id = $run_id
            ORDER BY id
            """;
        command.Parameters.AddWithValue("$run_id", runId);

        var buildingSet = buildings
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<RealtimeDetailRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var payload = reader.GetString(0);
            var row = JsonSerializer.Deserialize<RealtimeDetailRecord>(payload, JsonOptions);
            if (row is not null && (buildingSet.Count == 0 || buildingSet.Contains(row.Building)))
            {
                rows.Add(row);
            }
        }

        return rows.Count == 0
            ? new RealtimeDetailSet([], RealtimeDetailAvailability.MissingSnapshot)
            : new RealtimeDetailSet(rows);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken,
        bool readOnly = false)
    {
        var mode = readOnly ? "ReadOnly" : "ReadWrite";
        var connection = new SqliteConnection($"Data Source={databasePathResolver()};Mode={mode};Cache=Shared");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS run_realtime_details (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL,
                source_row_id TEXT NOT NULL,
                building TEXT NOT NULL,
                floor REAL,
                sub_area TEXT,
                page_name TEXT,
                name TEXT,
                source_file TEXT,
                source_updated_at TEXT,
                payload_json TEXT NOT NULL,
                FOREIGN KEY(run_id) REFERENCES collection_runs(id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_run_realtime_details_row
                ON run_realtime_details(run_id, source_row_id);
            CREATE INDEX IF NOT EXISTS idx_run_realtime_details_run_building
                ON run_realtime_details(run_id, building);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureDatabaseExists()
    {
        var databasePath = databasePathResolver();
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Cannot find EMS SQLite database.", databasePath);
        }
    }
}
