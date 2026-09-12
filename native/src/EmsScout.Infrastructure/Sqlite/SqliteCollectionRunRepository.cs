using System.Text.Json;
using EmsScout.Application;
using EmsScout.Application.Collection;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteCollectionRunRepository(Func<string> databasePathResolver) : ICollectionRunRepository
{
    public async Task<IReadOnlyList<CollectionRunRecord>> ListAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: true);
        if (!await TableExistsAsync(connection, "collection_runs", cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var snapshotCardCount = await TableExistsAsync(connection, "run_cards", cancellationToken).ConfigureAwait(false)
            ? "(SELECT COUNT(*) FROM run_cards snapshot WHERE snapshot.run_id = collection_runs.id)"
            : "0";

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT collection_runs.*,
                   {snapshotCardCount} AS snapshot_card_count
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

        foreach (var index in Enumerable.Range(0, rows.Count))
        {
            rows[index] = rows[index] with
            {
                BuildingCardCounts = await LoadBuildingCardCountsAsync(
                    connection,
                    rows[index].Id,
                    snapshot: true,
                    cancellationToken).ConfigureAwait(false),
            };
        }

        return rows;
    }

    public async Task<CollectionRunComparison> CompareCurrentAsync(
        long runId,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: true);
        var run = await LoadRunAsync(connection, runId, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"Run not found: {runId}");

        var snapshotByBuilding = await LoadBuildingCardCountsAsync(connection, runId, snapshot: true, cancellationToken).ConfigureAwait(false);
        var currentByBuilding = await LoadBuildingCardCountsAsync(connection, runId, snapshot: false, cancellationToken).ConfigureAwait(false);
        var changedCount = await CountChangedCardsAsync(connection, runId, cancellationToken).ConfigureAwait(false);
        var blockingReason = await GetRestoreBlockingReasonAsync(connection, run, cancellationToken).ConfigureAwait(false);

        return CollectionRunComparison.Create(
            run,
            snapshotByBuilding,
            currentByBuilding,
            changedCount,
            isRestorable: blockingReason is null,
            blockingReason);
    }

    public async Task<CollectionRunRecord> SetAnomalyAsync(
        long runId,
        bool isAnomaly,
        string note,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: false);
        await EnsureCollectionRunMetadataColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        var current = await LoadRunAsync(connection, runId, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"Run not found: {runId}");
        var nextNote = NextAnomalyNote(current.Note, isAnomaly, note);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE collection_runs SET is_anomaly = $is_anomaly, note = $note WHERE id = $id";
            command.Parameters.AddWithValue("$is_anomaly", isAnomaly ? 1 : 0);
            command.Parameters.AddWithValue("$note", nextNote);
            command.Parameters.AddWithValue("$id", runId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await LoadRunAsync(connection, runId, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException($"Run not found after update: {runId}");
    }

    public async Task<CollectionRunRestoreResult> RestoreCurrentAsync(
        long runId,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: false);
        await EnsureCollectionRunMetadataColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureQualityReasonColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        var run = await LoadRunAsync(connection, runId, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"Run not found: {runId}");
        if (run.IsAnomaly)
        {
            throw new InvalidOperationException("异常隔离批次不能恢复，请先取消异常标记并复核数据。");
        }

        if (CollectionRunCompleteness.HasBlockingQualityFailure(run.QualitySummary))
        {
            throw new InvalidOperationException("质量审计存在阻断问题的批次不能恢复，请先完成复核或重新采集。");
        }

        if (!run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"状态为“{run.Status}”的批次不能恢复，仅允许恢复已完成批次。");
        }

        if (!run.Scope.Equals("full", StringComparison.OrdinalIgnoreCase) &&
            !run.Scope.Equals("partial", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"批次范围“{run.Scope}”无效，无法安全恢复。");
        }

        if (run.Scope.Equals("full", StringComparison.OrdinalIgnoreCase) &&
            !run.Buildings.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(CollectionRunCompleteness.RequiredBuildings))
        {
            throw new InvalidOperationException("全量批次未覆盖 1-6 号楼，无法安全恢复。");
        }

        await ValidateSnapshotAsync(connection, run, cancellationToken).ConfigureAwait(false);

        var isPartial = run.Scope.Equals("partial", StringComparison.OrdinalIgnoreCase);
        if (isPartial && run.Buildings.Count == 0)
        {
            throw new InvalidOperationException("部分批次没有楼栋范围，无法安全恢复。");
        }

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
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
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: false);
        await EnsureCollectionRunMetadataColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        var run = await LoadRunAsync(connection, runId, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"Run not found: {runId}");

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (await TableExistsAsync(connection, "run_realtime_details", cancellationToken).ConfigureAwait(false))
        {
            await ExecuteCountAsync(connection, transaction, "DELETE FROM run_realtime_details WHERE run_id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        }
        var deletedCards = await ExecuteCountAsync(connection, transaction, "DELETE FROM run_cards WHERE run_id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        var deletedPages = await ExecuteCountAsync(connection, transaction, "DELETE FROM run_pages WHERE run_id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        var deletedSubAreas = await ExecuteCountAsync(connection, transaction, "DELETE FROM run_sub_areas WHERE run_id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        var deletedBuildings = await ExecuteCountAsync(connection, transaction, "DELETE FROM run_buildings WHERE run_id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        await ExecuteCountAsync(connection, transaction, "DELETE FROM collection_runs WHERE id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        DeleteAssociatedArtifacts(run);

        return new CollectionRunDeleteResult(
            run.Id,
            run.RunKey,
            run.CompletedAt,
            deletedCards,
            deletedPages,
            deletedSubAreas,
            deletedBuildings);
    }

    private SqliteConnection OpenConnection(bool readOnly)
    {
        var mode = readOnly ? "ReadOnly" : "ReadWrite";
        var connection = new SqliteConnection($"Data Source={databasePathResolver()};Mode={mode}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void EnsureDatabaseExists()
    {
        var databasePath = databasePathResolver();
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Cannot find EMS SQLite database.", databasePath);
        }
    }

    private static async Task<CollectionRunRecord?> LoadRunAsync(
        SqliteConnection connection,
        long runId,
        CancellationToken cancellationToken)
    {
        var snapshotCardCount = await TableExistsAsync(connection, "run_cards", cancellationToken).ConfigureAwait(false)
            ? "(SELECT COUNT(*) FROM run_cards snapshot WHERE snapshot.run_id = collection_runs.id)"
            : "0";

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT collection_runs.*,
                   {snapshotCardCount} AS snapshot_card_count
            FROM collection_runs
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", runId);

        CollectionRunRecord? run;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            run = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadRun(reader)
                : null;
        }

        return run is null
            ? null
            : run with
            {
                BuildingCardCounts = await LoadBuildingCardCountsAsync(
                    connection,
                    run.Id,
                    snapshot: true,
                    cancellationToken).ConfigureAwait(false),
            };
    }

    private static CollectionRunRecord ReadRun(SqliteDataReader reader)
    {
        return new CollectionRunRecord(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            RunKey: ReadString(reader, "run_key"),
            StartedAt: ReadString(reader, "started_at"),
            CompletedAt: ReadString(reader, "completed_at"),
            ImportedAt: ReadString(reader, "imported_at"),
            Status: ReadString(reader, "status"),
            Scope: ReadString(reader, "scope"),
            Buildings: ParseStringArray(ReadString(reader, "buildings")),
            JsonPath: ReadString(reader, "json_path"),
            DbSnapshotPath: ReadString(reader, "db_snapshot_path"),
            CardCount: ReadInt32(reader, "card_count"),
            OnCount: ReadInt32(reader, "on_count"),
            OffCount: ReadInt32(reader, "off_count"),
            OfflineCount: ReadInt32(reader, "offline_count"),
            UnknownCount: ReadInt32(reader, "unknown_count"),
            QualitySummary: ReadString(reader, "quality_summary"),
            IsAnomaly: ReadInt32(reader, "is_anomaly") != 0,
            Note: ReadString(reader, "note"),
            SnapshotCardCount: ReadInt32(reader, "snapshot_card_count"),
            Source: ReadString(reader, "source", "采集导入"),
            DataVersion: ReadString(reader, "data_version", "v1.0.0"),
            Operator: ReadString(reader, "operator_name", "本机"),
            RestoredFromRunId: ReadNullableInt64(reader, "restored_from_run_id"));
    }

    private static async Task<Dictionary<string, int>> LoadBuildingCardCountsAsync(
        SqliteConnection connection,
        long runId,
        bool snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = snapshot
            ? """
              SELECT sa.building, COUNT(*)
              FROM run_cards c
              JOIN run_pages p ON p.id = c.run_page_id AND p.run_id = c.run_id
              JOIN run_sub_areas sa ON sa.id = p.run_sub_area_id AND sa.run_id = c.run_id
              WHERE c.run_id = $run_id
              GROUP BY sa.building
              """
            : """
              SELECT sa.building, COUNT(*)
              FROM cards c
              JOIN pages p ON p.id = c.page_id
              JOIN sub_areas sa ON sa.id = p.sub_area_id
              GROUP BY sa.building
              """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }

        return result;
    }

    private static async Task<int> CountChangedCardsAsync(
        SqliteConnection connection,
        long runId,
        CancellationToken cancellationToken)
    {
        var snapshot = await LoadCardSignaturesAsync(connection, runId, snapshot: true, cancellationToken).ConfigureAwait(false);
        var current = await LoadCardSignaturesAsync(connection, runId, snapshot: false, cancellationToken).ConfigureAwait(false);
        var changed = 0;
        foreach (var key in snapshot.Keys.Intersect(current.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var snapshotCounts = CountSignatures(snapshot[key]);
            var currentCounts = CountSignatures(current[key]);
            var exactMatches = snapshotCounts.Sum(pair => Math.Min(pair.Value, currentCounts.GetValueOrDefault(pair.Key)));
            var remainingSnapshot = snapshot[key].Count - exactMatches;
            var remainingCurrent = current[key].Count - exactMatches;
            changed += Math.Min(remainingSnapshot, remainingCurrent);
        }

        return changed;
    }

    private static async Task<Dictionary<string, List<string>>> LoadCardSignaturesAsync(
        SqliteConnection connection,
        long runId,
        bool snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = snapshot
            ? """
              SELECT sa.building, sa.text, p.page_name, c.name, c.id,
                     c.switch, c.mode, c.indoor, c.set_temp, c.fan, c.indicator, c.comm
              FROM run_cards c
              JOIN run_pages p ON p.id = c.run_page_id AND p.run_id = c.run_id
              JOIN run_sub_areas sa ON sa.id = p.run_sub_area_id AND sa.run_id = c.run_id
              WHERE c.run_id = $run_id
              ORDER BY c.id
              """
            : """
              SELECT sa.building, sa.text, p.page_name, c.name, c.id,
                     c.switch, c.mode, c.indoor, c.set_temp, c.fan, c.indicator, c.comm
              FROM cards c
              JOIN pages p ON p.id = c.page_id
              JOIN sub_areas sa ON sa.id = p.sub_area_id
              ORDER BY c.id
              """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<(string BaseKey, string Signature)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var building = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var subArea = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var page = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var baseKey = string.Join("\u001F", building, subArea, page, name);
            var values = Enumerable.Range(5, 7)
                .Select(index => reader.IsDBNull(index) ? string.Empty : reader.GetString(index));
            rows.Add((baseKey, string.Join("\u001F", values)));
        }

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in rows.GroupBy(row => row.BaseKey, StringComparer.OrdinalIgnoreCase))
        {
            result[group.Key] = group.Select(row => row.Signature).ToList();
        }

        return result;
    }

    private static Dictionary<string, int> CountSignatures(IEnumerable<string> signatures) =>
        signatures
            .GroupBy(signature => signature, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static async Task<string?> GetRestoreBlockingReasonAsync(
        SqliteConnection connection,
        CollectionRunRecord run,
        CancellationToken cancellationToken)
    {
        if (run.IsAnomaly)
        {
            return "异常隔离批次不能恢复，请先取消异常标记并复核数据。";
        }

        if (CollectionRunCompleteness.HasBlockingQualityFailure(run.QualitySummary))
        {
            return "质量审计存在阻断问题的批次不能恢复，请先完成复核或重新采集。";
        }

        if (!run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            return $"状态为“{run.Status}”的批次不能恢复，仅允许恢复已完成批次。";
        }

        if (!run.Scope.Equals("full", StringComparison.OrdinalIgnoreCase) &&
            !run.Scope.Equals("partial", StringComparison.OrdinalIgnoreCase))
        {
            return $"批次范围“{run.Scope}”无效，无法安全恢复。";
        }

        if (run.Scope.Equals("full", StringComparison.OrdinalIgnoreCase) &&
            !run.Buildings.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(CollectionRunCompleteness.RequiredBuildings))
        {
            return "全量批次未覆盖 1-6 号楼，无法安全恢复。";
        }

        try
        {
            await ValidateSnapshotAsync(connection, run, cancellationToken).ConfigureAwait(false);
            if (run.Scope.Equals("partial", StringComparison.OrdinalIgnoreCase) && run.Buildings.Count == 0)
            {
                return "部分批次没有楼栋范围，无法安全恢复。";
            }

            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    private static async Task ValidateSnapshotAsync(
        SqliteConnection connection,
        CollectionRunRecord run,
        CancellationToken cancellationToken)
    {
        var runId = run.Id;
        var snapshotByBuilding = await LoadBuildingCardCountsAsync(connection, runId, snapshot: true, cancellationToken).ConfigureAwait(false);
        var declaredBuildings = run.Buildings
            .Where(building => !string.IsNullOrWhiteSpace(building))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var buildings = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM run_buildings WHERE run_id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        var subAreas = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM run_sub_areas WHERE run_id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        var pages = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM run_pages WHERE run_id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        var cards = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM run_cards WHERE run_id = $run_id", runId, cancellationToken).ConfigureAwait(false);
        if (run.Buildings.Count != declaredBuildings.Count ||
            buildings != declaredBuildings.Count ||
            !snapshotByBuilding.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(declaredBuildings) ||
            subAreas == 0 || pages == 0 || cards != run.CardCount || cards == 0 ||
            !CollectionRunCompleteness.HasSelfConsistentBuildingCardCounts(
                snapshotByBuilding,
                declaredBuildings,
                run.CardCount))
        {
            throw new InvalidOperationException($"批次 #{runId} 快照计数不完整或不自洽：楼栋 {buildings}/{run.Buildings.Count}，子区 {subAreas}，页面 {pages}，卡片 {cards}/{run.CardCount}。");
        }

        var orphanSubAreas = await ScalarLongAsync(connection, """
            SELECT COUNT(*) FROM run_sub_areas sa
            WHERE sa.run_id = $run_id AND NOT EXISTS (
                SELECT 1 FROM run_buildings b WHERE b.run_id = sa.run_id AND b.building = sa.building)
            """, runId, cancellationToken).ConfigureAwait(false);
        var orphanPages = await ScalarLongAsync(connection, """
            SELECT COUNT(*) FROM run_pages p
            WHERE p.run_id = $run_id AND NOT EXISTS (
                SELECT 1 FROM run_sub_areas sa WHERE sa.run_id = p.run_id AND sa.id = p.run_sub_area_id)
            """, runId, cancellationToken).ConfigureAwait(false);
        var orphanCards = await ScalarLongAsync(connection, """
            SELECT COUNT(*) FROM run_cards c
            WHERE c.run_id = $run_id AND NOT EXISTS (
                SELECT 1 FROM run_pages p WHERE p.run_id = c.run_id AND p.id = c.run_page_id)
            """, runId, cancellationToken).ConfigureAwait(false);
        if (orphanSubAreas > 0 || orphanPages > 0 || orphanCards > 0)
        {
            throw new InvalidOperationException($"批次 #{runId} 层级映射不完整：孤立子区 {orphanSubAreas}，孤立页面 {orphanPages}，孤立卡片 {orphanCards}。");
        }
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        long runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$run_id", runId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
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

        var now = StoredTimestamp.FormatLocal(DateTimeOffset.Now);
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
                     card_count, on_count, off_count, offline_count, unknown_count, note,
                     source, data_version, operator_name, restored_from_run_id)
                SELECT $run_key, $now, $now, $now, 'backup', 'full', $buildings,
                       COUNT(*),
                       SUM(comm = '开机' OR switch = 'ON'),
                       SUM(comm = '关机' OR switch = 'OFF'),
                       SUM(comm = '离线'),
                       SUM(COALESCE(comm, '') NOT IN ('开机', '关机', '离线') AND COALESCE(switch, '') NOT IN ('ON', 'OFF')),
                       $note, '手动恢复', $data_version, '本机', $restored_from_run_id
                FROM cards
                RETURNING id;
                """;
            insertRun.Parameters.AddWithValue("$run_key", runKey);
            insertRun.Parameters.AddWithValue("$now", now);
            insertRun.Parameters.AddWithValue("$buildings", JsonSerializer.Serialize(buildings));
            insertRun.Parameters.AddWithValue("$note", $"恢复批次 #{targetRun.Id} 前自动备份");
            insertRun.Parameters.AddWithValue("$data_version", targetRun.DataVersion);
            insertRun.Parameters.AddWithValue("$restored_from_run_id", targetRun.Id);
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
                 duplicate_names, on_href, off_href, layout, quality_reason, collected_at, err)
            SELECT $run_id, rsa.id, p.id, p.page_name, p.count, p.raw_count, p.unique_count,
                   p.duplicate_names, p.on_href, p.off_href, p.layout, p.quality_reason, p.collected_at, p.err
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
            building.Value = ReadString(reader, "building");
            subAreaCount.Value = DbValue(ReadNullableInt64(reader, "sub_area_count"));
            menuClicked.Value = DbValue(ReadString(reader, "menu_clicked"));
            updatedAt.Value = DbValue(ReadString(reader, "updated_at"));
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
            building.Value = ReadString(reader, "building");
            subIdx.Value = DbValue(ReadNullableInt64(reader, "sub_idx"));
            floor.Value = DbValue(ReadNullableDouble(reader, "floor"));
            text.Value = DbValue(ReadString(reader, "text"));
            x.Value = DbValue(ReadNullableInt64(reader, "x"));
            y.Value = DbValue(ReadNullableInt64(reader, "y"));
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
                   duplicate_names, on_href, off_href, layout, quality_reason, collected_at, err
            FROM run_pages
            WHERE run_id = $run_id
            ORDER BY id
            """;
        select.Parameters.AddWithValue("$run_id", runId);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO pages (sub_area_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, collected_at, err)
            VALUES ($sub_area_id, $page_name, $count, $raw_count, $unique_count, $duplicate_names, $on_href, $off_href, $layout, $quality_reason, $collected_at, $err)
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
        var collectedAt = insert.Parameters.Add("$collected_at", SqliteType.Text);
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
            pageName.Value = DbValue(ReadString(reader, "page_name"));
            count.Value = DbValue(ReadNullableInt64(reader, "count"));
            rawCount.Value = DbValue(ReadNullableInt64(reader, "raw_count"));
            uniqueCount.Value = DbValue(ReadNullableInt64(reader, "unique_count"));
            duplicateNames.Value = DbValue(ReadString(reader, "duplicate_names"));
            onHref.Value = DbValue(ReadString(reader, "on_href"));
            offHref.Value = DbValue(ReadString(reader, "off_href"));
            layout.Value = DbValue(ReadString(reader, "layout"));
            qualityReason.Value = DbValue(ReadString(reader, "quality_reason"));
            collectedAt.Value = DbValue(ReadString(reader, "collected_at"));
            err.Value = DbValue(ReadString(reader, "err"));
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
            name.Value = DbValue(ReadString(reader, "name"));
            switchState.Value = DbValue(ReadString(reader, "switch"));
            mode.Value = DbValue(ReadString(reader, "mode"));
            indoor.Value = DbValue(ReadString(reader, "indoor"));
            setTemp.Value = DbValue(ReadString(reader, "set_temp"));
            fan.Value = DbValue(ReadString(reader, "fan"));
            indicator.Value = DbValue(ReadString(reader, "indicator"));
            comm.Value = DbValue(ReadString(reader, "comm"));
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

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task EnsureQualityReasonColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await AddColumnIfMissingAsync(connection, "pages", "quality_reason", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "run_pages", "quality_reason", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "pages", "collected_at", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "run_pages", "collected_at", "TEXT", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureCollectionRunMetadataColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "collection_runs", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await AddColumnIfMissingAsync(
            connection,
            "collection_runs",
            "source",
            "TEXT NOT NULL DEFAULT '采集导入'",
            cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection,
            "collection_runs",
            "data_version",
            "TEXT NOT NULL DEFAULT 'v1.0.0'",
            cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection,
            "collection_runs",
            "operator_name",
            "TEXT NOT NULL DEFAULT '本机'",
            cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection,
            "collection_runs",
            "restored_from_run_id",
            "INTEGER",
            cancellationToken).ConfigureAwait(false);
    }

    private void DeleteAssociatedArtifacts(CollectionRunRecord run)
    {
        var databaseDirectory = Path.GetFullPath(Path.GetDirectoryName(databasePathResolver()) ?? Directory.GetCurrentDirectory());
        var candidates = new List<string>();
        foreach (var path in new[] { run.JsonPath, run.DbSnapshotPath })
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                candidates.Add(Path.IsPathRooted(path) ? path : Path.Combine(databaseDirectory, path));
            }
        }

        candidates.Add(Path.Combine(databaseDirectory, $"quality_report_run{run.Id}.json"));
        candidates.Add(Path.Combine(databaseDirectory, $"quality_report_run{run.Id}.txt"));
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (!fullPath.StartsWith(databaseDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fullPath, Path.GetFullPath(databasePathResolver()), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            catch (IOException)
            {
                // History deletion must not leave the SQLite database half-deleted because an optional artifact is locked.
            }
            catch (UnauthorizedAccessException)
            {
                // The next cleanup can remove a file that is temporarily protected by another process.
            }
        }
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnType,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, tableName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = $"PRAGMA table_info({tableName})";
            await using var reader = await probe.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(ReadString(reader, "name"), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnType}";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static string ReadString(SqliteDataReader reader, string column, string fallback = "")
    {
        var ordinal = GetOrdinal(reader, column);
        if (ordinal < 0)
        {
            return fallback;
        }

        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static int GetOrdinal(SqliteDataReader reader, string column)
    {
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (string.Equals(reader.GetName(index), column, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static int ReadInt32(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, string column)
    {
        var ordinal = GetOrdinal(reader, column);
        if (ordinal < 0)
        {
            return null;
        }

        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static double? ReadNullableDouble(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }
}
