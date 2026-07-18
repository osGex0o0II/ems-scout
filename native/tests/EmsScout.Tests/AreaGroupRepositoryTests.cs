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
    public async Task PhysicalDeviceUidCollapsesPageOccurrencesAndPersistsMembership()
    {
        var databasePath = CreateDatabase();
        ExecuteSql(databasePath, """
            UPDATE cards
            SET name = 'MOVE-OLD-KT', source_key = 'source-a-primary', device_uid = 'uid-a'
            WHERE id = 1;
            UPDATE cards
            SET name = 'SAME-LABEL-KT', source_key = 'source-b', device_uid = 'uid-b'
            WHERE id = 2;
            INSERT INTO pages (id, sub_area_id, page_name, layout)
            VALUES (4, 1, '二页', 'grid');
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            VALUES
                (6, 4, 'MOVE-OLD-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机', 'source-a-secondary', 'uid-a'),
                (7, 1, 'SAME-LABEL-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'source-c', 'uid-c');
            """);
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var options = (await repository.LoadTargetOptionsAsync("1号", "1F"))
            .Where(option => option.Type == "device")
            .ToArray();
        var physical = Assert.Single(options, option => ReadStringProperty(option, "DeviceUid") == "uid-a");
        Assert.Equal(2, physical.Count);
        Assert.Equal("default", ReadStringProperty(physical, "PageName"));
        Assert.Equal("source-a-primary", ReadStringProperty(physical, "SourceKey"));
        Assert.Equal(1, ReadIntProperty(physical, "Occurrence"));
        var sameLabel = options.Where(option => option.CardName == "SAME-LABEL-KT").ToArray();
        Assert.Equal(2, sameLabel.Length);
        Assert.Equal(["uid-b", "uid-c"], sameLabel.Select(option => ReadStringProperty(option, "DeviceUid")).Order().ToArray());
        Assert.Equal([1, 2], sameLabel.Select(option => ReadIntProperty(option, "Occurrence")).Order().ToArray());

        var group = await SaveCustomGroupAsync(repository, "UID 设备组");
        var legacy = await repository.SaveItemAsync(DeviceItemEdit(
            group.Id, "1F", "1F A", "MOVE-OLD-KT", "legacy", deviceUid: string.Empty));
        var schedule = await repository.SaveScheduleGroupAsync(new ScheduleGroupEdit(group.Id, "UID 计划", "", true));
        await repository.SaveScheduleMemberAsync(new ScheduleMemberEdit(
            schedule.Id, legacy.Id, legacy.TargetType, legacy.Building, legacy.FloorLabel,
            legacy.SubAreaText, legacy.CardName, legacy.DeviceUid, "normal", ""));

        var promoted = await repository.SaveItemAsync(DeviceItemEdit(
            group.Id, "1F", "1F A", "MOVE-OLD-KT", "promoted", deviceUid: "uid-a"));
        var uidB = await repository.SaveItemAsync(DeviceItemEdit(
            group.Id, "1F", "1F A", "SAME-LABEL-KT", "B", deviceUid: "uid-b"));
        var uidC = await repository.SaveItemAsync(DeviceItemEdit(
            group.Id, "1F", "1F A", "SAME-LABEL-KT", "C", deviceUid: "uid-c"));

        Assert.Equal(legacy.Id, promoted.Id);
        Assert.Equal("uid-a", promoted.DeviceUid);
        Assert.NotEqual(uidB.Id, uidC.Id);
        ExecuteSql(databasePath, "UPDATE cards SET page_id = 3, name = 'MOVED-KT' WHERE device_uid = 'uid-a';");

        var movedSet = await repository.LoadAsync();
        var movedSummary = Assert.Single(movedSet.Groups, item => item.Id == group.Id);
        Assert.Equal(3, movedSummary.Total);
        Assert.Equal(movedSummary.Total,
            movedSummary.OnCount + movedSummary.OffCount + movedSummary.OfflineCount + movedSummary.UnknownCount);
        var moved = await repository.SaveItemAsync(DeviceItemEdit(
            group.Id, "3F", "3F C", "MOVED-KT", "moved", deviceUid: "uid-a"));
        Assert.Equal(promoted.Id, moved.Id);
        Assert.Equal("3F C", moved.SubAreaText);
        Assert.Equal("uid-a", moved.DeviceUid);

        var linkedMember = Assert.Single((await repository.LoadScheduleGroupsAsync(group.Id)).Single().Members);
        Assert.Equal("uid-a", linkedMember.DeviceUid);
        Assert.Equal("MOVED-KT", linkedMember.CardName);
        var nonDevice = await repository.SaveItemAsync(CreateAreaGroupItemEdit(
            group.Id, "sub_area", "1号", "1F", "1F A", string.Empty, "clear UID", uidC.Id, "uid-c"));
        Assert.Equal(string.Empty, nonDevice.DeviceUid);
    }

    [Fact]
    public async Task TargetCandidatesPartitionNonblankUidBeforeCanonicalDisplayFields()
    {
        var databasePath = CreateDatabase();
        ExecuteSql(databasePath, """
            UPDATE cards
            SET name = 'UID-OLD-KT', source_key = 'source-old', device_uid = 'uid-shared'
            WHERE id = 1;
            INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y)
            VALUES (10, '1号', 1, '1F MOVED', 10, 200, 100);
            INSERT INTO pages (id, sub_area_id, page_name, layout)
            VALUES
                (10, 10, 'moved-page', 'grid'),
                (11, 1, 'duplicate-label-page', 'grid');
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            VALUES
                (10, 10, 'UID-MOVED-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机', 'source-moved', 'uid-shared'),
                (11, 11, 'UID-RENAMED-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机', 'source-renamed', 'uid-shared'),
                (12, 11, 'SAME-LABEL-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'source-b', 'uid-b'),
                (13, 11, 'SAME-LABEL-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'source-c', 'uid-c');
            """);
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var candidates = (await repository.LoadTargetOptionsAsync("1号", "1F"))
            .Where(option => option.Type == "device")
            .ToArray();

        var shared = Assert.Single(candidates, option => option.DeviceUid == "uid-shared");
        Assert.Equal(3, shared.Count);
        Assert.Equal("UID-OLD-KT", shared.CardName);
        Assert.Equal("1F A", shared.SubAreaText);
        Assert.Equal("default", shared.PageName);
        Assert.Equal("source-old", shared.SourceKey);
        var sameLabel = candidates.Where(option => option.CardName == "SAME-LABEL-KT").ToArray();
        Assert.Equal(2, sameLabel.Length);
        Assert.Equal(["uid-b", "uid-c"], sameLabel.Select(option => option.DeviceUid).Order().ToArray());
        Assert.Equal([1, 2], sameLabel.Select(option => option.Occurrence).Order().ToArray());
    }

    [Fact]
    public async Task TargetOptionsExposeDevicesAfterFormerTwoThousandRowBoundary()
    {
        var databasePath = CreateDatabase();
        ExecuteSql(databasePath, """
            INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y)
            VALUES (20, '6号', 31, '31F Z', 20, 100, 100);
            INSERT INTO pages (id, sub_area_id, page_name, layout)
            VALUES (20, 20, '批量设备', 'grid');
            WITH RECURSIVE sequence(number) AS (
                VALUES(1)
                UNION ALL
                SELECT number + 1 FROM sequence WHERE number < 2480
            )
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            SELECT 100 + number, 20, printf('6-%04d-KT', number), 'OFF', '制冷', '25', '24', '中',
                   'green.png', '关机', printf('source-6-%04d', number), printf('uid-6-%04d', number)
            FROM sequence;
            """);
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var devices = (await repository.LoadTargetOptionsAsync(string.Empty, string.Empty))
            .Where(option => option.Type == "device")
            .ToArray();

        Assert.Equal(2485, devices.Length);
        Assert.Contains(devices, option => option.DeviceUid == "uid-6-2480" && option.CardName == "6-2480-KT");
    }

    [Fact]
    public async Task TargetOptionsRejectIncompleteDirectoryAboveSafetyLimit()
    {
        var databasePath = CreateDatabase();
        ExecuteSql(databasePath, """
            INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y)
            VALUES (20, '6号', 31, '31F Z', 20, 100, 100);
            INSERT INTO pages (id, sub_area_id, page_name, layout)
            VALUES (20, 20, '超限设备', 'grid');
            WITH RECURSIVE sequence(number) AS (
                VALUES(1)
                UNION ALL
                SELECT number + 1 FROM sequence WHERE number < 10001
            )
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            SELECT 100 + number, 20, printf('6-%05d-KT', number), 'OFF', '制冷', '25', '24', '中',
                   'green.png', '关机', printf('source-6-%05d', number), printf('uid-6-%05d', number)
            FROM sequence;
            """);
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.LoadTargetOptionsAsync(string.Empty, string.Empty));

        Assert.Contains("超过一次可安全加载", error.Message);
        Assert.Contains("选择楼栋或楼层", error.Message);
        Assert.Contains("未显示任何不完整目录", error.Message);
    }

    [Fact]
    public async Task SystemGroupStatsUnionClassifierAndManualMembersWithoutDoubleCounting()
    {
        var databasePath = CreateDatabase();
        ExecuteSql(databasePath, """
            UPDATE cards SET source_key = 'source-1', device_uid = 'uid-manual' WHERE id = 1;
            UPDATE cards SET source_key = 'source-2', device_uid = 'uid-nonpublic' WHERE id = 2;
            UPDATE cards SET source_key = 'source-3', device_uid = 'uid-offline' WHERE id = 3;
            UPDATE cards SET source_key = 'source-4', device_uid = 'uid-public' WHERE id = 4;
            UPDATE cards SET source_key = 'source-5', device_uid = 'uid-extra' WHERE id = 5;
            INSERT INTO pages (id, sub_area_id, page_name, layout) VALUES (4, 3, '二页', 'grid');
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            VALUES
                (6, 4, 'GQ-0301-KT', '-', '-', '', '', '', '', '', 'source-public-2', 'uid-public'),
                (7, 4, 'GQ-0301-KT', '-', '-', '', '', '', '', '', 'source-public-3', 'uid-public');
            INSERT INTO monitor_group_items
                (group_id, target_type, building, floor_label, floor_value, sub_area_text, card_name, device_uid, note)
            VALUES
                (1, 'device', '1号', '1F', 1, '1F A', '1-0101-KT', 'uid-manual', '人工扩展'),
                (1, 'device', '1号', '3F', 3, '3F C', 'GQ-0301-KT', 'uid-public', '与分类器重叠'),
                (2, 'device', '1号', '1F', 1, '1F A', '1-0101-KT', 'uid-manual', '与分类器重叠'),
                (2, 'device', '1号', '3F', 3, '3F C', 'GQ-0301-KT', 'uid-public', '人工扩展');
            """);
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var groups = (await repository.LoadAsync()).Groups;
        var publicGroup = Assert.Single(groups, item => item.SystemKey == "public");
        var nonPublicGroup = Assert.Single(groups, item => item.SystemKey == "non_public");

        Assert.Equal(2, publicGroup.Total);
        Assert.Equal(1, publicGroup.OnCount);
        Assert.Equal(1, publicGroup.UnknownCount);
        Assert.Equal(publicGroup.Total,
            publicGroup.OnCount + publicGroup.OffCount + publicGroup.OfflineCount + publicGroup.UnknownCount);
        Assert.Equal(5, nonPublicGroup.Total);
        Assert.Equal(1, nonPublicGroup.OnCount);
        Assert.Equal(1, nonPublicGroup.OffCount);
        Assert.Equal(1, nonPublicGroup.OfflineCount);
        Assert.Equal(2, nonPublicGroup.UnknownCount);
        Assert.Equal(nonPublicGroup.Total,
            nonPublicGroup.OnCount + nonPublicGroup.OffCount + nonPublicGroup.OfflineCount + nonPublicGroup.UnknownCount);
    }

    [Fact]
    public async Task RepositoryLoadDoesNotReseedOrRewriteEditablePresetDescriptions()
    {
        var databasePath = CreateDatabase();
        const string retiredPublic = "规则识别的公区；可维护人工成员，并在日期管理中设置计划。";
        const string retiredNonPublic = "规则识别的非公区；可维护人工成员，并在日期管理中设置计划。";
        const string custom = "  现场自定义说明；保留空格。  ";
        ExecuteSql(databasePath, $"""
            UPDATE monitor_groups SET description = '{retiredPublic}' WHERE system_key = 'public';
            UPDATE monitor_groups SET description = '{custom}' WHERE system_key = 'non_public';
            """);
        var repository = new SqliteAreaGroupRepository(() => databasePath);

        var first = (await repository.LoadAsync()).Groups;
        Assert.Equal(retiredPublic, Assert.Single(first, item => item.SystemKey == "public").Description);
        Assert.Equal(custom, Assert.Single(first, item => item.SystemKey == "non_public").Description);

        ExecuteSql(databasePath, $"""
            UPDATE monitor_groups SET description = '{custom}' WHERE system_key = 'public';
            UPDATE monitor_groups SET description = '{retiredNonPublic}' WHERE system_key = 'non_public';
            """);
        var second = (await repository.LoadAsync()).Groups;
        Assert.Equal(custom, Assert.Single(second, item => item.SystemKey == "public").Description);
        Assert.Equal(retiredNonPublic, Assert.Single(second, item => item.SystemKey == "non_public").Description);

        ExecuteSql(databasePath, "UPDATE monitor_groups SET description = '' WHERE system_key = 'public';");
        Assert.Equal(string.Empty,
            Assert.Single((await repository.LoadAsync()).Groups, item => item.SystemKey == "public").Description);
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
    public async Task DiscoveredFloorRemainsDisabledAcrossResync()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteAreaGroupRepository(() => databasePath);
        var discovered = Assert.Single(
            await repository.LoadFloorsAsync("1号"),
            floor => floor.FloorLabel == "1F" && floor.Source == "discovered");

        await repository.DeleteFloorAsync(discovered.Id);
        await repository.LoadAsync();
        var enabledOnly = await repository.LoadFloorsAsync("1号");
        var includeDisabled = await repository.LoadFloorsAsync("1号", includeDisabled: true);

        Assert.DoesNotContain(enabledOnly, floor => floor.Id == discovered.Id);
        var disabled = Assert.Single(includeDisabled, floor => floor.Id == discovered.Id);
        Assert.False(disabled.Enabled);
        Assert.Equal("discovered", disabled.Source);
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

    private static AreaGroupItemEdit DeviceItemEdit(
        long groupId,
        string floorLabel,
        string subAreaText,
        string cardName,
        string note,
        string deviceUid,
        long? id = null) =>
        CreateAreaGroupItemEdit(
            groupId, "device", "1号", floorLabel, subAreaText, cardName, note, id, deviceUid);

    private static AreaGroupItemEdit CreateAreaGroupItemEdit(
        long groupId,
        string targetType,
        string building,
        string floorLabel,
        string subAreaText,
        string cardName,
        string note,
        long? id,
        string deviceUid)
    {
        var constructor = Assert.Single(typeof(AreaGroupItemEdit).GetConstructors());
        var parameters = constructor.GetParameters();
        Assert.Contains(parameters, parameter =>
            string.Equals(parameter.Name, "DeviceUid", StringComparison.OrdinalIgnoreCase));
        var values = parameters.Select(parameter => parameter.Name?.ToLowerInvariant() switch
        {
            "groupid" => (object)groupId,
            "targettype" => targetType,
            "building" => building,
            "floorlabel" => floorLabel,
            "subareatext" => subAreaText,
            "cardname" => cardName,
            "note" => note,
            "id" => id,
            "deviceuid" => deviceUid,
            _ => throw new InvalidOperationException("Unexpected AreaGroupItemEdit parameter: " + parameter.Name),
        }).ToArray();
        return Assert.IsType<AreaGroupItemEdit>(constructor.Invoke(values));
    }

    private static string ReadStringProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<string>(property.GetValue(instance));
    }

    private static int ReadIntProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<int>(property.GetValue(instance));
    }

    private static void ExecuteSql(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
                source_key TEXT,
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
