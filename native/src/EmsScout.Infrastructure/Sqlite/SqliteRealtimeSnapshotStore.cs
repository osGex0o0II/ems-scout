using System.Text.Json;
using EmsScout.Application;
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
        await SaveCoreAsync(runId, null, dataDirectory, buildings, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        long runId,
        string batchUid,
        string dataDirectory,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(batchUid))
        {
            throw new ArgumentException("批次 UID 不能为空。", nameof(batchUid));
        }

        await SaveCoreAsync(runId, batchUid.Trim(), dataDirectory, buildings, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveCoreAsync(
        long runId,
        string? expectedBatchUid,
        string dataDirectory,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken)
    {
        EnsureDatabaseExists();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var targetBatchUid = expectedBatchUid ??
            await LoadBatchUidAsync(connection, runId, cancellationToken).ConfigureAwait(false);
        if (targetBatchUid is not null &&
            !await RunHasBatchUidAsync(connection, runId, targetBatchUid, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"目标批次 #{runId} 与 batch_uid 不一致，未保存任何快照数据。");
        }

        var expectedRunKey = targetBatchUid is null
            ? null
            : await LoadRunKeyAsync(connection, runId, cancellationToken).ConfigureAwait(false);
        if (targetBatchUid is not null && string.IsNullOrWhiteSpace(expectedRunKey))
        {
            throw new InvalidOperationException($"目标批次 #{runId} 缺少 runKey，未保存任何快照数据。请先完成数据库迁移。");
        }

        var selectedBuildings = buildings
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedBuildings.Length == 0)
        {
            throw new InvalidOperationException("未指定需要保存的实时详情楼栋。");
        }

        var expectedCounts = await LoadRunCardCountsAsync(connection, runId, cancellationToken).ConfigureAwait(false);
        var pendingRows = new Dictionary<string, IReadOnlyList<RealtimeDetailRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var building in selectedBuildings)
        {
            var file = RealtimeLatestJsonSource.FindLatestFile(dataDirectory, building);
            if (string.IsNullOrWhiteSpace(file))
            {
                throw new InvalidOperationException($"缺少 {building} 的实时详情文件，未保存任何快照数据。请重新采集实时详情。");
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

            var sourceBatchUid = RealtimeLatestJsonSource.ReadBatchUid(document.RootElement);
            if (targetBatchUid is not null &&
                !string.Equals(sourceBatchUid, targetBatchUid, StringComparison.Ordinal))
            {
                var actual = string.IsNullOrWhiteSpace(sourceBatchUid) ? "缺失" : sourceBatchUid;
                throw new InvalidOperationException(
                    $"实时详情文件 {sourceFile} 批次 UID 不匹配：目标 {targetBatchUid}，文件 {actual}。请重新采集实时详情。");
            }

            var sourceRunKey = RealtimeLatestJsonSource.ReadRunKey(document.RootElement);
            if (expectedRunKey is not null &&
                !string.Equals(sourceRunKey, expectedRunKey, StringComparison.Ordinal))
            {
                var actual = string.IsNullOrWhiteSpace(sourceRunKey) ? "缺失" : sourceRunKey;
                throw new InvalidOperationException(
                    $"实时详情文件 {sourceFile} runKey 不匹配：目标 {expectedRunKey}，文件 {actual}。请重新采集实时详情。");
            }

            if (!RealtimeLatestJsonSource.HasSourceTimestamp(document.RootElement))
            {
                throw new InvalidOperationException($"实时详情文件 {sourceFile} 缺少采集时间，未保存任何快照数据。");
            }

            var updatedAt = RealtimeLatestJsonSource.ReadSourceUpdatedAt(document.RootElement, file);
            var rows = RealtimeLatestJsonSource.ReadRows(document.RootElement, building, sourceFile, updatedAt);
            if (rows.Count == 0)
            {
                throw new InvalidOperationException($"{building} 的实时详情 rows 为空，未保存任何快照数据。");
            }

            if (expectedCounts.TryGetValue(building, out var expected) && rows.Count != expected)
            {
                throw new InvalidOperationException(
                    $"{building} 实时详情数量 {rows.Count} 与批次 #{runId} 基础卡片数 {expected} 不一致，未保存任何快照数据。");
            }

            pendingRows[building] = rows;
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
                (run_id, batch_uid, source_row_id, building, floor, sub_area, page_name, name,
                 source_file, source_updated_at, payload_json)
            VALUES
                ($run_id, $batch_uid, $source_row_id, $building, $floor, $sub_area, $page_name, $name,
                 $source_file, $source_updated_at, $payload_json)
            """;
        var runIdParameter = insert.Parameters.Add("$run_id", SqliteType.Integer);
        var batchUidParameter = insert.Parameters.Add("$batch_uid", SqliteType.Text);
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
            foreach (var row in pendingRows[building])
            {
                runIdParameter.Value = runId;
                batchUidParameter.Value = targetBatchUid is null ? DBNull.Value : targetBatchUid;
                sourceRowIdParameter.Value = row.RowId;
                buildingParameter.Value = row.Building;
                floorParameter.Value = row.Floor.HasValue ? row.Floor.Value : DBNull.Value;
                subAreaParameter.Value = row.SubArea;
                pageNameParameter.Value = row.PageName;
                nameParameter.Value = row.Name;
                sourceFileParameter.Value = row.SourceFile;
                sourceUpdatedAtParameter.Value = StoredTimestamp.FormatLocal(row.SourceUpdatedAt);
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
        return await LoadCoreAsync(runId, null, buildings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RealtimeDetailSet> LoadAsync(
        long runId,
        string batchUid,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(batchUid))
        {
            throw new ArgumentException("批次 UID 不能为空。", nameof(batchUid));
        }

        return await LoadCoreAsync(runId, batchUid.Trim(), buildings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RealtimeDetailSet> LoadCoreAsync(
        long runId,
        string? expectedBatchUid,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken)
    {
        EnsureDatabaseExists();
        await using var connection = await OpenConnectionAsync(cancellationToken, readOnly: true).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, "run_realtime_details", cancellationToken).ConfigureAwait(false))
        {
            return new RealtimeDetailSet([], RealtimeDetailAvailability.MissingSnapshot);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = expectedBatchUid is null ? """
            SELECT payload_json
            FROM run_realtime_details
            WHERE run_id = $run_id
            ORDER BY id
            """ : """
            SELECT payload_json
            FROM run_realtime_details
            WHERE run_id = $run_id AND batch_uid = $batch_uid
            ORDER BY id
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        if (expectedBatchUid is not null)
        {
            command.Parameters.AddWithValue("$batch_uid", expectedBatchUid);
        }

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

        if (rows.Count == 0)
        {
            return new RealtimeDetailSet([], RealtimeDetailAvailability.MissingSnapshot,
                expectedBatchUid is null
                    ? $"批次 #{runId} 未保存实时详情快照。"
                    : $"批次 #{runId} 未保存匹配 batch_uid 的实时详情快照。");
        }

        var expectedCounts = await LoadRunCardCountsAsync(connection, runId, cancellationToken).ConfigureAwait(false);
        if (expectedCounts.Count > 0 && buildingSet.Count > 0)
        {
            var actualCounts = rows
                .GroupBy(row => row.Building, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var building in buildingSet)
            {
                if (!expectedCounts.TryGetValue(building, out var expected))
                {
                    return new RealtimeDetailSet([], RealtimeDetailAvailability.Unavailable,
                        $"批次 #{runId} 没有 {building} 的基础卡片范围，实时详情无法校验。");
                }

                var actual = actualCounts.GetValueOrDefault(building);
                if (actual != expected)
                {
                    return new RealtimeDetailSet([], RealtimeDetailAvailability.Unavailable,
                        $"批次 #{runId} 的 {building} 实时详情数量 {actual} 与基础卡片数 {expected} 不一致。");
                }
            }
        }

        return new RealtimeDetailSet(rows, RealtimeDetailAvailability.Available, null, runId, expectedBatchUid);
    }

    private static async Task<string?> LoadBatchUidAsync(
        SqliteConnection connection,
        long runId,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(connection, "collection_runs", "batch_uid", cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT NULLIF(TRIM(batch_uid), '') FROM collection_runs WHERE id = $run_id LIMIT 1";
        command.Parameters.AddWithValue("$run_id", runId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task<bool> RunHasBatchUidAsync(
        SqliteConnection connection,
        long runId,
        string batchUid,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(connection, "collection_runs", "batch_uid", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM collection_runs WHERE id = $run_id AND batch_uid = $batch_uid LIMIT 1";
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$batch_uid", batchUid);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<string?> LoadRunKeyAsync(
        SqliteConnection connection,
        long runId,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(connection, "collection_runs", "run_key", cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT NULLIF(TRIM(run_key), '') FROM collection_runs WHERE id = $run_id LIMIT 1";
        command.Parameters.AddWithValue("$run_id", runId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task<Dictionary<string, int>> LoadRunCardCountsAsync(
        SqliteConnection connection,
        long runId,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "run_cards", cancellationToken).ConfigureAwait(false) ||
            !await TableExistsAsync(connection, "run_pages", cancellationToken).ConfigureAwait(false) ||
            !await TableExistsAsync(connection, "run_sub_areas", cancellationToken).ConfigureAwait(false))
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sa.building, COUNT(*)
            FROM run_cards c
            JOIN run_pages p ON p.id = c.run_page_id AND p.run_id = c.run_id
            JOIN run_sub_areas sa ON sa.id = p.run_sub_area_id AND sa.run_id = c.run_id
            WHERE c.run_id = $run_id
            GROUP BY sa.building
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }

        return result;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken,
        bool readOnly = false)
    {
        var mode = readOnly ? "ReadOnly" : "ReadWrite";
        var connection = new SqliteConnection($"Data Source={databasePathResolver()};Mode={mode}");
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
                batch_uid TEXT,
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
        await AddColumnIfMissingAsync(connection, "run_realtime_details", "batch_uid", "TEXT", cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(connection, tableName, columnName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private void EnsureDatabaseExists()
    {
        var databasePath = databasePathResolver();
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Cannot find EMS SQLite database.", databasePath);
        }
    }
}
