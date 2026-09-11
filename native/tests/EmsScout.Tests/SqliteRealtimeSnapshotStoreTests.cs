using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class SqliteRealtimeSnapshotStoreTests
{
    [Fact]
    public async Task PersistsAndLoadsRealtimeDetailsForAHistoricalRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-realtime-snapshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "ac.db");
        var outputDirectory = Path.Combine(root, "out");
        Directory.CreateDirectory(outputDirectory);
        await CreateDatabaseAsync(databasePath);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "realtime_1号_latest.json"),
            """
            {
              "runId": 1,
              "capturedAt": "2026-08-31T03:00:00Z",
              "rows": [
                {
                  "building": "1号",
                  "floor": 1,
                  "subAreaText": "1F",
                  "pageName": "default",
                  "name": "1-0101-KT",
                  "devId": "dev-1",
                  "meterId": "meter-1",
                  "rtuId": "rtu-1",
                  "fieldCount": 54,
                  "realtimeTagCount": 54,
                  "realtimeValidTagCount": 54,
                  "defaultLike": false,
                  "error": "",
                  "cardComm": "开机",
                  "cardSwitch": "ON",
                  "cardIndicator": "red.png",
                  "fields": { "集控锁定": "开启", "当前开关机状态": "开机" },
                  "validFields": { "集控锁定": true }
                }
              ]
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "realtime_2号_latest.json"),
            """
            {
              "runId": 1,
              "capturedAt": "2026-08-31T03:00:00Z",
              "rows": [
                {
                  "building": "2号",
                  "floor": 1,
                  "subAreaText": "1F",
                  "pageName": "default",
                  "name": "2-0101-KT",
                  "fields": { "集控锁定": "关闭" },
                  "validFields": { "集控锁定": true }
                }
              ]
            }
            """);

        var store = new SqliteRealtimeSnapshotStore(() => databasePath);
        await store.SaveAsync(1, outputDirectory, ["1号"]);
        await store.SaveAsync(1, outputDirectory, ["2号"]);

        var snapshot = await store.LoadAsync(1, ["1号", "2号"]);

        Assert.Equal(2, snapshot.Rows.Count);
        var row = Assert.Single(snapshot.Rows, item => item.Building == "1号");
        Assert.Equal("开启", row.LockState);
        Assert.True(row.LockStateValid);
        Assert.Contains(snapshot.Rows, item => item.Building == "2号");
    }

    [Fact]
    public async Task LoadingMissingSnapshotDoesNotCreateRealtimeTable()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-realtime-snapshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "ac.db");
        await CreateDatabaseAsync(databasePath);

        var store = new SqliteRealtimeSnapshotStore(() => databasePath);
        var snapshot = await store.LoadAsync(1, ["1号"]);

        Assert.Empty(snapshot.Rows);
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'run_realtime_details'";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    private static async Task CreateDatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE collection_runs (id INTEGER PRIMARY KEY, completed_at TEXT);
            INSERT INTO collection_runs (id, completed_at) VALUES (1, '2026-08-31T03:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
