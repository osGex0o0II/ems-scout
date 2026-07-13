using EmsScout.Application.Groups;
using EmsScout.Application.Watch;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class AreaGroupRepositoryTests
{
    [Fact]
    public async Task MaintainsCustomGroupsAndMembers()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var saved = await repository.SaveGroupAsync(new AreaGroupEdit(
            Id: null,
            Name: "装修区",
            AreaLabel: "施工",
            Description: "临时施工区域",
            Priority: "重点",
            Enabled: true));
        var updated = await repository.SaveGroupAsync(new AreaGroupEdit(
            saved.Id,
            "装修复核区",
            "复核",
            "施工后复核区域",
            "紧急",
            false));
        var floor = await repository.SaveItemAsync(new AreaGroupItemEdit(
            saved.Id,
            "floor",
            "1号",
            "1F",
            string.Empty,
            string.Empty,
            "整层"));
        var subArea = await repository.SaveItemAsync(new AreaGroupItemEdit(
            saved.Id,
            "sub_area",
            "1号",
            "2F",
            "2F B",
            string.Empty,
            "子区"));
        var device = await repository.SaveItemAsync(new AreaGroupItemEdit(
            saved.Id,
            "device",
            "1号",
            "3F",
            "3F C",
            "1-0301-KT",
            "单台"));

        var set = await repository.LoadAsync();
        var group = Assert.Single(set.Groups, item => item.Id == saved.Id);

        Assert.Equal(saved.Id, updated.Id);
        Assert.Equal("装修复核区", group.Name);
        Assert.Equal("复核", group.AreaLabel);
        Assert.Equal("施工后复核区域", group.Description);
        Assert.Equal("紧急", group.Priority);
        Assert.False(group.Enabled);
        Assert.Equal(3, group.ItemCount);
        Assert.Equal(0, group.Total);
        Assert.Equal(0, group.OnCount);
        Assert.Equal(0, group.OffCount);
        Assert.Equal(0, group.OfflineCount);
        Assert.Equal(0, group.UnknownCount);
        Assert.Equal([floor.Id, subArea.Id, device.Id], set.Items.Where(item => item.GroupId == saved.Id).Select(item => item.Id).ToArray());

        await repository.DeleteItemAsync(device.Id);
        set = await repository.LoadAsync();
        group = Assert.Single(set.Groups, item => item.Id == saved.Id);
        Assert.Equal(2, group.ItemCount);
        Assert.Equal(0, group.Total);

        await repository.DeleteGroupAsync(saved.Id);
        set = await repository.LoadAsync();
        Assert.DoesNotContain(set.Groups, item => item.Id == saved.Id);
    }

    [Fact]
    public async Task ProtectsSystemGroupsAndLoadsTargetOptions()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var set = await repository.LoadAsync();
        var publicGroup = Assert.Single(set.Groups, item => item.SystemKey == "public");
        var options = await repository.LoadTargetOptionsAsync("1号", "1F");

        Assert.True(publicGroup.Locked);
        Assert.Equal(1, publicGroup.Total);
        Assert.Contains(options, option => option.Type == "sub_area" && option.SubAreaText == "1F A" && option.Count == 2);
        Assert.Contains(options, option => option.Type == "device" && option.CardName == "1-0101-KT");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteGroupAsync(publicGroup.Id));
        var member = await repository.SaveItemAsync(new AreaGroupItemEdit(
            publicGroup.Id,
            "floor",
            "1号",
            "1F",
            string.Empty,
            string.Empty,
            "人工维护的公区成员"));

        Assert.Equal(publicGroup.Id, member.GroupId);
        Assert.Contains((await repository.LoadAsync()).Items, item => item.Id == member.Id);
    }

    [Fact]
    public async Task RejectsDuplicateGroupNameWhenCreating()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var original = await repository.SaveGroupAsync(new AreaGroupEdit(
            Id: null,
            Name: "复核区",
            AreaLabel: "原标签",
            Description: "原说明",
            Priority: "重点",
            Enabled: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveGroupAsync(new AreaGroupEdit(
            Id: null,
            Name: "复核区",
            AreaLabel: "覆盖标签",
            Description: "覆盖说明",
            Priority: "紧急",
            Enabled: false)));

        var set = await repository.LoadAsync();
        var group = Assert.Single(set.Groups, item => item.Id == original.Id);
        Assert.Equal("原标签", group.AreaLabel);
        Assert.Equal("原说明", group.Description);
        Assert.Equal("重点", group.Priority);
        Assert.True(group.Enabled);
    }

    [Fact]
    public async Task RejectsDuplicateGroupNameWhenEditing()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var first = await SaveCustomGroupAsync(repository, "一期复核区");
        var second = await SaveCustomGroupAsync(repository, "二期复核区");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveGroupAsync(new AreaGroupEdit(
            Id: second.Id,
            Name: first.Name,
            AreaLabel: "撞名",
            Description: "编辑时撞名",
            Priority: "重点",
            Enabled: true)));

        Assert.Contains("已存在同名区域组", error.Message);
    }

    [Fact]
    public async Task RejectsDuplicateGroupNameIgnoringCase()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var first = await SaveCustomGroupAsync(repository, "Review-A");
        var second = await SaveCustomGroupAsync(repository, "Review-B");

        var createError = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveGroupAsync(new AreaGroupEdit(
            Id: null,
            Name: "review-a",
            AreaLabel: "撞名",
            Description: "大小写撞名",
            Priority: "重点",
            Enabled: true)));
        var editError = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveGroupAsync(new AreaGroupEdit(
            Id: second.Id,
            Name: "review-a",
            AreaLabel: "撞名",
            Description: "编辑大小写撞名",
            Priority: "重点",
            Enabled: true)));

        Assert.Contains("已存在同名区域组", createError.Message);
        Assert.Contains("已存在同名区域组", editError.Message);
        var set = await repository.LoadAsync();
        Assert.Contains(set.Groups, group => group.Id == first.Id && group.Name == "Review-A");
        Assert.Contains(set.Groups, group => group.Id == second.Id && group.Name == "Review-B");
    }

    [Fact]
    public async Task UpsertsDuplicateMembersWithoutInflatingItems()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var group = await SaveCustomGroupAsync(repository, "复核区");

        var first = await repository.SaveItemAsync(new AreaGroupItemEdit(
            group.Id,
            "sub_area",
            "1号",
            "2F",
            "2F B",
            string.Empty,
            "第一次"));
        var second = await repository.SaveItemAsync(new AreaGroupItemEdit(
            group.Id,
            "sub_area",
            "1号",
            "2F",
            "2F B",
            string.Empty,
            "更新备注"));

        var set = await repository.LoadAsync();
        var items = set.Items.Where(item => item.GroupId == group.Id).ToArray();

        Assert.Equal(first.Id, second.Id);
        var item = Assert.Single(items);
        Assert.Equal("更新备注", item.Note);
        Assert.Equal(1, Assert.Single(set.Groups, item => item.Id == group.Id).ItemCount);
    }

    [Fact]
    public async Task UpdatesMemberByIdAndRefreshesStats()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var group = await SaveCustomGroupAsync(repository, "编辑区");

        var floor = await repository.SaveItemAsync(new AreaGroupItemEdit(
            group.Id,
            "floor",
            "1号",
            "1F",
            string.Empty,
            string.Empty,
            "整层"));
        var updated = await repository.SaveItemAsync(new AreaGroupItemEdit(
            group.Id,
            "device",
            "1号",
            "3F",
            "3F C",
            "1-0301-KT",
            "单台",
            floor.Id));

        var set = await repository.LoadAsync();
        var item = Assert.Single(set.Items, item => item.GroupId == group.Id);
        var summary = Assert.Single(set.Groups, item => item.Id == group.Id);

        Assert.Equal(floor.Id, updated.Id);
        Assert.Equal(floor.Id, item.Id);
        Assert.Equal("device", item.TargetType);
        Assert.Equal("1-0301-KT", item.CardName);
        Assert.Equal("单台", item.Note);
        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.UnknownCount);
    }

    [Fact]
    public async Task UpdatingMemberIntoExistingNaturalKeyMergesRows()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var group = await SaveCustomGroupAsync(repository, "合并区");

        var first = await repository.SaveItemAsync(new AreaGroupItemEdit(
            group.Id,
            "floor",
            "1号",
            "1F",
            string.Empty,
            string.Empty,
            "整层"));
        var second = await repository.SaveItemAsync(new AreaGroupItemEdit(
            group.Id,
            "sub_area",
            "1号",
            "2F",
            "2F B",
            string.Empty,
            "子区"));
        var merged = await repository.SaveItemAsync(new AreaGroupItemEdit(
            group.Id,
            "sub_area",
            "1号",
            "2F",
            "2F B",
            string.Empty,
            "合并后",
            first.Id));

        var set = await repository.LoadAsync();
        var items = set.Items.Where(item => item.GroupId == group.Id).ToArray();

        Assert.Equal(second.Id, merged.Id);
        var item = Assert.Single(items);
        Assert.Equal(second.Id, item.Id);
        Assert.Equal("合并后", item.Note);
        Assert.Equal(1, Assert.Single(set.Groups, item => item.Id == group.Id).ItemCount);
    }

    [Fact]
    public async Task DeviceNaturalKeyIncludesFloorAndSubAreaWhenPresent()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var group = await SaveCustomGroupAsync(repository, "设备区");

        var first = await repository.SaveItemAsync(new AreaGroupItemEdit(
            group.Id,
            "device",
            "1号",
            "1F",
            "1F A",
            "1-0101-KT",
            "一层候选"));
        var second = await repository.SaveItemAsync(new AreaGroupItemEdit(
            group.Id,
            "device",
            "1号",
            "2F",
            "2F B",
            "1-0101-KT",
            "二层同名设备"));

        var set = await repository.LoadAsync();
        var items = set.Items.Where(item => item.GroupId == group.Id).OrderBy(item => item.Id).ToArray();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, items.Length);
        Assert.Equal(["1F A", "2F B"], items.Select(item => item.SubAreaText).ToArray());
        Assert.Equal(["一层候选", "二层同名设备"], items.Select(item => item.Note).ToArray());
    }

    [Fact]
    public async Task RejectsBroadDeviceTargetWithoutFloorAndSubArea()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var group = await SaveCustomGroupAsync(repository, "设备精确区");

        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveItemAsync(new AreaGroupItemEdit(
            group.Id,
            "device",
            "1号",
            string.Empty,
            string.Empty,
            "1-0101-KT",
            "缺少楼层和子区")));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveItemAsync(new AreaGroupItemEdit(
            group.Id,
            "device",
            "1号",
            "1F",
            string.Empty,
            "1-0101-KT",
            "缺少子区")));
    }

    [Fact]
    public async Task DeleteGroupRemovesWatchRuleInSameOperation()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var watch = new SqliteDeviceWatchRepository(() => databasePath);
        var group = await SaveCustomGroupAsync(groups, "夜间关注区");
        await groups.SaveItemAsync(new AreaGroupItemEdit(
            group.Id,
            "device",
            "1号",
            "1F",
            "1F A",
            "1-0101-KT",
            "单台关注"));
        await watch.SaveRuleAsync(new DeviceWatchEdit(
            Id: null,
            GroupId: group.Id,
            Name: "夜间关注",
            StartAt: DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
            EndAt: DateTimeOffset.Parse("2026-07-02T12:00:00Z"),
            Enabled: true,
            Note: "窗口内开关变化异常"));

        await groups.DeleteGroupAsync(group.Id);

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM device_watch_rules WHERE group_id = $group_id";
        command.Parameters.AddWithValue("$group_id", group.Id);
        var remaining = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task MaintainsFloorCatalogForManualGrouping()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var discovered = await repository.LoadFloorsAsync("1号");
        var manual = await repository.SaveFloorAsync(new FloorCatalogEdit(null, "1号", "4F", true, "人工新增楼层目录"));

        var floors = await repository.LoadFloorsAsync("1号");
        await repository.DeleteFloorAsync(manual.Id);
        var enabledOnly = await repository.LoadFloorsAsync("1号");
        var includeDisabled = await repository.LoadFloorsAsync("1号", includeDisabled: true);

        Assert.Contains(discovered, floor => floor.FloorLabel == "1F" && floor.Source == "discovered");
        Assert.Equal("4F", manual.FloorLabel);
        Assert.Equal(4, manual.FloorValue);
        Assert.Equal("manual", manual.Source);
        Assert.Contains(floors, floor => floor.Id == manual.Id && floor.Enabled);
        Assert.DoesNotContain(enabledOnly, floor => floor.Id == manual.Id);
        Assert.Contains(includeDisabled, floor => floor.Id == manual.Id && !floor.Enabled);
    }

    [Fact]
    public async Task ScheduleRulesUpsertMembersDeduplicateAndOverlapsAreRejected()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var area = await SaveCustomGroupAsync(repository, "公区巡检");
        var item = await repository.SaveItemAsync(new AreaGroupItemEdit(
            area.Id, "floor", "1号", "1F", string.Empty, string.Empty, "一层"));
        var schedule = await repository.SaveScheduleGroupAsync(new ScheduleGroupEdit(
            area.Id, "上午启用", "", true));

        var firstRule = await repository.SaveScheduleRuleAsync(new ScheduleRuleEdit(
            schedule.Id, "2026-07-12", "enabled",
            [new ScheduleIntervalEdit("08:00", "12:00"), new ScheduleIntervalEdit("13:00", "18:00")], ""));
        var updatedRule = await repository.SaveScheduleRuleAsync(new ScheduleRuleEdit(
            schedule.Id, "2026-07-12", "enabled", [new ScheduleIntervalEdit("09:00", "11:00")], "调整"));
        var firstMember = await repository.SaveScheduleMemberAsync(new ScheduleMemberEdit(
            schedule.Id, item.Id, item.TargetType, item.Building, item.FloorLabel, item.SubAreaText,
            item.CardName, item.DeviceUid, "normal", ""));
        var duplicateMember = await repository.SaveScheduleMemberAsync(new ScheduleMemberEdit(
            schedule.Id, item.Id, item.TargetType, item.Building, item.FloorLabel, item.SubAreaText,
            item.CardName, item.DeviceUid, "not_open", "重复添加应更新"));

        Assert.Equal(firstRule.Id, updatedRule.Id);
        Assert.Single(updatedRule.Intervals);
        Assert.Equal(firstMember.Id, duplicateMember.Id);
        Assert.Equal("not_open", duplicateMember.ExpectedStatus);
        var batch = await repository.SaveScheduleRulesAsync(new ScheduleRuleBatchEdit(
            schedule.Id,
            ["2026-07-14", "2026-07-15", "2026-07-14"],
            "enabled",
            [new ScheduleIntervalEdit("07:30", "09:30"), new ScheduleIntervalEdit("16:00", "18:00")],
            "批量规则"));
        Assert.Equal(2, batch.Count);
        Assert.All(batch, rule => Assert.Equal(2, rule.Intervals.Count));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveScheduleRuleAsync(new ScheduleRuleEdit(
            schedule.Id, "2026-07-13", "enabled",
            [new ScheduleIntervalEdit("08:00", "12:00"), new ScheduleIntervalEdit("11:00", "13:00")], "")));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveScheduleRulesAsync(new ScheduleRuleBatchEdit(
            schedule.Id, ["2026-07-16", "2026-07-17"], "enabled",
            [new ScheduleIntervalEdit("08:00", "12:00"), new ScheduleIntervalEdit("11:00", "13:00")], "")));
        var afterFailedBatch = await repository.LoadScheduleGroupsAsync(area.Id);
        Assert.DoesNotContain(afterFailedBatch.Single().Rules, rule => rule.CalendarDate is "2026-07-16" or "2026-07-17");
        await repository.DeleteScheduleRulesAsync(schedule.Id, ["2026-07-14", "2026-07-15"]);
        Assert.DoesNotContain((await repository.LoadScheduleGroupsAsync(area.Id)).Single().Rules,
            rule => rule.CalendarDate is "2026-07-14" or "2026-07-15");
    }

    [Fact]
    public async Task ScheduleAuditEvaluatesEveryDeviceAndCascadesAreaMemberDeletion()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var area = await SaveCustomGroupAsync(repository, "设备启用核查");
        var item = await repository.SaveItemAsync(new AreaGroupItemEdit(
            area.Id, "floor", "1号", "1F", string.Empty, string.Empty, "一层"));
        var schedule = await repository.SaveScheduleGroupAsync(new ScheduleGroupEdit(area.Id, "工作时段", "", true));
        await repository.SaveScheduleRuleAsync(new ScheduleRuleEdit(
            schedule.Id, "2026-07-12", "enabled", [new ScheduleIntervalEdit("08:00", "12:00")], ""));
        var member = await repository.SaveScheduleMemberAsync(new ScheduleMemberEdit(
            schedule.Id, item.Id, item.TargetType, item.Building, item.FloorLabel, item.SubAreaText,
            item.CardName, item.DeviceUid, "normal", ""));

        var audit = await repository.EvaluateSchedulesAsync(
            null, new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.FromHours(8)));

        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, row => row.TargetLabel.Contains("1-0101-KT") && row.ResultCode == "ok");
        Assert.Contains(audit, row => row.TargetLabel.Contains("1-0102-KT") && row.ResultCode == "not_enabled");

        await repository.SaveScheduleMemberAsync(new ScheduleMemberEdit(
            schedule.Id, item.Id, item.TargetType, item.Building, item.FloorLabel, item.SubAreaText,
            item.CardName, item.DeviceUid, "not_open", "", member.Id));
        audit = await repository.EvaluateSchedulesAsync(
            null, new DateTimeOffset(2026, 7, 12, 10, 0, 0, TimeSpan.FromHours(8)));
        Assert.Contains(audit, row => row.TargetLabel.Contains("1-0101-KT") && row.ResultCode == "unexpected_running");

        await repository.DeleteItemAsync(item.Id);
        Assert.Empty((await repository.LoadScheduleGroupsAsync(area.Id)).Single().Members);
    }

    private static Task<AreaGroupRecord> SaveCustomGroupAsync(SqliteAreaGroupRepository repository, string name)
    {
        return repository.SaveGroupAsync(new AreaGroupEdit(
            Id: null,
            Name: name,
            AreaLabel: "自定义",
            Description: "测试区域",
            Priority: "重点",
            Enabled: true));
    }

    private static string CreateDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-area-group-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ac.db");
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
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
                layout TEXT NOT NULL DEFAULT ''
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
                comm TEXT,
                device_uid TEXT
            );
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
                target_type TEXT NOT NULL DEFAULT 'floor',
                building TEXT NOT NULL,
                floor_label TEXT,
                floor_value REAL,
                sub_area_text TEXT,
                card_name TEXT,
                device_uid TEXT,
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE floor_catalog (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                building TEXT NOT NULL,
                floor_label TEXT NOT NULL,
                floor_value REAL,
                source TEXT NOT NULL DEFAULT 'manual',
                enabled INTEGER NOT NULL DEFAULT 1,
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE UNIQUE INDEX idx_floor_catalog_key ON floor_catalog(building, floor_label);
            CREATE INDEX idx_monitor_group_items_group ON monitor_group_items(group_id);
            CREATE INDEX idx_monitor_group_items_target ON monitor_group_items(building, floor_value, sub_area_text, card_name);
            CREATE TABLE device_watch_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                group_id INTEGER NOT NULL UNIQUE,
                name TEXT NOT NULL DEFAULT '关注设备',
                start_at TEXT NOT NULL,
                end_at TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX idx_device_watch_rules_enabled ON device_watch_rules(enabled, start_at, end_at);
            PRAGMA user_version = 2;

            INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y) VALUES
                (1, '1号', 1, '1F A', 1, 100, 100),
                (2, '1号', 2, '2F B', 2, 100, 200),
                (3, '1号', 3, '3F C', 3, 100, 300);
            INSERT INTO pages (id, sub_area_id, page_name, layout) VALUES
                (1, 1, 'default', 'grid'),
                (2, 2, 'default', 'grid'),
                (3, 3, 'default', 'grid');
            INSERT INTO cards (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm) VALUES
                (1, 1, '1-0101-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机'),
                (2, 1, '1-0102-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机'),
                (3, 2, '1-0201-KT', '-', '-', '0', '0', '-', 'gray.png', '离线'),
                (4, 3, 'GQ-0301-KT', '-', '-', '', '', '', '', ''),
                (5, 3, '1-0301-KT', '-', '-', '', '', '', '', '');
            INSERT INTO monitor_groups
                (id, name, area_label, description, priority, group_kind, system_key, locked, enabled)
            VALUES
                (1, '公区', '公区', '系统公区', '重点', 'system', 'public', 1, 1),
                (2, '非公区', '非公区', '系统非公区', '重点', 'system', 'non_public', 1, 1);
            """;
        command.ExecuteNonQuery();
        TestScheduleSchema.Apply(connection);
        return path;
    }
}
