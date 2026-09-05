using EmsScout.Application.Devices;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class DeviceAnnotationServiceTests
{
    [Fact]
    public async Task SavesNotesAndTags()
    {
        var databasePath = CreateDatabase();
        var service = new SqliteDeviceAnnotationService(() => databasePath);
        var key = new DeviceAnnotationKey("1号", "1-0101-KT");

        await service.SaveNoteAsync(key, "重点复核");
        await service.AddTagAsync(key, "巡检");
        await service.DeleteTagAsync(key, "巡检");

        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        Assert.Equal("重点复核", Scalar(connection, "SELECT note FROM device_notes WHERE building = '1号' AND card_name = '1-0101-KT'"));
        Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM device_tags"));
    }

    [Fact]
    public async Task SavesAndClearsRealtimeOverride()
    {
        var databasePath = CreateDatabase();
        var service = new SqliteDeviceAnnotationService(() => databasePath);
        var edit = new RealtimeOverrideEdit(
            Building: "1号",
            DevId: "dev-1",
            FloorLabel: "1F",
            SubArea: "1F A",
            PageName: "default",
            RealtimeName: "1-0101-KT",
            Action: "classify_only",
            ZuoOverride: "A",
            AreaTypeOverride: "公区",
            Note: "人工确认");

        var saved = await service.SaveRealtimeOverrideAsync(edit);
        var cleared = await service.SaveRealtimeOverrideAsync(edit with
        {
            ZuoOverride = string.Empty,
            AreaTypeOverride = string.Empty,
            Note = string.Empty,
        });

        Assert.NotNull(saved);
        Assert.Equal("A座", saved.ZuoOverride);
        Assert.Equal("公区", saved.AreaTypeOverride);
        Assert.Null(cleared);
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM realtime_match_overrides"));
    }

    private static string CreateDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-annotation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ac.db");
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE device_tags (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                card_name TEXT NOT NULL,
                building TEXT,
                tag TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(card_name, building, tag)
            );
            CREATE TABLE device_notes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                card_name TEXT NOT NULL,
                building TEXT,
                note TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(card_name, building)
            );
            CREATE TABLE realtime_match_overrides (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                building TEXT NOT NULL,
                dev_id TEXT,
                floor_label TEXT,
                sub_area TEXT,
                page_name TEXT,
                realtime_name TEXT NOT NULL,
                action TEXT NOT NULL DEFAULT 'classify_only',
                target_card_id INTEGER,
                zuo_override TEXT,
                area_type_override TEXT,
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """;
        command.ExecuteNonQuery();
        return path;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        return Convert.ToInt64(Scalar(connection, sql), System.Globalization.CultureInfo.InvariantCulture);
    }
}
