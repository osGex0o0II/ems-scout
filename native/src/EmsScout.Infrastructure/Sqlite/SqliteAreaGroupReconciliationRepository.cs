using System.Globalization;
using System.Text.RegularExpressions;
using EmsScout.Application.Groups;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed partial class SqliteAreaGroupReconciliationRepository(Func<string> databasePathResolver)
    : IAreaGroupReconciliationRepository
{
    private static readonly string[] SupportedRuleTypes =
        ["area_public", "area_non_public", "floor", "name_exact", "name_keyword"];

    public async Task<AreaGroupManagementSnapshot> LoadAsync(
        long? groupId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadOnly);
        var rules = await LoadRulesAsync(connection, groupId, cancellationToken).ConfigureAwait(false);
        var members = await LoadMembersAsync(connection, groupId, cancellationToken).ConfigureAwait(false);
        var exceptions = await LoadExceptionsAsync(connection, groupId, cancellationToken).ConfigureAwait(false);
        var pending = await LoadPendingChangesAsync(connection, groupId, cancellationToken).ConfigureAwait(false);
        return new AreaGroupManagementSnapshot(rules, members, exceptions, pending);
    }

    public async Task<AreaGroupRuleRecord> SaveRuleAsync(
        AreaGroupRuleEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var normalized = NormalizeRule(edit);
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await RequireGroupAsync(connection, normalized.GroupId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand();
        if (normalized.Id is null)
        {
            command.CommandText = """
                INSERT INTO area_group_rules
                    (group_id, rule_type, building, floor_label, floor_value, match_value,
                     enabled, note, created_at, updated_at)
                VALUES
                    ($group_id, $rule_type, $building, $floor_label, $floor_value, $match_value,
                     $enabled, $note, $now, $now)
                RETURNING id
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE area_group_rules
                SET rule_type = $rule_type,
                    building = $building,
                    floor_label = $floor_label,
                    floor_value = $floor_value,
                    match_value = $match_value,
                    enabled = $enabled,
                    note = $note,
                    updated_at = $now
                WHERE id = $id AND group_id = $group_id
                RETURNING id
                """;
            command.Parameters.AddWithValue("$id", normalized.Id.Value);
        }

        command.Parameters.AddWithValue("$group_id", normalized.GroupId);
        command.Parameters.AddWithValue("$rule_type", normalized.RuleType);
        command.Parameters.AddWithValue("$building", normalized.Building);
        command.Parameters.AddWithValue("$floor_label", normalized.FloorLabel);
        command.Parameters.AddWithValue("$floor_value", normalized.FloorValue is null ? DBNull.Value : normalized.FloorValue.Value);
        command.Parameters.AddWithValue("$match_value", normalized.MatchValue);
        command.Parameters.AddWithValue("$enabled", normalized.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$note", normalized.Note);
        command.Parameters.AddWithValue("$now", now);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            throw new InvalidOperationException("区域组规则不存在或不属于当前分组。");
        }

        var id = Convert.ToInt64(result, CultureInfo.InvariantCulture);
        return AssertSingle(await LoadRulesAsync(connection, normalized.GroupId, cancellationToken).ConfigureAwait(false), id);
    }

    public async Task DeleteRuleAsync(long ruleId, CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var sql in new[]
                 {
                     "UPDATE area_group_change_requests SET status = 'superseded', decided_at = $now WHERE rule_id = $id AND status = 'pending'",
                     "UPDATE area_group_change_requests SET rule_id = NULL WHERE rule_id = $id",
                     "UPDATE area_group_members SET rule_id = NULL, updated_at = $now WHERE rule_id = $id",
                     "DELETE FROM area_group_rules WHERE id = $id",
                 })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", ruleId);
            command.Parameters.AddWithValue("$now", now);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (sql.StartsWith("DELETE", StringComparison.Ordinal) && changed == 0)
            {
                throw new InvalidOperationException("区域组规则不存在。");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AreaGroupMemberRecord> AddManualMemberAsync(
        AreaGroupManualMemberEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var normalized = NormalizeMember(edit);
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await RequireGroupAsync(connection, normalized.GroupId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO area_group_members
                (group_id, rule_id, member_origin, identity_key, device_uid, building, floor_label,
                 floor_value, sub_area_text, page_name, card_name, source_key, occurrence,
                 note, created_at, updated_at)
            VALUES
                ($group_id, NULL, 'manual', $identity_key, $device_uid, $building, $floor_label,
                 $floor_value, $sub_area, $page_name, $card_name, $source_key, $occurrence,
                 $note, $now, $now)
            ON CONFLICT(group_id, identity_key) DO UPDATE SET
                rule_id = NULL,
                member_origin = 'manual',
                device_uid = excluded.device_uid,
                building = excluded.building,
                floor_label = excluded.floor_label,
                floor_value = excluded.floor_value,
                sub_area_text = excluded.sub_area_text,
                page_name = excluded.page_name,
                card_name = excluded.card_name,
                source_key = excluded.source_key,
                occurrence = excluded.occurrence,
                note = excluded.note,
                updated_at = excluded.updated_at
            RETURNING id
            """;
        AddDeviceParameters(command, normalized);
        command.Parameters.AddWithValue("$note", normalized.Note);
        command.Parameters.AddWithValue("$now", now);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        await using (var supersede = connection.CreateCommand())
        {
            supersede.Transaction = transaction;
            supersede.CommandText = """
                UPDATE area_group_change_requests
                SET status = 'superseded', decided_at = $now,
                    decision_note = CASE WHEN decision_note = '' THEN '已由手动成员操作覆盖' ELSE decision_note END
                WHERE group_id = $group_id AND identity_key = $identity_key AND status = 'pending'
                """;
            supersede.Parameters.AddWithValue("$group_id", normalized.GroupId);
            supersede.Parameters.AddWithValue("$identity_key", normalized.IdentityKey);
            supersede.Parameters.AddWithValue("$now", now);
            await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var clearException = connection.CreateCommand())
        {
            clearException.Transaction = transaction;
            clearException.CommandText = "DELETE FROM area_group_exceptions WHERE group_id = $group_id AND identity_key = $identity_key";
            clearException.Parameters.AddWithValue("$group_id", normalized.GroupId);
            clearException.Parameters.AddWithValue("$identity_key", normalized.IdentityKey);
            await clearException.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AssertSingle(await LoadMembersAsync(connection, normalized.GroupId, cancellationToken).ConfigureAwait(false), id);
    }

    public async Task DeleteManualMemberAsync(long memberId, CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM area_group_members WHERE id = $id AND member_origin IN ('manual', 'legacy')";
        command.Parameters.AddWithValue("$id", memberId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException("手动或历史成员不存在，或不能直接删除。");
        }
    }

    public async Task UpdateMemberNoteAsync(
        long memberId,
        string note,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE area_group_members
            SET note = $note, updated_at = $now
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", memberId);
        command.Parameters.AddWithValue("$note", (note ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException("区域组成员不存在。");
        }
    }

    public async Task BlockMemberAsync(
        long memberId,
        string note,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT group_id, member_origin, identity_key, device_uid, building, floor_label,
                   sub_area_text, page_name, card_name, source_key, occurrence, note
            FROM area_group_members
            WHERE id = $id
            """;
        select.Parameters.AddWithValue("$id", memberId);

        long groupId;
        string identityKey;
        string deviceUid;
        string building;
        string floorLabel;
        string subArea;
        string pageName;
        string cardName;
        string sourceKey;
        int occurrence;
        string existingNote;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("区域组成员不存在。");
            }
            if (!SqliteValueReader.ReadString(reader, "member_origin").Equals("rule", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("只有规则成员可以加入长期屏蔽名单。");
            }

            groupId = reader.GetInt64(reader.GetOrdinal("group_id"));
            identityKey = SqliteValueReader.ReadString(reader, "identity_key");
            deviceUid = SqliteValueReader.ReadString(reader, "device_uid");
            building = SqliteValueReader.ReadString(reader, "building");
            floorLabel = SqliteValueReader.ReadString(reader, "floor_label");
            subArea = SqliteValueReader.ReadString(reader, "sub_area_text");
            pageName = SqliteValueReader.ReadString(reader, "page_name");
            cardName = SqliteValueReader.ReadString(reader, "card_name");
            sourceKey = SqliteValueReader.ReadString(reader, "source_key");
            occurrence = SqliteValueReader.ReadInt32(reader, "occurrence");
            existingNote = SqliteValueReader.ReadString(reader, "note");
        }

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var normalizedNote = string.IsNullOrWhiteSpace(note) ? existingNote : note.Trim();
        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO area_group_exceptions
                    (group_id, exception_type, identity_key, device_uid, building, floor_label,
                     sub_area_text, page_name, card_name, source_key, occurrence, note, created_at, updated_at)
                VALUES
                    ($group_id, 'blocked', $identity_key, $device_uid, $building, $floor_label,
                     $sub_area, $page_name, $card_name, $source_key, $occurrence, $note, $now, $now)
                ON CONFLICT(group_id, identity_key) DO UPDATE SET
                    exception_type = 'blocked',
                    device_uid = excluded.device_uid,
                    building = excluded.building,
                    floor_label = excluded.floor_label,
                    sub_area_text = excluded.sub_area_text,
                    page_name = excluded.page_name,
                    card_name = excluded.card_name,
                    source_key = excluded.source_key,
                    occurrence = excluded.occurrence,
                    note = excluded.note,
                    updated_at = excluded.updated_at
                """;
            upsert.Parameters.AddWithValue("$group_id", groupId);
            upsert.Parameters.AddWithValue("$identity_key", identityKey);
            upsert.Parameters.AddWithValue("$device_uid", deviceUid);
            upsert.Parameters.AddWithValue("$building", building);
            upsert.Parameters.AddWithValue("$floor_label", floorLabel);
            upsert.Parameters.AddWithValue("$sub_area", subArea);
            upsert.Parameters.AddWithValue("$page_name", pageName);
            upsert.Parameters.AddWithValue("$card_name", cardName);
            upsert.Parameters.AddWithValue("$source_key", sourceKey);
            upsert.Parameters.AddWithValue("$occurrence", occurrence);
            upsert.Parameters.AddWithValue("$note", normalizedNote);
            upsert.Parameters.AddWithValue("$now", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM area_group_members WHERE id = $id AND member_origin = 'rule'";
            delete.Parameters.AddWithValue("$id", memberId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("规则成员已被其他操作处理。");
            }
        }

        await using (var resolve = connection.CreateCommand())
        {
            resolve.Transaction = transaction;
            resolve.CommandText = """
                UPDATE area_group_change_requests
                SET status = 'rejected', decision_note = $note, decided_at = $now
                WHERE group_id = $group_id AND identity_key = $identity_key AND status = 'pending'
                """;
            resolve.Parameters.AddWithValue("$group_id", groupId);
            resolve.Parameters.AddWithValue("$identity_key", identityKey);
            resolve.Parameters.AddWithValue("$note", normalizedNote);
            resolve.Parameters.AddWithValue("$now", now);
            await resolve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateExceptionNoteAsync(
        long exceptionId,
        string note,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE area_group_exceptions SET note = $note, updated_at = $now WHERE id = $id";
        command.Parameters.AddWithValue("$id", exceptionId);
        command.Parameters.AddWithValue("$note", (note ?? string.Empty).Trim());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException("区域组例外不存在。");
        }
    }

    public async Task DeleteExceptionAsync(long exceptionId, CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM area_group_exceptions WHERE id = $id";
        command.Parameters.AddWithValue("$id", exceptionId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new InvalidOperationException("区域组例外不存在。");
        }
    }

    public async Task DecideChangeAsync(
        long requestId,
        AreaGroupChangeDecision decision,
        string note,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var request = await LoadPendingChangeAsync(connection, transaction, requestId, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("待确认变更不存在或已经处理。");
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var normalizedNote = (note ?? string.Empty).Trim();

        if (!await IsPendingChangeStillValidAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false))
        {
            await SupersedeAsync(connection, transaction, request.Id, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("待确认变更已不符合当前规则或成员状态，请刷新审计后重试。");
        }

        if (request.Action == "add" && decision == AreaGroupChangeDecision.Accept)
        {
            await UpsertRuleMemberAsync(connection, transaction, request, normalizedNote, now, cancellationToken).ConfigureAwait(false);
        }
        else if (request.Action == "add")
        {
            await UpsertExceptionAsync(connection, transaction, request, "blocked", normalizedNote, now, cancellationToken).ConfigureAwait(false);
        }
        else if (request.Action == "remove" && decision == AreaGroupChangeDecision.Accept)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM area_group_members WHERE group_id = $group_id AND identity_key = $identity_key AND member_origin = 'rule'";
            delete.Parameters.AddWithValue("$group_id", request.GroupId);
            delete.Parameters.AddWithValue("$identity_key", request.IdentityKey);
            if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("待移除的规则成员已变化，请刷新审计后重试。");
            }
        }
        else
        {
            await UpsertExceptionAsync(connection, transaction, request, "retained", normalizedNote, now, cancellationToken).ConfigureAwait(false);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE area_group_change_requests
                SET status = $status, decision_note = $note, decided_at = $now
                WHERE id = $id AND status = 'pending'
                """;
            update.Parameters.AddWithValue("$status", decision == AreaGroupChangeDecision.Accept ? "accepted" : "rejected");
            update.Parameters.AddWithValue("$note", normalizedNote);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", requestId);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("待确认变更已被其他操作处理。");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReconcileAsync(
        long? runId,
        IReadOnlyCollection<string> importedBuildings,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ReconcileAsync(connection, transaction, runId, importedBuildings, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ReconcileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? runId,
        IReadOnlyCollection<string> importedBuildings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(importedBuildings);
        var buildings = importedBuildings
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (buildings.Length == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var buildingSet = buildings.ToHashSet(StringComparer.Ordinal);
        var currentBuildings = await LoadCurrentBuildingNamesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var allDevices = currentBuildings.Count == 0
            ? Array.Empty<DeviceSnapshot>()
            : await LoadCurrentDevicesAsync(connection, transaction, currentBuildings, cancellationToken).ConfigureAwait(false);
        var devices = allDevices.Where(device => buildingSet.Contains(device.Building)).ToArray();
        var rules = await LoadActiveRulesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var enabledGroupIds = await LoadEnabledGroupIdsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var members = await LoadAllMembersAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var exceptions = await LoadAllExceptionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var pending = await LoadAllPendingAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var membersByGroupIdentity = members.ToDictionary(item => (item.GroupId, item.IdentityKey));
        var exceptionsByGroupIdentity = exceptions.ToDictionary(item => (item.GroupId, item.IdentityKey));
        var pendingByKey = pending.ToDictionary(item => (item.GroupId, item.IdentityKey, item.Action));

        var rulesByGroup = rules.GroupBy(rule => rule.GroupId).ToDictionary(group => group.Key, group => group.ToArray());
        var matches = new Dictionary<(long GroupId, string IdentityKey), RuleMatch>();
        foreach (var (groupId, groupRules) in rulesByGroup)
        {
            foreach (var device in devices)
            {
                var matchingRules = groupRules.Where(rule => Matches(rule, device)).ToArray();
                if (matchingRules.Length == 0)
                {
                    continue;
                }

                matches[(groupId, device.IdentityKey)] = new RuleMatch(
                    matchingRules[0].Id,
                    string.Join("；", matchingRules.Select(RuleReason).Distinct(StringComparer.Ordinal)),
                    device);
            }
        }

        var currentDevicesByIdentity = allDevices.ToDictionary(device => device.IdentityKey, StringComparer.Ordinal);
        bool IsIdentityInScope(string identityKey, string storedBuilding) =>
            currentDevicesByIdentity.TryGetValue(identityKey, out var currentDevice)
                ? buildingSet.Contains(currentDevice.Building)
                : buildingSet.Contains(storedBuilding);

        foreach (var (key, match) in matches)
        {
            if (membersByGroupIdentity.TryGetValue(key, out var member) &&
                !string.IsNullOrWhiteSpace(member.DeviceUid))
            {
                await RefreshMemberLocationAsync(
                    connection, transaction, member, match, now, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var request in pending)
        {
            var key = (request.GroupId, request.IdentityKey);
            if (!IsIdentityInScope(request.IdentityKey, request.Building) &&
                enabledGroupIds.Contains(request.GroupId))
            {
                continue;
            }

            var stillValid = enabledGroupIds.Contains(request.GroupId) && (request.Action == "add"
                ? matches.ContainsKey(key) &&
                  !membersByGroupIdentity.ContainsKey(key) &&
                  (!exceptionsByGroupIdentity.TryGetValue(key, out var addException) || addException.ExceptionType != "blocked")
                : membersByGroupIdentity.TryGetValue(key, out var removeMember) &&
                  removeMember.MemberOrigin == "rule" &&
                  !matches.ContainsKey(key) &&
                  (!exceptionsByGroupIdentity.TryGetValue(key, out var removeException) || removeException.ExceptionType != "retained"));
            if (!stillValid)
            {
                await SupersedeAsync(connection, transaction, request.Id, now, cancellationToken).ConfigureAwait(false);
                pendingByKey.Remove((request.GroupId, request.IdentityKey, request.Action));
            }
            else if (request.Action == "add" && matches.TryGetValue(key, out var currentMatch))
            {
                await RefreshPendingAddAsync(
                    connection, transaction, request.Id, currentMatch, runId, now, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var (key, match) in matches)
        {
            if (membersByGroupIdentity.ContainsKey(key) ||
                (exceptionsByGroupIdentity.TryGetValue(key, out var exception) && exception.ExceptionType == "blocked") ||
                pendingByKey.ContainsKey((key.GroupId, key.IdentityKey, "add")))
            {
                continue;
            }

            await InsertPendingAsync(
                connection, transaction, key.GroupId, match.RuleId, runId, "add", match.Device,
                match.Reason, now, cancellationToken).ConfigureAwait(false);
        }

        foreach (var member in members)
        {
            var key = (member.GroupId, member.IdentityKey);
            if (member.MemberOrigin != "rule" ||
                !enabledGroupIds.Contains(member.GroupId) ||
                !IsIdentityInScope(member.IdentityKey, member.Building) ||
                matches.ContainsKey(key) ||
                (exceptionsByGroupIdentity.TryGetValue(key, out var exception) && exception.ExceptionType == "retained") ||
                pendingByKey.ContainsKey((key.GroupId, key.IdentityKey, "remove")))
            {
                continue;
            }

            await InsertPendingAsync(
                connection, transaction, member.GroupId, member.RuleId, runId, "remove",
                DeviceSnapshot.FromMember(member), "不再匹配任何启用规则", now, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<AreaGroupRuleRecord>> LoadRulesAsync(
        SqliteConnection connection,
        long? groupId,
        CancellationToken cancellationToken)
    {
        var rows = new List<AreaGroupRuleRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, group_id, rule_type, building, floor_label, floor_value,
                   match_value, enabled, note
            FROM area_group_rules
            WHERE ($group_id IS NULL OR group_id = $group_id)
            ORDER BY group_id, id
            """;
        command.Parameters.AddWithValue("$group_id", groupId is null ? DBNull.Value : groupId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AreaGroupRuleRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetDouble(5), reader.GetString(6),
                reader.GetInt64(7) != 0, reader.GetString(8)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<AreaGroupMemberRecord>> LoadMembersAsync(
        SqliteConnection connection,
        long? groupId,
        CancellationToken cancellationToken)
    {
        var rows = new List<AreaGroupMemberRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, group_id, rule_id, member_origin, identity_key, device_uid, building,
                   floor_label, floor_value, sub_area_text, page_name, card_name, source_key,
                   occurrence, note
            FROM area_group_members
            WHERE ($group_id IS NULL OR group_id = $group_id)
            ORDER BY group_id, building, floor_value, sub_area_text, card_name, id
            """;
        command.Parameters.AddWithValue("$group_id", groupId is null ? DBNull.Value : groupId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadMember(reader));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<AreaGroupExceptionRecord>> LoadExceptionsAsync(
        SqliteConnection connection,
        long? groupId,
        CancellationToken cancellationToken)
    {
        var rows = new List<AreaGroupExceptionRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, group_id, exception_type, identity_key, device_uid, building,
                   floor_label, sub_area_text, page_name, card_name, source_key, occurrence, note
            FROM area_group_exceptions
            WHERE ($group_id IS NULL OR group_id = $group_id)
            ORDER BY group_id, exception_type, building, floor_label, card_name, id
            """;
        command.Parameters.AddWithValue("$group_id", groupId is null ? DBNull.Value : groupId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AreaGroupExceptionRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetInt32(11), reader.GetString(12)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<AreaGroupChangeRequestRecord>> LoadPendingChangesAsync(
        SqliteConnection connection,
        long? groupId,
        CancellationToken cancellationToken)
    {
        var rows = new List<AreaGroupChangeRequestRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id, r.group_id, g.name AS group_name, r.rule_id, r.run_id, r.action, r.status,
                   r.identity_key, r.device_uid, r.building, r.floor_label, r.sub_area_text,
                   r.page_name, r.card_name, r.source_key, r.occurrence, r.match_reason,
                   r.decision_note, r.detected_at, COALESCE(r.decided_at, '') AS decided_at
            FROM area_group_change_requests r
            JOIN monitor_groups g ON g.id = r.group_id
            WHERE r.status = 'pending' AND ($group_id IS NULL OR r.group_id = $group_id)
            ORDER BY r.detected_at DESC, r.id DESC
            """;
        command.Parameters.AddWithValue("$group_id", groupId is null ? DBNull.Value : groupId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadChange(reader));
        }

        return rows;
    }

    private static async Task<PendingChange?> LoadPendingChangeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, group_id, rule_id, action, identity_key, device_uid, building, floor_label,
                   sub_area_text, page_name, card_name, source_key, occurrence
            FROM area_group_change_requests
            WHERE id = $id AND status = 'pending'
            """;
        command.Parameters.AddWithValue("$id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PendingChange(
                reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
                reader.GetString(11), reader.GetInt32(12))
            : null;
    }

    private static async Task<bool> IsPendingChangeStillValidAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PendingChange request,
        CancellationToken cancellationToken)
    {
        var enabledGroupIds = await LoadEnabledGroupIdsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (!enabledGroupIds.Contains(request.GroupId))
        {
            return false;
        }

        var members = await LoadAllMembersAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var member = members.SingleOrDefault(item =>
            item.GroupId == request.GroupId && item.IdentityKey == request.IdentityKey);
        var exceptions = await LoadAllExceptionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var exception = exceptions.SingleOrDefault(item =>
            item.GroupId == request.GroupId && item.IdentityKey == request.IdentityKey);
        var buildings = await LoadCurrentBuildingNamesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DeviceSnapshot> devices = buildings.Count == 0
            ? Array.Empty<DeviceSnapshot>()
            : await LoadCurrentDevicesAsync(connection, transaction, buildings, cancellationToken).ConfigureAwait(false);
        var device = devices.SingleOrDefault(item => item.IdentityKey == request.IdentityKey);
        var rules = await LoadActiveRulesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var hasCurrentMatch = device is not null && rules.Any(rule =>
            rule.GroupId == request.GroupId && Matches(rule, device));

        return request.Action == "add"
            ? hasCurrentMatch && member is null && exception?.ExceptionType != "blocked"
            : member?.MemberOrigin == "rule" && !hasCurrentMatch && exception?.ExceptionType != "retained";
    }

    private static async Task<IReadOnlyList<string>> LoadCurrentBuildingNamesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var buildings = new List<string>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT building FROM sub_areas WHERE TRIM(building) <> '' ORDER BY building";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            buildings.Add(reader.GetString(0));
        }

        return buildings;
    }

    private static async Task<IReadOnlyList<ActiveRule>> LoadActiveRulesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<ActiveRule>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.id, r.group_id, r.rule_type, r.building, r.floor_value, r.match_value
            FROM area_group_rules r
            JOIN monitor_groups g ON g.id = r.group_id
            WHERE r.enabled = 1 AND g.enabled = 1
            ORDER BY r.group_id, r.id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ActiveRule(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.GetString(5)));
        }

        return rows;
    }

    private static async Task<HashSet<long>> LoadEnabledGroupIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<long>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM monitor_groups WHERE enabled = 1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static async Task<IReadOnlyList<DeviceSnapshot>> LoadCurrentDevicesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken)
    {
        var observations = new List<DeviceObservation>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var placeholders = new List<string>(buildings.Count);
        for (var index = 0; index < buildings.Count; index++)
        {
            var name = "$building" + index.ToString(CultureInfo.InvariantCulture);
            placeholders.Add(name);
            command.Parameters.AddWithValue(name, buildings[index]);
        }

        command.CommandText = $"""
            SELECT c.id, s.building, s.floor, COALESCE(s.text, ''), COALESCE(p.page_name, ''),
                   COALESCE(p.layout, ''), COALESCE(c.name, ''), COALESCE(c.source_key, ''),
                   COALESCE(c.device_uid, ''), COALESCE(b.updated_at, '')
            FROM cards c
            JOIN pages p ON p.id = c.page_id
            JOIN sub_areas s ON s.id = p.sub_area_id
            LEFT JOIN buildings b ON b.building = s.building
            WHERE s.building IN ({string.Join(", ", placeholders)})
            ORDER BY c.id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            observations.Add(new DeviceObservation(
                reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9)));
        }

        var results = new List<DeviceSnapshot>();
        foreach (var uidGroup in observations
                     .Where(item => !string.IsNullOrWhiteSpace(item.DeviceUid))
                     .GroupBy(item => item.DeviceUid.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var canonical = uidGroup
                .OrderByDescending(item => item.BuildingUpdatedAt, StringComparer.Ordinal)
                .ThenByDescending(item => item.Id)
                .First();
            results.Add(DeviceSnapshot.From(canonical, "uid:" + canonical.DeviceUid.Trim().ToUpperInvariant(), 1));
        }

        foreach (var labelGroup in observations
                     .Where(item => string.IsNullOrWhiteSpace(item.DeviceUid))
                     .GroupBy(item => new { item.Building, item.Floor, item.SubAreaText, item.PageName, item.CardName }))
        {
            var occurrence = 0;
            foreach (var observation in labelGroup.OrderBy(item => item.SourceKey, StringComparer.Ordinal).ThenBy(item => item.Id))
            {
                occurrence++;
                var identity = LegacyIdentity(
                    observation.Building, FloorLabel(observation.Floor), observation.SubAreaText,
                    observation.PageName, observation.CardName, observation.SourceKey, occurrence);
                results.Add(DeviceSnapshot.From(observation, identity, occurrence));
            }
        }

        return results;
    }

    private static async Task<IReadOnlyList<MemberState>> LoadAllMembersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<MemberState>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, group_id, rule_id, member_origin, identity_key, device_uid, building,
                   floor_label, floor_value, sub_area_text, page_name, card_name, source_key,
                   occurrence, note
            FROM area_group_members
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new MemberState(
                reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetDouble(8), reader.GetString(9),
                reader.GetString(10), reader.GetString(11), reader.GetString(12), reader.GetInt32(13), reader.GetString(14)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<ExceptionState>> LoadAllExceptionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<ExceptionState>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, group_id, exception_type, identity_key FROM area_group_exceptions";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ExceptionState(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PendingState>> LoadAllPendingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<PendingState>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, group_id, identity_key, action, building
            FROM area_group_change_requests
            WHERE status = 'pending'
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PendingState(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return rows;
    }

    private static bool Matches(ActiveRule rule, DeviceSnapshot device)
    {
        return rule.RuleType switch
        {
            "area_public" => IsPublic(device),
            "area_non_public" => !IsPublic(device),
            "floor" => string.Equals(rule.Building, device.Building, StringComparison.Ordinal) &&
                       rule.FloorValue is not null && device.FloorValue is not null &&
                       Math.Abs(rule.FloorValue.Value - device.FloorValue.Value) < 0.001,
            "name_exact" =>
                (string.IsNullOrEmpty(rule.Building) || string.Equals(rule.Building, device.Building, StringComparison.Ordinal)) &&
                (rule.FloorValue is null || device.FloorValue is not null && Math.Abs(rule.FloorValue.Value - device.FloorValue.Value) < 0.001) &&
                string.Equals(device.CardName, rule.MatchValue, StringComparison.OrdinalIgnoreCase),
            "name_keyword" =>
                (string.IsNullOrEmpty(rule.Building) || string.Equals(rule.Building, device.Building, StringComparison.Ordinal)) &&
                (rule.FloorValue is null || device.FloorValue is not null && Math.Abs(rule.FloorValue.Value - device.FloorValue.Value) < 0.001) &&
                device.CardName.Contains(rule.MatchValue, StringComparison.OrdinalIgnoreCase),
            "legacy_sub_area" => string.Equals(rule.Building, device.Building, StringComparison.Ordinal) &&
                                 (rule.FloorValue is null || device.FloorValue is not null && Math.Abs(rule.FloorValue.Value - device.FloorValue.Value) < 0.001) &&
                                 string.Equals(rule.MatchValue, device.SubAreaText, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool IsPublic(DeviceSnapshot device)
    {
        if (string.Equals(device.Layout, "group", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (NonPublicQlName().IsMatch(device.CardName))
        {
            return false;
        }

        return new[] { "GQ", "WSJ", "DTT", "FDT", "XFDT", "CSJ", "FWJ", "ZBS", "ZSG", "MD", "RDJHJF" }
            .Any(marker => device.CardName.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("QL-[0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonPublicQlName();

    private static async Task InsertPendingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long groupId,
        long? ruleId,
        long? runId,
        string action,
        DeviceSnapshot device,
        string reason,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO area_group_change_requests
                (group_id, rule_id, run_id, action, status, identity_key, device_uid, building,
                 floor_label, sub_area_text, page_name, card_name, source_key, occurrence,
                 match_reason, decision_note, detected_at, decided_at)
            VALUES
                ($group_id, $rule_id, $run_id, $action, 'pending', $identity_key, $device_uid, $building,
                 $floor_label, $sub_area, $page_name, $card_name, $source_key, $occurrence,
                 $reason, '', $now, NULL)
            """;
        command.Parameters.AddWithValue("$group_id", groupId);
        command.Parameters.AddWithValue("$rule_id", ruleId is null ? DBNull.Value : ruleId.Value);
        command.Parameters.AddWithValue("$run_id", runId is null ? DBNull.Value : runId.Value);
        command.Parameters.AddWithValue("$action", action);
        AddDeviceParameters(command, device, includeGroupId: false);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SupersedeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE area_group_change_requests SET status = 'superseded', decided_at = $now WHERE id = $id AND status = 'pending'";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RefreshMemberLocationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MemberState member,
        RuleMatch match,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE area_group_members
            SET rule_id = CASE WHEN member_origin = 'rule' THEN $rule_id ELSE rule_id END,
                device_uid = $device_uid,
                building = $building,
                floor_label = $floor_label,
                floor_value = $floor_value,
                sub_area_text = $sub_area,
                page_name = $page_name,
                card_name = $card_name,
                source_key = $source_key,
                occurrence = $occurrence,
                updated_at = $now
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", member.Id);
        command.Parameters.AddWithValue("$rule_id", match.RuleId);
        AddDeviceParameters(command, match.Device, includeGroupId: false);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RefreshPendingAddAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long requestId,
        RuleMatch match,
        long? runId,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE area_group_change_requests
            SET rule_id = $rule_id,
                run_id = $run_id,
                device_uid = $device_uid,
                building = $building,
                floor_label = $floor_label,
                sub_area_text = $sub_area,
                page_name = $page_name,
                card_name = $card_name,
                source_key = $source_key,
                occurrence = $occurrence,
                match_reason = $reason,
                detected_at = $now
            WHERE id = $id AND status = 'pending' AND action = 'add'
            """;
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$rule_id", match.RuleId);
        command.Parameters.AddWithValue("$run_id", runId is null ? DBNull.Value : runId.Value);
        AddDeviceParameters(command, match.Device, includeGroupId: false);
        command.Parameters.AddWithValue("$reason", match.Reason);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertRuleMemberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PendingChange request,
        string note,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO area_group_members
                (group_id, rule_id, member_origin, identity_key, device_uid, building, floor_label,
                 floor_value, sub_area_text, page_name, card_name, source_key, occurrence,
                 note, created_at, updated_at)
            VALUES
                ($group_id, $rule_id, 'rule', $identity_key, $device_uid, $building, $floor_label,
                 $floor_value, $sub_area, $page_name, $card_name, $source_key, $occurrence,
                 $note, $now, $now)
            ON CONFLICT(group_id, identity_key) DO UPDATE SET
                rule_id = CASE WHEN area_group_members.member_origin = 'manual' THEN area_group_members.rule_id ELSE excluded.rule_id END,
                member_origin = CASE WHEN area_group_members.member_origin = 'manual' THEN 'manual' ELSE 'rule' END,
                note = CASE WHEN area_group_members.member_origin = 'manual' THEN area_group_members.note ELSE excluded.note END,
                updated_at = excluded.updated_at
            """;
        AddRequestDeviceParameters(command, request);
        command.Parameters.AddWithValue("$rule_id", request.RuleId is null ? DBNull.Value : request.RuleId.Value);
        command.Parameters.AddWithValue("$floor_value",
            string.IsNullOrWhiteSpace(request.FloorLabel) ? DBNull.Value : ParseFloor(request.FloorLabel));
        command.Parameters.AddWithValue("$note", note);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertExceptionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PendingChange request,
        string exceptionType,
        string note,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO area_group_exceptions
                (group_id, exception_type, identity_key, device_uid, building, floor_label,
                 sub_area_text, page_name, card_name, source_key, occurrence, note, created_at, updated_at)
            VALUES
                ($group_id, $exception_type, $identity_key, $device_uid, $building, $floor_label,
                 $sub_area, $page_name, $card_name, $source_key, $occurrence, $note, $now, $now)
            ON CONFLICT(group_id, identity_key) DO UPDATE SET
                exception_type = excluded.exception_type,
                device_uid = excluded.device_uid,
                building = excluded.building,
                floor_label = excluded.floor_label,
                sub_area_text = excluded.sub_area_text,
                page_name = excluded.page_name,
                card_name = excluded.card_name,
                source_key = excluded.source_key,
                occurrence = excluded.occurrence,
                note = excluded.note,
                updated_at = excluded.updated_at
            """;
        AddRequestDeviceParameters(command, request);
        command.Parameters.AddWithValue("$exception_type", exceptionType);
        command.Parameters.AddWithValue("$note", note);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RequireGroupAsync(
        SqliteConnection connection,
        long groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM monitor_groups WHERE id = $id";
        command.Parameters.AddWithValue("$id", groupId);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException("区域组不存在。");
        }
    }

    private static NormalizedRule NormalizeRule(AreaGroupRuleEdit edit)
    {
        var type = (edit.RuleType ?? string.Empty).Trim().ToLowerInvariant();
        if (!SupportedRuleTypes.Contains(type, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("不支持整栋或未知匹配规则。");
        }

        var building = (edit.Building ?? string.Empty).Trim();
        var floorLabel = (edit.FloorLabel ?? string.Empty).Trim().ToUpperInvariant();
        var matchValue = (edit.MatchValue ?? string.Empty).Trim();
        double? floorValue = null;
        if (type == "floor")
        {
            if (building.Length == 0 || floorLabel.Length == 0)
            {
                throw new InvalidOperationException("楼层规则必须选择楼栋和楼层；不支持整栋规则。");
            }

            floorValue = ParseFloor(floorLabel);
            matchValue = string.Empty;
        }
        else if (type is "name_exact" or "name_keyword")
        {
            if (matchValue.Length == 0)
            {
                throw new InvalidOperationException(type == "name_exact" ? "设备名不能为空。" : "设备名关键字不能为空。");
            }

            if (floorLabel.Length > 0)
            {
                if (building.Length == 0)
                {
                    throw new InvalidOperationException("限定楼层的关键字规则必须选择楼栋。");
                }

                floorValue = ParseFloor(floorLabel);
            }
        }
        else
        {
            building = string.Empty;
            floorLabel = string.Empty;
            matchValue = string.Empty;
        }

        return new NormalizedRule(edit.Id, edit.GroupId, type, building, floorLabel, floorValue,
            matchValue, edit.Enabled, (edit.Note ?? string.Empty).Trim());
    }

    private static DeviceSnapshot NormalizeMember(AreaGroupManualMemberEdit edit)
    {
        var building = RequireText(edit.Building, "楼栋");
        var cardName = RequireText(edit.CardName, "设备名");
        var deviceUid = (edit.DeviceUid ?? string.Empty).Trim();
        var occurrence = deviceUid.Length > 0 ? 1 : Math.Max(1, edit.Occurrence);
        var floorLabel = (edit.FloorLabel ?? string.Empty).Trim();
        var subArea = (edit.SubAreaText ?? string.Empty).Trim();
        var pageName = (edit.PageName ?? string.Empty).Trim();
        var sourceKey = (edit.SourceKey ?? string.Empty).Trim();
        var identityFloorLabel = edit.FloorValue is null ? floorLabel : FloorLabel(edit.FloorValue);
        var identity = deviceUid.Length > 0
            ? "uid:" + deviceUid.ToUpperInvariant()
            : LegacyIdentity(building, identityFloorLabel, subArea, pageName, cardName, sourceKey, occurrence);
        return new DeviceSnapshot(edit.GroupId, identity, deviceUid, building, floorLabel, edit.FloorValue,
            subArea, pageName, string.Empty, cardName, sourceKey, occurrence, (edit.Note ?? string.Empty).Trim());
    }

    private static void AddDeviceParameters(SqliteCommand command, DeviceSnapshot device, bool includeGroupId = true)
    {
        if (includeGroupId)
        {
            command.Parameters.AddWithValue("$group_id", device.GroupId);
        }
        command.Parameters.AddWithValue("$identity_key", device.IdentityKey);
        command.Parameters.AddWithValue("$device_uid", device.DeviceUid);
        command.Parameters.AddWithValue("$building", device.Building);
        command.Parameters.AddWithValue("$floor_label", device.FloorLabel);
        command.Parameters.AddWithValue("$floor_value", device.FloorValue is null ? DBNull.Value : device.FloorValue.Value);
        command.Parameters.AddWithValue("$sub_area", device.SubAreaText);
        command.Parameters.AddWithValue("$page_name", device.PageName);
        command.Parameters.AddWithValue("$card_name", device.CardName);
        command.Parameters.AddWithValue("$source_key", device.SourceKey);
        command.Parameters.AddWithValue("$occurrence", device.Occurrence);
    }

    private static void AddRequestDeviceParameters(SqliteCommand command, PendingChange request)
    {
        command.Parameters.AddWithValue("$group_id", request.GroupId);
        command.Parameters.AddWithValue("$identity_key", request.IdentityKey);
        command.Parameters.AddWithValue("$device_uid", request.DeviceUid);
        command.Parameters.AddWithValue("$building", request.Building);
        command.Parameters.AddWithValue("$floor_label", request.FloorLabel);
        command.Parameters.AddWithValue("$sub_area", request.SubAreaText);
        command.Parameters.AddWithValue("$page_name", request.PageName);
        command.Parameters.AddWithValue("$card_name", request.CardName);
        command.Parameters.AddWithValue("$source_key", request.SourceKey);
        command.Parameters.AddWithValue("$occurrence", request.Occurrence);
    }

    private static AreaGroupMemberRecord ReadMember(SqliteDataReader reader)
    {
        return new AreaGroupMemberRecord(
            reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetDouble(8), reader.GetString(9),
            reader.GetString(10), reader.GetString(11), reader.GetString(12), reader.GetInt32(13), reader.GetString(14));
    }

    private static AreaGroupChangeRequestRecord ReadChange(SqliteDataReader reader)
    {
        return new AreaGroupChangeRequestRecord(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11), reader.GetString(12),
            reader.GetString(13), reader.GetString(14), reader.GetInt32(15), reader.GetString(16), reader.GetString(17),
            reader.GetString(18), reader.GetString(19));
    }

    private static AreaGroupRuleRecord AssertSingle(IReadOnlyList<AreaGroupRuleRecord> rows, long id) =>
        rows.Single(item => item.Id == id);

    private static AreaGroupMemberRecord AssertSingle(IReadOnlyList<AreaGroupMemberRecord> rows, long id) =>
        rows.Single(item => item.Id == id);

    private static double ParseFloor(string floorLabel)
    {
        var value = floorLabel.Trim().ToUpperInvariant();
        if (value == "B1F" || value == "B1") return -1;
        if (value is "B2F" or "B2" or "BM") return -2;
        if (value.EndsWith('F')) value = value[..^1];
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var floor)) return floor;
        throw new InvalidOperationException("楼层格式无效。");
    }

    private static string FloorLabel(double? floor) => floor is null
        ? string.Empty
        : floor.Value < 0
            ? $"B{Math.Abs(floor.Value).ToString("0.###", CultureInfo.InvariantCulture)}F"
            : floor.Value.ToString("0.###", CultureInfo.InvariantCulture) + "F";

    private static string LegacyIdentity(
        string building,
        string floorLabel,
        string subArea,
        string pageName,
        string cardName,
        string sourceKey,
        int occurrence) =>
        "legacy:" + string.Join("|", new[]
        {
            building.Trim().ToUpperInvariant(), floorLabel.Trim().ToUpperInvariant(),
            subArea.Trim().ToUpperInvariant(), pageName.Trim().ToUpperInvariant(),
            cardName.Trim().ToUpperInvariant(), sourceKey.Trim().ToUpperInvariant(),
            Math.Max(1, occurrence).ToString(CultureInfo.InvariantCulture),
        });

    private static string RequireText(string? value, string label)
    {
        var result = (value ?? string.Empty).Trim();
        return result.Length == 0 ? throw new InvalidOperationException(label + "不能为空。") : result;
    }

    private static string RuleReason(ActiveRule rule) => rule.RuleType switch
    {
        "area_public" => "公区预设规则",
        "area_non_public" => "非公区预设规则",
        "floor" => $"楼层规则 {rule.Building} {FloorLabel(rule.FloorValue)}",
        "name_exact" => $"设备名等于 {rule.MatchValue}",
        "name_keyword" => $"设备名包含 {rule.MatchValue}",
        "legacy_sub_area" => $"兼容子区规则 {rule.MatchValue}",
        _ => rule.RuleType,
    };

    private sealed record NormalizedRule(
        long? Id,
        long GroupId,
        string RuleType,
        string Building,
        string FloorLabel,
        double? FloorValue,
        string MatchValue,
        bool Enabled,
        string Note);

    private sealed record DeviceObservation(
        long Id,
        string Building,
        double? Floor,
        string SubAreaText,
        string PageName,
        string Layout,
        string CardName,
        string SourceKey,
        string DeviceUid,
        string BuildingUpdatedAt);

    private sealed record DeviceSnapshot(
        long GroupId,
        string IdentityKey,
        string DeviceUid,
        string Building,
        string FloorLabel,
        double? FloorValue,
        string SubAreaText,
        string PageName,
        string Layout,
        string CardName,
        string SourceKey,
        int Occurrence,
        string Note)
    {
        public static DeviceSnapshot From(DeviceObservation item, string identityKey, int occurrence) =>
            new(0, identityKey, item.DeviceUid.Trim(), item.Building,
                SqliteAreaGroupReconciliationRepository.FloorLabel(item.Floor), item.Floor,
                item.SubAreaText, item.PageName, item.Layout, item.CardName, item.SourceKey, occurrence, string.Empty);

        public static DeviceSnapshot FromMember(MemberState item) =>
            new(item.GroupId, item.IdentityKey, item.DeviceUid, item.Building, item.FloorLabel, item.FloorValue,
                item.SubAreaText, item.PageName, string.Empty, item.CardName, item.SourceKey, item.Occurrence, item.Note);
    }

    private sealed record ActiveRule(long Id, long GroupId, string RuleType, string Building, double? FloorValue, string MatchValue);
    private sealed record RuleMatch(long RuleId, string Reason, DeviceSnapshot Device);
    private sealed record MemberState(
        long Id, long GroupId, long? RuleId, string MemberOrigin, string IdentityKey, string DeviceUid,
        string Building, string FloorLabel, double? FloorValue, string SubAreaText, string PageName,
        string CardName, string SourceKey, int Occurrence, string Note);
    private sealed record ExceptionState(long Id, long GroupId, string ExceptionType, string IdentityKey);
    private sealed record PendingState(long Id, long GroupId, string IdentityKey, string Action, string Building);
    private sealed record PendingChange(
        long Id, long GroupId, long? RuleId, string Action, string IdentityKey, string DeviceUid,
        string Building, string FloorLabel, string SubAreaText, string PageName, string CardName,
        string SourceKey, int Occurrence);
}
