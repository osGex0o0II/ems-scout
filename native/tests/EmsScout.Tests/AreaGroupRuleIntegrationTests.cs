using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class AreaGroupRuleIntegrationTests
{
    [Fact]
    public async Task DeviceSearchUsesOnlyEnabledAreaRulesAndShowsUnmatchedDash()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var group = await groups.SaveGroupAsync(new AreaGroupEdit(
            null, "公共设备", string.Empty, "规则来源", "重点", true, "public-devices"));
        await groups.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "1号", "-", "1F", "include", "GQ | WSJ", string.Empty));

        var repository = new SqliteDeviceReadRepository(databasePath);
        var filtered = await repository.SearchAsync(new DeviceQuery(
            MonitorGroupIds: group.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Limit: 100));
        var all = await repository.SearchAsync(new DeviceQuery(Limit: 100));

        var matched = Assert.Single(filtered.Rows);
        Assert.Equal("公共设备", matched.AreaGroupText);
        Assert.Equal(2, all.Total);
        Assert.Contains(all.Rows, row => row.AreaGroupText == "-");

        await groups.SaveGroupAsync(new AreaGroupEdit(
            group.Id, group.Name, string.Empty, group.Description, group.Priority, false, group.GroupKey));
        var disabled = await repository.SearchAsync(new DeviceQuery(
            MonitorGroupIds: group.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Limit: 100));
        Assert.Equal(0, disabled.Total);
    }

    [Fact]
    public async Task FilterOptionsExposeUnmatchedDevices()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var group = await groups.SaveGroupAsync(new AreaGroupEdit(
            null, "一号楼设备", string.Empty, string.Empty, "重点", true, "building-one"));
        await groups.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "1号", "-", "1F", "include", "GQ", string.Empty));

        var options = await new SqliteDeviceReadRepository(databasePath).LoadFilterOptionsAsync();

        Assert.Equal(1, options.UnmatchedCount);
    }

    [Fact]
    public async Task EmptyKeywordRulePersistsAsAWholeScopeRule()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var group = await groups.SaveGroupAsync(new AreaGroupEdit(
            null, "整栋楼", string.Empty, string.Empty, "重点", true, "whole-building"));

        await groups.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "1号", "-", "-", "-", string.Empty, string.Empty));

        var loaded = await groups.LoadAsync();
        var rule = Assert.Single(loaded.RuleRecords, item => item.GroupId == group.Id);
        Assert.Empty(rule.Keywords);
        Assert.True(rule.IsInclude);
        Assert.Equal(string.Empty, rule.FloorLabel);
    }

    [Fact]
    public async Task GroupStatsIgnoreAStaleLegacyMemberTableWithoutRules()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var group = await groups.SaveGroupAsync(new AreaGroupEdit(
            null, "规则唯一来源", string.Empty, string.Empty, "重点", true, "rules-only"));

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE monitor_group_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    group_id INTEGER NOT NULL,
                    target_type TEXT NOT NULL,
                    building TEXT NOT NULL,
                    floor_label TEXT,
                    floor_value REAL,
                    sub_area_text TEXT,
                    card_name TEXT,
                    note TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO monitor_group_items(group_id, target_type, building, floor_label, floor_value)
                VALUES ($group_id, 'floor', '1号', '1F', 1);
                """;
            command.Parameters.AddWithValue("$group_id", group.Id);
            command.ExecuteNonQuery();
        }

        var loaded = await groups.LoadAsync();
        var summary = Assert.Single(loaded.Groups, item => item.Id == group.Id);

        Assert.DoesNotContain(loaded.RuleRecords, rule => rule.GroupId == group.Id);
        Assert.Equal(0, summary.ItemCount);
        Assert.Equal(0, summary.Total);
    }

    [Fact]
    public async Task LegacyMembersCannotDivergeOverviewFromDataManagement()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var group = await groups.SaveGroupAsync(new AreaGroupEdit(
            null, "规则唯一来源", string.Empty, string.Empty, "重点", true, "rules-only"));

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE monitor_group_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    group_id INTEGER NOT NULL,
                    target_type TEXT NOT NULL,
                    building TEXT NOT NULL,
                    floor_label TEXT,
                    floor_value REAL,
                    sub_area_text TEXT,
                    card_name TEXT,
                    note TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE legacy_area_api_state (id INTEGER PRIMARY KEY CHECK (id = 1), enabled_at TEXT NOT NULL);
                INSERT INTO monitor_group_items(group_id, target_type, building, floor_label, floor_value)
                VALUES ($group_id, 'floor', '1号', '1F', 1);
                INSERT INTO legacy_area_api_state(id, enabled_at) VALUES (1, CURRENT_TIMESTAMP);
                """;
            command.Parameters.AddWithValue("$group_id", group.Id);
            command.ExecuteNonQuery();
        }

        var loaded = await groups.LoadAsync();
        var summary = Assert.Single(loaded.Groups, item => item.Id == group.Id);
        var devices = new SqliteDeviceReadRepository(databasePath);
        var all = await devices.SearchAsync(new DeviceQuery(Limit: 100));
        var selected = await devices.SearchAsync(new DeviceQuery(
            MonitorGroupIds: group.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Limit: 100));
        var dashboard = EmsScout.Application.DashboardAreaGroupBuilder.Build(all.Rows, loaded)
            .Single(item => item.Id == group.Id);

        Assert.Equal(0, summary.ItemCount);
        Assert.Equal(0, summary.Total);
        Assert.All(all.Rows, row => Assert.Equal("-", row.AreaGroupText));
        Assert.Equal(0, selected.Total);
        Assert.Equal(0, dashboard.MemberCount);
        Assert.Equal(0, dashboard.Total);
    }

    [Fact]
    public async Task LegacyMemberApiCannotCreateLegacyStorage()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var group = await groups.SaveGroupAsync(new AreaGroupEdit(
            null, "规则唯一来源", string.Empty, string.Empty, "重点", true, "rules-only"));

        await Assert.ThrowsAsync<NotSupportedException>(() => groups.SaveItemAsync(new AreaGroupItemEdit(
            group.Id, "floor", "1号", "1F", string.Empty, string.Empty, "旧成员")));

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'monitor_group_items'";
        Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string CreateDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-area-rule-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ac.db");
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE buildings (
                building TEXT PRIMARY KEY,
                sub_area_count INTEGER NOT NULL DEFAULT 0,
                menu_clicked TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE sub_areas (
                id INTEGER PRIMARY KEY,
                building TEXT NOT NULL,
                floor REAL,
                text TEXT NOT NULL,
                sub_idx INTEGER NOT NULL DEFAULT 0,
                x REAL,
                y REAL
            );
            CREATE TABLE pages (
                id INTEGER PRIMARY KEY,
                sub_area_id INTEGER NOT NULL,
                page_name TEXT NOT NULL,
                layout TEXT NOT NULL DEFAULT '',
                collected_at TEXT
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
            INSERT INTO buildings(building) VALUES ('1号');
            INSERT INTO sub_areas(id, building, floor, text, sub_idx, x, y) VALUES
                (1, '1号', 1, '1F A', 1, 100, 100),
                (2, '1号', 1, '1F B', 2, 100, 200);
            INSERT INTO pages(id, sub_area_id, page_name, layout) VALUES
                (1, 1, '1F', 'grid'),
                (2, 2, '1F', 'grid');
            INSERT INTO cards(id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm) VALUES
                (1, 1, 'GQ-0101-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机'),
                (2, 2, 'ROOM-0102-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机');
            """;
        command.ExecuteNonQuery();
        return path;
    }
}
