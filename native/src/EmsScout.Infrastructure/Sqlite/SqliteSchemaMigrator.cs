using Microsoft.Data.Sqlite;
using EmsScout.Application;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteSchemaMigrator(Func<string> databasePathResolver)
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = databasePathResolver();
        if (!File.Exists(databasePath))
        {
            return;
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await EnsureCollectionRunColumnsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureGovernanceSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureLegacyIdentityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureBatchUidIndexAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureLegacyCurrentDataStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureQualityColumnsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureRealtimeSnapshotSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureAreaGroupRuleSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await AreaGroupRuleOrderMigration.ApplyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureAreaGroupRuleSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "monitor_groups", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await AddColumnIfMissingAsync(connection, transaction, "monitor_groups", "group_key", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS ux_monitor_groups_group_key
                    ON monitor_groups(group_key) WHERE group_key <> '';
                CREATE TABLE IF NOT EXISTS area_group_rules (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    group_id INTEGER NOT NULL,
                    rule_order INTEGER NOT NULL DEFAULT 0,
                    building TEXT NOT NULL,
                    zuo TEXT NOT NULL DEFAULT '-',
                    floor_label TEXT NOT NULL DEFAULT '',
                    floor_value REAL,
                    match_mode TEXT NOT NULL,
                    keywords TEXT NOT NULL,
                    note TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(group_id) REFERENCES monitor_groups(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_area_group_rules_group_order
                    ON area_group_rules(group_id, rule_order, id);
                CREATE TABLE IF NOT EXISTS run_area_group_rules (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id INTEGER NOT NULL,
                    group_id INTEGER NOT NULL,
                    group_key TEXT NOT NULL DEFAULT '',
                    group_name TEXT NOT NULL DEFAULT '',
                    enabled INTEGER NOT NULL DEFAULT 1,
                    rule_order INTEGER NOT NULL,
                    building TEXT NOT NULL,
                    zuo TEXT NOT NULL DEFAULT '-',
                    floor_label TEXT NOT NULL DEFAULT '',
                    floor_value REAL,
                    match_mode TEXT NOT NULL,
                    keywords TEXT NOT NULL DEFAULT '',
                    note TEXT NOT NULL DEFAULT '',
                    FOREIGN KEY(run_id) REFERENCES collection_runs(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_run_area_group_rules_run_group
                    ON run_area_group_rules(run_id, group_id, rule_order, id);
                CREATE TABLE IF NOT EXISTS ems_schema_migrations (
                    name TEXT PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );
                """;
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT 1 FROM ems_schema_migrations WHERE name = 'area-groups-rules-v1'";
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            await CleanupLegacyAreaStorageAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            return;
        }

        var clearStatements = new List<string> { "DELETE FROM area_group_rules" };
        if (await TableExistsAsync(connection, transaction, "monitor_group_items", cancellationToken).ConfigureAwait(false))
        {
            // Legacy members reference monitor_groups without cascade delete.
            // Remove them before deleting groups that are not retained for Watch.
            clearStatements.Add("DELETE FROM monitor_group_items");
        }
        if (await TableExistsAsync(connection, transaction, "device_watch_rules", cancellationToken).ConfigureAwait(false))
        {
            clearStatements.Add("UPDATE monitor_groups SET enabled = 0, group_key = '' WHERE EXISTS (SELECT 1 FROM device_watch_rules WHERE device_watch_rules.group_id = monitor_groups.id)");
            clearStatements.Add("DELETE FROM monitor_groups WHERE NOT EXISTS (SELECT 1 FROM device_watch_rules WHERE device_watch_rules.group_id = monitor_groups.id)");
            clearStatements.Add("DELETE FROM device_watch_rules WHERE NOT EXISTS (SELECT 1 FROM monitor_groups WHERE monitor_groups.id = device_watch_rules.group_id)");
        }
        else
        {
            clearStatements.Add("DELETE FROM monitor_groups");
        }
        if (await TableExistsAsync(connection, transaction, "monitor_group_items", cancellationToken).ConfigureAwait(false))
            clearStatements.Add("DROP TABLE monitor_group_items");
        if (await TableExistsAsync(connection, transaction, "legacy_area_api_state", cancellationToken).ConfigureAwait(false))
            clearStatements.Add("DROP TABLE legacy_area_api_state");
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = string.Join(';', clearStatements);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = "INSERT INTO ems_schema_migrations(name, applied_at) VALUES ('area-groups-rules-v1', $applied_at)";
        mark.Parameters.AddWithValue("$applied_at", StoredTimestamp.FormatLocal(DateTimeOffset.Now));
        await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CleanupLegacyAreaStorageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var statements = new List<string>();
        if (await TableExistsAsync(connection, transaction, "monitor_group_items", cancellationToken).ConfigureAwait(false))
            statements.Add("DROP TABLE monitor_group_items");
        if (await TableExistsAsync(connection, transaction, "legacy_area_api_state", cancellationToken).ConfigureAwait(false))
            statements.Add("DROP TABLE legacy_area_api_state");
        if (statements.Count == 0)
            return;

        await using var cleanup = connection.CreateCommand();
        cleanup.Transaction = transaction;
        cleanup.CommandText = string.Join(';', statements);
        await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureCollectionRunColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "collection_runs", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "source", "TEXT NOT NULL DEFAULT '采集导入'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "run_no", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "data_version", "TEXT NOT NULL DEFAULT 'v1.0.0'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "collection_mode", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "operator_name", "TEXT NOT NULL DEFAULT '本机'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "restored_from_run_id", "INTEGER", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "batch_uid", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "lifecycle_state", "TEXT NOT NULL DEFAULT 'completed'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "current_revision_uid", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "restored_from_batch_uid", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "duration_ms", "INTEGER", cancellationToken).ConfigureAwait(false);

    }

    private static async Task EnsureLegacyIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "collection_runs", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT id, run_no, run_key, batch_uid, imported_at, completed_at FROM collection_runs ORDER BY id";
        var legacy = new List<(long Id, long? RunNumber, string RunKey, string BatchUid, string FirstSeen)>();
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var firstSeen = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                if (string.IsNullOrWhiteSpace(firstSeen) && !reader.IsDBNull(5))
                {
                    firstSeen = reader.GetString(5);
                }

                legacy.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    firstSeen));
            }
        }

        var usedRunNumbers = new HashSet<long>();
        var usedBatchUids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in legacy)
        {
            var runNumber = row.RunNumber is > 0 && usedRunNumbers.Add(row.RunNumber.Value)
                ? row.RunNumber.Value
                : row.RunNumber is null && row.Id > 0 && usedRunNumbers.Add(row.Id)
                    ? row.Id
                    : NextAvailableRunNumber(usedRunNumbers);
            if (row.RunNumber != runNumber)
            {
                await ExecuteUpdateAsync(connection, transaction,
                    "UPDATE collection_runs SET run_no = $value WHERE id = $id",
                    runNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Id, "$value", cancellationToken).ConfigureAwait(false);
            }

            var batchUid = string.IsNullOrWhiteSpace(row.BatchUid)
                ? Guid.NewGuid().ToString("N")
                : row.BatchUid;
            if (!usedBatchUids.Add(batchUid))
            {
                batchUid = $"legacy_batch_{row.Id}_{Guid.NewGuid():N}";
                usedBatchUids.Add(batchUid);
            }
            if (!string.Equals(batchUid, row.BatchUid, StringComparison.Ordinal))
            {
                await ExecuteUpdateAsync(connection, transaction,
                    "UPDATE collection_runs SET batch_uid = $value WHERE id = $id",
                    batchUid, row.Id, "$value", cancellationToken).ConfigureAwait(false);

                if (await TableExistsAsync(connection, transaction, "run_realtime_details", cancellationToken).ConfigureAwait(false) &&
                    await ColumnExistsAsync(connection, transaction, "run_realtime_details", "batch_uid", cancellationToken).ConfigureAwait(false))
                {
                    await ExecuteUpdateAsync(connection, transaction,
                        "UPDATE run_realtime_details SET batch_uid = $value WHERE run_id = $id",
                        batchUid, row.Id, "$value", cancellationToken).ConfigureAwait(false);
                }
            }

            var runKey = string.IsNullOrWhiteSpace(row.RunKey)
                ? $"legacy_run_{row.Id}_{Guid.NewGuid():N}"
                : row.RunKey;
            if (!string.Equals(runKey, row.RunKey, StringComparison.Ordinal))
            {
                await ExecuteUpdateAsync(connection, transaction,
                    "UPDATE collection_runs SET run_key = $value WHERE id = $id",
                    runKey, row.Id, "$value", cancellationToken).ConfigureAwait(false);
            }

            await using var register = connection.CreateCommand();
            register.Transaction = transaction;
            register.CommandText = """
                INSERT INTO run_key_registry (run_key, batch_uid, first_seen_at, last_run_id)
                VALUES ($run_key, $batch_uid, $first_seen_at, $last_run_id)
                ON CONFLICT(run_key) DO UPDATE SET
                    batch_uid = CASE WHEN run_key_registry.batch_uid = '' THEN excluded.batch_uid ELSE run_key_registry.batch_uid END,
                    last_run_id = excluded.last_run_id
                """;
            register.Parameters.AddWithValue("$run_key", runKey);
            register.Parameters.AddWithValue("$batch_uid", batchUid);
            register.Parameters.AddWithValue("$first_seen_at", string.IsNullOrWhiteSpace(row.FirstSeen)
                ? StoredTimestamp.FormatLocal(DateTimeOffset.Now)
                : row.FirstSeen);
            register.Parameters.AddWithValue("$last_run_id", row.Id);
            await register.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var runNumberIndex = connection.CreateCommand();
        runNumberIndex.Transaction = transaction;
        runNumberIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_collection_runs_run_no ON collection_runs(run_no) WHERE run_no IS NOT NULL";
        await runNumberIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureBatchUidIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "collection_runs", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_collection_runs_batch_uid ON collection_runs(batch_uid) WHERE batch_uid <> ''";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long NextAvailableRunNumber(ISet<long> usedRunNumbers)
    {
        var candidate = 1L;
        while (!usedRunNumbers.Add(candidate))
        {
            candidate++;
        }

        return candidate;
    }

    private static async Task EnsureGovernanceSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "collection_runs", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS run_key_registry (
                run_key TEXT PRIMARY KEY,
                batch_uid TEXT NOT NULL DEFAULT '',
                first_seen_at TEXT NOT NULL,
                deleted_at TEXT,
                last_run_id INTEGER
            );
            CREATE TABLE IF NOT EXISTS run_id_registry (
                technical_id INTEGER PRIMARY KEY,
                allocated_at TEXT NOT NULL,
                allocation_kind TEXT NOT NULL DEFAULT 'collection_run'
            );
            CREATE INDEX IF NOT EXISTS idx_run_id_registry_allocated ON run_id_registry(allocated_at);
            CREATE INDEX IF NOT EXISTS idx_run_key_registry_deleted ON run_key_registry(deleted_at);
            CREATE TABLE IF NOT EXISTS run_operations (
                operation_id TEXT PRIMARY KEY,
                operation_type TEXT NOT NULL,
                run_id INTEGER,
                batch_uid TEXT,
                run_key TEXT,
                occurred_at TEXT NOT NULL,
                result TEXT NOT NULL,
                summary TEXT NOT NULL DEFAULT '',
                deleted_cards INTEGER NOT NULL DEFAULT 0,
                deleted_pages INTEGER NOT NULL DEFAULT 0,
                deleted_sub_areas INTEGER NOT NULL DEFAULT 0,
                deleted_buildings INTEGER NOT NULL DEFAULT 0,
                pending_artifacts INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS current_data_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                revision_uid TEXT NOT NULL UNIQUE,
                updated_at TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT '本机 SQLite'
            );
            CREATE TABLE IF NOT EXISTS current_data_sources (
                building TEXT PRIMARY KEY,
                revision_uid TEXT NOT NULL,
                run_id INTEGER,
                batch_uid TEXT,
                source_updated_at TEXT,
                card_count INTEGER NOT NULL DEFAULT 0,
                state TEXT NOT NULL DEFAULT 'bound',
                reason TEXT NOT NULL DEFAULT ''
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using (var registerIds = connection.CreateCommand())
        {
            registerIds.Transaction = transaction;
            registerIds.CommandText = "INSERT OR IGNORE INTO run_id_registry (technical_id, allocated_at, allocation_kind) SELECT id, COALESCE(imported_at, completed_at, $now), 'collection_run' FROM collection_runs";
            registerIds.Parameters.AddWithValue("$now", StoredTimestamp.FormatLocal(DateTimeOffset.Now));
            await registerIds.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await RegisterLegacySequenceHighWaterMarkAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "current_data_sources", "state", "TEXT NOT NULL DEFAULT 'bound'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "current_data_sources", "reason", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
    }

    private static async Task RegisterLegacySequenceHighWaterMarkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var probe = connection.CreateCommand();
        probe.Transaction = transaction;
        probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_sequence' LIMIT 1";
        if (await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return;
        }

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT seq FROM sqlite_sequence WHERE name = 'collection_runs' LIMIT 1";
        var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null || value is DBNull || Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) <= 0)
        {
            return;
        }

        await using var register = connection.CreateCommand();
        register.Transaction = transaction;
        register.CommandText = "INSERT OR IGNORE INTO run_id_registry (technical_id, allocated_at, allocation_kind) VALUES ($id, $allocated_at, 'sqlite_sequence_high_watermark')";
        register.Parameters.AddWithValue("$id", Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
        register.Parameters.AddWithValue("$allocated_at", StoredTimestamp.FormatLocal(DateTimeOffset.Now));
        await register.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureLegacyCurrentDataStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "current_data_sources", cancellationToken).ConfigureAwait(false) ||
            !await TableExistsAsync(connection, transaction, "buildings", cancellationToken).ConfigureAwait(false) ||
            !await TableExistsAsync(connection, transaction, "cards", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM current_data_sources";
        var sourceCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (sourceCount > 0)
        {
            return;
        }

        await using var cardCount = connection.CreateCommand();
        cardCount.Transaction = transaction;
        cardCount.CommandText = "SELECT COUNT(*) FROM cards";
        var cards = Convert.ToInt64(await cardCount.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (cards == 0)
        {
            return;
        }

        var revisionUid = "legacy-unresolved-" + Guid.NewGuid().ToString("N");
        var now = StoredTimestamp.FormatLocal(DateTimeOffset.Now);
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                INSERT INTO current_data_state (id, revision_uid, updated_at, source)
                VALUES (1, $revision_uid, $updated_at, '本机旧库迁移，来源未确定')
                ON CONFLICT(id) DO UPDATE SET revision_uid = excluded.revision_uid, updated_at = excluded.updated_at, source = excluded.source
                """;
            state.Parameters.AddWithValue("$revision_uid", revisionUid);
            state.Parameters.AddWithValue("$updated_at", now);
            await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var sources = connection.CreateCommand();
        sources.Transaction = transaction;
        sources.CommandText = """
            INSERT INTO current_data_sources
                (building, revision_uid, run_id, batch_uid, source_updated_at, card_count, state, reason)
            SELECT b.building, $revision_uid, NULL, NULL, b.updated_at,
                   (SELECT COUNT(*) FROM cards c JOIN pages p ON p.id = c.page_id JOIN sub_areas sa ON sa.id = p.sub_area_id WHERE sa.building = b.building),
                   'unresolved', '旧数据库未记录当前数据来源，禁止自动绑定到最新历史批次'
            FROM buildings b
            """;
        sources.Parameters.AddWithValue("$revision_uid", revisionUid);
        await sources.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteUpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string value,
        long id,
        string valueParameter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(valueParameter, value);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureQualityColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await AddColumnIfMissingAsync(connection, transaction, "pages", "quality_reason", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "run_pages", "quality_reason", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "pages", "collected_at", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "run_pages", "collected_at", "TEXT", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureRealtimeSnapshotSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "collection_runs", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
        await AddColumnIfMissingAsync(connection, transaction, "run_realtime_details", "batch_uid", "TEXT", cancellationToken).ConfigureAwait(false);
        await using var backfill = connection.CreateCommand();
        backfill.Transaction = transaction;
        backfill.CommandText = """
            UPDATE run_realtime_details
            SET batch_uid = (
                SELECT NULLIF(TRIM(batch_uid), '')
                FROM collection_runs
                WHERE collection_runs.id = run_realtime_details.run_id
            )
            WHERE NULLIF(TRIM(batch_uid), '') IS NULL
              AND EXISTS (
                SELECT 1 FROM collection_runs
                WHERE collection_runs.id = run_realtime_details.run_id
                  AND NULLIF(TRIM(collection_runs.batch_uid), '') IS NOT NULL
              );
            """;
        await backfill.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, tableName, cancellationToken).ConfigureAwait(false) ||
            await ColumnExistsAsync(connection, transaction, tableName, columnName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
}
