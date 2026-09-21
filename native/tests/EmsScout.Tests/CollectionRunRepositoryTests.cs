using EmsScout.Application.Collection;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class CollectionRunRepositoryTests
{
    [Fact]
    public async Task CannotDeleteCurrentRun()
    {
        var databasePath = CreateDatabase();
        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();
        await ExecuteAsync(databasePath, "UPDATE current_data_state SET revision_uid = 'revision-1', updated_at = '2026-07-01T00:02:00Z'; UPDATE current_data_sources SET revision_uid = 'revision-1', run_id = 1, batch_uid = (SELECT batch_uid FROM collection_runs WHERE id = 1), source_updated_at = (SELECT completed_at FROM collection_runs WHERE id = 1), card_count = 1, state = 'bound', reason = '' WHERE building = '1号';");
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteAsync(1));

        Assert.Contains("当前数据", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivityGuardBlocksDeleteEvenWhenUiIsBypassed()
    {
        var databasePath = CreateDatabase();
        var activity = new CollectionRunActivityRegistry();
        var repository = new SqliteCollectionRunRepository(() => databasePath, activity);
        using var lease = activity.Begin();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteAsync(1));

        Assert.Contains("采集任务", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivityGuardBlocksRestoreEvenWhenUiIsBypassed()
    {
        var databasePath = CreateDatabase();
        var activity = new CollectionRunActivityRegistry();
        var repository = new SqliteCollectionRunRepository(() => databasePath, activity);
        using var lease = activity.Begin();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RestoreCurrentAsync(1));

        Assert.Contains("采集任务", error.Message, StringComparison.Ordinal);
        await using var verify = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await verify.OpenAsync();
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM collection_runs WHERE status = 'completed'"));
    }

    [Fact]
    public async Task BatchDeleteValidatesEveryRunBeforeChangingAnyRow()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteManyAsync([1, 999]));

        Assert.Single(await repository.ListAsync(null));
    }

    [Fact]
    public async Task MigrationRepairsDuplicateBatchUidsWithoutLeavingPartialSchema()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "ac.db");
        File.WriteAllBytes(databasePath, []);
        await ExecuteAsync(databasePath, """
            CREATE TABLE collection_runs (
                id INTEGER PRIMARY KEY,
                run_key TEXT UNIQUE,
                batch_uid TEXT NOT NULL DEFAULT '',
                completed_at TEXT NOT NULL,
                imported_at TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'completed',
                scope TEXT NOT NULL DEFAULT 'full',
                buildings TEXT NOT NULL DEFAULT '[]'
            );
            INSERT INTO collection_runs (id, run_key, batch_uid, completed_at, imported_at, buildings)
            VALUES (1, 'legacy-1', 'duplicate-batch', '2026-09-18', '2026-09-18', '["1号"]'),
                   (2, 'legacy-2', 'duplicate-batch', '2026-09-18', '2026-09-18', '["2号"]');
            """);

        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();

        await using var verify = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COUNT(DISTINCT batch_uid) FROM collection_runs WHERE batch_uid <> ''";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(2, reader.GetInt64(1));
    }

    [Fact]
    public async Task DeleteImpactIncludesOnlyIdentityVerifiedArtifacts()
    {
        var databasePath = CreateDatabase();
        var directory = Path.GetDirectoryName(databasePath)!;
        File.WriteAllText(Path.Combine(directory, "quality_report_run1.json"), "{\"run_id\":1}");
        File.WriteAllText(Path.Combine(directory, "collection_manifest_1.json"), "{\"runId\":1}");
        File.WriteAllText(Path.Combine(directory, "realtime_all_buildings_batch_summary_run1.json"), "{\"runId\":1}");
        File.WriteAllText(Path.Combine(directory, "quality_report.json"), "shared");

        var repository = new SqliteCollectionRunRepository(() => databasePath);
        var impact = await repository.GetDeleteImpactAsync(1);

        Assert.Contains(impact.Artifacts, item => item.RelativePath.EndsWith("quality_report_run1.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(impact.Artifacts, item => item.RelativePath.EndsWith("collection_manifest_1.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(impact.Artifacts, item => item.RelativePath.EndsWith("realtime_all_buildings_batch_summary_run1.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(impact.Artifacts, item => item.RelativePath.EndsWith("quality_report.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ManifestChildrenRequireTheirOwnIdentityAndDirectoriesBecomePending()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteCollectionRunRepository(() => databasePath);
        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();
        var run = Assert.Single(await repository.ListAsync(null));
        var directory = Path.GetDirectoryName(databasePath)!;
        var wrongChild = Path.Combine(directory, "wrong-batch.json");
        var childDirectory = Path.Combine(directory, "artifact-directory");
        Directory.CreateDirectory(childDirectory);
        File.WriteAllText(wrongChild, "{\"runId\":999,\"batchUid\":\"other\",\"runKey\":\"other\"}");
        File.WriteAllText(
            Path.Combine(directory, "collection_manifest_1.json"),
            $"{{\"runId\":{run.Id},\"batchUid\":\"{run.BatchUid}\",\"runKey\":\"{run.RunKey}\",\"resultFiles\":[\"wrong-batch.json\",\"artifact-directory\"]}}");

        var impact = await repository.GetDeleteImpactAsync(run.Id);

        var wrongCandidate = Assert.Single(impact.Artifacts, item => item.RelativePath == "wrong-batch.json");
        Assert.False(wrongCandidate.IdentityVerified);
        var directoryCandidate = Assert.Single(impact.Artifacts, item => item.RelativePath == "artifact-directory");
        Assert.False(directoryCandidate.IdentityVerified);

        var result = new CollectionRunArtifactCleaner(() => databasePath)
            .Cleanup(run, impact.Artifacts);

        Assert.Contains("wrong-batch.json", result.PendingPaths);
        Assert.Contains("artifact-directory", result.PendingPaths);
        Assert.True(File.Exists(wrongChild));
        Assert.True(Directory.Exists(childDirectory));
    }

    [Fact]
    public async Task VerifiedManifestAllowsCleanupOfNdjsonButKeepsLogChildren()
    {
        var databasePath = CreateDatabase();
        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();
        var repository = new SqliteCollectionRunRepository(() => databasePath);
        var run = Assert.Single(await repository.ListAsync(null));
        var directory = Path.GetDirectoryName(databasePath)!;
        var ndjsonPath = Path.Combine(directory, "realtime_1号.ndjson");
        var logPath = Path.Combine(directory, "realtime_all_batch_20260918_000000.log");
        File.WriteAllText(ndjsonPath, "{\"runId\":1,\"batchUid\":\"" + run.BatchUid + "\",\"runKey\":\"" + run.RunKey + "\",\"row\":1}\n");
        File.WriteAllText(logPath, "2026-09-18 INFO realtime batch\n");
        File.WriteAllText(
            Path.Combine(directory, "collection_manifest_1.json"),
            "{\"runId\":1,\"batchUid\":\"" + run.BatchUid + "\",\"runKey\":\"" + run.RunKey + "\",\"resultFiles\":[\"realtime_1号.ndjson\",\"realtime_all_batch_20260918_000000.log\"]}");

        var impact = await repository.GetDeleteImpactAsync(run.Id);
        var ndjson = Assert.Single(impact.Artifacts, item => item.RelativePath == "realtime_1号.ndjson");

        Assert.True(ndjson.IdentityVerified);
        Assert.DoesNotContain(impact.Artifacts, item => item.RelativePath == "realtime_all_batch_20260918_000000.log");
        var cleanup = new CollectionRunArtifactCleaner(() => databasePath).Cleanup(run, impact.Artifacts);
        Assert.True(cleanup.IsComplete);
        Assert.False(File.Exists(ndjsonPath));
        Assert.True(File.Exists(logPath));
    }

    [Fact]
    public async Task RestoreUpdatesCurrentDataSourceToRestoredBatchIdentity()
    {
        var databasePath = CreateDatabase();
        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();
        await ExecuteAsync(databasePath, "UPDATE current_data_state SET revision_uid = 'revision-before', updated_at = '2026-07-01T00:02:00Z'; UPDATE current_data_sources SET revision_uid = 'revision-before', run_id = 1, batch_uid = (SELECT batch_uid FROM collection_runs WHERE id = 1), source_updated_at = (SELECT completed_at FROM collection_runs WHERE id = 1), card_count = 1, state = 'bound', reason = '' WHERE building = '1号';");
        await ExecuteAsync(databasePath, "UPDATE cards SET name = 'BROKEN' WHERE id = 1");

        var repository = new SqliteCollectionRunRepository(() => databasePath);
        await repository.RestoreCurrentAsync(1);

        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.run_id, s.batch_uid, s.revision_uid, r.batch_uid FROM current_data_sources s JOIN collection_runs r ON r.id = s.run_id WHERE s.building = '1号'";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(reader.GetString(1), reader.GetString(3));
        Assert.NotEqual("revision-before", reader.GetString(2));
    }

    [Fact]
    public async Task DeleteWritesMinimalOperationWithImmutableBatchIdentity()
    {
        var databasePath = CreateDatabase();
        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();
        await ExecuteAsync(databasePath, "UPDATE current_data_sources SET state = 'bound', reason = '', run_id = 1, batch_uid = (SELECT batch_uid FROM collection_runs WHERE id = 1) WHERE building = '1号'; INSERT INTO collection_runs (id, run_key, batch_uid, completed_at, imported_at, status, scope, buildings, card_count) VALUES (2, 'run_2', 'batch-2', '2026-07-02T00:00:00Z', '2026-07-02T00:00:00Z', 'completed', 'partial', '[\"1号\"]', 0);");
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var result = await repository.DeleteAsync(2);

        Assert.NotEqual(Guid.Empty, result.OperationId);
        Assert.NotNull(result.ArtifactCleanup);
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_key, batch_uid, result, summary FROM run_operations WHERE operation_id = $id";
        command.Parameters.AddWithValue("$id", result.OperationId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("run_2", reader.GetString(0));
        Assert.Equal("batch-2", reader.GetString(1));
        Assert.Equal("completed", reader.GetString(2));
        Assert.DoesNotContain("1-0101-KT", reader.GetString(3), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MigratesImmutableBatchIdentityAndGovernanceTables()
    {
        var databasePath = CreateDatabase();

        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();

        var columns = await ReadColumnsAsync(databasePath, "collection_runs");
        Assert.Contains("batch_uid", columns);
        Assert.Contains("lifecycle_state", columns);
        Assert.Contains("current_revision_uid", columns);
        Assert.Contains("run_key_registry", await ReadTablesAsync(databasePath));
        Assert.Contains("run_operations", await ReadTablesAsync(databasePath));
        Assert.Contains("current_data_sources", await ReadTablesAsync(databasePath));
    }

    [Fact]
    public async Task MigrationPreservesLegacySqliteSequenceHighWaterMark()
    {
        var databasePath = CreateDatabase();
        await ExecuteAsync(databasePath, "INSERT INTO collection_runs (id, run_key, completed_at, imported_at, scope, buildings) VALUES (41, 'deleted-41', '2026-07-02T00:00:00Z', '2026-07-02T00:00:00Z', 'partial', '[\"1号\"]'); DELETE FROM collection_runs WHERE id = 41;");

        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();

        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        Assert.Equal(41L, await ScalarLongAsync(connection, "SELECT MAX(technical_id) FROM run_id_registry"));
    }

    [Fact]
    public async Task MigrationPreservesLegacyRunIdAsDisplayNumberWhenRunNumberIsMissing()
    {
        var databasePath = CreateDatabase();
        await ExecuteAsync(databasePath, "INSERT INTO collection_runs (id, run_key, completed_at, imported_at, scope, buildings) VALUES (37, 'run_37', '2026-07-02T00:00:00Z', '2026-07-02T00:00:00Z', 'partial', '[\"1号\"]'), (39, 'run_39', '2026-07-03T00:00:00Z', '2026-07-03T00:00:00Z', 'partial', '[\"1号\"]'), (41, 'run_41', '2026-07-04T00:00:00Z', '2026-07-04T00:00:00Z', 'partial', '[\"1号\"]');");

        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();

        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, run_no FROM collection_runs WHERE id IN (37, 39, 41) ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        var values = new Dictionary<long, long>();
        while (await reader.ReadAsync())
        {
            values[reader.GetInt64(0)] = reader.GetInt64(1);
        }

        Assert.Equal(37L, values[37]);
        Assert.Equal(39L, values[39]);
        Assert.Equal(41L, values[41]);
    }

    [Fact]
    public async Task PartialRestoreDoesNotRewriteRevisionOfUnselectedBuilding()
    {
        var databasePath = CreateDatabase();
        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();
        await ExecuteAsync(databasePath, "UPDATE current_data_sources SET state = 'bound', reason = '', run_id = 1, batch_uid = (SELECT batch_uid FROM collection_runs WHERE id = 1), revision_uid = 'revision-1' WHERE building = '1号'; INSERT INTO current_data_sources (building, revision_uid, run_id, batch_uid, source_updated_at, card_count, state, reason) VALUES ('2号', 'revision-2', 99, 'batch-99', '2026-07-02T00:00:00Z', 1, 'bound', '');");

        var repository = new SqliteCollectionRunRepository(() => databasePath);
        await repository.RestoreCurrentAsync(1);

        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision_uid, run_id, batch_uid FROM current_data_sources WHERE building = '2号'";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("revision-2", reader.GetString(0));
        Assert.Equal(99L, reader.GetInt64(1));
        Assert.Equal("batch-99", reader.GetString(2));
    }

    [Fact]
    public async Task PartialComparisonExcludesCurrentCardsOutsideSnapshotScope()
    {
        var databasePath = CreateDatabase();
        await ExecuteAsync(databasePath, "INSERT INTO buildings (building, sub_area_count, menu_clicked, updated_at) VALUES ('2号', 1, 'yes', '2026-07-02T00:00:00Z'); INSERT INTO sub_areas (id, building, sub_idx, floor, text, x, y) VALUES (2, '2号', 1, 2, '2F A', 30, 40); INSERT INTO pages (id, sub_area_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err) VALUES (2, 2, '2F', 1, 1, 1, '', '', '', 'grid', 'current_quality', ''); INSERT INTO cards (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm) VALUES (2, 2, '2-0201-KT', 'ON', '制冷', '27', '24', '高', 'red.png', '开机');");

        var repository = new SqliteCollectionRunRepository(() => databasePath);
        var comparison = await repository.CompareCurrentAsync(1);

        Assert.Equal(1, comparison.CurrentCardCount);
        Assert.DoesNotContain(comparison.BuildingDifferences, difference => difference.Building == "2号");
    }

    [Fact]
    public async Task MigratesLegacyRunMetadataAndComparesSnapshotToCurrentData()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();
        var runs = await repository.ListAsync();
        var comparison = await repository.CompareCurrentAsync(1);

        Assert.Equal("采集导入", runs[0].Source);
        Assert.Equal("v1.0.0", runs[0].DataVersion);
        Assert.Equal("本机", runs[0].Operator);
        Assert.Equal(1, comparison.SnapshotCardCount);
        Assert.Equal(1, comparison.CurrentCardCount);
        Assert.Equal(0, comparison.AddedCount);
        Assert.Equal(0, comparison.MissingCount);
        Assert.True(comparison.IsRestorable);
        Assert.Contains("source", await ReadColumnsAsync(databasePath, "collection_runs"));
        Assert.Contains("data_version", await ReadColumnsAsync(databasePath, "collection_runs"));
        Assert.Contains("operator_name", await ReadColumnsAsync(databasePath, "collection_runs"));
    }

    [Fact]
    public async Task ListsSnapshotCardCountsByBuilding()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var run = Assert.Single(await repository.ListAsync());

        Assert.Equal(1, run.BuildingCardCounts["1号"]);
        Assert.DoesNotContain("2号", run.BuildingCardCounts.Keys);
    }

    [Fact]
    public async Task ComparesDuplicateNamesByStableLocationOccurrence()
    {
        var databasePath = CreateDatabase();
        await ExecuteAsync(databasePath, """
            UPDATE cards SET indoor = '28' WHERE id = 1;
            INSERT INTO cards (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            VALUES (2, 1, '1-0101-KT', 'OFF', '制冷', '27', '25', '中', 'green.png', '关机');
            INSERT INTO run_cards (id, run_id, run_page_id, source_card_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            VALUES (2, 1, 21, 2, '1-0101-KT', 'OFF', '制冷', '27', '25', '中', 'green.png', '关机');
            """);

        var repository = new SqliteCollectionRunRepository(() => databasePath);
        var comparison = await repository.CompareCurrentAsync(1);

        Assert.Equal(2, comparison.SnapshotCardCount);
        Assert.Equal(2, comparison.CurrentCardCount);
        Assert.Equal(1, comparison.ChangedCount);
    }

    [Fact]
    public async Task DoesNotTreatDuplicateCardOrderAsFieldChanges()
    {
        var databasePath = CreateDatabase();
        await ExecuteAsync(databasePath, """
            UPDATE cards SET indoor = '27' WHERE id = 1;
            INSERT INTO cards (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            VALUES (2, 1, '1-0101-KT', 'OFF', '制冷', '26', '25', '中', 'green.png', '关机');
            INSERT INTO run_cards (id, run_id, run_page_id, source_card_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            VALUES (2, 1, 21, 2, '1-0101-KT', 'OFF', '制冷', '27', '25', '中', 'green.png', '关机');
            """);

        var repository = new SqliteCollectionRunRepository(() => databasePath);
        var comparison = await repository.CompareCurrentAsync(1);

        Assert.Equal(0, comparison.ChangedCount);
    }

    [Fact]
    public async Task ListsAndMarksCollectionRuns()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var runs = await repository.ListAsync();
        var marked = await repository.SetAnomalyAsync(1, true, "采集数据异常，已隔离");
        var cleared = await repository.SetAnomalyAsync(1, false, string.Empty);

        Assert.Single(runs);
        Assert.Equal("run_1", runs[0].RunKey);
        Assert.Equal(["1号"], runs[0].Buildings);
        Assert.True(marked.IsAnomaly);
        Assert.Contains("采集数据异常，已隔离", marked.Note);
        Assert.False(cleared.IsAnomaly);
        Assert.DoesNotContain("采集数据异常，已隔离", cleared.Note);
    }

    [Fact]
    public async Task RestoresCurrentTablesFromRunSnapshot()
    {
        var databasePath = CreateDatabase();
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite"))
        {
            connection.Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE cards SET name = 'BROKEN' WHERE id = 1";
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var result = await repository.RestoreCurrentAsync(1);

        Assert.Equal(1, result.RunId);
        Assert.Equal(1, result.RestoredCards);
        await using var verify = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        verify.Open();
        await using var cardCommand = verify.CreateCommand();
        cardCommand.CommandText = "SELECT name FROM cards LIMIT 1";
        Assert.Equal("1-0101-KT", await cardCommand.ExecuteScalarAsync());
        await using var pageCommand = verify.CreateCommand();
        pageCommand.CommandText = "SELECT quality_reason FROM pages LIMIT 1";
        Assert.Equal("quality_pass", await pageCommand.ExecuteScalarAsync());
        await using var notesCommand = verify.CreateCommand();
        notesCommand.CommandText = "SELECT note FROM device_notes WHERE card_name = '1-0101-KT'";
        Assert.Equal("keep note", await notesCommand.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PartialRestoreReplacesOnlySnapshotBuildingsAndCreatesBackup()
    {
        var databasePath = CreateDatabase();
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite"))
        {
            connection.Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO buildings (building, sub_area_count, menu_clicked, updated_at) VALUES ('2号', 1, 'yes', '2026-07-02T00:00:00Z');
                INSERT INTO sub_areas (id, building, sub_idx, floor, text, x, y) VALUES (2, '2号', 1, 2, '2F A', 30, 40);
                INSERT INTO pages (id, sub_area_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err)
                VALUES (2, 2, '2F', 1, 1, 1, '', '', '', 'grid', 'current_quality', '');
                INSERT INTO cards (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
                VALUES (2, 2, '2-0201-KT', 'ON', '制冷', '27', '24', '高', 'red.png', '开机');
                UPDATE cards SET name = 'STALE-1号' WHERE id = 1;
                INSERT INTO collection_runs (id, run_key, completed_at, imported_at, status, scope, buildings)
                VALUES (3, 'run_3', '2026-07-03T00:00:00Z', '2026-07-03T00:00:00Z', 'completed', 'partial', '["2号"]');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteCollectionRunRepository(() => databasePath);
        var result = await repository.RestoreCurrentAsync(1);

        Assert.True(result.IsPartial);
        Assert.Equal(4L, result.BackupRunId);
        await using var verify = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        verify.Open();
        Assert.Equal(2L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM buildings"));
        Assert.Equal(2L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM cards"));
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM cards WHERE name = '1-0101-KT'"));
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM cards WHERE name = '2-0201-KT'"));
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM collection_runs WHERE status = 'backup' AND note LIKE '恢复批次 #1 前自动备份%'"));
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM collection_runs WHERE status = 'backup' AND source = '手动恢复' AND restored_from_run_id = 1"));
        Assert.Equal(2L, await ScalarLongAsync(verify, $"SELECT COUNT(*) FROM run_cards WHERE run_id = {result.BackupRunId}"));
    }

    [Fact]
    public async Task TechnicalRunIdIsNotReusedAfterDeletingTheHighestBackup()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var firstRestore = await repository.RestoreCurrentAsync(1);
        Assert.Equal(2L, firstRestore.BackupRunId);

        await ExecuteAsync(databasePath, "UPDATE current_data_sources SET state = 'bound', reason = '', run_id = 1, batch_uid = 'batch-current' WHERE building = '1号';");

        await repository.DeleteAsync(firstRestore.BackupRunId!.Value);

        var secondRestore = await repository.RestoreCurrentAsync(1);

        Assert.Equal(3L, secondRestore.BackupRunId);
    }

    [Fact]
    public async Task AnomalyRunCannotBeRestored()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteCollectionRunRepository(() => databasePath);
        await repository.SetAnomalyAsync(1, true, "采集数据异常，已隔离");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RestoreCurrentAsync(1));

        Assert.Contains("异常隔离批次不能恢复", error.Message, StringComparison.Ordinal);
        await using var verify = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        verify.Open();
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM cards"));
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM collection_runs"));
    }

    [Fact]
    public async Task FailedRunCannotBeRestored()
    {
        var databasePath = CreateDatabase();
        await ExecuteAsync(databasePath, "UPDATE collection_runs SET status = 'failed' WHERE id = 1");
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RestoreCurrentAsync(1));

        Assert.Contains("仅允许恢复已完成批次", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QualityBlockedRunCannotBeRestored()
    {
        var databasePath = CreateDatabase();
        await ExecuteAsync(
            databasePath,
            "UPDATE collection_runs SET quality_summary = '{\"summary\":{\"invalid_card_fields\":1}}' WHERE id = 1");
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RestoreCurrentAsync(1));

        Assert.Contains("质量审计", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrokenSnapshotMappingCannotBeRestored()
    {
        var databasePath = CreateDatabase();
        await ExecuteAsync(databasePath, "DELETE FROM run_pages WHERE run_id = 1");
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RestoreCurrentAsync(1));

        Assert.Contains("快照计数不完整", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullRestoreAcceptsDifferentPerBuildingCountsWhenSnapshotIsSelfConsistent()
    {
        var databasePath = CreateDatabase();
        await ExecuteAsync(databasePath, """
            UPDATE collection_runs
            SET scope = 'full', buildings = '["1号","2号","3号","4号","5号","6号"]', card_count = 6
            WHERE id = 1;
            INSERT INTO run_buildings (run_id, building, sub_area_count, menu_clicked, updated_at)
            VALUES (1, '2号', 1, 'yes', '2026-07-01T00:00:00Z'),
                   (1, '3号', 1, 'yes', '2026-07-01T00:00:00Z'),
                   (1, '4号', 1, 'yes', '2026-07-01T00:00:00Z'),
                   (1, '5号', 1, 'yes', '2026-07-01T00:00:00Z'),
                   (1, '6号', 1, 'yes', '2026-07-01T00:00:00Z');
            INSERT INTO run_sub_areas (id, run_id, source_sub_area_id, building, sub_idx, floor, floor_label, text, x, y)
            VALUES (12, 1, 2, '2号', 1, 1, '1F', '2F A', 10, 20),
                   (13, 1, 3, '3号', 1, 1, '1F', '3F A', 10, 20),
                   (14, 1, 4, '4号', 1, 1, '1F', '4F A', 10, 20),
                   (15, 1, 5, '5号', 1, 1, '1F', '5F A', 10, 20),
                   (16, 1, 6, '6号', 1, 1, '1F', '6F A', 10, 20);
            INSERT INTO run_pages (id, run_id, run_sub_area_id, source_page_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err)
            VALUES (22, 1, 12, 2, '1F', 1, 1, 1, '', '', '', 'grid', 'quality_pass', ''),
                   (23, 1, 13, 3, '1F', 1, 1, 1, '', '', '', 'grid', 'quality_pass', ''),
                   (24, 1, 14, 4, '1F', 1, 1, 1, '', '', '', 'grid', 'quality_pass', ''),
                   (25, 1, 15, 5, '1F', 1, 1, 1, '', '', '', 'grid', 'quality_pass', ''),
                   (26, 1, 16, 6, '1F', 1, 1, 1, '', '', '', 'grid', 'quality_pass', '');
            INSERT INTO run_cards (run_id, run_page_id, source_card_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            VALUES (1, 22, 2, '2-0101-KT', 'OFF', '制冷', '26', '25', '中', 'green.png', '关机'),
                   (1, 23, 3, '3-0101-KT', 'OFF', '制冷', '26', '25', '中', 'green.png', '关机'),
                   (1, 24, 4, '4-0101-KT', 'OFF', '制冷', '26', '25', '中', 'green.png', '关机'),
                   (1, 25, 5, '5-0101-KT', 'OFF', '制冷', '26', '25', '中', 'green.png', '关机'),
                   (1, 26, 6, '6-0101-KT', 'OFF', '制冷', '26', '25', '中', 'green.png', '关机');
            """);

        var repository = new SqliteCollectionRunRepository(() => databasePath);
        var result = await repository.RestoreCurrentAsync(1);

        Assert.Equal(6, result.RestoredCards);
    }

    [Fact]
    public async Task DeletesRunHistoryWithoutTouchingCurrentDataOrAnnotations()
    {
        var databasePath = CreateDatabase();
        await ExecuteAsync(databasePath, """
            CREATE TABLE run_realtime_details (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL,
                source_row_id TEXT NOT NULL,
                building TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            INSERT INTO run_realtime_details (run_id, source_row_id, building, payload_json)
            VALUES (1, 'out/realtime_1号_latest.json#0', '1号', '{}');
            """);
        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();
        await ExecuteAsync(databasePath, "UPDATE current_data_sources SET state = 'bound', reason = '', run_id = 999, batch_uid = 'current-external', source_updated_at = '2026-07-01T00:02:00Z' WHERE building = '1号';");
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var deleted = await repository.DeleteAsync(1);
        var runs = await repository.ListAsync();

        Assert.Equal(1, deleted.RunId);
        Assert.Equal("run_1", deleted.RunKey);
        Assert.Equal(1, deleted.DeletedCards);
        Assert.Equal(1, deleted.DeletedPages);
        Assert.Equal(1, deleted.DeletedSubAreas);
        Assert.Equal(1, deleted.DeletedBuildings);
        Assert.Empty(runs);

        await using var verify = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        verify.Open();
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM cards"));
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM pages"));
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM sub_areas"));
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM buildings"));
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM device_notes"));
        Assert.Equal(0L, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM run_realtime_details"));
    }

    [Fact]
    public async Task DeleteIsBlockedWhenBoundCurrentSourceHasIncompleteIdentity()
    {
        var databasePath = CreateDatabase();
        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();
        await ExecuteAsync(databasePath, "UPDATE current_data_sources SET state = 'bound', run_id = NULL, batch_uid = NULL, reason = '' WHERE building = '1号';");
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteAsync(1));

        Assert.Contains("身份未完整", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullRunDeleteIsBlockedWhenCurrentSourcesDoNotCoverAllDeclaredBuildings()
    {
        var databasePath = CreateDatabase();
        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();
        await ExecuteAsync(databasePath, "UPDATE collection_runs SET scope = 'full', buildings = '[\"1号\",\"2号\"]', batch_uid = 'batch-full' WHERE id = 1; DELETE FROM current_data_sources; INSERT INTO current_data_sources (building, revision_uid, run_id, batch_uid, source_updated_at, card_count, state, reason) VALUES ('1号', 'revision-1', 999, 'external-1', '2026-07-01T00:02:00Z', 1, 'bound', '');");
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteAsync(1));

        Assert.Contains("未覆盖", error.Message, StringComparison.Ordinal);
        Assert.Contains("2号", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryIsOrderedByCompletionTimeBeforeImportTime()
    {
        var databasePath = CreateDatabase();
        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();
        await ExecuteAsync(databasePath, "INSERT INTO collection_runs (id, run_key, batch_uid, completed_at, imported_at, status, scope, buildings, card_count) VALUES (2, 'run_2', 'batch-2', '2026-06-30T00:00:00Z', '2026-07-02T00:00:00Z', 'completed', 'partial', '[\"1号\"]', 0);");
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var runs = await repository.ListAsync();

        Assert.Equal([1L, 2L], runs.Select(run => run.Id).ToArray());
    }

    [Fact]
    public async Task DeletesRunArtifactsInsideTheDatabaseDirectory()
    {
        var databasePath = CreateDatabase();
        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();
        var directory = Path.GetDirectoryName(databasePath)!;
        var jsonPath = Path.Combine(directory, "enum_run1.json");
        var snapshotPath = Path.Combine(directory, "snapshot_run1.db");
        var reportPath = Path.Combine(directory, "quality_report_run1.json");
        var textPath = Path.Combine(directory, "quality_report_run1.txt");
        var knownRun = Assert.Single(await new SqliteCollectionRunRepository(() => databasePath).ListAsync());
        File.WriteAllText(jsonPath, $"{{\"runId\":1,\"runKey\":\"{knownRun.RunKey}\",\"batchUid\":\"{knownRun.BatchUid}\"}}");
        File.WriteAllText(textPath, "artifact");
        File.WriteAllText(reportPath, $"{{\"run_id\":1,\"run_key\":\"{knownRun.RunKey}\",\"batch_uid\":\"{knownRun.BatchUid}\"}}");
        await using (var snapshot = new SqliteConnection($"Data Source={snapshotPath}"))
        {
            await snapshot.OpenAsync();
            await using var snapshotCommand = snapshot.CreateCommand();
            snapshotCommand.CommandText = $"CREATE TABLE collection_runs (id INTEGER PRIMARY KEY, run_key TEXT, batch_uid TEXT); INSERT INTO collection_runs VALUES (1, '{knownRun.RunKey}', '{knownRun.BatchUid}');";
            await snapshotCommand.ExecuteNonQueryAsync();
        }

        await ExecuteAsync(databasePath, "UPDATE collection_runs SET json_path = 'enum_run1.json', db_snapshot_path = 'snapshot_run1.db' WHERE id = 1");
        await ExecuteAsync(databasePath, "UPDATE current_data_sources SET state = 'bound', reason = '', run_id = 999, batch_uid = 'current-external', source_updated_at = '2026-07-01T00:02:00Z' WHERE building = '1号';");
        var repository = new SqliteCollectionRunRepository(() => databasePath);
        var impact = await repository.GetDeleteImpactAsync(1);
        var snapshotCandidate = Assert.Single(impact.Artifacts, item => item.RelativePath == "snapshot_run1.db");
        Assert.True(snapshotCandidate.IdentityVerified);

        var deleteResult = await repository.DeleteAsync(1);

        Assert.All(
            new[] { jsonPath, snapshotPath, reportPath, textPath },
            path => Assert.False(
                File.Exists(path),
                $"{path}; pending={string.Join(",", deleteResult.ArtifactCleanup!.PendingPaths)}; reasons={string.Join(",", deleteResult.ArtifactCleanup.PendingReasons)}"));
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value);
    }

    private static async Task<IReadOnlyList<string>> ReadColumnsAsync(string databasePath, string tableName)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<IReadOnlyList<string>> ReadTablesAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-run-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ac.db");
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE buildings (building TEXT PRIMARY KEY, sub_area_count INT, menu_clicked TEXT, updated_at TEXT);
            CREATE TABLE sub_areas (id INTEGER PRIMARY KEY AUTOINCREMENT, building TEXT NOT NULL, sub_idx INT, floor REAL, text TEXT, x INT, y INT);
            CREATE TABLE pages (id INTEGER PRIMARY KEY AUTOINCREMENT, sub_area_id INT NOT NULL, page_name TEXT, count INT, raw_count INT, unique_count INT, duplicate_names TEXT, on_href TEXT, off_href TEXT, layout TEXT, quality_reason TEXT, err TEXT);
            CREATE TABLE cards (id INTEGER PRIMARY KEY AUTOINCREMENT, page_id INT NOT NULL, name TEXT, switch TEXT, mode TEXT, indoor TEXT, set_temp TEXT, fan TEXT, indicator TEXT, comm TEXT);
            CREATE TABLE collection_runs (id INTEGER PRIMARY KEY AUTOINCREMENT, run_key TEXT UNIQUE, started_at TEXT, completed_at TEXT NOT NULL, imported_at TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'completed', scope TEXT NOT NULL DEFAULT 'full', buildings TEXT NOT NULL DEFAULT '[]', json_path TEXT, db_snapshot_path TEXT, card_count INTEGER NOT NULL DEFAULT 0, on_count INTEGER NOT NULL DEFAULT 0, off_count INTEGER NOT NULL DEFAULT 0, offline_count INTEGER NOT NULL DEFAULT 0, unknown_count INTEGER NOT NULL DEFAULT 0, quality_summary TEXT NOT NULL DEFAULT '{}', is_anomaly INTEGER NOT NULL DEFAULT 0, note TEXT NOT NULL DEFAULT '');
            CREATE TABLE run_buildings (id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INTEGER NOT NULL, building TEXT NOT NULL, sub_area_count INTEGER, menu_clicked TEXT, updated_at TEXT);
            CREATE TABLE run_sub_areas (id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INTEGER NOT NULL, source_sub_area_id INTEGER, building TEXT NOT NULL, sub_idx INTEGER, floor REAL, floor_label TEXT, text TEXT, x INTEGER, y INTEGER);
            CREATE TABLE run_pages (id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INTEGER NOT NULL, run_sub_area_id INTEGER NOT NULL, source_page_id INTEGER, page_name TEXT, count INTEGER, raw_count INTEGER, unique_count INTEGER, duplicate_names TEXT, on_href TEXT, off_href TEXT, layout TEXT, quality_reason TEXT, err TEXT);
            CREATE TABLE run_cards (id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INTEGER NOT NULL, run_page_id INTEGER NOT NULL, source_card_id INTEGER, name TEXT, switch TEXT, mode TEXT, indoor TEXT, set_temp TEXT, fan TEXT, indicator TEXT, comm TEXT);
            CREATE TABLE device_notes (id INTEGER PRIMARY KEY AUTOINCREMENT, card_name TEXT NOT NULL, building TEXT, note TEXT NOT NULL, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP, UNIQUE(card_name, building));
            INSERT INTO buildings (building, sub_area_count, menu_clicked, updated_at) VALUES ('1号', 1, 'yes', '2026-07-01T00:00:00Z');
            INSERT INTO sub_areas (id, building, sub_idx, floor, text, x, y) VALUES (1, '1号', 1, 1, '1F A', 10, 20);
            INSERT INTO pages (id, sub_area_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err) VALUES (1, 1, '1F', 1, 1, 1, '', '', '', 'grid', 'stale_before_restore', '');
            INSERT INTO cards (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm) VALUES (1, 1, '1-0101-KT', 'OFF', '制冷', '26', '25', '中', 'green.png', '关机');
            INSERT INTO collection_runs (id, run_key, completed_at, imported_at, scope, buildings, card_count, off_count) VALUES (1, 'run_1', '2026-07-01T00:00:00Z', '2026-07-01T00:01:00Z', 'partial', '["1号"]', 1, 1);
            INSERT INTO run_buildings (run_id, building, sub_area_count, menu_clicked, updated_at) VALUES (1, '1号', 1, 'yes', '2026-07-01T00:00:00Z');
            INSERT INTO run_sub_areas (id, run_id, source_sub_area_id, building, sub_idx, floor, floor_label, text, x, y) VALUES (11, 1, 1, '1号', 1, 1, '1F', '1F A', 10, 20);
            INSERT INTO run_pages (id, run_id, run_sub_area_id, source_page_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err) VALUES (21, 1, 11, 1, '1F', 1, 1, 1, '', '', '', 'grid', 'quality_pass', '');
            INSERT INTO run_cards (run_id, run_page_id, source_card_id, name, switch, mode, indoor, set_temp, fan, indicator, comm) VALUES (1, 21, 1, '1-0101-KT', 'OFF', '制冷', '26', '25', '中', 'green.png', '关机');
            INSERT INTO device_notes (card_name, building, note) VALUES ('1-0101-KT', '1号', 'keep note');
            """;
        command.ExecuteNonQuery();
        return path;
    }
}
