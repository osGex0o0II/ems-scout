using System.Text.Json;
using EmsScout.Application;
using EmsScout.Application.Collection;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteCollectionRunRepository(
    Func<string> databasePathResolver,
    ICollectionRunActivity? activity = null) : ICollectionRunRepository
{
    private CollectionRunArtifactCleaner ArtifactCleaner { get; } = new(databasePathResolver);

    public async Task<IReadOnlyList<CollectionRunRecord>> ListAsync(
        int? limit = 50,
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

        var limitClause = limit.HasValue ? "LIMIT $limit" : string.Empty;
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT collection_runs.*,
                   {snapshotCardCount} AS snapshot_card_count
            FROM collection_runs
            ORDER BY datetime(COALESCE(completed_at, imported_at)) DESC, id DESC
            {limitClause}
            """;
        if (limit.HasValue)
        {
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit.Value, 1, 500));
        }

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
        var currentByBuilding = await LoadBuildingCardCountsAsync(
            connection,
            runId,
            snapshot: false,
            cancellationToken,
            scopeBuildings: run.Buildings).ConfigureAwait(false);
        var changedCount = await CountChangedCardsAsync(
            connection,
            runId,
            run.Buildings,
            cancellationToken).ConfigureAwait(false);
        var blockingReason = await GetRestoreBlockingReasonAsync(connection, run, cancellationToken).ConfigureAwait(false);

        return CollectionRunComparison.Create(
            run,
            snapshotByBuilding,
            currentByBuilding,
            changedCount,
            isRestorable: blockingReason is null,
            blockingReason);
    }

    public async Task<RunDeleteImpact> GetDeleteImpactAsync(
        long runId,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: true);
        var run = await LoadRunAsync(connection, runId, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException($"Run not found: {runId}");
        var currentRunIds = await LoadCurrentRunIdsAsync(connection, null, cancellationToken).ConfigureAwait(false);
        var blockers = await GetDeleteBlockingReasonsAsync(
            connection,
            null,
            run,
            currentRunIds,
            cancellationToken).ConfigureAwait(false);
        var realtimeRows = await CountAsync(connection, null,
            "SELECT COUNT(*) FROM run_realtime_details WHERE run_id = $run_id",
            runId,
            cancellationToken).ConfigureAwait(false);
        var pages = await CountAsync(connection, null,
            "SELECT COUNT(*) FROM run_pages WHERE run_id = $run_id",
            runId,
            cancellationToken).ConfigureAwait(false);
        var subAreas = await CountAsync(connection, null,
            "SELECT COUNT(*) FROM run_sub_areas WHERE run_id = $run_id",
            runId,
            cancellationToken).ConfigureAwait(false);
        var buildings = await CountAsync(connection, null,
            "SELECT COUNT(*) FROM run_buildings WHERE run_id = $run_id",
            runId,
            cancellationToken).ConfigureAwait(false);
        var artifacts = ArtifactCleaner.FindCandidates(run);
        return new RunDeleteImpact(
            run.Id,
            run.RunKey,
            currentRunIds.Contains(run.Id),
            run.SnapshotCardCount,
            realtimeRows,
            pages,
            subAreas,
            buildings,
            artifacts,
            blockers);
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
        if (activity?.IsActive == true)
        {
            throw new InvalidOperationException("采集任务正在运行，暂时不能恢复历史批次。");
        }

        await using var connection = OpenConnection(readOnly: false);
        await EnsureCollectionRunMetadataColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureQualityReasonColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureGovernanceTablesAsync(connection, cancellationToken).ConfigureAwait(false);
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

        if (activity?.IsActive == true)
        {
            throw new InvalidOperationException("采集任务正在运行，暂时不能恢复历史批次。");
        }

        await using var transaction = connection.BeginTransaction(deferred: false);
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
        var revisionUid = Guid.NewGuid().ToString("N");
        await UpdateCurrentDataSourcesAsync(
            connection,
            transaction,
            run,
            revisionUid,
            isPartial,
            cancellationToken).ConfigureAwait(false);

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
        var results = await DeleteManyAsync([runId], cancellationToken).ConfigureAwait(false);
        return results.Single();
    }

    public async Task<IReadOnlyList<CollectionRunDeleteResult>> DeleteManyAsync(
        IReadOnlyList<long> runIds,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        var requestedIds = runIds.Distinct().ToArray();
        if (requestedIds.Length == 0 || requestedIds.Any(id => id <= 0))
        {
            throw new InvalidOperationException("没有有效的历史批次可删除。");
        }

        if (activity?.IsActive == true)
        {
            throw new InvalidOperationException("采集任务正在运行，暂时不能删除历史批次。");
        }

        await using var connection = OpenConnection(readOnly: false);
        await EnsureCollectionRunMetadataColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureGovernanceTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var currentRunIds = await LoadCurrentRunIdsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var runs = new List<CollectionRunRecord>();
        foreach (var id in requestedIds)
        {
            var run = await LoadRunAsync(connection, id, cancellationToken, transaction).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"Run not found: {id}");
            runs.Add(run);
        }

        var blocking = new List<string>();
        foreach (var run in runs)
        {
            var reasons = await GetDeleteBlockingReasonsAsync(
                connection,
                transaction,
                run,
                currentRunIds,
                cancellationToken).ConfigureAwait(false);
            blocking.AddRange(reasons.Select(reason => $"批次 #{run.Id}：{reason}"));
        }

        if (blocking.Count > 0)
        {
            throw new InvalidOperationException(string.Join("；", blocking));
        }

        var artifactCandidates = runs.ToDictionary(
            run => run.Id,
            run => ArtifactCleaner.FindCandidates(run));
        var results = new List<CollectionRunDeleteResult>(runs.Count);
        foreach (var run in runs)
        {
            var deletedRealtime = await TableExistsAsync(connection, "run_realtime_details", cancellationToken, transaction).ConfigureAwait(false)
                ? await ExecuteCountAsync(connection, transaction, "DELETE FROM run_realtime_details WHERE run_id = $run_id", run.Id, cancellationToken).ConfigureAwait(false)
                : 0;
            var deletedCards = await ExecuteCountAsync(connection, transaction, "DELETE FROM run_cards WHERE run_id = $run_id", run.Id, cancellationToken).ConfigureAwait(false);
            var deletedPages = await ExecuteCountAsync(connection, transaction, "DELETE FROM run_pages WHERE run_id = $run_id", run.Id, cancellationToken).ConfigureAwait(false);
            var deletedSubAreas = await ExecuteCountAsync(connection, transaction, "DELETE FROM run_sub_areas WHERE run_id = $run_id", run.Id, cancellationToken).ConfigureAwait(false);
            var deletedBuildings = await ExecuteCountAsync(connection, transaction, "DELETE FROM run_buildings WHERE run_id = $run_id", run.Id, cancellationToken).ConfigureAwait(false);
            await ExecuteCountAsync(connection, transaction, "DELETE FROM collection_runs WHERE id = $run_id", run.Id, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "UPDATE run_key_registry SET deleted_at = $deleted_at, last_run_id = $run_id WHERE run_key = $run_key AND deleted_at IS NULL", cancellationToken,
                ("$deleted_at", StoredTimestamp.FormatLocal(DateTimeOffset.Now)),
                ("$run_id", run.Id),
                ("$run_key", run.RunKey));

            var operationId = Guid.NewGuid();
            await InsertOperationAsync(connection, transaction, new RunOperationRecord(
                operationId,
                "delete",
                run.Id,
                run.RunKey,
                run.BatchUid,
                StoredTimestamp.FormatLocal(DateTimeOffset.Now),
                "database_deleted",
                $"删除历史快照 {run.RunKey}",
                deletedCards,
                deletedPages,
                deletedSubAreas,
                deletedBuildings,
                artifactCandidates[run.Id].Count(item => !item.IsShared)), cancellationToken).ConfigureAwait(false);
            results.Add(new CollectionRunDeleteResult(
                run.Id,
                run.RunKey,
                run.CompletedAt,
                deletedCards,
                deletedPages,
                deletedSubAreas,
                deletedBuildings,
                operationId,
                new ArtifactCleanupResult(0, [], [])));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var run in runs)
        {
            var resultIndex = results.FindIndex(item => item.RunId == run.Id && item.RunKey == run.RunKey);
            var operationResult = results[resultIndex];
            ArtifactCleanupResult cleanup;
            try
            {
                cleanup = ArtifactCleaner.Cleanup(run, artifactCandidates[run.Id]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                var pending = artifactCandidates[run.Id]
                    .Where(item => !item.IsShared)
                    .Select(item => item.RelativePath)
                    .ToArray();
                cleanup = new ArtifactCleanupResult(
                    0,
                    pending,
                    pending.Select(_ => "清理阶段发生异常：" + exception.GetType().Name).ToArray());
            }
            results[resultIndex] = operationResult with { ArtifactCleanup = cleanup };
            await UpdateOperationCleanupAsync(
                connection,
                operationResult.OperationId,
                cleanup.IsComplete ? "completed" : "database_deleted_artifacts_pending",
                cleanup.PendingPaths.Count,
                cancellationToken).ConfigureAwait(false);
        }

        return results;
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

    private static async Task<HashSet<long>> LoadCurrentRunIdsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "current_data_sources", cancellationToken, transaction).ConfigureAwait(false))
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT run_id FROM current_data_sources WHERE run_id IS NOT NULL AND COALESCE(state, 'bound') = 'bound'";
        var result = new HashSet<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    private async Task<IReadOnlyList<string>> GetDeleteBlockingReasonsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CollectionRunRecord run,
        IReadOnlySet<long> currentRunIds,
        CancellationToken cancellationToken)
    {
        var reasons = new List<string>();
        if (currentRunIds.Contains(run.Id))
        {
            reasons.Add("当前数据正在使用该批次");
        }

        if (await TableExistsAsync(connection, "current_data_sources", cancellationToken, transaction).ConfigureAwait(false))
        {
            var declaredBuildings = run.Buildings.ToHashSet(StringComparer.OrdinalIgnoreCase);
            await using var sourceCommand = connection.CreateCommand();
            sourceCommand.Transaction = transaction;
            sourceCommand.CommandText = "SELECT building, state, run_id, batch_uid, reason FROM current_data_sources";
            await using var sourceReader = await sourceCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var sourceRows = new List<(string Building, string State, long? RunId, string BatchUid, string Reason)>();
            while (await sourceReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sourceRows.Add((
                    sourceReader.GetString(0),
                    sourceReader.IsDBNull(1) ? CurrentDataSourceStates.Bound : sourceReader.GetString(1),
                    sourceReader.IsDBNull(2) ? null : sourceReader.GetInt64(2),
                    sourceReader.IsDBNull(3) ? string.Empty : sourceReader.GetString(3),
                    sourceReader.IsDBNull(4) ? string.Empty : sourceReader.GetString(4)));
            }

            var overlapping = sourceRows.Where(row => declaredBuildings.Contains(row.Building)).ToArray();
            if (overlapping.Any(row => row.RunId == run.Id ||
                                       !string.IsNullOrWhiteSpace(row.BatchUid) &&
                                       string.Equals(row.BatchUid, run.BatchUid, StringComparison.Ordinal)))
            {
                reasons.Add("当前数据正在使用该批次");
            }

            var unresolved = overlapping.Where(row =>
                !string.Equals(row.State, CurrentDataSourceStates.Bound, StringComparison.OrdinalIgnoreCase) ||
                row.RunId is null or <= 0 ||
                string.IsNullOrWhiteSpace(row.BatchUid)).ToArray();
            if (unresolved.Length > 0)
            {
                var detail = unresolved.Select(row =>
                    string.IsNullOrWhiteSpace(row.Reason)
                        ? row.State.Equals(CurrentDataSourceStates.Bound, StringComparison.OrdinalIgnoreCase)
                            ? $"{row.Building}：身份未完整"
                            : row.Building
                        : $"{row.Building}：{row.Reason}");
                reasons.Add("当前数据来源未确定，不能安全删除重叠楼栋的历史批次（" + string.Join("、", detail) + "）");
            }

            if (run.Scope.Equals("full", StringComparison.OrdinalIgnoreCase))
            {
                var missingBuildings = declaredBuildings
                    .Except(sourceRows.Select(row => row.Building), StringComparer.OrdinalIgnoreCase)
                    .OrderBy(building => building, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (declaredBuildings.Count == 0)
                {
                    reasons.Add("全量历史批次没有声明楼栋范围，不能安全删除");
                }
                else if (missingBuildings.Length > 0)
                {
                    reasons.Add("当前数据来源未覆盖全量批次声明的楼栋（" + string.Join("、", missingBuildings) + "）");
                }
            }
        }
        else
        {
            reasons.Add("当前数据来源尚未建立，不能安全删除历史批次");
        }

        if (await TableExistsAsync(connection, "collection_runs", cancellationToken, transaction).ConfigureAwait(false))
        {
            await using var dependency = connection.CreateCommand();
            dependency.Transaction = transaction;
            var hasRestoredRunId = await ColumnExistsAsync(
                connection,
                "collection_runs",
                "restored_from_run_id",
                cancellationToken,
                transaction).ConfigureAwait(false);
            var hasRestoredBatchUid = await ColumnExistsAsync(
                connection,
                "collection_runs",
                "restored_from_batch_uid",
                cancellationToken,
                transaction).ConfigureAwait(false);
            dependency.CommandText = hasRestoredBatchUid && hasRestoredRunId
                ? "SELECT COUNT(*) FROM collection_runs WHERE id <> $id AND (restored_from_run_id = $id OR restored_from_batch_uid = $batch_uid)"
                : hasRestoredRunId
                    ? "SELECT COUNT(*) FROM collection_runs WHERE id <> $id AND restored_from_run_id = $id"
                    : hasRestoredBatchUid
                        ? "SELECT COUNT(*) FROM collection_runs WHERE id <> $id AND restored_from_batch_uid = $batch_uid"
                        : null;
            dependency.Parameters.AddWithValue("$id", run.Id);
            if (hasRestoredBatchUid)
            {
                dependency.Parameters.AddWithValue("$batch_uid", run.BatchUid);
            }

            if (dependency.CommandText is not null)
            {
                var count = Convert.ToInt64(await dependency.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                if (count > 0)
                {
                    reasons.Add($"仍有 {count} 个恢复/备份记录依赖该批次");
                }
            }
        }

        if (activity?.IsActive == true)
        {
            reasons.Add("采集任务正在运行，暂时不能删除历史批次");
        }

        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        long runId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$run_id", runId);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static async Task EnsureGovernanceTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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
            CREATE UNIQUE INDEX IF NOT EXISTS ux_collection_runs_batch_uid
                ON collection_runs(batch_uid) WHERE batch_uid <> '';
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
            registerIds.CommandText = "INSERT OR IGNORE INTO run_id_registry (technical_id, allocated_at, allocation_kind) SELECT id, COALESCE(imported_at, completed_at, $now), 'collection_run' FROM collection_runs";
            registerIds.Parameters.AddWithValue("$now", StoredTimestamp.FormatLocal(DateTimeOffset.Now));
            await registerIds.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            await using var sequence = connection.CreateCommand();
            sequence.CommandText = "SELECT seq FROM sqlite_sequence WHERE name = 'collection_runs' LIMIT 1";
            var value = await sequence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null && value is not DBNull && Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                await using var registerSequence = connection.CreateCommand();
                registerSequence.CommandText = "INSERT OR IGNORE INTO run_id_registry (technical_id, allocated_at, allocation_kind) VALUES ($id, $allocated_at, 'sqlite_sequence_high_watermark')";
                registerSequence.Parameters.AddWithValue("$id", Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                registerSequence.Parameters.AddWithValue("$allocated_at", StoredTimestamp.FormatLocal(DateTimeOffset.Now));
                await registerSequence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqliteException)
        {
            // Databases without AUTOINCREMENT do not expose sqlite_sequence.
        }
        await AddColumnIfMissingAsync(connection, "current_data_sources", "state", "TEXT NOT NULL DEFAULT 'bound'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "current_data_sources", "reason", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunOperationRecord operation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO run_operations
                (operation_id, operation_type, run_id, batch_uid, run_key, occurred_at, result, summary,
                 deleted_cards, deleted_pages, deleted_sub_areas, deleted_buildings, pending_artifacts)
            VALUES
                ($operation_id, $operation_type, $run_id, $batch_uid, $run_key, $occurred_at, $result, $summary,
                 $deleted_cards, $deleted_pages, $deleted_sub_areas, $deleted_buildings, $pending_artifacts)
            """;
        command.Parameters.AddWithValue("$operation_id", operation.OperationId.ToString());
        command.Parameters.AddWithValue("$operation_type", operation.OperationType);
        command.Parameters.AddWithValue("$run_id", operation.RunId);
        command.Parameters.AddWithValue("$batch_uid", operation.BatchUid);
        command.Parameters.AddWithValue("$run_key", operation.RunKey);
        command.Parameters.AddWithValue("$occurred_at", operation.OccurredAt);
        command.Parameters.AddWithValue("$result", operation.Result);
        command.Parameters.AddWithValue("$summary", operation.Summary);
        command.Parameters.AddWithValue("$deleted_cards", operation.DeletedCards);
        command.Parameters.AddWithValue("$deleted_pages", operation.DeletedPages);
        command.Parameters.AddWithValue("$deleted_sub_areas", operation.DeletedSubAreas);
        command.Parameters.AddWithValue("$deleted_buildings", operation.DeletedBuildings);
        command.Parameters.AddWithValue("$pending_artifacts", operation.PendingArtifacts);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateOperationCleanupAsync(
        SqliteConnection connection,
        Guid operationId,
        string result,
        int pendingArtifacts,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE run_operations SET result = $result, pending_artifacts = $pending WHERE operation_id = $operation_id";
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$pending", pendingArtifacts);
        command.Parameters.AddWithValue("$operation_id", operationId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CollectionRunRecord?> LoadRunAsync(
        SqliteConnection connection,
        long runId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var snapshotCardCount = await TableExistsAsync(connection, "run_cards", cancellationToken, transaction).ConfigureAwait(false)
            ? "(SELECT COUNT(*) FROM run_cards snapshot WHERE snapshot.run_id = collection_runs.id)"
            : "0";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
                    cancellationToken,
                    transaction).ConfigureAwait(false),
            };
    }

    private static CollectionRunRecord ReadRun(SqliteDataReader reader)
    {
        return new CollectionRunRecord(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            RunNumber: ReadNullableInt64(reader, "run_no") ?? reader.GetInt64(reader.GetOrdinal("id")),
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
            CollectionMode: ReadString(reader, "collection_mode"),
            Operator: ReadString(reader, "operator_name", "本机"),
            RestoredFromRunId: ReadNullableInt64(reader, "restored_from_run_id"),
            BatchUid: ReadString(reader, "batch_uid"),
            LifecycleState: ReadString(reader, "lifecycle_state", "completed"),
            CurrentRevisionUid: ReadNullableString(reader, "current_revision_uid"),
            RestoredFromBatchUid: ReadNullableString(reader, "restored_from_batch_uid"));
    }

    private static async Task<Dictionary<string, int>> LoadBuildingCardCountsAsync(
        SqliteConnection connection,
        long runId,
        bool snapshot,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null,
        IReadOnlyList<string>? scopeBuildings = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var scope = !snapshot && scopeBuildings is { Count: > 0 }
            ? $"WHERE sa.building IN ({string.Join(", ", scopeBuildings.Select((_, index) => "$scope_building_" + index))})"
            : string.Empty;
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
              """ + Environment.NewLine + scope + """
              GROUP BY sa.building
              """;
        command.Parameters.AddWithValue("$run_id", runId);
        if (!snapshot && scopeBuildings is { Count: > 0 })
        {
            for (var index = 0; index < scopeBuildings.Count; index++)
            {
                command.Parameters.AddWithValue("$scope_building_" + index, scopeBuildings[index]);
            }
        }
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
        IReadOnlyList<string> scopeBuildings,
        CancellationToken cancellationToken)
    {
        var snapshot = await LoadCardSignaturesAsync(connection, runId, snapshot: true, cancellationToken).ConfigureAwait(false);
        var current = await LoadCardSignaturesAsync(
            connection,
            runId,
            snapshot: false,
            cancellationToken,
            scopeBuildings).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        IReadOnlyList<string>? scopeBuildings = null)
    {
        await using var command = connection.CreateCommand();
        var scope = !snapshot && scopeBuildings is { Count: > 0 }
            ? $"WHERE sa.building IN ({string.Join(", ", scopeBuildings.Select((_, index) => "$scope_building_" + index))})"
            : string.Empty;
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
              """ + Environment.NewLine + scope + """
              ORDER BY c.id
              """;
        command.Parameters.AddWithValue("$run_id", runId);
        if (!snapshot && scopeBuildings is { Count: > 0 })
        {
            for (var index = 0; index < scopeBuildings.Count; index++)
            {
                command.Parameters.AddWithValue("$scope_building_" + index, scopeBuildings[index]);
            }
        }
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
        var batchUid = Guid.NewGuid().ToString("N");
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
        var allocatedRunId = await AllocateCollectionRunIdAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        var allocatedRunNumber = await AllocateRunNumberAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await RegisterTechnicalRunIdAsync(
            connection,
            transaction,
            allocatedRunId,
            now,
            cancellationToken).ConfigureAwait(false);
        await using (var insertRun = connection.CreateCommand())
        {
            insertRun.Transaction = transaction;
            insertRun.CommandText = """
                INSERT INTO collection_runs
                    (id, run_no, run_key, batch_uid, started_at, completed_at, imported_at, status, scope, buildings,
                     card_count, on_count, off_count, offline_count, unknown_count, note,
                     source, data_version, operator_name, restored_from_run_id, restored_from_batch_uid, lifecycle_state)
                SELECT $run_id, $run_no, $run_key, $batch_uid, $now, $now, $now, 'backup', 'full', $buildings,
                       COUNT(*),
                       SUM(comm = '开机' OR switch = 'ON'),
                       SUM(comm = '关机' OR switch = 'OFF'),
                       SUM(comm = '离线'),
                       SUM(COALESCE(comm, '') NOT IN ('开机', '关机', '离线') AND COALESCE(switch, '') NOT IN ('ON', 'OFF')),
                       $note, '手动恢复', $data_version, '本机', $restored_from_run_id, $restored_from_batch_uid, 'completed'
                FROM cards
                RETURNING id;
                """;
            insertRun.Parameters.AddWithValue("$run_id", allocatedRunId);
            insertRun.Parameters.AddWithValue("$run_no", allocatedRunNumber);
            insertRun.Parameters.AddWithValue("$run_key", runKey);
            insertRun.Parameters.AddWithValue("$batch_uid", batchUid);
            insertRun.Parameters.AddWithValue("$now", now);
            insertRun.Parameters.AddWithValue("$buildings", JsonSerializer.Serialize(buildings));
            insertRun.Parameters.AddWithValue("$note", $"恢复批次 #{targetRun.Id} 前自动备份");
            insertRun.Parameters.AddWithValue("$data_version", targetRun.DataVersion);
            insertRun.Parameters.AddWithValue("$restored_from_run_id", targetRun.Id);
            insertRun.Parameters.AddWithValue("$restored_from_batch_uid", targetRun.BatchUid);
            backupRunId = Convert.ToInt64(
                await insertRun.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (backupRunId != allocatedRunId)
        {
            throw new InvalidOperationException(
                $"Allocated collection run id {allocatedRunId}, but SQLite inserted {backupRunId}.");
        }

        await using (var register = connection.CreateCommand())
        {
            register.Transaction = transaction;
            register.CommandText = """
                INSERT INTO run_key_registry (run_key, batch_uid, first_seen_at, last_run_id)
                VALUES ($run_key, $batch_uid, $first_seen_at, $last_run_id)
                """;
            register.Parameters.AddWithValue("$run_key", runKey);
            register.Parameters.AddWithValue("$batch_uid", batchUid);
            register.Parameters.AddWithValue("$first_seen_at", now);
            register.Parameters.AddWithValue("$last_run_id", backupRunId);
            await register.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task<long> AllocateCollectionRunIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MAX(value) + 1
            FROM (
                SELECT COALESCE(MAX(id), 0) AS value FROM collection_runs
                UNION ALL
                SELECT COALESCE(MAX(technical_id), 0) AS value FROM run_id_registry
            );
            """;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task RegisterTechnicalRunIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        string allocatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO run_id_registry (technical_id, allocated_at, allocation_kind) VALUES ($id, $allocated_at, 'collection_run')";
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$allocated_at", allocatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> AllocateRunNumberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(
                (
                    SELECT MIN(candidate)
                    FROM (
                        SELECT 1 AS candidate
                        UNION ALL
                        SELECT run_no + 1
                        FROM collection_runs
                        WHERE run_no IS NOT NULL AND run_no > 0
                    ) candidates
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM collection_runs existing
                        WHERE existing.run_no = candidates.candidate
                    )
                ),
                1
            );
            """;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task UpdateCurrentDataSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CollectionRunRecord run,
        string revisionUid,
        bool isPartial,
        CancellationToken cancellationToken)
    {
        var now = StoredTimestamp.FormatLocal(DateTimeOffset.Now);
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                INSERT INTO current_data_state (id, revision_uid, updated_at, source)
                VALUES (1, $revision_uid, $updated_at, '本机 SQLite')
                ON CONFLICT(id) DO UPDATE SET revision_uid = excluded.revision_uid, updated_at = excluded.updated_at, source = excluded.source
                """;
            state.Parameters.AddWithValue("$revision_uid", revisionUid);
            state.Parameters.AddWithValue("$updated_at", now);
            await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (isPartial)
        {
            var buildings = run.Buildings
                .Where(building => !string.IsNullOrWhiteSpace(building))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (buildings.Length == 0)
            {
                throw new InvalidOperationException("部分批次没有有效楼栋范围，无法更新当前数据来源。");
            }

            var parameters = buildings
                .Select((building, index) => (Name: "$building_" + index, Value: (object)building))
                .ToArray();
            await ExecuteAsync(
                connection,
                transaction,
                $"UPDATE current_data_sources SET revision_uid = $revision_uid WHERE building IN ({string.Join(", ", parameters.Select(item => item.Name))})",
                cancellationToken,
                new[] { (Name: "$revision_uid", Value: (object)revisionUid) }
                    .Concat(parameters)
                    .ToArray()).ConfigureAwait(false);
        }
        else
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM current_data_sources", cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO current_data_sources
                (building, revision_uid, run_id, batch_uid, source_updated_at, card_count, state, reason)
            VALUES
                ($building, $revision_uid, $run_id, $batch_uid, $source_updated_at, $card_count, 'bound', '')
            ON CONFLICT(building) DO UPDATE SET
                revision_uid = excluded.revision_uid,
                run_id = excluded.run_id,
                batch_uid = excluded.batch_uid,
                source_updated_at = excluded.source_updated_at,
                card_count = excluded.card_count,
                state = excluded.state,
                reason = excluded.reason
            """;
        var building = insert.Parameters.Add("$building", SqliteType.Text);
        var revision = insert.Parameters.Add("$revision_uid", SqliteType.Text);
        var runId = insert.Parameters.Add("$run_id", SqliteType.Integer);
        var batchUid = insert.Parameters.Add("$batch_uid", SqliteType.Text);
        var sourceUpdatedAt = insert.Parameters.Add("$source_updated_at", SqliteType.Text);
        var cardCount = insert.Parameters.Add("$card_count", SqliteType.Integer);

        foreach (var item in run.Buildings.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            building.Value = item;
            revision.Value = revisionUid;
            runId.Value = run.Id;
            batchUid.Value = string.IsNullOrWhiteSpace(run.BatchUid) ? DBNull.Value : run.BatchUid;
            sourceUpdatedAt.Value = string.IsNullOrWhiteSpace(run.CompletedAt) ? now : run.CompletedAt;
            cardCount.Value = run.BuildingCardCounts.GetValueOrDefault(item);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE collection_runs SET current_revision_uid = $revision_uid WHERE id = $run_id",
            cancellationToken,
            ("$revision_uid", revisionUid),
            ("$run_id", run.Id)).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

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
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
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
            "run_no",
            "INTEGER",
            cancellationToken).ConfigureAwait(false);
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
            "collection_mode",
            "TEXT NOT NULL DEFAULT ''",
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
        await AddColumnIfMissingAsync(
            connection,
            "collection_runs",
            "batch_uid",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection,
            "collection_runs",
            "lifecycle_state",
            "TEXT NOT NULL DEFAULT 'completed'",
            cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection,
            "collection_runs",
            "current_revision_uid",
            "TEXT",
            cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(
            connection,
            "collection_runs",
            "restored_from_batch_uid",
            "TEXT",
            cancellationToken).ConfigureAwait(false);
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

    private static string? ReadNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = GetOrdinal(reader, column);
        return ordinal < 0 || reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static double? ReadNullableDouble(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }
}
