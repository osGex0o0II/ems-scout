using EmsScout.Infrastructure.Migrations;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

internal static class CollectionImportDatabaseFixture
{
    public static async Task<string> CreateMigratedAsync(
        string directory,
        params (string Building, string DeviceName, string PageName, string Comm)[] devices)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ac.db");
        await using (var connection = Open(path, create: true))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, """
                CREATE TABLE buildings (
                    building TEXT PRIMARY KEY,
                    sub_area_count INT,
                    menu_clicked TEXT,
                    updated_at TEXT
                );
                CREATE TABLE sub_areas (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    building TEXT NOT NULL,
                    sub_idx INT,
                    floor REAL,
                    text TEXT,
                    x INT,
                    y INT
                );
                CREATE TABLE pages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    sub_area_id INT NOT NULL,
                    page_name TEXT,
                    count INT,
                    raw_count INT,
                    unique_count INT,
                    duplicate_names TEXT,
                    on_href TEXT,
                    off_href TEXT,
                    layout TEXT,
                    quality_reason TEXT,
                    err TEXT
                );
                CREATE TABLE cards (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    page_id INT NOT NULL,
                    name TEXT,
                    switch TEXT,
                    mode TEXT,
                    indoor TEXT,
                    set_temp TEXT,
                    fan TEXT,
                    indicator TEXT,
                    comm TEXT
                );
                """);
            foreach (var device in devices)
            {
                await ExecuteAsync(connection, """
                    INSERT OR IGNORE INTO buildings (building, sub_area_count, menu_clicked, updated_at)
                    VALUES ($building, 1, $building, '2026-07-10T00:00:00Z');
                    """, ("$building", device.Building));
                var subAreaId = await InsertIdAsync(connection, """
                    INSERT INTO sub_areas (building, sub_idx, floor, text, x, y)
                    VALUES ($building, 0, 1, 'fixture-area', 10, 20)
                    """, ("$building", device.Building));
                var pageId = await InsertIdAsync(connection, """
                    INSERT INTO pages
                        (sub_area_id, page_name, count, raw_count, unique_count, duplicate_names,
                         on_href, off_href, layout, quality_reason, err)
                    VALUES ($sub_area_id, $page, 1, 1, 1, '[]', NULL, NULL, 'grid', 'quality_pass', NULL)
                    """, ("$sub_area_id", subAreaId), ("$page", device.PageName));
                await ExecuteAsync(connection, """
                    INSERT INTO cards
                        (page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
                    VALUES ($page_id, $name, 'OFF', '制冷', '26.5', '26', '自动', 'indicator.png', $comm)
                    """, ("$page_id", pageId), ("$name", device.DeviceName), ("$comm", device.Comm));
            }
        }

        await new SqliteSchemaMigrator().MigrateAsync(path);
        return path;
    }

    public static SqliteConnection Open(string path, bool create = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        return new(builder.ToString());
    }

    public static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public static async Task<string?> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static async Task<long> InsertIdAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql + "; SELECT last_insert_rowid();";
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
