using EmsScout.Application.Groups;
using EmsScout.Application.Watch;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using System.IO.Compression;

namespace EmsScout.Tests;

public sealed class DeviceWatchRepositoryTests
{
    [Fact]
    public async Task DetectsPowerChangesInsideWatchWindow()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var watch = new SqliteDeviceWatchRepository(() => databasePath);

        var group = await groups.SaveGroupAsync(new AreaGroupEdit(
            Id: null,
            Name: "夜间关注",
            AreaLabel: "夜间",
            Description: "夜间关注设备",
            Priority: "重点",
            Enabled: true));
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

        var evaluation = await watch.EvaluateAsync(new DeviceWatchQuery());

        var incident = Assert.Single(evaluation.Incidents);
        Assert.Equal("1-0101-KT", incident.Device.Name);
        Assert.Equal("OFF", incident.PreviousState);
        Assert.Equal("ON", incident.CurrentState);
        Assert.Equal(1, evaluation.WatchedDevices);
        Assert.Equal(1, evaluation.AbnormalDevices);
        var state = Assert.Single(evaluation.DeviceStates.Values);
        Assert.True(state.IsWatched);
        Assert.True(state.IsAbnormal);
        Assert.Contains("#1", state.Evidence);
        Assert.Contains("#2", state.Evidence);
    }

    [Fact]
    public async Task IgnoresChangesOutsideWatchWindow()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var watch = new SqliteDeviceWatchRepository(() => databasePath);

        var group = await groups.SaveGroupAsync(new AreaGroupEdit(
            Id: null,
            Name: "白天关注",
            AreaLabel: "白天",
            Description: "白天关注设备",
            Priority: "重点",
            Enabled: true));
        await groups.SaveItemAsync(new AreaGroupItemEdit(group.Id, "device", "1号", "1F", "1F A", "1-0101-KT", string.Empty));
        await watch.SaveRuleAsync(new DeviceWatchEdit(
            Id: null,
            GroupId: group.Id,
            Name: "白天关注",
            StartAt: DateTimeOffset.Parse("2026-07-03T00:00:00Z"),
            EndAt: DateTimeOffset.Parse("2026-07-03T12:00:00Z"),
            Enabled: true,
            Note: string.Empty));

        var evaluation = await watch.EvaluateAsync(new DeviceWatchQuery());

        Assert.Empty(evaluation.Incidents);
        Assert.Equal(1, evaluation.WatchedDevices);
        Assert.Equal(0, evaluation.AbnormalDevices);
        Assert.False(Assert.Single(evaluation.DeviceStates.Values).IsAbnormal);
    }

    [Fact]
    public async Task SaveRuleRejectsMissingAndSystemGroups()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var watch = new SqliteDeviceWatchRepository(() => databasePath);
        await groups.SaveGroupAsync(new AreaGroupEdit(null, "自定义组", "自定义", "测试", "重点", true));
        long systemGroupId;
        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO monitor_groups
                    (name, area_label, description, priority, group_kind, system_key, locked, enabled)
                VALUES
                    ('系统公区', '公区', '系统规则', '重点', 'system', 'public', 1, 1)
                RETURNING id
                """;
            systemGroupId = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        await Assert.ThrowsAsync<ArgumentException>(() => watch.SaveRuleAsync(new DeviceWatchEdit(
            Id: null,
            GroupId: 9999,
            Name: "不存在分组",
            StartAt: DateTimeOffset.Parse("2026-07-03T00:00:00Z"),
            EndAt: DateTimeOffset.Parse("2026-07-03T12:00:00Z"),
            Enabled: true,
            Note: string.Empty)));
        await Assert.ThrowsAsync<ArgumentException>(() => watch.SaveRuleAsync(new DeviceWatchEdit(
            Id: null,
            GroupId: systemGroupId,
            Name: "系统分组",
            StartAt: DateTimeOffset.Parse("2026-07-03T00:00:00Z"),
            EndAt: DateTimeOffset.Parse("2026-07-03T12:00:00Z"),
            Enabled: true,
            Note: string.Empty)));
    }

    [Fact]
    public async Task DeviceRepositoryFiltersWatchAbnormalRows()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var watch = new SqliteDeviceWatchRepository(() => databasePath);
        var devices = new SqliteDeviceReadRepository(() => databasePath, watchRepository: watch);

        var group = await groups.SaveGroupAsync(new AreaGroupEdit(null, "夜间关注", "夜间", "夜间关注设备", "重点", true));
        await groups.SaveItemAsync(new AreaGroupItemEdit(group.Id, "device", "1号", "1F", "1F A", "1-0101-KT", string.Empty));
        await watch.SaveRuleAsync(new DeviceWatchEdit(
            null,
            group.Id,
            "夜间关注",
            DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-02T12:00:00Z"),
            true,
            string.Empty));

        var abnormal = await devices.SearchAsync(new(WatchState: "abnormal"));

        var row = Assert.Single(abnormal.Rows);
        Assert.Equal("1-0101-KT", row.Name);
        Assert.True(row.IsWatchAbnormal);
        Assert.Equal(1, abnormal.Facets.WatchAbnormal);
    }

    [Fact]
    public async Task WatchAbnormalRowsAreExportedWithoutLegacyEvidenceColumns()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var watch = new SqliteDeviceWatchRepository(() => databasePath);
        var devices = new SqliteDeviceReadRepository(() => databasePath, watchRepository: watch);
        var exportService = new SqliteDeviceExportService(devices);
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-watch-export-tests", Guid.NewGuid().ToString("N"));

        var group = await groups.SaveGroupAsync(new AreaGroupEdit(null, "夜间关注", "夜间", "夜间关注设备", "重点", true));
        await groups.SaveItemAsync(new AreaGroupItemEdit(group.Id, "device", "1号", "1F", "1F A", "1-0101-KT", string.Empty));
        await watch.SaveRuleAsync(new DeviceWatchEdit(
            null,
            group.Id,
            "夜间关注",
            DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-02T12:00:00Z"),
            true,
            "窗口内开关变化异常"));

        var export = await exportService.ExportAsync(new(WatchState: "abnormal"), output);

        Assert.Equal(1, export.RowCount);
        Assert.Equal(1, export.Facets.WatchAbnormal);
        UserDeviceWorkbookAssert.AssertShape(export);
        using var archive = ZipFile.OpenRead(export.Path);
        var devicesSheet = ReadEntry(archive, "xl/worksheets/sheet1.xml");
        Assert.DoesNotContain("watch_state", devicesSheet);
        Assert.DoesNotContain("watch_window", devicesSheet);
        Assert.DoesNotContain("watch_evidence", devicesSheet);
        Assert.Contains("1-0101-KT", devicesSheet);
        Assert.DoesNotContain("#1", devicesSheet);
        Assert.DoesNotContain("#2", devicesSheet);
    }

    [Fact]
    public async Task RejectsWatchRuleUpdateWhenRuleBelongsToAnotherGroup()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var watch = new SqliteDeviceWatchRepository(() => databasePath);
        var groupA = await groups.SaveGroupAsync(new AreaGroupEdit(null, "关注A", "A", "A组", "重点", true));
        var groupB = await groups.SaveGroupAsync(new AreaGroupEdit(null, "关注B", "B", "B组", "重点", true));
        var ruleA = await watch.SaveRuleAsync(new DeviceWatchEdit(
            null,
            groupA.Id,
            "A规则",
            DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-02T12:00:00Z"),
            true,
            "A"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => watch.SaveRuleAsync(new DeviceWatchEdit(
            ruleA.Id,
            groupB.Id,
            "错误移动",
            DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-02T12:00:00Z"),
            true,
            "B")));

        Assert.Contains("不属于当前分组", error.Message);
        Assert.Equal(groupA.Id, (await watch.LoadRuleForGroupAsync(groupA.Id))?.GroupId);
        Assert.Null(await watch.LoadRuleForGroupAsync(groupB.Id));
    }

    [Fact]
    public async Task DeleteWatchRuleRequiresMatchingGroup()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var watch = new SqliteDeviceWatchRepository(() => databasePath);
        var groupA = await groups.SaveGroupAsync(new AreaGroupEdit(null, "删除A", "A", "A组", "重点", true));
        var groupB = await groups.SaveGroupAsync(new AreaGroupEdit(null, "删除B", "B", "B组", "重点", true));
        var ruleA = await watch.SaveRuleAsync(new DeviceWatchEdit(
            null,
            groupA.Id,
            "A规则",
            DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-02T12:00:00Z"),
            true,
            "A"));

        await watch.DeleteRuleAsync(ruleA.Id, groupB.Id);
        Assert.NotNull(await watch.LoadRuleForGroupAsync(groupA.Id));

        await watch.DeleteRuleAsync(ruleA.Id, groupA.Id);
        Assert.Null(await watch.LoadRuleForGroupAsync(groupA.Id));
    }

    [Fact]
    public async Task MixedFloorAndExactDeviceTargetsMarkExpectedWatchRows()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var watch = new SqliteDeviceWatchRepository(() => databasePath);
        var devices = new SqliteDeviceReadRepository(() => databasePath, watchRepository: watch);

        var group = await groups.SaveGroupAsync(new AreaGroupEdit(null, "混合关注", "混合", "楼层加单台设备", "重点", true));
        await groups.SaveItemAsync(new AreaGroupItemEdit(group.Id, "floor", "1号", "1F", string.Empty, string.Empty, "1号 1F 整层"));
        await groups.SaveItemAsync(new AreaGroupItemEdit(group.Id, "device", "2号", "1F", "1F A", "2-0101-KT", "2号单台"));
        await watch.SaveRuleAsync(new DeviceWatchEdit(
            null,
            group.Id,
            "混合关注",
            DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-02T12:00:00Z"),
            true,
            string.Empty));

        var evaluation = await watch.EvaluateAsync(new DeviceWatchQuery());
        var abnormal = await devices.SearchAsync(new(WatchState: "abnormal"));
        var watched = await devices.SearchAsync(new(WatchState: "watched"));

        Assert.Equal(3, evaluation.WatchedDevices);
        Assert.Equal(2, evaluation.AbnormalDevices);
        Assert.Equal(3, evaluation.DeviceStates.Count);
        Assert.Equal(1, evaluation.DeviceStates.Values.Count(state => state.IsWatched && !state.IsAbnormal));
        Assert.Equal(["1-0101-KT", "2-0101-KT"], abnormal.Rows.Select(row => row.Name).Order().ToArray());
        Assert.Equal(["1-0101-KT", "1-0102-KT", "2-0101-KT"], watched.Rows.Select(row => row.Name).Order().ToArray());
    }

    [Fact]
    public async Task DisabledCustomGroupWatchRuleIsNotEvaluatedByDefault()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var watch = new SqliteDeviceWatchRepository(() => databasePath);

        var group = await groups.SaveGroupAsync(new AreaGroupEdit(null, "停用关注", "停用", "停用组关注", "重点", true));
        await groups.SaveItemAsync(new AreaGroupItemEdit(group.Id, "device", "1号", "1F", "1F A", "1-0101-KT", string.Empty));
        await watch.SaveRuleAsync(new DeviceWatchEdit(
            null,
            group.Id,
            "停用关注",
            DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-02T12:00:00Z"),
            true,
            string.Empty));
        await groups.SaveGroupAsync(new AreaGroupEdit(group.Id, "停用关注", "停用", "停用组关注", "重点", false));

        var evaluation = await watch.EvaluateAsync(new DeviceWatchQuery());
        var storedRule = await watch.LoadRuleForGroupAsync(group.Id);

        Assert.NotNull(storedRule);
        Assert.Empty(evaluation.Rules);
        Assert.Empty(evaluation.Incidents);
        Assert.Empty(evaluation.DeviceStates);
    }

    [Fact]
    public async Task ExactDeviceWatchTargetDoesNotExpandToSameNameDevicesInOtherAreas()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var watch = new SqliteDeviceWatchRepository(() => databasePath);
        var devices = new SqliteDeviceReadRepository(() => databasePath, watchRepository: watch);

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y)
                VALUES
                    (20, '1号', 3, '3F C', 3, 100, 300),
                    (21, '1号', 4, '4F D', 4, 100, 400);
                INSERT INTO pages (id, sub_area_id, page_name, layout)
                VALUES
                    (20, 20, 'default', 'grid'),
                    (21, 21, 'default', 'grid');
                INSERT INTO cards (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
                VALUES
                    (20, 20, 'DUP-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机'),
                    (21, 21, 'DUP-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机');
                INSERT INTO run_sub_areas (id, run_id, building, sub_idx, floor, text, x, y)
                VALUES
                    (20, 1, '1号', 3, 3, '3F C', 100, 300),
                    (21, 2, '1号', 3, 3, '3F C', 100, 300),
                    (22, 1, '1号', 4, 4, '4F D', 100, 400),
                    (23, 2, '1号', 4, 4, '4F D', 100, 400);
                INSERT INTO run_pages (id, run_id, run_sub_area_id, page_name, layout)
                VALUES
                    (20, 1, 20, 'default', 'grid'),
                    (21, 2, 21, 'default', 'grid'),
                    (22, 1, 22, 'default', 'grid'),
                    (23, 2, 23, 'default', 'grid');
                INSERT INTO run_cards (id, run_id, run_page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
                VALUES
                    (20, 1, 20, 'DUP-KT', 'OFF', '制冷', '26', '24', '中', 'green.png', '关机'),
                    (21, 2, 21, 'DUP-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机'),
                    (22, 1, 22, 'DUP-KT', 'OFF', '制冷', '26', '24', '中', 'green.png', '关机'),
                    (23, 2, 23, 'DUP-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机');
                """;
            command.ExecuteNonQuery();
        }

        var group = await groups.SaveGroupAsync(new AreaGroupEdit(null, "同名设备关注", "同名", "同名设备精确关注", "重点", true));
        await groups.SaveItemAsync(new AreaGroupItemEdit(group.Id, "device", "1号", "3F", "3F C", "DUP-KT", string.Empty));
        await watch.SaveRuleAsync(new DeviceWatchEdit(
            null,
            group.Id,
            "同名设备关注",
            DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-02T12:00:00Z"),
            true,
            string.Empty));

        var evaluation = await watch.EvaluateAsync(new DeviceWatchQuery());
        var abnormal = await devices.SearchAsync(new(WatchState: "abnormal"));

        Assert.Equal(1, evaluation.WatchedDevices);
        Assert.Equal(1, evaluation.AbnormalDevices);
        var row = Assert.Single(abnormal.Rows, row => row.Name == "DUP-KT");
        Assert.Equal("3F C", row.SubArea);
    }

    [Fact]
    public async Task DeviceRepositoryMarksSpecialBmSubAreaWatchRows()
    {
        var databasePath = CreateDatabase();
        var groups = new SqliteAreaGroupRepository(() => databasePath);
        var watch = new SqliteDeviceWatchRepository(() => databasePath);
        var devices = new SqliteDeviceReadRepository(() => databasePath, watchRepository: watch);

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y)
                VALUES (10, '6号', 1, 'BM', 10, 100, 100);
                INSERT INTO pages (id, sub_area_id, page_name, layout)
                VALUES (10, 10, 'default', 'grid');
                INSERT INTO cards (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
                VALUES (10, 10, '6-BM-001-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机');
                INSERT INTO collection_runs (id, run_key, completed_at, imported_at, buildings, card_count, off_count)
                VALUES
                    (10, 'bm1', '2026-07-04T00:00:00Z', '2026-07-04T00:01:00Z', '["6号"]', 1, 1),
                    (11, 'bm2', '2026-07-04T06:00:00Z', '2026-07-04T06:01:00Z', '["6号"]', 1, 0);
                INSERT INTO run_sub_areas (id, run_id, building, sub_idx, floor, text, x, y)
                VALUES
                    (10, 10, '6号', 10, 1, 'BM', 100, 100),
                    (11, 11, '6号', 10, 1, 'BM', 100, 100);
                INSERT INTO run_pages (id, run_id, run_sub_area_id, page_name, layout)
                VALUES
                    (10, 10, 10, 'default', 'grid'),
                    (11, 11, 11, 'default', 'grid');
                INSERT INTO run_cards (id, run_id, run_page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
                VALUES
                    (10, 10, 10, '6-BM-001-KT', 'OFF', '制冷', '26', '24', '中', 'green.png', '关机'),
                    (11, 11, 11, '6-BM-001-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机');
                """;
            command.ExecuteNonQuery();
        }

        var group = await groups.SaveGroupAsync(new AreaGroupEdit(null, "BM关注", "BM", "BM 关注设备", "重点", true));
        await groups.SaveItemAsync(new AreaGroupItemEdit(group.Id, "sub_area", "6号", "1F", "BM", string.Empty, string.Empty));
        await watch.SaveRuleAsync(new DeviceWatchEdit(
            null,
            group.Id,
            "BM关注",
            DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-04T12:00:00Z"),
            true,
            string.Empty));

        var abnormal = await devices.SearchAsync(new(WatchState: "abnormal", Building: "6号"));

        var row = Assert.Single(abnormal.Rows);
        Assert.Equal("6-BM-001-KT", row.Name);
        Assert.Equal("BM", row.FloorLabel);
        Assert.True(row.IsWatchAbnormal);
    }

    private static string CreateDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-watch-tests", Guid.NewGuid().ToString("N"));
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
                comm TEXT
            );
            CREATE TABLE collection_runs (
                id INTEGER PRIMARY KEY,
                run_key TEXT,
                completed_at TEXT NOT NULL,
                imported_at TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'completed',
                scope TEXT NOT NULL DEFAULT 'full',
                buildings TEXT NOT NULL DEFAULT '[]',
                card_count INTEGER NOT NULL DEFAULT 0,
                on_count INTEGER NOT NULL DEFAULT 0,
                off_count INTEGER NOT NULL DEFAULT 0,
                offline_count INTEGER NOT NULL DEFAULT 0,
                unknown_count INTEGER NOT NULL DEFAULT 0,
                quality_summary TEXT NOT NULL DEFAULT '{}',
                is_anomaly INTEGER NOT NULL DEFAULT 0,
                note TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE run_sub_areas (
                id INTEGER PRIMARY KEY,
                run_id INTEGER NOT NULL,
                building TEXT NOT NULL,
                sub_idx INTEGER,
                floor REAL,
                text TEXT,
                x REAL,
                y REAL
            );
            CREATE TABLE run_pages (
                id INTEGER PRIMARY KEY,
                run_id INTEGER NOT NULL,
                run_sub_area_id INTEGER NOT NULL,
                page_name TEXT,
                layout TEXT
            );
            CREATE TABLE run_cards (
                id INTEGER PRIMARY KEY,
                run_id INTEGER NOT NULL,
                run_page_id INTEGER NOT NULL,
                name TEXT,
                switch TEXT,
                mode TEXT,
                indoor TEXT,
                set_temp TEXT,
                fan TEXT,
                indicator TEXT,
                comm TEXT
            );

            INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y) VALUES
                (1, '1号', 1, '1F A', 1, 100, 100),
                (2, '1号', 1, '1F B', 2, 100, 120),
                (3, '2号', 1, '1F A', 1, 100, 100);
            INSERT INTO pages (id, sub_area_id, page_name, layout) VALUES
                (1, 1, 'default', 'grid'),
                (2, 2, 'default', 'grid'),
                (3, 3, 'default', 'grid');
            INSERT INTO cards (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            VALUES
                (1, 1, '1-0101-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机'),
                (2, 2, '1-0102-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机'),
                (3, 3, '2-0101-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机');

            INSERT INTO collection_runs (id, run_key, completed_at, imported_at, buildings, card_count, off_count)
            VALUES
                (1, 'r1', '2026-07-02T00:00:00Z', '2026-07-02T00:01:00Z', '["1号"]', 1, 1),
                (2, 'r2', '2026-07-02T06:00:00Z', '2026-07-02T06:01:00Z', '["1号"]', 1, 0),
                (3, 'r3', '2026-07-02T18:00:00Z', '2026-07-02T18:01:00Z', '["1号"]', 1, 1);
            INSERT INTO run_sub_areas (id, run_id, building, sub_idx, floor, text, x, y)
            VALUES
                (1, 1, '1号', 1, 1, '1F A', 100, 100),
                (2, 2, '1号', 1, 1, '1F A', 100, 100),
                (3, 3, '1号', 1, 1, '1F A', 100, 100),
                (4, 1, '1号', 2, 1, '1F B', 100, 120),
                (5, 2, '1号', 2, 1, '1F B', 100, 120),
                (6, 3, '1号', 2, 1, '1F B', 100, 120),
                (7, 1, '2号', 1, 1, '1F A', 100, 100),
                (8, 2, '2号', 1, 1, '1F A', 100, 100),
                (9, 3, '2号', 1, 1, '1F A', 100, 100);
            INSERT INTO run_pages (id, run_id, run_sub_area_id, page_name, layout)
            VALUES
                (1, 1, 1, 'default', 'grid'),
                (2, 2, 2, 'default', 'grid'),
                (3, 3, 3, 'default', 'grid'),
                (4, 1, 4, 'default', 'grid'),
                (5, 2, 5, 'default', 'grid'),
                (6, 3, 6, 'default', 'grid'),
                (7, 1, 7, 'default', 'grid'),
                (8, 2, 8, 'default', 'grid'),
                (9, 3, 9, 'default', 'grid');
            INSERT INTO run_cards (id, run_id, run_page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            VALUES
                (1, 1, 1, '1-0101-KT', 'OFF', '制冷', '26', '24', '中', 'green.png', '关机'),
                (2, 2, 2, '1-0101-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机'),
                (3, 3, 3, '1-0101-KT', 'OFF', '制冷', '26', '24', '中', 'green.png', '关机'),
                (4, 1, 4, '1-0102-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机'),
                (5, 2, 5, '1-0102-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机'),
                (6, 3, 6, '1-0102-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机'),
                (7, 1, 7, '2-0101-KT', 'OFF', '制冷', '26', '24', '中', 'green.png', '关机'),
                (8, 2, 8, '2-0101-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机'),
                (9, 3, 9, '2-0101-KT', 'OFF', '制冷', '26', '24', '中', 'green.png', '关机');
            """;
        command.ExecuteNonQuery();
        return path;
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
