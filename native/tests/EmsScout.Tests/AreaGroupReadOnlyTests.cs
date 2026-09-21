using EmsScout.Application.Groups;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class AreaGroupReadOnlyTests
{
    [Fact]
    public async Task LoadFloorsAsyncDoesNotWriteCatalogAndProjectsDiscoveredFloors()
    {
        var databasePath = CreateDatabase("""
            CREATE TABLE floor_catalog (
                id INTEGER PRIMARY KEY,
                building TEXT NOT NULL,
                floor_label TEXT NOT NULL,
                floor_value REAL NOT NULL,
                source TEXT NOT NULL,
                enabled INTEGER NOT NULL,
                note TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE sub_areas (building TEXT NOT NULL, floor REAL);
            INSERT INTO floor_catalog(id, building, floor_label, floor_value, source, enabled, note, updated_at)
            VALUES (7, '1号', '1F', 1, 'manual', 1, '保留人工目录', '2026-01-01T00:00:00+08:00');
            INSERT INTO sub_areas(building, floor) VALUES ('1号', 1), ('1号', 2);
            """);

        var before = ReadCatalogSnapshot(databasePath);
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var floors = await repository.LoadFloorsAsync("1号");

        var after = ReadCatalogSnapshot(databasePath);
        Assert.Equal(before, after);
        Assert.Equal(["1F", "2F"], floors.Select(floor => floor.FloorLabel).ToArray());
        Assert.Equal(7, floors[0].Id);
        Assert.Equal(0, floors[1].Id);
        Assert.Equal("discovered", floors[1].Source);
    }

    [Fact]
    public async Task LoadConfigurationAsyncReadsGroupsAndRulesWithoutCardsTable()
    {
        var databasePath = CreateDatabase("""
            CREATE TABLE monitor_groups (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                area_label TEXT NOT NULL,
                description TEXT NOT NULL,
                priority TEXT NOT NULL,
                enabled INTEGER NOT NULL,
                group_key TEXT NOT NULL
            );
            CREATE TABLE area_group_rules (
                id INTEGER PRIMARY KEY,
                group_id INTEGER NOT NULL,
                rule_order INTEGER NOT NULL,
                building TEXT NOT NULL,
                zuo TEXT NOT NULL,
                floor_label TEXT NOT NULL,
                floor_value REAL,
                match_mode TEXT NOT NULL,
                keywords TEXT NOT NULL,
                note TEXT NOT NULL
            );
            INSERT INTO monitor_groups(id, name, area_label, description, priority, enabled, group_key)
            VALUES (3, '公共区域', '一号楼', '只读配置', '重点', 1, 'public-area');
            INSERT INTO area_group_rules(id, group_id, rule_order, building, zuo, floor_label, floor_value, match_mode, keywords, note)
            VALUES (9, 3, 1, '1号', '-', '1F', 1, 'include', 'GQ', '规则');
            """);

        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var before = ReadDataVersion(databasePath);
        var configuration = await repository.LoadConfigurationAsync();
        var after = ReadDataVersion(databasePath);

        Assert.Equal(before, after);
        var group = Assert.Single(configuration.Groups);
        Assert.Equal("公共区域", group.Name);
        Assert.Equal(0, group.Total);
        Assert.Equal(0, group.CoveredAreas);
        var rule = Assert.Single(configuration.RuleRecords);
        Assert.Equal(9, rule.Id);
        Assert.Equal("GQ", rule.KeywordText);
    }

    [Fact]
    public async Task LoadReadOnlyAsyncPreservesStatisticsWithoutWritingDatabase()
    {
        var databasePath = CreateDatabase("""
            CREATE TABLE monitor_groups (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                area_label TEXT NOT NULL,
                description TEXT NOT NULL,
                priority TEXT NOT NULL,
                enabled INTEGER NOT NULL,
                group_key TEXT NOT NULL
            );
            CREATE TABLE area_group_rules (
                id INTEGER PRIMARY KEY,
                group_id INTEGER NOT NULL,
                rule_order INTEGER NOT NULL,
                building TEXT NOT NULL,
                zuo TEXT NOT NULL,
                floor_label TEXT NOT NULL,
                floor_value REAL,
                match_mode TEXT NOT NULL,
                keywords TEXT NOT NULL,
                note TEXT NOT NULL
            );
            CREATE TABLE sub_areas (
                id INTEGER PRIMARY KEY,
                building TEXT NOT NULL,
                floor REAL,
                text TEXT NOT NULL,
                sub_idx INTEGER NOT NULL,
                x REAL,
                y REAL
            );
            CREATE TABLE pages (
                id INTEGER PRIMARY KEY,
                sub_area_id INTEGER NOT NULL,
                page_name TEXT NOT NULL,
                layout TEXT NOT NULL
            );
            CREATE TABLE cards (
                id INTEGER PRIMARY KEY,
                page_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                switch TEXT,
                mode TEXT,
                indoor TEXT,
                set_temp TEXT,
                fan TEXT,
                indicator TEXT,
                comm TEXT
            );
            INSERT INTO monitor_groups(id, name, area_label, description, priority, enabled, group_key)
            VALUES (3, '公共区域', '一号楼', '带统计只读', '重点', 1, 'public-area');
            INSERT INTO area_group_rules(id, group_id, rule_order, building, zuo, floor_label, floor_value, match_mode, keywords, note)
            VALUES (9, 3, 1, '1号', '-', '1F', 1, 'include', 'GQ', '规则');
            INSERT INTO sub_areas(id, building, floor, text, sub_idx, x, y)
            VALUES (11, '1号', 1, '1F 公区', 1, 100, 100);
            INSERT INTO pages(id, sub_area_id, page_name, layout)
            VALUES (21, 11, '1F', 'grid');
            INSERT INTO cards(id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            VALUES (31, 21, 'GQ-0101-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机');
            """);
        using var observer = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        observer.Open();
        var before = ReadDataVersion(observer);
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var loaded = await repository.LoadReadOnlyAsync();

        var after = ReadDataVersion(observer);
        Assert.Equal(before, after);
        var group = Assert.Single(loaded.Groups);
        Assert.Equal(1, group.ItemCount);
        Assert.Equal(1, group.Total);
        Assert.Equal(1, group.OnCount);
        Assert.Equal(1, group.CoveredAreas);
        Assert.Single(loaded.RuleRecords);
    }

    [Fact]
    public async Task LoadReadOnlyAsyncReturnsEmptyWhenAreaTablesAreAbsent()
    {
        var databasePath = CreateDatabase("CREATE TABLE cards (id INTEGER PRIMARY KEY);");
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var loaded = await repository.LoadReadOnlyAsync();

        Assert.Empty(loaded.Groups);
        Assert.Empty(loaded.RuleRecords);
    }

    private static (string UpdatedAt, int Count) ReadCatalogSnapshot(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT updated_at, COUNT(*) FROM floor_catalog GROUP BY updated_at ORDER BY updated_at LIMIT 1";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetInt32(1));
    }

    private static long ReadDataVersion(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        return ReadDataVersion(connection);
    }

    private static long ReadDataVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string CreateDatabase(string schema)
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-area-read-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ac.db");
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = schema;
        command.ExecuteNonQuery();
        return path;
    }
}
