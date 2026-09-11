using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class CollectionRunRepositoryTests
{
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
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteCollectionRunRepository(() => databasePath);
        var result = await repository.RestoreCurrentAsync(1);

        Assert.True(result.IsPartial);
        Assert.NotNull(result.BackupRunId);
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
    public async Task BrokenSnapshotMappingCannotBeRestored()
    {
        var databasePath = CreateDatabase();
        await ExecuteAsync(databasePath, "DELETE FROM run_pages WHERE run_id = 1");
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RestoreCurrentAsync(1));

        Assert.Contains("快照计数不完整", error.Message, StringComparison.Ordinal);
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
    public async Task DeletesRunArtifactsInsideTheDatabaseDirectory()
    {
        var databasePath = CreateDatabase();
        var directory = Path.GetDirectoryName(databasePath)!;
        var jsonPath = Path.Combine(directory, "enum_run1.json");
        var snapshotPath = Path.Combine(directory, "snapshot_run1.db");
        var reportPath = Path.Combine(directory, "quality_report_run1.json");
        var textPath = Path.Combine(directory, "quality_report_run1.txt");
        foreach (var path in new[] { jsonPath, snapshotPath, reportPath, textPath })
        {
            File.WriteAllText(path, "artifact");
        }

        await ExecuteAsync(databasePath, "UPDATE collection_runs SET json_path = 'enum_run1.json', db_snapshot_path = 'snapshot_run1.db' WHERE id = 1");
        var repository = new SqliteCollectionRunRepository(() => databasePath);

        await repository.DeleteAsync(1);

        Assert.All(new[] { jsonPath, snapshotPath, reportPath, textPath }, path => Assert.False(File.Exists(path), path));
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
