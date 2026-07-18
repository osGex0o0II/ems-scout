using System.Text.Json;
using EmsScout.Application.Collection;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteCollectionRunRepository(Func<string> databasePathResolver) : ICollectionRunRepository
{
    public async Task<IReadOnlyList<CollectionRunRecord>> ListAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadOnly);
        if (!await SqliteSchemaGuard.TableExistsAsync(connection, "collection_runs", cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, run_key, started_at, completed_at, imported_at, status, scope,
                   buildings, json_path, db_snapshot_path, card_count, on_count,
                   off_count, offline_count, unknown_count, quality_summary, is_anomaly, note
            FROM collection_runs
            ORDER BY datetime(completed_at) DESC, id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var rows = new List<CollectionRunRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadRun(reader));
        }

        return rows;
    }

    public async Task<CollectionRunRecord> SetAnomalyAsync(
        long runId,
        bool isAnomaly,
        string note,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadRunAsync(connection, transaction, runId, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"Run not found: {runId}");
        var nextNote = NextAnomalyNote(current.Note, isAnomaly, note);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE collection_runs SET is_anomaly = $is_anomaly, note = $note WHERE id = $id";
            command.Parameters.AddWithValue("$is_anomaly", isAnomaly ? 1 : 0);
            command.Parameters.AddWithValue("$note", nextNote);
            command.Parameters.AddWithValue("$id", runId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var updated = await LoadRunAsync(connection, transaction, runId, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"Run not found after update: {runId}");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<CollectionRunRestoreResult> RestoreCurrentAsync(
        long runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureQualityReasonColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var run = await LoadRunAsync(connection, transaction, runId, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"Run not found: {runId}");
        if (run.IsAnomaly)
        {
            throw new InvalidOperationException("异常隔离批次不能恢复，请先取消异常标记并复核数据。");
        }

        if (!await HasRunSnapshotAsync(connection, transaction, runId, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Run {runId} does not contain a restorable snapshot.");
        }

        var isPartial = run.Scope.Equals("partial", StringComparison.OrdinalIgnoreCase);
        if (isPartial && run.Buildings.Count == 0)
        {
            throw new InvalidOperationException("部分批次没有楼栋范围，无法安全恢复。");
        }

        var backupRunId = await CreatePreRestoreBackupAsync(connection, transaction, run, cancellationToken).ConfigureAwait(false);
        if (isPartial)
        {
            await DeleteCurrentBuildingsAsync(connection, transaction, run.Buildings, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM cards", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "DELETE FROM pages", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "DELETE FROM sub_areas", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "DELETE FROM buildings", cancellationToken).ConfigureAwait(false);
        }

        await RestoreBuildingsAsync(connection, transaction, runId, cancellationToken).ConfigureAwait(false);
        var subAreaMap = await RestoreSubAreasAsync(connection, transaction, runId, cancellationToken).ConfigureAwait(false);
        var pageMap = await RestorePagesAsync(connection, transaction, runId, subAreaMap, cancellationToken).ConfigureAwait(false);
        var restoredCards = await RestoreCardsAsync(connection, transaction, runId, pageMap, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CollectionRunRestoreResult(
            run.Id,
            run.RunKey,
            run.CompletedAt,
            run.Buildings,
            restoredCards,
            backupRunId,
            isPartial);
    }

    public async Task<CollectionRunDeleteResult> DeleteAsync(
        long runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var run = await LoadRunAsync(connection, transaction, runId, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"Run not found: {runId}");

        if (await SqliteSchemaGuard.TableExistsAsync(connection, "area_group_change_requests", cancellationToken).ConfigureAwait(false))
        {
            await ExecuteCountAsync(
                    connection,
                    transaction,
                    "UPDATE area_group_change_requests SET run_id = NULL WHERE run_id = $run_id",
                    runId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        var deletedCards = await ExecuteCountAsync(connection, transaction, "DELETE FROM run_cards WHERE run_id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        var deletedPages = await ExecuteCountAsync(connection, transaction, "DELETE FROM run_pages WHERE run_id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        var deletedSubAreas = await ExecuteCountAsync(connection, transaction, "DELETE FROM run_sub_areas WHERE run_id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        var deletedBuildings = await ExecuteCountAsync(connection, transaction, "DELETE FROM run_buildings WHERE run_id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        await ExecuteCountAsync(connection, transaction, "DELETE FROM collection_runs WHERE id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CollectionRunDeleteResult(
            run.Id,
            run.RunKey,
            run.CompletedAt,
            deletedCards,
            deletedPages,
            deletedSubAreas,
            deletedBuildings);
    }

    private static async Task<CollectionRunRecord?> LoadRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, run_key, started_at, completed_at, imported_at, status, scope,
                   buildings, json_path, db_snapshot_path, card_count, on_count,
                   off_count, offline_count, unknown_count, quality_summary, is_anomaly, note
            FROM collection_runs
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRun(reader)
            : null;
    }

    private static CollectionRunRecord ReadRun(SqliteDataReader reader)
    {
        return new CollectionRunRecord(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            RunKey: SqliteValueReader.ReadString(reader, "run_key"),
            StartedAt: SqliteValueReader.ReadString(reader, "started_at"),
            CompletedAt: SqliteValueReader.ReadString(reader, "completed_at"),
            ImportedAt: SqliteValueReader.ReadString(reader, "imported_at"),
            Status: SqliteValueReader.ReadString(reader, "status"),
            Scope: SqliteValueReader.ReadString(reader, "scope"),
            Buildings: ParseStringArray(SqliteValueReader.ReadString(reader, "buildings")),
            JsonPath: SqliteValueReader.ReadString(reader, "json_path"),
            DbSnapshotPath: SqliteValueReader.ReadString(reader, "db_snapshot_path"),
            CardCount: SqliteValueReader.ReadInt32(reader, "card_count"),
            OnCount: SqliteValueReader.ReadInt32(reader, "on_count"),
            OffCount: SqliteValueReader.ReadInt32(reader, "off_count"),
            OfflineCount: SqliteValueReader.ReadInt32(reader, "offline_count"),
            UnknownCount: SqliteValueReader.ReadInt32(reader, "unknown_count"),
            QualitySummary: SqliteValueReader.ReadString(reader, "quality_summary"),
            IsAnomaly: SqliteValueReader.ReadInt32(reader, "is_anomaly") != 0,
            Note: SqliteValueReader.ReadString(reader, "note"));
    }

    private static async Task<bool> HasRunSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM run_cards WHERE run_id = $run_id";
        command.Parameters.AddWithValue("$run_id", runId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<long?> CreatePreRestoreBackupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CollectionRunRecord targetRun,
        CancellationToken cancellationToken)
    {
        await using var countCommand = connection.CreateCommand();
        countCommand.Transaction = transaction;
        countCommand.CommandText = "SELECT COUNT(*) FROM cards";
        var currentCardCount = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (currentCardCount == 0)
        {
            return null;
        }

        var now = DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var runKey = $"pre_restore_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
        var buildings = new List<string>();
        await using (var buildingsCommand = connection.CreateCommand())
        {
            buildingsCommand.Transaction = transaction;
            buildingsCommand.CommandText = "SELECT building FROM buildings ORDER BY building";
            await using var reader = await buildingsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                buildings.Add(reader.GetString(0));
            }
        }

        long backupRunId;
        await using (var insertRun = connection.CreateCommand())
        {
            insertRun.Transaction = transaction;
            insertRun.CommandText = """
                INSERT INTO collection_runs
                    (run_key, started_at, completed_at, imported_at, status, scope, buildings,
                     card_count, on_count, off_count, offline_count, unknown_count, note)
                SELECT $run_key, $now, $now, $now, 'backup', 'full', $buildings,
                       COUNT(*),
                       SUM(comm = '开机' OR switch = 'ON'),
                       SUM(comm = '关机' OR switch = 'OFF'),
                       SUM(comm = '离线'),
                       SUM(COALESCE(comm, '') NOT IN ('开机', '关机', '离线') AND COALESCE(switch, '') NOT IN ('ON', 'OFF')),
                       $note
                FROM cards
                RETURNING id;
                """;
            insertRun.Parameters.AddWithValue("$run_key", runKey);
            insertRun.Parameters.AddWithValue("$now", now);
            insertRun.Parameters.AddWithValue("$buildings", JsonSerializer.Serialize(buildings));
            insertRun.Parameters.AddWithValue("$note", $"恢复批次 #{targetRun.Id} 前自动备份");
            backupRunId = Convert.ToInt64(
                await insertRun.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        await ExecuteSnapshotCopyAsync(
            connection,
            transaction,
            """
            INSERT INTO run_buildings (run_id, building, sub_area_count, menu_clicked, updated_at)
            SELECT $run_id, building, sub_area_count, menu_clicked, updated_at FROM buildings
            """,
            backupRunId,
            cancellationToken).ConfigureAwait(false);
        await ExecuteSnapshotCopyAsync(
            connection,
            transaction,
            """
            INSERT INTO run_sub_areas (run_id, source_sub_area_id, building, sub_idx, floor, floor_label, text, x, y)
            SELECT $run_id, id, building, sub_idx, floor, NULL, text, x, y FROM sub_areas
            """,
            backupRunId,
            cancellationToken).ConfigureAwait(false);
        await ExecuteSnapshotCopyAsync(
            connection,
            transaction,
            """
            INSERT INTO run_pages
                (run_id, run_sub_area_id, source_page_id, page_name, count, raw_count, unique_count,
                 duplicate_names, on_href, off_href, layout, quality_reason, err)
            SELECT $run_id, rsa.id, p.id, p.page_name, p.count, p.raw_count, p.unique_count,
                   p.duplicate_names, p.on_href, p.off_href, p.layout, p.quality_reason, p.err
            FROM pages p
            JOIN run_sub_areas rsa
              ON rsa.run_id = $run_id AND rsa.source_sub_area_id = p.sub_area_id
            """,
            backupRunId,
            cancellationToken).ConfigureAwait(false);
        await ExecuteSnapshotCopyAsync(
            connection,
            transaction,
            """
            INSERT INTO run_cards
                (run_id, run_page_id, source_card_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            SELECT $run_id, rp.id, c.id, c.name, c.switch, c.mode, c.indoor, c.set_temp, c.fan, c.indicator, c.comm
            FROM cards c
            JOIN run_pages rp
              ON rp.run_id = $run_id AND rp.source_page_id = c.page_id
            """,
            backupRunId,
            cancellationToken).ConfigureAwait(false);

        return backupRunId;
    }

    private static async Task ExecuteSnapshotCopyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        long runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$run_id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteCurrentBuildingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken)
    {
        foreach (var building in buildings.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var sql in new[]
                     {
                         "DELETE FROM cards WHERE page_id IN (SELECT p.id FROM pages p JOIN sub_areas sa ON sa.id = p.sub_area_id WHERE sa.building = $building)",
                         "DELETE FROM pages WHERE sub_area_id IN (SELECT id FROM sub_areas WHERE building = $building)",
                         "DELETE FROM sub_areas WHERE building = $building",
                         "DELETE FROM buildings WHERE building = $building",
                     })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$building", building);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task RestoreBuildingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT building, sub_area_count, menu_clicked, updated_at
            FROM run_buildings
            WHERE run_id = $run_id
            ORDER BY building
            """;
        select.Parameters.AddWithValue("$run_id", runId);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO buildings (building, sub_area_count, menu_clicked, updated_at)
            VALUES ($building, $sub_area_count, $menu_clicked, $updated_at)
            """;
        var building = insert.Parameters.Add("$building", SqliteType.Text);
        var subAreaCount = insert.Parameters.Add("$sub_area_count", SqliteType.Integer);
        var menuClicked = insert.Parameters.Add("$menu_clicked", SqliteType.Text);
        var updatedAt = insert.Parameters.Add("$updated_at", SqliteType.Text);

        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            building.Value = SqliteValueReader.ReadString(reader, "building");
            subAreaCount.Value = DbValue(SqliteValueReader.ReadNullableInt64(reader, "sub_area_count"));
            menuClicked.Value = DbValue(SqliteValueReader.ReadString(reader, "menu_clicked"));
            updatedAt.Value = DbValue(SqliteValueReader.ReadString(reader, "updated_at"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<long, long>> RestoreSubAreasAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT id, building, sub_idx, floor, text, x, y
            FROM run_sub_areas
            WHERE run_id = $run_id
            ORDER BY id
            """;
        select.Parameters.AddWithValue("$run_id", runId);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO sub_areas (building, sub_idx, floor, text, x, y)
            VALUES ($building, $sub_idx, $floor, $text, $x, $y)
            RETURNING id
            """;
        var building = insert.Parameters.Add("$building", SqliteType.Text);
        var subIdx = insert.Parameters.Add("$sub_idx", SqliteType.Integer);
        var floor = insert.Parameters.Add("$floor", SqliteType.Real);
        var text = insert.Parameters.Add("$text", SqliteType.Text);
        var x = insert.Parameters.Add("$x", SqliteType.Integer);
        var y = insert.Parameters.Add("$y", SqliteType.Integer);

        var map = new Dictionary<long, long>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            building.Value = SqliteValueReader.ReadString(reader, "building");
            subIdx.Value = DbValue(SqliteValueReader.ReadNullableInt64(reader, "sub_idx"));
            floor.Value = DbValue(SqliteValueReader.ReadNullableDouble(reader, "floor"));
            text.Value = DbValue(SqliteValueReader.ReadString(reader, "text"));
            x.Value = DbValue(SqliteValueReader.ReadNullableInt64(reader, "x"));
            y.Value = DbValue(SqliteValueReader.ReadNullableInt64(reader, "y"));
            var newId = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            map[reader.GetInt64(reader.GetOrdinal("id"))] =
                Convert.ToInt64(newId, System.Globalization.CultureInfo.InvariantCulture);
        }

        return map;
    }

    private static async Task<Dictionary<long, long>> RestorePagesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        IReadOnlyDictionary<long, long> subAreaMap,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT id, run_sub_area_id, page_name, count, raw_count, unique_count,
                   duplicate_names, on_href, off_href, layout, quality_reason, err
            FROM run_pages
            WHERE run_id = $run_id
            ORDER BY id
            """;
        select.Parameters.AddWithValue("$run_id", runId);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO pages (sub_area_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err)
            VALUES ($sub_area_id, $page_name, $count, $raw_count, $unique_count, $duplicate_names, $on_href, $off_href, $layout, $quality_reason, $err)
            RETURNING id
            """;
        var subAreaId = insert.Parameters.Add("$sub_area_id", SqliteType.Integer);
        var pageName = insert.Parameters.Add("$page_name", SqliteType.Text);
        var count = insert.Parameters.Add("$count", SqliteType.Integer);
        var rawCount = insert.Parameters.Add("$raw_count", SqliteType.Integer);
        var uniqueCount = insert.Parameters.Add("$unique_count", SqliteType.Integer);
        var duplicateNames = insert.Parameters.Add("$duplicate_names", SqliteType.Text);
        var onHref = insert.Parameters.Add("$on_href", SqliteType.Text);
        var offHref = insert.Parameters.Add("$off_href", SqliteType.Text);
        var layout = insert.Parameters.Add("$layout", SqliteType.Text);
        var qualityReason = insert.Parameters.Add("$quality_reason", SqliteType.Text);
        var err = insert.Parameters.Add("$err", SqliteType.Text);

        var map = new Dictionary<long, long>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var oldSubAreaId = reader.GetInt64(reader.GetOrdinal("run_sub_area_id"));
            if (!subAreaMap.TryGetValue(oldSubAreaId, out var newSubAreaId))
            {
                continue;
            }

            subAreaId.Value = newSubAreaId;
            pageName.Value = DbValue(SqliteValueReader.ReadString(reader, "page_name"));
            count.Value = DbValue(SqliteValueReader.ReadNullableInt64(reader, "count"));
            rawCount.Value = DbValue(SqliteValueReader.ReadNullableInt64(reader, "raw_count"));
            uniqueCount.Value = DbValue(SqliteValueReader.ReadNullableInt64(reader, "unique_count"));
            duplicateNames.Value = DbValue(SqliteValueReader.ReadString(reader, "duplicate_names"));
            onHref.Value = DbValue(SqliteValueReader.ReadString(reader, "on_href"));
            offHref.Value = DbValue(SqliteValueReader.ReadString(reader, "off_href"));
            layout.Value = DbValue(SqliteValueReader.ReadString(reader, "layout"));
            qualityReason.Value = DbValue(SqliteValueReader.ReadString(reader, "quality_reason"));
            err.Value = DbValue(SqliteValueReader.ReadString(reader, "err"));
            var newId = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            map[reader.GetInt64(reader.GetOrdinal("id"))] =
                Convert.ToInt64(newId, System.Globalization.CultureInfo.InvariantCulture);
        }

        return map;
    }

    private static async Task<int> RestoreCardsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        IReadOnlyDictionary<long, long> pageMap,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT run_page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm
            FROM run_cards
            WHERE run_id = $run_id
            ORDER BY id
            """;
        select.Parameters.AddWithValue("$run_id", runId);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO cards (page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            VALUES ($page_id, $name, $switch, $mode, $indoor, $set_temp, $fan, $indicator, $comm)
            """;
        var pageId = insert.Parameters.Add("$page_id", SqliteType.Integer);
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var switchState = insert.Parameters.Add("$switch", SqliteType.Text);
        var mode = insert.Parameters.Add("$mode", SqliteType.Text);
        var indoor = insert.Parameters.Add("$indoor", SqliteType.Text);
        var setTemp = insert.Parameters.Add("$set_temp", SqliteType.Text);
        var fan = insert.Parameters.Add("$fan", SqliteType.Text);
        var indicator = insert.Parameters.Add("$indicator", SqliteType.Text);
        var comm = insert.Parameters.Add("$comm", SqliteType.Text);

        var restored = 0;
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var oldPageId = reader.GetInt64(reader.GetOrdinal("run_page_id"));
            if (!pageMap.TryGetValue(oldPageId, out var newPageId))
            {
                continue;
            }

            pageId.Value = newPageId;
            name.Value = DbValue(SqliteValueReader.ReadString(reader, "name"));
            switchState.Value = DbValue(SqliteValueReader.ReadString(reader, "switch"));
            mode.Value = DbValue(SqliteValueReader.ReadString(reader, "mode"));
            indoor.Value = DbValue(SqliteValueReader.ReadString(reader, "indoor"));
            setTemp.Value = DbValue(SqliteValueReader.ReadString(reader, "set_temp"));
            fan.Value = DbValue(SqliteValueReader.ReadString(reader, "fan"));
            indicator.Value = DbValue(SqliteValueReader.ReadString(reader, "indicator"));
            comm.Value = DbValue(SqliteValueReader.ReadString(reader, "comm"));
            restored += await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return restored;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        long runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$run_id", runId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureQualityReasonColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await SqliteSchemaGuard.RequireCurrentAsync(
            connection,
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["pages"] = ["id", "sub_area_id", "quality_reason"],
                ["run_pages"] = ["id", "run_id", "run_sub_area_id", "quality_reason"],
            },
            ["idx_pg_sa", "idx_run_pages_sa"],
            cancellationToken).ConfigureAwait(false);
    }

    private static string NextAnomalyNote(string existingNote, bool isAnomaly, string note)
    {
        var extra = (note ?? string.Empty).Trim();
        if (isAnomaly)
        {
            if (string.IsNullOrWhiteSpace(extra) || existingNote.Contains(extra, StringComparison.OrdinalIgnoreCase))
            {
                return existingNote;
            }

            return string.Join("；", new[] { existingNote, extra }.Where(item => !string.IsNullOrWhiteSpace(item)));
        }

        return string.Join(
            "；",
            existingNote
                .Split('；', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !item.Equals("采集数据异常，已隔离", StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<string> ParseStringArray(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<string>>(value) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static object DbValue(string value) => string.IsNullOrEmpty(value) ? DBNull.Value : value;

    private static object DbValue(long? value) => value.HasValue ? value.Value : DBNull.Value;

    private static object DbValue(double? value) => value.HasValue ? value.Value : DBNull.Value;

}
