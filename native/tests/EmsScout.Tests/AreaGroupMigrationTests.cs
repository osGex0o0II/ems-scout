using EmsScout.Application.Groups;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class AreaGroupMigrationTests
{
    [Fact]
    public async Task ClearsLegacyGroupsOnceWithoutTouchingDeviceTables()
    {
        var databasePath = CreateLegacyDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var first = await repository.LoadAsync();

        Assert.Empty(first.Groups);
        Assert.Empty(first.Items);
        Assert.Empty(first.RuleRecords);
        Assert.Equal(1, Scalar(databasePath, "SELECT COUNT(*) FROM cards"));

        var saved = await repository.SaveGroupAsync(new AreaGroupEdit(
            Id: null,
            Name: "研发区",
            AreaLabel: string.Empty,
            Description: "测试",
            Priority: "重点",
            Enabled: true,
            GroupKey: "dev-zone"));
        var second = await repository.LoadAsync();

        Assert.Single(second.Groups, group => group.Id == saved.Id && group.GroupKey == "dev-zone");
        Assert.Equal(1, Scalar(databasePath, "SELECT COUNT(*) FROM cards"));
        Assert.Equal(1, Scalar(databasePath, "SELECT COUNT(*) FROM ems_schema_migrations WHERE name = 'area-groups-rules-v1'"));
    }

    [Fact]
    public async Task PersistsOrderedRulesAndCountsRulesInGroupSummary()
    {
        var databasePath = CreateLegacyDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var group = await repository.SaveGroupAsync(new AreaGroupEdit(
            Id: null,
            Name: "公共区域设备",
            AreaLabel: string.Empty,
            Description: "备注",
            Priority: "重点",
            Enabled: true,
            GroupKey: "public-area"));

        var first = await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "1号", "-", "", "include", "GQ | WSJ", "一号楼"));
        var second = await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "5号", "C座", "BM", "exclude", "TEMP", "排除临时"));
        var set = await repository.LoadAsync();

        var rules = set.RuleRecords.Where(rule => rule.GroupId == group.Id).ToArray();
        Assert.Equal([first.Id, second.Id], rules.Select(rule => rule.Id).ToArray());
        Assert.Equal([1, 2], rules.Select(rule => rule.RuleOrder).ToArray());
        Assert.Equal(["GQ", "WSJ"], rules[0].Keywords);
        Assert.Equal("BM", rules[1].FloorLabel);
        Assert.Equal(2, Assert.Single(set.Groups, item => item.Id == group.Id).ItemCount);
    }

    [Fact]
    public async Task DeleteRuleRenumbersRemainingRulesAndDisableKeepsGroup()
    {
        var databasePath = CreateLegacyDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var group = await repository.SaveGroupAsync(new AreaGroupEdit(
            null, "待停用", "", "", "重点", true, "disabled-check"));
        var first = await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "1号", "-", "1F", "include", "GQ", ""));
        var second = await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "2号", "-", "2F", "include", "WSJ", ""));

        await repository.DeleteRuleAsync(first.Id);
        await repository.SaveGroupAsync(new AreaGroupEdit(
            group.Id, group.Name, "", "", "重点", false, group.GroupKey));
        var set = await repository.LoadAsync();

        var remaining = Assert.Single(set.RuleRecords, rule => rule.GroupId == group.Id);
        Assert.Equal(second.Id, remaining.Id);
        Assert.Equal(1, remaining.RuleOrder);
        Assert.False(Assert.Single(set.Groups, item => item.Id == group.Id).Enabled);
    }

    [Fact]
    public async Task NormalizesExistingGappedRuleOrdersOnStartup()
    {
        var databasePath = CreateLegacyDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var group = await repository.SaveGroupAsync(new AreaGroupEdit(
            null, "断号规则", "", "", "重点", true, "gapped-orders"));
        var first = await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "1号", "-", "1F", "include", "A", ""));
        var second = await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "1号", "-", "2F", "include", "B", ""));
        var third = await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "1号", "-", "3F", "include", "C", ""));

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE area_group_rules SET rule_order = CASE id
                    WHEN $first THEN 0
                    WHEN $second THEN 2
                    WHEN $third THEN 4
                END WHERE group_id = $group_id;
                DELETE FROM ems_schema_migrations WHERE name = 'area-group-rule-order-v2';
                """;
            command.Parameters.AddWithValue("$first", first.Id);
            command.Parameters.AddWithValue("$second", second.Id);
            command.Parameters.AddWithValue("$third", third.Id);
            command.Parameters.AddWithValue("$group_id", group.Id);
            command.ExecuteNonQuery();
        }

        var reloaded = await new SqliteAreaGroupRepository(() => databasePath).LoadAsync();
        var rules = reloaded.RuleRecords.Where(rule => rule.GroupId == group.Id).ToArray();

        Assert.Equal([first.Id, second.Id, third.Id], rules.Select(rule => rule.Id).ToArray());
        Assert.Equal([1, 2, 3], rules.Select(rule => rule.RuleOrder).ToArray());
    }

    [Fact]
    public async Task ImportsByGroupKeyAndPreservesGroupsOutsideTheFile()
    {
        var databasePath = CreateLegacyDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var existing = await repository.SaveGroupAsync(new AreaGroupEdit(
            null, "原名称", "", "原备注", "重点", true, "same-key"));
        var untouched = await repository.SaveGroupAsync(new AreaGroupEdit(
            null, "保留组", "", "不应被导入覆盖", "重点", true, "untouched"));

        await repository.ImportAsync(new AreaGroupTransferDocument(1,
        [
            new AreaGroupTransferGroup(
                "same-key", "更新名称", "更新备注", false,
                [new AreaGroupTransferRule(4, "1号", "-", "1F", "include", ["GQ"], "规则")]),
            new AreaGroupTransferGroup("new-key", "新组", "", true, [])
        ]));

        var set = await repository.LoadAsync();
        var updated = Assert.Single(set.Groups, group => group.Id == existing.Id);
        Assert.Equal("更新名称", updated.Name);
        Assert.Equal("更新备注", updated.Description);
        Assert.False(updated.Enabled);
        Assert.Single(set.RuleRecords, rule => rule.GroupId == existing.Id && rule.Keywords.SequenceEqual(["GQ"]));
        Assert.Contains(set.Groups, group => group.Id == untouched.Id && group.Name == "保留组");
        Assert.Single(set.Groups, group => group.GroupKey == "new-key");
    }

    [Fact]
    public async Task LockedGroupRejectsRuleDeletion()
    {
        var databasePath = CreateLegacyDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var group = await repository.SaveGroupAsync(new AreaGroupEdit(
            null, "锁定组", "", "", "重点", true, "locked-rules"));
        var rule = await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "1号", "-", "1F", "include", "GQ", ""));
        await ExecuteAsync(databasePath, "UPDATE monitor_groups SET locked = 1 WHERE id = " + group.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteRuleAsync(rule.Id));

        Assert.Equal(1, Scalar(databasePath, "SELECT COUNT(*) FROM area_group_rules WHERE id = " + rule.Id));
    }

    [Fact]
    public async Task DeletingAreaGroupDoesNotSilentlyDeleteWatchRule()
    {
        var databasePath = CreateLegacyDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var group = await repository.SaveGroupAsync(new AreaGroupEdit(
            null, "关注引用组", "", "", "重点", true, "watch-reference"));
        await ExecuteAsync(databasePath, """
            CREATE TABLE device_watch_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                group_id INTEGER NOT NULL UNIQUE,
                name TEXT NOT NULL,
                start_at TEXT NOT NULL,
                end_at TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """ + $"INSERT INTO device_watch_rules(group_id, name, start_at, end_at, created_at, updated_at) VALUES ({group.Id}, '关注', '2026-09-01', '2026-09-02', '2026-09-01', '2026-09-01');");

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteGroupAsync(group.Id));

        Assert.Equal(1, Scalar(databasePath, "SELECT COUNT(*) FROM device_watch_rules WHERE group_id = " + group.Id));
        Assert.Equal(1, Scalar(databasePath, "SELECT COUNT(*) FROM monitor_groups WHERE id = " + group.Id));
    }

    [Fact]
    public async Task ImportDoesNotOverrideLockedGroup()
    {
        var databasePath = CreateLegacyDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var group = await repository.SaveGroupAsync(new AreaGroupEdit(
            null, "原锁定组", "原标签", "原备注", "重点", true, "locked-import"));
        await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "1号", "-", "1F", "include", "OLD", ""));
        await ExecuteAsync(databasePath, "UPDATE monitor_groups SET locked = 1 WHERE id = " + group.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ImportAsync(new AreaGroupTransferDocument(1,
        [new AreaGroupTransferGroup(
            "locked-import", "覆盖名称", "覆盖备注", false,
            [new AreaGroupTransferRule(1, "1号", "-", "2F", "include", ["NEW"], "")])])));

        var set = await repository.LoadAsync();
        var loaded = Assert.Single(set.Groups, item => item.Id == group.Id);
        Assert.True(loaded.Locked);
        Assert.Equal("原锁定组", loaded.Name);
        Assert.Contains(set.RuleRecords, rule => rule.GroupId == group.Id && rule.Keywords.SequenceEqual(["OLD"]));
    }

    [Fact]
    public void TransferRejectsNullCollectionsAtTheBoundary()
    {
        var document = new AreaGroupTransferDocument(1, null!);

        Assert.ThrowsAny<Exception>(() => AreaGroupTransferCodec.Validate(document));
    }

    [Fact]
    public async Task ExportAndImportPreserveAreaLabelAndPriority()
    {
        var databasePath = CreateLegacyDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        await repository.SaveGroupAsync(new AreaGroupEdit(
            null, "可迁移组", "原区域标签", "备注", "普通", true, "transfer-fields"));

        var exported = await repository.ExportAsync();
        var exportedGroup = Assert.Single(exported.Groups);
        Assert.Equal("原区域标签", exportedGroup.AreaLabel);
        Assert.Equal("普通", exportedGroup.Priority);

        var importedDatabasePath = CreateLegacyDatabase();
        var importedRepository = new SqliteAreaGroupRepository(() => importedDatabasePath);
        await importedRepository.ImportAsync(exported);
        var imported = Assert.Single((await importedRepository.LoadAsync()).Groups);
        Assert.Equal("原区域标签", imported.AreaLabel);
        Assert.Equal("普通", imported.Priority);
    }

    [Fact]
    public async Task SavesGroupAndRulesAsOneConfiguration()
    {
        var databasePath = CreateLegacyDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var saved = await repository.SaveConfigurationAsync(
            new AreaGroupEdit(null, "原子保存组", "", "", "重点", true, "atomic-save"),
            [new AreaGroupRuleEdit(0, "1号", "-", "1F", "include", "GQ", "")]);

        var loaded = await repository.LoadAsync();
        Assert.Contains(loaded.Groups, group => group.Id == saved.Id && group.Name == "原子保存组");
        Assert.Single(loaded.RuleRecords, rule => rule.GroupId == saved.Id && rule.Keywords.SequenceEqual(["GQ"]));
    }

    [Fact]
    public async Task AreaGroupListExcludesWatchOnlyGroupsWithoutAnAreaKey()
    {
        var databasePath = CreateLegacyDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var area = await repository.SaveGroupAsync(new AreaGroupEdit(
            null, "区域组", "", "", "重点", true, "area-only"));
        var watchOnly = await repository.SaveGroupAsync(new AreaGroupEdit(
            null, "关注容器", "", "", "重点", true, "watch-only"));
        await ExecuteAsync(databasePath, "UPDATE monitor_groups SET group_key = '' WHERE id = " + watchOnly.Id);

        var loaded = await repository.LoadAsync();

        Assert.Contains(loaded.Groups, group => group.Id == area.Id);
        Assert.DoesNotContain(loaded.Groups, group => group.Id == watchOnly.Id);
    }

    [Fact]
    public async Task StartupSchemaMigratorAppliesAreaRuleMigration()
    {
        var databasePath = CreateLegacyDatabase();
        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();

        Assert.Equal(0, Scalar(databasePath, "SELECT COUNT(*) FROM monitor_groups"));
        Assert.Equal(1, Scalar(databasePath, "SELECT COUNT(*) FROM ems_schema_migrations WHERE name = 'area-groups-rules-v1'"));
        Assert.Equal(1, Scalar(databasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'area_group_rules'"));
    }

    [Fact]
    public async Task PreservesWatchRulesAndClearsLegacyStateDuringAreaRuleMigration()
    {
        var databasePath = CreateLegacyDatabase();
        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE device_watch_rules (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    group_id INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    start_at TEXT NOT NULL,
                    end_at TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    note TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY(group_id) REFERENCES monitor_groups(id) ON DELETE CASCADE
                );
                CREATE TABLE legacy_area_api_state (id INTEGER PRIMARY KEY CHECK (id = 1), enabled_at TEXT NOT NULL);
                INSERT INTO monitor_groups(name) VALUES ('待清理旧组');
                INSERT INTO monitor_group_items(group_id, target_type, building)
                VALUES (2, 'floor', '2号');
                INSERT INTO device_watch_rules(group_id, name, start_at, end_at)
                VALUES (1, '旧关注', '2026-09-17', '2026-09-19');
                INSERT INTO legacy_area_api_state(id, enabled_at) VALUES (1, CURRENT_TIMESTAMP);
                """;
            command.ExecuteNonQuery();
        }

        await new SqliteSchemaMigrator(() => databasePath).MigrateAsync();

        Assert.Equal(1, Scalar(databasePath, "SELECT COUNT(*) FROM device_watch_rules"));
        Assert.Equal(1, Scalar(databasePath, "SELECT COUNT(*) FROM monitor_groups"));
        Assert.Equal(0, Scalar(databasePath, "SELECT COUNT(*) FROM monitor_groups WHERE enabled = 1"));
        Assert.Equal(0, Scalar(databasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'monitor_group_items'"));
        Assert.Equal(0, Scalar(databasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'legacy_area_api_state'"));
    }

    private static int Scalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateLegacyDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-area-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ac.db");
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE monitor_groups (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                area_label TEXT NOT NULL DEFAULT '',
                description TEXT NOT NULL DEFAULT '',
                priority TEXT NOT NULL DEFAULT '重点',
                group_kind TEXT NOT NULL DEFAULT 'custom',
                system_key TEXT,
                locked INTEGER NOT NULL DEFAULT 0,
                enabled INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
                CREATE TABLE monitor_group_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    group_id INTEGER NOT NULL,
                    target_type TEXT NOT NULL,
                    building TEXT NOT NULL,
                    floor_label TEXT,
                    floor_value REAL,
                    sub_area_text TEXT,
                    card_name TEXT,
                    note TEXT NOT NULL DEFAULT '',
                    FOREIGN KEY(group_id) REFERENCES monitor_groups(id)
                );
            CREATE TABLE cards (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO monitor_groups(name, group_kind, system_key, locked) VALUES ('公区', 'system', 'public', 1);
            INSERT INTO monitor_group_items(group_id, target_type, building) VALUES (1, 'floor', '1号');
            INSERT INTO cards(id, name) VALUES (1, '现场设备');
            """;
        command.ExecuteNonQuery();
        return path;
    }
}
