using EmsScout.Application.Groups;
using EmsScout.Infrastructure.Migrations;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class AreaGroupReconciliationRepositoryTests
{
    [Fact]
    public async Task FloorAndKeywordRulesCreatePendingAddsWithoutChangingMembers()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var group = await CreateGroupAsync(database.Path, "规则组");
        var repository = new SqliteAreaGroupReconciliationRepository(() => database.Path);

        await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            GroupId: group.Id,
            RuleType: "floor",
            Building: "1号",
            FloorLabel: "1F",
            MatchValue: string.Empty,
            Note: "持续覆盖一层"));
        await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            GroupId: group.Id,
            RuleType: "name_keyword",
            Building: string.Empty,
            FloorLabel: string.Empty,
            MatchValue: "PUMP",
            Note: "设备名关键字"));

        await repository.ReconcileAsync(runId: null, ["1号"]);
        var snapshot = await repository.LoadAsync(group.Id);

        Assert.Equal(2, snapshot.Rules.Count);
        Assert.Empty(snapshot.Members);
        Assert.Empty(snapshot.Exceptions);
        Assert.Equal(2, snapshot.PendingChanges.Count);
        Assert.All(snapshot.PendingChanges, change => Assert.Equal("add", change.Action));
        Assert.Equal(["uid:UID-A", "uid:UID-B"],
            snapshot.PendingChanges.Select(change => change.IdentityKey).Order().ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "floor", "1号", string.Empty, string.Empty, "整栋不允许")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "name_keyword", string.Empty, string.Empty, "   ", "空关键字")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "building", "1号", string.Empty, string.Empty, "整栋不允许")));
    }

    [Fact]
    public async Task RejectAcceptAndExceptionReversalFollowTheConfirmedMembershipStateMachine()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var group = await CreateGroupAsync(database.Path, "状态机组");
        var repository = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        var rule = await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "floor", "1号", "1F", string.Empty, "持续覆盖一层"));

        await repository.ReconcileAsync(null, ["1号"]);
        var add = Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A");
        await repository.DecideChangeAsync(add.Id, AreaGroupChangeDecision.Reject, "长期不纳入");

        var blocked = await repository.LoadAsync(group.Id);
        Assert.DoesNotContain(blocked.Members, member => member.DeviceUid == "UID-A");
        var blockedException = Assert.Single(blocked.Exceptions,
            exception => exception.DeviceUid == "UID-A" && exception.ExceptionType == "blocked");
        Assert.Equal("长期不纳入", blockedException.Note);
        Assert.DoesNotContain(blocked.PendingChanges, change => change.DeviceUid == "UID-A");

        await repository.UpdateExceptionNoteAsync(blockedException.Id, "屏蔽备注已更新");
        Assert.Equal("屏蔽备注已更新",
            Assert.Single((await repository.LoadAsync(group.Id)).Exceptions,
                exception => exception.Id == blockedException.Id).Note);
        await repository.DeleteExceptionAsync(blockedException.Id);
        await repository.ReconcileAsync(null, ["1号"]);
        add = Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "add");
        await repository.DecideChangeAsync(add.Id, AreaGroupChangeDecision.Accept, "确认加入");

        var joined = await repository.LoadAsync(group.Id);
        var member = Assert.Single(joined.Members, item => item.DeviceUid == "UID-A");
        Assert.Equal("rule", member.MemberOrigin);
        Assert.Equal(rule.Id, member.RuleId);

        await repository.DeleteRuleAsync(rule.Id);
        await repository.ReconcileAsync(null, ["1号"]);
        var remove = Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "remove");
        await repository.DecideChangeAsync(remove.Id, AreaGroupChangeDecision.Reject, "长期保留");

        var retained = await repository.LoadAsync(group.Id);
        Assert.Contains(retained.Members, item => item.Id == member.Id);
        var retainedException = Assert.Single(retained.Exceptions,
            exception => exception.DeviceUid == "UID-A" && exception.ExceptionType == "retained");
        Assert.Equal("长期保留", retainedException.Note);

        await repository.DeleteExceptionAsync(retainedException.Id);
        await repository.ReconcileAsync(null, ["1号"]);
        remove = Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "remove");
        await repository.DecideChangeAsync(remove.Id, AreaGroupChangeDecision.Accept, "确认移除");
        Assert.DoesNotContain((await repository.LoadAsync(group.Id)).Members, item => item.DeviceUid == "UID-A");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DecideChangeAsync(remove.Id, AreaGroupChangeDecision.Accept, "重复处理"));
    }

    [Fact]
    public async Task RuleMemberCanBeRemovedIntoDurableBlockedListAndReenabledLater()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var group = await CreateGroupAsync(database.Path, "可屏蔽规则组");
        var repository = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        await repository.SaveRuleAsync(new AreaGroupRuleEdit(
            group.Id, "floor", "1号", "1F", string.Empty, "持续覆盖一层"));
        await repository.ReconcileAsync(null, ["1号"]);
        var add = Assert.Single((await repository.LoadAsync(group.Id)).PendingChanges,
            change => change.DeviceUid == "UID-A" && change.Action == "add");
        await repository.DecideChangeAsync(add.Id, AreaGroupChangeDecision.Accept, "先加入");
        var member = Assert.Single((await repository.LoadAsync(group.Id)).Members,
            item => item.DeviceUid == "UID-A");
        var deviceRepository = new SqliteDeviceReadRepository(database.Path);
        var included = await deviceRepository.SearchAsync(new(
            MonitorGroupIds: group.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Limit: 10));
        Assert.Single(included.Rows, row => row.Name == "AHU-01");

        await repository.BlockMemberAsync(member.Id, "设备长期不参与此规则组");
        await repository.ReconcileAsync(null, ["1号"]);

        var blocked = await repository.LoadAsync(group.Id);
        Assert.DoesNotContain(blocked.Members, item => item.DeviceUid == "UID-A");
        var exception = Assert.Single(blocked.Exceptions,
            item => item.DeviceUid == "UID-A" && item.ExceptionType == "blocked");
        Assert.Equal("设备长期不参与此规则组", exception.Note);
        Assert.DoesNotContain(blocked.PendingChanges, item => item.DeviceUid == "UID-A");
        Assert.Empty((await deviceRepository.SearchAsync(new(
            MonitorGroupIds: group.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Limit: 10))).Rows);

        await repository.DeleteExceptionAsync(exception.Id);
        await repository.ReconcileAsync(null, ["1号"]);
        Assert.Contains((await repository.LoadAsync(group.Id)).PendingChanges,
            item => item.DeviceUid == "UID-A" && item.Action == "add");
    }

    [Fact]
    public async Task ManualMemberSurvivesRuleDriftAndUidWinsAcrossLocationChanges()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var group = await CreateGroupAsync(database.Path, "人工成员组");
        var repository = new SqliteAreaGroupReconciliationRepository(() => database.Path);

        var original = await repository.AddManualMemberAsync(new AreaGroupManualMemberEdit(
            group.Id, "UID-A", "1号", "1F", 1, "1F A", "default", "AHU-01", "source-a", 1, "人工加入"));
        database.Execute("""
            UPDATE sub_areas SET floor = 2, text = '2F MOVED' WHERE id = 1;
            UPDATE cards SET name = 'AHU-RENAMED', source_key = 'source-a-new' WHERE device_uid = 'UID-A';
            """);
        var moved = await repository.AddManualMemberAsync(new AreaGroupManualMemberEdit(
            group.Id, "UID-A", "1号", "2F", 2, "2F MOVED", "default", "AHU-RENAMED", "source-a-new", 1, "位置已更新"));

        await repository.ReconcileAsync(null, ["1号"]);
        var snapshot = await repository.LoadAsync(group.Id);

        Assert.Equal(original.Id, moved.Id);
        var member = Assert.Single(snapshot.Members);
        Assert.Equal("manual", member.MemberOrigin);
        Assert.Equal("2F MOVED", member.SubAreaText);
        Assert.Equal("AHU-RENAMED", member.CardName);
        Assert.DoesNotContain(snapshot.PendingChanges, change => change.Action == "remove");
    }

    [Fact]
    public async Task LegacyIdentityFallbackAndRequestsRemainIsolatedByGroup()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        database.Execute("UPDATE cards SET device_uid = '', source_key = 'legacy-source' WHERE id = 1;");
        var first = await CreateGroupAsync(database.Path, "甲组");
        var second = await CreateGroupAsync(database.Path, "乙组");
        var repository = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        await repository.SaveRuleAsync(new AreaGroupRuleEdit(first.Id, "floor", "1号", "1F", string.Empty, "甲规则"));
        await repository.SaveRuleAsync(new AreaGroupRuleEdit(second.Id, "floor", "1号", "1F", string.Empty, "乙规则"));

        await repository.ReconcileAsync(null, ["1号"]);
        var firstAdd = Assert.Single((await repository.LoadAsync(first.Id)).PendingChanges,
            change => change.CardName == "AHU-01");
        var secondAdd = Assert.Single((await repository.LoadAsync(second.Id)).PendingChanges,
            change => change.CardName == "AHU-01");
        Assert.StartsWith("legacy:", firstAdd.IdentityKey);
        Assert.Equal(firstAdd.IdentityKey, secondAdd.IdentityKey);

        await repository.DecideChangeAsync(firstAdd.Id, AreaGroupChangeDecision.Accept, "甲组确认");
        Assert.Contains((await repository.LoadAsync(first.Id)).Members,
            member => member.IdentityKey == firstAdd.IdentityKey);
        Assert.Contains((await repository.LoadAsync(second.Id)).PendingChanges,
            change => change.Id == secondAdd.Id && change.Status == "pending");
    }

    [Fact]
    public async Task PresetClassificationRulesCreatePublicAndNonPublicCandidates()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var repository = new SqliteAreaGroupReconciliationRepository(() => database.Path);
        var groups = (await new SqliteAreaGroupRepository(() => database.Path).LoadAsync()).Groups;
        var publicGroup = Assert.Single(groups, group => group.SystemKey == "public");
        var nonPublicGroup = Assert.Single(groups, group => group.SystemKey == "non_public");

        await repository.ReconcileAsync(null, ["1号"]);

        Assert.Contains((await repository.LoadAsync(publicGroup.Id)).PendingChanges,
            change => change.CardName == "GQ-PUMP-01" && change.Action == "add");
        Assert.Contains((await repository.LoadAsync(nonPublicGroup.Id)).PendingChanges,
            change => change.CardName == "AHU-01" && change.Action == "add");
    }

    private static async Task<AreaGroupRecord> CreateGroupAsync(string databasePath, string name)
    {
        return await new SqliteAreaGroupRepository(() => databasePath).SaveGroupAsync(new AreaGroupEdit(
            Id: null,
            Name: name,
            AreaLabel: string.Empty,
            Description: string.Empty,
            Priority: "重点",
            Enabled: true));
    }

    private sealed class TemporaryDatabase(string path, string directory) : IAsyncDisposable
    {
        public string Path { get; } = path;

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ems-scout-area-reconcile-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "ac.db");
            await new SqliteSchemaMigrator().CreateNewAsync(path);
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWrite");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO buildings (building, sub_area_count, menu_clicked)
                VALUES ('1号', 2, 'test');
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
                    (2, 2, 'GQ-PUMP-01', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'source-b', 'UID-B');
                """;
            command.ExecuteNonQuery();
            return new TemporaryDatabase(path, directory);
        }

        public void Execute(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Mode=ReadWrite");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
