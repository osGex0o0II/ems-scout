using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Infrastructure.Migrations;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class AreaGroupReconciliationEdgeCaseTests
{
    [Fact]
    public async Task ExactNameRuleMatchesOnlyTheExactDeviceAndDisabledGroupDoesNotProposeRemoval()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var groups = new SqliteAreaGroupRepository(() => database.Path);
        var group = await groups.SaveGroupAsync(new AreaGroupEdit(null, "精确设备组", "", "", "重点", true));
        var reconciliation = new SqliteAreaGroupReconciliationRepository(() => database.Path);

        await reconciliation.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "name_exact", "", "", "AHU-01", "精确设备名"));
        await reconciliation.ReconcileAsync(null, ["1号"]);
        var add = Assert.Single((await reconciliation.LoadAsync(group.Id)).PendingChanges);
        Assert.Equal("UID-A", add.DeviceUid);
        await reconciliation.DecideChangeAsync(add.Id, AreaGroupChangeDecision.Accept, "确认加入");

        await groups.SaveGroupAsync(new AreaGroupEdit(
            group.Id, group.Name, group.AreaLabel, group.Description, group.Priority, false));
        await reconciliation.ReconcileAsync(null, ["1号"]);

        var disabled = await reconciliation.LoadAsync(group.Id);
        Assert.Single(disabled.Members);
        Assert.Empty(disabled.PendingChanges);
    }

    [Fact]
    public async Task ManualAddSupersedesPendingChangesAndMemberNotesRemainEditable()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var group = await CreateGroupAsync(database.Path, "人工覆盖组");
        var repository = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "floor", "1号", "1F", "", "持续覆盖"));
        await repository.ReconcileAsync(null, ["1号"]);
        Assert.Contains((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "add");

        var member = await repository.AddManualMemberAsync(new AreaGroupManualMemberEdit(
            group.Id, "uid-a", "1号", "1F", 1, "1F A", "default", "AHU-01", "source-a", 1, "人工加入"));
        await repository.UpdateMemberNoteAsync(member.Id, "更新后的成员备注");

        var snapshot = await repository.LoadAsync(group.Id);
        Assert.DoesNotContain(snapshot.PendingChanges, change => change.IdentityKey == "uid:UID-A");
        member = Assert.Single(snapshot.Members);
        Assert.Equal("manual", member.MemberOrigin);
        Assert.Equal("更新后的成员备注", member.Note);
    }

    [Fact]
    public async Task LegacyMemberCanBeRemovedAndBmFloorRuleCanMatch()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var group = await CreateGroupAsync(database.Path, "兼容组");
        var repository = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        database.Execute($"""
            INSERT INTO area_group_members
                (group_id, rule_id, member_origin, identity_key, device_uid, building, floor_label,
                 floor_value, sub_area_text, page_name, card_name, source_key, occurrence,
                 note, created_at, updated_at)
            VALUES
                ({group.Id}, NULL, 'legacy', 'legacy:OLD', '', '1号', '1F', 1,
                 '1F A', '', 'OLD-KT', '', 1, '历史成员', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y)
            VALUES (3, '1号', -2, 'BM 设备区', 3, 100, 300);
            INSERT INTO pages (id, sub_area_id, page_name, layout)
            VALUES (3, 3, 'default', 'grid');
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            VALUES (3, 3, 'BM-AHU-01', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'source-bm', 'UID-BM');
            """);

        var legacy = Assert.Single((await repository.LoadAsync(group.Id)).Members);
        await repository.DeleteManualMemberAsync(legacy.Id);
        await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "floor", "1号", "BM", "", "持续覆盖 BM"));
        await repository.ReconcileAsync(null, ["1号"]);

        var snapshot = await repository.LoadAsync(group.Id);
        Assert.Empty(snapshot.Members);
        Assert.Contains(snapshot.PendingChanges, change => change.DeviceUid == "UID-BM");
    }

    [Fact]
    public async Task ManualBmMemberUsesSameLegacyIdentityAsReconciliation()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var group = await CreateGroupAsync(database.Path, "BM 无 UID 组");
        var repository = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        database.Execute("""
            INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y)
            VALUES (3, '1号', -2, 'BM 设备区', 3, 100, 300);
            INSERT INTO pages (id, sub_area_id, page_name, layout)
            VALUES (3, 3, 'default', 'grid');
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            VALUES (3, 3, 'BM-AHU-01', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'source-bm', '');
            """);

        var directoryDevices = (await new SqliteAreaGroupRepository(() => database.Path)
                .LoadTargetOptionsAsync("1号", "BM"))
            .Where(option => option.Type == "device")
            .ToArray();
        Assert.Single(directoryDevices);
        Assert.Equal("BM-AHU-01", directoryDevices[0].CardName);

        await repository.AddManualMemberAsync(new AreaGroupManualMemberEdit(
            group.Id, "", "1号", "BM", -2, "BM 设备区", "default", "BM-AHU-01", "source-bm", 1,
            "人工确认的 BM 设备"));
        await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "floor", "1号", "BM", "", "持续覆盖 BM"));
        await repository.ReconcileAsync(null, ["1号"]);

        var snapshot = await repository.LoadAsync(group.Id);
        var member = Assert.Single(snapshot.Members);
        Assert.Equal("manual", member.MemberOrigin);
        Assert.Equal("BM", member.FloorLabel);
        Assert.Equal("legacy:1号|B2F|BM 设备区|DEFAULT|BM-AHU-01|SOURCE-BM|1", member.IdentityKey);
        Assert.Empty(snapshot.PendingChanges);
    }

    [Fact]
    public async Task DirectoryAndFormalMembershipKeepNoUidDuplicatesDistinctWhileUidCanMoveBuildings()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        database.Execute("""
            UPDATE cards SET device_uid = '', name = 'DUP-KT', source_key = 'source-a' WHERE id = 1;
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            VALUES (3, 1, 'DUP-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'source-b', '');
            """);
        var groups = new SqliteAreaGroupRepository(() => database.Path);
        var options = (await groups.LoadTargetOptionsAsync("1号", "1F"))
            .Where(option => option.Type == "device" && option.CardName == "DUP-KT")
            .OrderBy(option => option.Occurrence)
            .ToArray();
        Assert.Equal(2, options.Length);
        Assert.Equal(["source-a", "source-b"], options.Select(option => option.SourceKey).ToArray());
        Assert.Equal([1, 2], options.Select(option => option.Occurrence).ToArray());

        var group = await CreateGroupAsync(database.Path, "精确旧身份组");
        var reconciliation = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        await reconciliation.AddManualMemberAsync(new AreaGroupManualMemberEdit(
            group.Id, "", "1号", "1F", 1, "1F A", "default", "DUP-KT",
            options[0].SourceKey, options[0].Occurrence, "只加入第一台"));
        var devices = new SqliteDeviceReadRepository(database.Path);
        var filtered = await devices.SearchAsync(new DeviceQuery(
            MonitorGroupIds: group.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Limit: 10));
        Assert.Single(filtered.Rows);
        Assert.Equal(1, filtered.Rows[0].Id);
        Assert.Equal(1, Assert.Single((await groups.LoadAsync()).Groups, item => item.Id == group.Id).Total);

        database.Execute("""
            UPDATE cards SET device_uid = 'uid-move', name = 'MOVE-KT', source_key = 'source-move' WHERE id = 2;
            INSERT INTO buildings (building, sub_area_count, menu_clicked) VALUES ('2号', 1, 'test');
            INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y) VALUES (4, '2号', 3, '3F C', 1, 100, 100);
            INSERT INTO pages (id, sub_area_id, page_name, layout) VALUES (4, 4, 'default', 'grid');
            UPDATE cards SET page_id = 4, device_uid = 'UID-MOVE' WHERE id = 2;
            """);
        await reconciliation.AddManualMemberAsync(new AreaGroupManualMemberEdit(
            group.Id, "uid-move", "1号", "2F", 2, "2F B", "default", "MOVE-KT", "source-move", 1, "UID 成员"));
        filtered = await devices.SearchAsync(new DeviceQuery(
            MonitorGroupIds: group.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Limit: 10));
        Assert.Contains(filtered.Rows, row => row.Id == 2 && row.Building == "2号");
    }

    [Fact]
    public async Task GroupStatsCountDistinctFormalNoUidMembersWithTheSameLocationAndName()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        database.Execute("""
            UPDATE cards SET device_uid = '', name = 'DUP-KT', source_key = 'source-a' WHERE id = 1;
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            VALUES (3, 1, 'DUP-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'source-b', '');
            """);
        var groups = new SqliteAreaGroupRepository(() => database.Path);
        var devices = (await groups.LoadTargetOptionsAsync("1号", "1F"))
            .Where(option => option.Type == "device" && option.CardName == "DUP-KT")
            .OrderBy(option => option.Occurrence)
            .ToArray();
        Assert.Equal(2, devices.Length);
        var group = await CreateGroupAsync(database.Path, "无 UID 重名统计组");
        var reconciliation = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        foreach (var device in devices)
        {
            await reconciliation.AddManualMemberAsync(new AreaGroupManualMemberEdit(
                group.Id, "", device.Building, device.FloorLabel, device.FloorValue, device.SubAreaText,
                device.PageName, device.CardName, device.SourceKey, device.Occurrence, "正式成员"));
        }

        var summary = Assert.Single((await groups.LoadAsync()).Groups, item => item.Id == group.Id);

        Assert.Equal(2, summary.ItemCount);
        Assert.Equal(2, summary.Total);
        Assert.Equal(summary.Total,
            summary.OnCount + summary.OffCount + summary.OfflineCount + summary.UnknownCount);
    }

    [Fact]
    public async Task UidMoveAcrossPartialBuildingImportSupersedesStaleRemoval()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var group = await CreateGroupAsync(database.Path, "跨楼移动组");
        var repository = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "name_keyword", "", "", "AHU", "跨楼持续匹配"));
        await repository.ReconcileAsync(null, ["1号"]);
        var add = Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "add");
        await repository.DecideChangeAsync(add.Id, AreaGroupChangeDecision.Accept, "确认原成员");

        database.Execute("UPDATE cards SET name = 'OTHER-01' WHERE device_uid = 'UID-A';");
        await repository.ReconcileAsync(null, ["1号"]);
        Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "remove");

        database.Execute("""
            INSERT INTO buildings (building, sub_area_count, menu_clicked) VALUES ('2号', 1, 'test');
            INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y)
            VALUES (4, '2号', 3, '3F C', 1, 100, 100);
            INSERT INTO pages (id, sub_area_id, page_name, layout) VALUES (4, 4, 'default', 'grid');
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            VALUES (4, 4, 'AHU-MOVED', 'ON', '制冷', '26', '24', '中', 'red.png', '开机', 'source-a-moved', 'UID-A');
            """);
        await repository.ReconcileAsync(null, ["2号"]);

        var snapshot = await repository.LoadAsync(group.Id);
        Assert.Single(snapshot.Members, member => member.DeviceUid == "UID-A");
        Assert.DoesNotContain(snapshot.PendingChanges, change => change.DeviceUid == "UID-A");

        database.Execute("UPDATE cards SET name = 'OTHER-MOVED' WHERE device_uid = 'UID-A';");
        await repository.ReconcileAsync(null, ["2号"]);
        Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "remove");
    }

    [Fact]
    public async Task MatchingUidMoveRefreshesMemberLocationAndOldBuildingDoesNotProposeRemoval()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var group = await CreateGroupAsync(database.Path, "跨楼成员定位组");
        var repository = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "name_keyword", "", "", "AHU", "跨楼持续匹配"));
        await repository.ReconcileAsync(null, ["1号"]);
        var add = Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "add");
        await repository.DecideChangeAsync(add.Id, AreaGroupChangeDecision.Accept, "确认原成员");

        database.Execute("""
            UPDATE buildings SET updated_at = '2026-07-15T00:00:00.0000000+00:00' WHERE building = '1号';
            UPDATE cards SET name = 'OTHER-OLD' WHERE device_uid = 'UID-A';
            INSERT INTO buildings (building, sub_area_count, menu_clicked, updated_at)
            VALUES ('2号', 1, 'test', '2026-07-16T00:00:00.0000000+00:00');
            INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y)
            VALUES (4, '2号', 3, '3F C', 1, 100, 100);
            INSERT INTO pages (id, sub_area_id, page_name, layout) VALUES (4, 4, 'default', 'grid');
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            VALUES (4, 4, 'AHU-MOVED', 'ON', '制冷', '26', '24', '中', 'red.png', '开机', 'source-moved', 'UID-A');
            """);

        await repository.ReconcileAsync(null, ["2号"]);

        var moved = Assert.Single((await repository.LoadAsync(group.Id)).Members,
            member => member.DeviceUid == "UID-A");
        Assert.Equal("2号", moved.Building);
        Assert.Equal("3F", moved.FloorLabel);
        Assert.Equal(3, moved.FloorValue);
        Assert.Equal("3F C", moved.SubAreaText);
        Assert.Equal("AHU-MOVED", moved.CardName);

        await repository.ReconcileAsync(null, ["1号"]);

        var afterOldBuildingImport = await repository.LoadAsync(group.Id);
        Assert.Single(afterOldBuildingImport.Members, member => member.DeviceUid == "UID-A");
        Assert.DoesNotContain(afterOldBuildingImport.PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "remove");
    }

    [Fact]
    public async Task AuditDecisionRejectsStaleAddAndStaleRemove()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var group = await CreateGroupAsync(database.Path, "决策复核组");
        var repository = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        var rule = await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "name_exact", "", "", "AHU-01", "精确匹配"));
        await repository.ReconcileAsync(null, ["1号"]);
        var staleAdd = Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "add");
        await repository.DeleteRuleAsync(rule.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DecideChangeAsync(staleAdd.Id, AreaGroupChangeDecision.Accept, "不应加入"));
        var afterStaleAdd = await repository.LoadAsync(group.Id);
        Assert.DoesNotContain(afterStaleAdd.Members, member => member.DeviceUid == "UID-A");
        Assert.DoesNotContain(afterStaleAdd.PendingChanges, change => change.Id == staleAdd.Id);

        rule = await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "name_exact", "", "", "AHU-01", "重新匹配"));
        await repository.ReconcileAsync(null, ["1号"]);
        var add = Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "add");
        await repository.DecideChangeAsync(add.Id, AreaGroupChangeDecision.Accept, "确认加入");
        await repository.DeleteRuleAsync(rule.Id);
        await repository.ReconcileAsync(null, ["1号"]);
        var staleRemove = Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "remove");
        await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "name_exact", "", "", "AHU-01", "再次匹配"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DecideChangeAsync(staleRemove.Id, AreaGroupChangeDecision.Accept, "不应移除"));
        var afterStaleRemove = await repository.LoadAsync(group.Id);
        Assert.Single(afterStaleRemove.Members, member => member.DeviceUid == "UID-A");
        Assert.DoesNotContain(afterStaleRemove.PendingChanges, change => change.Id == staleRemove.Id);
    }

    [Fact]
    public async Task PendingAddFollowsUidAcrossBuildingsBeforeAcceptance()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var group = await CreateGroupAsync(database.Path, "跨楼待加入组");
        var repository = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        var rule = await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "name_keyword", "", "", "AHU", "全局设备名规则"));
        await repository.ReconcileAsync(null, ["1号"]);
        var pending = Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "add");
        Assert.Equal("1号", pending.Building);

        database.Execute("""
            INSERT INTO buildings (building, sub_area_count, menu_clicked) VALUES ('2号', 1, 'test');
            INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y)
            VALUES (4, '2号', 3, '3F C', 1, 100, 100);
            INSERT INTO pages (id, sub_area_id, page_name, layout) VALUES (4, 4, 'default', 'grid');
            UPDATE cards SET page_id = 4, name = 'AHU-MOVED' WHERE device_uid = 'UID-A';
            """);
        await repository.ReconcileAsync(null, ["2号"]);
        pending = Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "add");
        Assert.Equal("2号", pending.Building);
        Assert.Equal("3F", pending.FloorLabel);

        await repository.DecideChangeAsync(pending.Id, AreaGroupChangeDecision.Accept, "确认移动后的设备");
        var member = Assert.Single((await repository.LoadAsync(group.Id)).Members,
            item => item.DeviceUid == "UID-A");
        Assert.Equal("2号", member.Building);
        Assert.Equal(3, member.FloorValue);

        await repository.DeleteRuleAsync(rule.Id);
        await repository.ReconcileAsync(null, ["2号"]);
        Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "remove");
    }

    [Fact]
    public async Task AcceptedNoUidRuleMemberKeepsFloorScopeInFormalFiltering()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        database.Execute("""
            UPDATE sub_areas SET text = 'SAME AREA' WHERE id IN (1, 2);
            UPDATE cards SET device_uid = '', source_key = 'same-source', name = 'NOUID-AHU' WHERE id = 1;
            UPDATE cards SET device_uid = '', source_key = 'same-source', name = 'NOUID-AHU' WHERE id = 2;
            """);
        var group = await CreateGroupAsync(database.Path, "无 UID 楼层组");
        var reconciliation = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        await reconciliation.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "floor", "1号", "1F", "", "只匹配一层"));
        await reconciliation.ReconcileAsync(null, ["1号"]);
        var add = Assert.Single((await reconciliation.LoadAsync(group.Id)).PendingChanges);
        await reconciliation.DecideChangeAsync(add.Id, AreaGroupChangeDecision.Accept, "确认一层设备");

        var member = Assert.Single((await reconciliation.LoadAsync(group.Id)).Members);
        Assert.Equal(1, member.FloorValue);
        var filtered = await new SqliteDeviceReadRepository(database.Path).SearchAsync(new DeviceQuery(
            MonitorGroupIds: group.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), Limit: 10));
        Assert.Single(filtered.Rows);
        Assert.Equal(1, filtered.Rows[0].Floor);
    }

    [Fact]
    public async Task MixedUidAndNoUidCardsShareTheLegacyOccurrenceConvention()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        database.Execute("""
            UPDATE cards SET name = 'MIXED-AHU', source_key = 'a-uid', device_uid = 'UID-A' WHERE id = 1;
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            VALUES (3, 1, 'MIXED-AHU', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'b-legacy', '');
            """);
        var groups = new SqliteAreaGroupRepository(() => database.Path);
        var noUid = Assert.Single((await groups.LoadTargetOptionsAsync("1号", "1F")),
            option => option.Type == "device" && option.CardName == "MIXED-AHU" && option.DeviceUid == "");
        Assert.Equal(1, noUid.Occurrence);
        var group = await CreateGroupAsync(database.Path, "混合身份组");
        var reconciliation = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        await reconciliation.AddManualMemberAsync(new AreaGroupManualMemberEdit(
            group.Id, "", noUid.Building, noUid.FloorLabel, noUid.FloorValue, noUid.SubAreaText,
            noUid.PageName, noUid.CardName, noUid.SourceKey, noUid.Occurrence, "人工加入无 UID 设备"));
        await reconciliation.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "floor", "1号", "1F", "", "持续覆盖一层"));

        await reconciliation.ReconcileAsync(null, ["1号"]);
        var snapshot = await reconciliation.LoadAsync(group.Id);
        Assert.DoesNotContain(snapshot.PendingChanges,
            change => change.DeviceUid == "" && change.CardName == "MIXED-AHU");
        Assert.Contains(snapshot.PendingChanges,
            change => change.DeviceUid == "UID-A" && change.CardName == "MIXED-AHU");
    }

    [Fact]
    public async Task ManualUidMemberAlwaysNormalizesOccurrenceToOne()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        database.Execute("""
            UPDATE cards SET name = 'SAME-UID-LABEL-KT', source_key = 'source-a', device_uid = 'UID-A' WHERE id = 1;
            UPDATE cards SET page_id = 1, name = 'SAME-UID-LABEL-KT', source_key = 'source-b', device_uid = 'UID-B' WHERE id = 2;
            """);
        var directory = (await new SqliteAreaGroupRepository(() => database.Path)
                .LoadTargetOptionsAsync("1号", "1F"))
            .Where(option => option.Type == "device" && option.CardName == "SAME-UID-LABEL-KT")
            .OrderBy(option => option.Occurrence)
            .ToArray();
        Assert.Equal([1, 2], directory.Select(option => option.Occurrence).ToArray());
        var selected = directory[1];
        var group = await CreateGroupAsync(database.Path, "UID occurrence 归一化组");

        var member = await new SqliteAreaGroupReconciliationRepository(() => database.Path)
            .AddManualMemberAsync(new AreaGroupManualMemberEdit(
                group.Id, selected.DeviceUid, selected.Building, selected.FloorLabel, selected.FloorValue,
                selected.SubAreaText, selected.PageName, selected.CardName, selected.SourceKey,
                selected.Occurrence, "从目录加入"));

        Assert.Equal("uid:UID-B", member.IdentityKey);
        Assert.Equal(1, member.Occurrence);
    }

    [Fact]
    public async Task GroupStatsCollapseUidObservationsCaseInsensitively()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        database.Execute("""
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            VALUES
                (3, 1, 'AHU-01-MOVED', 'ON', '制冷', '26', '24', '中', 'red.png', '开机', 'source-a-moved', 'uid-a');
            """);
        var group = await CreateGroupAsync(database.Path, "UID 大小写去重组");
        await new SqliteAreaGroupReconciliationRepository(() => database.Path)
            .AddManualMemberAsync(new AreaGroupManualMemberEdit(
                group.Id, "UID-A", "1号", "1F", 1, "1F A", "default", "AHU-01",
                "source-a", 1, "同一 UID 的正式成员"));

        var summary = Assert.Single(
            (await new SqliteAreaGroupRepository(() => database.Path).LoadAsync()).Groups,
            item => item.Id == group.Id);

        Assert.Equal(1, summary.Total);
    }

    [Fact]
    public async Task FormalNoUidMemberFiltersOnlyNoUidOccurrenceInCurrentAndHistoricalSnapshots()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        database.Execute("""
            UPDATE cards
            SET name = 'MIXED-FILTER-AHU', source_key = 'a-uid', device_uid = 'UID-MIXED'
            WHERE id = 1;
            INSERT INTO cards
                (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
            VALUES
                (3, 1, 'MIXED-FILTER-AHU', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'b-no-uid', '');

            INSERT INTO collection_runs
                (id, run_key, completed_at, imported_at, status, scope, buildings, card_count)
            VALUES
                (99, 'mixed-filter-run', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 'completed', 'partial', '["1号"]', 2);
            INSERT INTO run_sub_areas
                (id, run_id, source_sub_area_id, building, sub_idx, floor, floor_label, text, x, y)
            VALUES
                (91, 99, 1, '1号', 1, 1, '1F', '1F A', 100, 100);
            INSERT INTO run_pages
                (id, run_id, run_sub_area_id, source_page_id, page_name, layout)
            VALUES
                (91, 99, 91, 1, 'default', 'grid');
            INSERT INTO run_cards
                (id, run_id, run_page_id, source_card_id, name, switch, mode, indoor, set_temp,
                 fan, indicator, comm, source_key, device_uid)
            VALUES
                (91, 99, 91, 1, 'MIXED-FILTER-AHU', 'ON', '制冷', '26', '24',
                 '中', 'red.png', '开机', 'a-uid', 'UID-MIXED'),
                (92, 99, 91, 3, 'MIXED-FILTER-AHU', 'OFF', '制冷', '25', '24',
                 '中', 'green.png', '关机', 'b-no-uid', '');
            """);
        var group = await CreateGroupAsync(database.Path, "混合身份筛选组");
        await new SqliteAreaGroupReconciliationRepository(() => database.Path).AddManualMemberAsync(
            new AreaGroupManualMemberEdit(
                group.Id, "", "1号", "1F", 1, "1F A", "default", "MIXED-FILTER-AHU", "", 1,
                "只加入无 UID 的第一台设备"));
        var repository = new SqliteDeviceReadRepository(database.Path);
        var query = new DeviceQuery(
            MonitorGroupIds: group.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Limit: 10);

        var current = await repository.SearchAsync(query);
        var historical = await repository.SearchAsync(query with { RunId = 99 });

        Assert.Equal(3, Assert.Single(current.Rows).Id);
        Assert.Equal(92, Assert.Single(historical.Rows).Id);
    }

    [Fact]
    public async Task DeletingReferencedCollectionRunPreservesAuditRequestAndClearsRunLink()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var group = await CreateGroupAsync(database.Path, "审计历史组");
        database.Execute($"""
            INSERT INTO collection_runs
                (id, run_key, completed_at, imported_at, status, scope, buildings, card_count)
            VALUES (1, 'run-1', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 'completed', 'partial', '["1号"]', 0);
            INSERT INTO area_group_change_requests
                (group_id, rule_id, run_id, action, status, identity_key, device_uid, building,
                 floor_label, sub_area_text, page_name, card_name, source_key, occurrence,
                 match_reason, decision_note, detected_at, decided_at)
            VALUES ({group.Id}, NULL, 1, 'add', 'accepted', 'uid:UID-A', 'UID-A', '1号',
                    '1F', '1F A', 'default', 'AHU-01', 'source-a', 1,
                    '历史确认', '已确认', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """);

        await new SqliteCollectionRunRepository(() => database.Path).DeleteAsync(1);

        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM area_group_change_requests"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM area_group_change_requests WHERE run_id IS NOT NULL"));
    }

    private static async Task<AreaGroupRecord> CreateGroupAsync(string databasePath, string name) =>
        await new SqliteAreaGroupRepository(() => databasePath).SaveGroupAsync(new AreaGroupEdit(
            null, name, "", "", "重点", true));

    private sealed class TemporaryDatabase(string path, string directory) : IAsyncDisposable
    {
        public string Path { get; } = path;

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ems-scout-area-edge-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "ac.db");
            await new SqliteSchemaMigrator().CreateNewAsync(path);
            var database = new TemporaryDatabase(path, directory);
            database.Execute("""
                INSERT INTO buildings (building, sub_area_count, menu_clicked) VALUES ('1号', 2, 'test');
                INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y) VALUES
                    (1, '1号', 1, '1F A', 1, 100, 100),
                    (2, '1号', 2, '2F B', 2, 100, 200);
                INSERT INTO pages (id, sub_area_id, page_name, layout) VALUES
                    (1, 1, 'default', 'grid'),
                    (2, 2, 'default', 'grid');
                INSERT INTO cards
                    (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
                VALUES
                    (1, 1, 'AHU-01', 'ON', '制冷', '26', '24', '中', 'red.png', '开机', 'source-a', 'UID-A'),
                    (2, 2, 'PUMP-02', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'source-b', 'UID-B');
                """);
            return database;
        }

        public void Execute(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Mode=ReadWrite");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public long Scalar(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
