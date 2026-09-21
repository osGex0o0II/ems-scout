using EmsScout.Application;
using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Domain;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteAreaGroupRepository(Func<string> databasePathResolver) : IAreaGroupRepository
{
    private static readonly string[] Buildings = ["1号", "2号", "3号", "4号", "5号", "6号"];

    public async Task<AreaGroupSet> LoadAsync(CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var groups = await LoadGroupsAsync(connection, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AreaGroupItemRecord> items = [];
        var rules = await LoadRulesAsync(connection, null, cancellationToken).ConfigureAwait(false);
        return new AreaGroupSet(groups, items, rules);
    }

    public async Task<IReadOnlyList<AreaGroupTargetOption>> LoadTargetOptionsAsync(
        string building,
        string floorLabel,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: true);
        var floorValue = string.IsNullOrWhiteSpace(floorLabel) ? null : ParseFloorValue(floorLabel);
        var subAreas = new List<AreaGroupTargetOption>();
        var devices = new List<AreaGroupTargetOption>();

        await using (var command = connection.CreateCommand())
        {
            var clauses = new List<string>();
            if (!string.IsNullOrWhiteSpace(building))
            {
                clauses.Add("s.building = $building");
                command.Parameters.AddWithValue("$building", building.Trim());
            }

            if (floorValue is not null)
            {
                clauses.Add("ABS(COALESCE(s.floor, -999999) - $floor_value) < 0.001");
                command.Parameters.AddWithValue("$floor_value", floorValue.Value);
            }

            command.CommandText = $"""
                SELECT s.building, s.floor, s.text AS sub_area_text, COUNT(c.id) AS count
                FROM sub_areas s
                JOIN pages p ON p.sub_area_id = s.id
                JOIN cards c ON c.page_id = p.id
                {(clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses))}
                GROUP BY s.building, s.floor, s.text
                ORDER BY s.building, s.floor, s.text
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var optionFloor = ReadNullableDouble(reader, "floor");
                subAreas.Add(new AreaGroupTargetOption(
                    Type: "sub_area",
                    Building: ReadString(reader, "building"),
                    FloorLabel: FloorLabelFromValue(optionFloor),
                    FloorValue: optionFloor,
                    SubAreaText: ReadString(reader, "sub_area_text"),
                    CardName: string.Empty,
                    Count: ReadInt32(reader, "count")));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            var clauses = new List<string>();
            if (!string.IsNullOrWhiteSpace(building))
            {
                clauses.Add("s.building = $building");
                command.Parameters.AddWithValue("$building", building.Trim());
            }

            if (floorValue is not null)
            {
                clauses.Add("ABS(COALESCE(s.floor, -999999) - $floor_value) < 0.001");
                command.Parameters.AddWithValue("$floor_value", floorValue.Value);
            }

            command.CommandText = $"""
                SELECT s.building, s.floor, s.text AS sub_area_text, c.name AS card_name, COUNT(*) AS count
                FROM sub_areas s
                JOIN pages p ON p.sub_area_id = s.id
                JOIN cards c ON c.page_id = p.id
                {(clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses))}
                GROUP BY s.building, s.floor, s.text, c.name
                ORDER BY s.building, s.floor, s.text, c.name
                LIMIT 2000
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var optionFloor = ReadNullableDouble(reader, "floor");
                devices.Add(new AreaGroupTargetOption(
                    Type: "device",
                    Building: ReadString(reader, "building"),
                    FloorLabel: FloorLabelFromValue(optionFloor),
                    FloorValue: optionFloor,
                    SubAreaText: ReadString(reader, "sub_area_text"),
                    CardName: ReadString(reader, "card_name"),
                    Count: ReadInt32(reader, "count")));
            }
        }

        return subAreas.Concat(devices).ToList();
    }

    public async Task<IReadOnlyList<FloorCatalogRecord>> LoadFloorsAsync(
        string building,
        bool includeDisabled = false,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await SyncFloorCatalogFromCurrentAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(building))
        {
            clauses.Add("building = $building");
            command.Parameters.AddWithValue("$building", building.Trim());
        }

        if (!includeDisabled)
        {
            clauses.Add("enabled = 1");
        }

        command.CommandText = $"""
            SELECT id, building, floor_label, floor_value, source, enabled, note
            FROM floor_catalog
            {(clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses))}
            ORDER BY building, floor_value, floor_label
            """;

        var rows = new List<FloorCatalogRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new FloorCatalogRecord(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                Building: ReadString(reader, "building"),
                FloorLabel: ReadString(reader, "floor_label"),
                FloorValue: ReadDouble(reader, "floor_value"),
                Source: ReadString(reader, "source"),
                Enabled: ReadInt32(reader, "enabled") != 0,
                Note: ReadString(reader, "note")));
        }

        return rows;
    }

    public async Task<FloorCatalogRecord> SaveFloorAsync(
        FloorCatalogEdit edit,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var building = Require(edit.Building, "building");
        if (!Buildings.Contains(building, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid building: " + building);
        }

        var floorLabel = NormalizeFloorLabel(Require(edit.FloorLabel, "floor"));
        var floorValue = ParseFloorValue(floorLabel) ?? throw new ArgumentException("Invalid floor: " + floorLabel);
        var now = StoredTimestamp.FormatLocal(DateTimeOffset.Now);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO floor_catalog (building, floor_label, floor_value, source, enabled, note, created_at, updated_at)
            VALUES ($building, $floor_label, $floor_value, 'manual', $enabled, $note, $created_at, $updated_at)
            ON CONFLICT(building, floor_label) DO UPDATE SET
              floor_value = excluded.floor_value,
              source = CASE
                WHEN floor_catalog.source = 'discovered' THEN 'manual+discovered'
                WHEN floor_catalog.source = 'manual+discovered' THEN 'manual+discovered'
                ELSE 'manual'
              END,
              enabled = excluded.enabled,
              note = excluded.note,
              updated_at = excluded.updated_at
            RETURNING id
            """;
        command.Parameters.AddWithValue("$building", building);
        command.Parameters.AddWithValue("$floor_label", floorLabel);
        command.Parameters.AddWithValue("$floor_value", floorValue);
        command.Parameters.AddWithValue("$enabled", edit.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$note", edit.Note ?? string.Empty);
        command.Parameters.AddWithValue("$created_at", now);
        command.Parameters.AddWithValue("$updated_at", now);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

        return (await LoadFloorByIdAsync(connection, id, cancellationToken).ConfigureAwait(false))
               ?? throw new InvalidOperationException("Saved floor catalog row not found.");
    }

    public async Task DeleteFloorAsync(long id, CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE floor_catalog
            SET enabled = 0, updated_at = $updated_at
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$updated_at", StoredTimestamp.FormatLocal(DateTimeOffset.Now));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AreaGroupRecord> SaveGroupAsync(
        AreaGroupEdit edit,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var name = Require(edit.Name, "group name");
        var priority = NormalizePriority(edit.Priority);
        var now = StoredTimestamp.FormatLocal(DateTimeOffset.Now);

        if (edit.Id is not null)
        {
            var current = await LoadGroupRawAsync(connection, edit.Id.Value, cancellationToken).ConfigureAwait(false)
                          ?? throw new InvalidOperationException($"Group not found: {edit.Id.Value}");
            var groupKey = string.IsNullOrWhiteSpace(edit.GroupKey)
                ? current.GroupKey
                : NormalizeGroupKey(edit.GroupKey);
            await using var update = connection.CreateCommand();
            if (current.Locked || current.GroupKind.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("锁定或系统区域组不支持编辑。");
            }
            else
            {
                var duplicate = await FindGroupIdByNameAsync(connection, name, cancellationToken).ConfigureAwait(false);
                if (duplicate is not null && duplicate.Value != edit.Id.Value)
                {
                    throw new InvalidOperationException("已存在同名区域组，请选择已有分组编辑或更换名称。");
                }

                update.CommandText = """
                    UPDATE monitor_groups
                    SET name = $name, area_label = $area_label, description = $description,
                        priority = $priority, enabled = $enabled, group_key = $group_key, updated_at = $updated_at
                    WHERE id = $id
                    """;
                update.Parameters.AddWithValue("$name", name);
                update.Parameters.AddWithValue("$area_label", (edit.AreaLabel ?? string.Empty).Trim());
                update.Parameters.AddWithValue("$description", (edit.Description ?? string.Empty).Trim());
                update.Parameters.AddWithValue("$priority", priority);
                update.Parameters.AddWithValue("$enabled", edit.Enabled ? 1 : 0);
                update.Parameters.AddWithValue("$group_key", groupKey);
                update.Parameters.AddWithValue("$updated_at", now);
                update.Parameters.AddWithValue("$id", edit.Id.Value);
            }

            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return (await LoadGroupsAsync(connection, cancellationToken).ConfigureAwait(false))
                .First(group => group.Id == edit.Id.Value);
        }

        var existing = await FindGroupIdByNameAsync(connection, name, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException("已存在同名区域组，请选择已有分组编辑或更换名称。");
        }

        var newGroupKey = string.IsNullOrWhiteSpace(edit.GroupKey)
            ? $"area-{Guid.NewGuid():N}"
            : NormalizeGroupKey(edit.GroupKey);

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO monitor_groups (name, area_label, description, priority, group_key, enabled, created_at, updated_at)
            VALUES ($name, $area_label, $description, $priority, $group_key, $enabled, $created_at, $updated_at)
            RETURNING id
            """;
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$area_label", (edit.AreaLabel ?? string.Empty).Trim());
        insert.Parameters.AddWithValue("$description", (edit.Description ?? string.Empty).Trim());
        insert.Parameters.AddWithValue("$priority", priority);
        insert.Parameters.AddWithValue("$group_key", newGroupKey);
        insert.Parameters.AddWithValue("$enabled", edit.Enabled ? 1 : 0);
        insert.Parameters.AddWithValue("$created_at", now);
        insert.Parameters.AddWithValue("$updated_at", now);
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        return (await LoadGroupsAsync(connection, cancellationToken).ConfigureAwait(false)).First(group => group.Id == id);
    }

    public async Task<AreaGroupRecord> SaveConfigurationAsync(
        AreaGroupEdit edit,
        IReadOnlyList<AreaGroupRuleEdit> rules,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        var normalizedRules = rules
            .Select((rule, index) => AreaGroupRuleNormalizer.Normalize(new AreaGroupRuleRecord(
                Id: rule.Id ?? 0,
                GroupId: 0,
                RuleOrder: index + 1,
                Building: rule.Building,
                Zuo: rule.Zuo,
                FloorLabel: rule.FloorLabel,
                FloorValue: AreaGroupRuleNormalizer.TryParseFloorValue(rule.FloorLabel),
                MatchMode: rule.MatchMode,
                Keywords: AreaGroupRuleNormalizer.NormalizeKeywords(rule.Keywords),
                Note: rule.Note)))
            .ToArray();

        await using var connection = OpenConnection(readOnly: false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var name = Require(edit.Name, "group name");
        var priority = NormalizePriority(edit.Priority);
        var now = StoredTimestamp.FormatLocal(DateTimeOffset.Now);
        long groupId;

        if (edit.Id is not null)
        {
            var current = await LoadGroupRawAsync(connection, edit.Id.Value, cancellationToken, transaction).ConfigureAwait(false)
                          ?? throw new InvalidOperationException($"Group not found: {edit.Id.Value}");
            if (current.Locked || current.GroupKind.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("锁定或系统区域组不支持编辑。");
            }

            var duplicate = await FindGroupIdByNameAsync(connection, name, cancellationToken, transaction).ConfigureAwait(false);
            if (duplicate is not null && duplicate.Value != edit.Id.Value)
            {
                throw new InvalidOperationException("已存在同名区域组，请选择已有分组编辑或更换名称。");
            }

            var groupKey = string.IsNullOrWhiteSpace(edit.GroupKey)
                ? current.GroupKey
                : NormalizeGroupKey(edit.GroupKey);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE monitor_groups
                SET name = $name, area_label = $area_label, description = $description,
                    priority = $priority, enabled = $enabled, group_key = $group_key, updated_at = $updated_at
                WHERE id = $id
                """;
            update.Parameters.AddWithValue("$name", name);
            update.Parameters.AddWithValue("$area_label", (edit.AreaLabel ?? string.Empty).Trim());
            update.Parameters.AddWithValue("$description", (edit.Description ?? string.Empty).Trim());
            update.Parameters.AddWithValue("$priority", priority);
            update.Parameters.AddWithValue("$enabled", edit.Enabled ? 1 : 0);
            update.Parameters.AddWithValue("$group_key", groupKey);
            update.Parameters.AddWithValue("$updated_at", now);
            update.Parameters.AddWithValue("$id", edit.Id.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            groupId = edit.Id.Value;
        }
        else
        {
            if (await FindGroupIdByNameAsync(connection, name, cancellationToken, transaction).ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException("已存在同名区域组，请选择已有分组编辑或更换名称。");
            }

            var groupKey = string.IsNullOrWhiteSpace(edit.GroupKey)
                ? $"area-{Guid.NewGuid():N}"
                : NormalizeGroupKey(edit.GroupKey);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO monitor_groups
                    (name, area_label, description, priority, group_key, enabled, created_at, updated_at)
                VALUES ($name, $area_label, $description, $priority, $group_key, $enabled, $created_at, $updated_at)
                RETURNING id
                """;
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$area_label", (edit.AreaLabel ?? string.Empty).Trim());
            insert.Parameters.AddWithValue("$description", (edit.Description ?? string.Empty).Trim());
            insert.Parameters.AddWithValue("$priority", priority);
            insert.Parameters.AddWithValue("$group_key", groupKey);
            insert.Parameters.AddWithValue("$enabled", edit.Enabled ? 1 : 0);
            insert.Parameters.AddWithValue("$created_at", now);
            insert.Parameters.AddWithValue("$updated_at", now);
            groupId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }

        await using (var deleteRules = connection.CreateCommand())
        {
            deleteRules.Transaction = transaction;
            deleteRules.CommandText = "DELETE FROM area_group_rules WHERE group_id = $group_id";
            deleteRules.Parameters.AddWithValue("$group_id", groupId);
            await deleteRules.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var rule in normalizedRules)
        {
            await using var insertRule = connection.CreateCommand();
            insertRule.Transaction = transaction;
            insertRule.CommandText = """
                INSERT INTO area_group_rules
                    (group_id, rule_order, building, zuo, floor_label, floor_value,
                     match_mode, keywords, note, created_at, updated_at)
                VALUES ($group_id, $rule_order, $building, $zuo, $floor_label, $floor_value,
                        $match_mode, $keywords, $note, $created_at, $updated_at)
                """;
            insertRule.Parameters.AddWithValue("$group_id", groupId);
            insertRule.Parameters.AddWithValue("$rule_order", rule.RuleOrder);
            insertRule.Parameters.AddWithValue("$building", rule.Building);
            insertRule.Parameters.AddWithValue("$zuo", rule.Zuo);
            insertRule.Parameters.AddWithValue("$floor_label", rule.FloorLabel);
            insertRule.Parameters.AddWithValue("$floor_value", (object?)rule.FloorValue ?? DBNull.Value);
            insertRule.Parameters.AddWithValue("$match_mode", rule.MatchMode);
            insertRule.Parameters.AddWithValue("$keywords", rule.KeywordText);
            insertRule.Parameters.AddWithValue("$note", rule.Note);
            insertRule.Parameters.AddWithValue("$created_at", now);
            insertRule.Parameters.AddWithValue("$updated_at", now);
            await insertRule.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await LoadAsync(cancellationToken).ConfigureAwait(false)).Groups.First(group => group.Id == groupId);
    }

    public async Task<AreaGroupRuleRecord> SaveRuleAsync(
        AreaGroupRuleEdit edit,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var group = await LoadGroupRawAsync(connection, edit.GroupId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Group not found: {edit.GroupId}");
        if (group.Locked || group.GroupKind.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("系统区域不支持规则编辑。");
        }

        var keywords = AreaGroupRuleNormalizer.NormalizeKeywords(edit.Keywords);
        var candidate = new AreaGroupRuleRecord(
            Id: edit.Id ?? 0,
            GroupId: edit.GroupId,
            RuleOrder: edit.RuleOrder ?? 0,
            Building: edit.Building,
            Zuo: edit.Zuo,
            FloorLabel: edit.FloorLabel,
            FloorValue: AreaGroupRuleNormalizer.TryParseFloorValue(edit.FloorLabel),
            MatchMode: edit.MatchMode,
            Keywords: keywords,
            Note: edit.Note);
        var normalized = AreaGroupRuleNormalizer.Normalize(candidate);
        var now = StoredTimestamp.FormatLocal(DateTimeOffset.Now);

        if (edit.Id is not null)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE area_group_rules
                SET rule_order = $rule_order, building = $building, zuo = $zuo,
                    floor_label = $floor_label, floor_value = $floor_value,
                    match_mode = $match_mode, keywords = $keywords, note = $note,
                    updated_at = $updated_at
                WHERE id = $id AND group_id = $group_id
                """;
            AddRuleParameters(update, normalized, edit.Id.Value, now);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException("规则不存在或不属于当前区域组。");
            }

            await AreaGroupRuleOrderMigration.RenumberGroupAsync(connection, null, edit.GroupId, cancellationToken).ConfigureAwait(false);
            return await LoadRuleByIdAsync(connection, edit.Id.Value, cancellationToken)
                   ?? throw new InvalidOperationException("规则保存后未找到。");
        }

        var nextOrder = edit.RuleOrder ?? await NextRuleOrderAsync(connection, edit.GroupId, cancellationToken).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO area_group_rules
                (group_id, rule_order, building, zuo, floor_label, floor_value,
                 match_mode, keywords, note, created_at, updated_at)
            VALUES ($group_id, $rule_order, $building, $zuo, $floor_label, $floor_value,
                    $match_mode, $keywords, $note, $created_at, $updated_at)
            RETURNING id
            """;
        insert.Parameters.AddWithValue("$group_id", normalized.GroupId);
        insert.Parameters.AddWithValue("$rule_order", nextOrder);
        insert.Parameters.AddWithValue("$building", normalized.Building);
        insert.Parameters.AddWithValue("$zuo", normalized.Zuo);
        insert.Parameters.AddWithValue("$floor_label", normalized.FloorLabel);
        insert.Parameters.AddWithValue("$floor_value", (object?)normalized.FloorValue ?? DBNull.Value);
        insert.Parameters.AddWithValue("$match_mode", normalized.MatchMode);
        insert.Parameters.AddWithValue("$keywords", normalized.KeywordText);
        insert.Parameters.AddWithValue("$note", normalized.Note);
        insert.Parameters.AddWithValue("$created_at", now);
        insert.Parameters.AddWithValue("$updated_at", now);
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        await AreaGroupRuleOrderMigration.RenumberGroupAsync(connection, null, edit.GroupId, cancellationToken).ConfigureAwait(false);
        return await LoadRuleByIdAsync(connection, id, cancellationToken)
               ?? throw new InvalidOperationException("规则保存后未找到。");
    }

    public async Task DeleteRuleAsync(long id, CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long groupId;
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = """
                SELECT r.group_id, g.locked, g.group_kind
                FROM area_group_rules r
                JOIN monitor_groups g ON g.id = r.group_id
                WHERE r.id = $id
                """;
            find.Parameters.AddWithValue("$id", id);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("规则不存在或已删除。");
            }

            groupId = reader.GetInt64(reader.GetOrdinal("group_id"));
            if (reader.GetInt32(reader.GetOrdinal("locked")) != 0 ||
                string.Equals(reader.GetString(reader.GetOrdinal("group_kind")), "system", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("锁定或系统区域组不支持规则删除。");
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM area_group_rules WHERE id = $id";
            delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AreaGroupRuleOrderMigration.RenumberGroupAsync(connection, transaction, groupId, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AreaGroupTransferDocument> ExportAsync(CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var groups = await LoadGroupsAsync(connection, cancellationToken).ConfigureAwait(false);
        var rules = await LoadRulesAsync(connection, null, cancellationToken).ConfigureAwait(false);
        var transferGroups = groups
            .Where(group => !string.IsNullOrWhiteSpace(group.GroupKey))
            .OrderBy(group => group.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AreaGroupTransferGroup(
                GroupKey: group.GroupKey,
                Name: group.Name,
                Note: group.Description,
                Enabled: group.Enabled,
                AreaLabel: group.AreaLabel,
                Priority: group.Priority,
                Rules: rules
                    .Where(rule => rule.GroupId == group.Id)
                    .OrderBy(rule => rule.RuleOrder)
                    .ThenBy(rule => rule.Id)
                    .Select(rule => new AreaGroupTransferRule(
                        rule.RuleOrder,
                        rule.Building,
                        rule.Zuo,
                        rule.FloorLabel,
                        rule.MatchMode,
                        rule.Keywords,
                        rule.Note))
                    .ToArray()))
            .ToArray();
        return new AreaGroupTransferDocument(AreaGroupTransferCodec.CurrentSchemaVersion, transferGroups);
    }

    public async Task ImportAsync(
        AreaGroupTransferDocument document,
        CancellationToken cancellationToken = default)
    {
        AreaGroupTransferCodec.Validate(document);
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var transferGroup in document.Groups)
        {
            var groupKey = NormalizeGroupKey(transferGroup.GroupKey);
            var groupId = await FindGroupIdByKeyAsync(connection, transaction, groupKey, cancellationToken).ConfigureAwait(false);
            if (groupId is null)
            {
                await using var insertGroup = connection.CreateCommand();
                insertGroup.Transaction = transaction;
                insertGroup.CommandText = """
                    INSERT INTO monitor_groups
                        (name, area_label, description, priority, group_kind, system_key, locked, enabled, group_key, created_at, updated_at)
                    VALUES ($name, $area_label, $description, $priority, 'custom', NULL, 0, $enabled, $group_key, $created_at, $updated_at)
                    RETURNING id
                    """;
                insertGroup.Parameters.AddWithValue("$name", Require(transferGroup.Name, "group name"));
                insertGroup.Parameters.AddWithValue("$area_label", (transferGroup.AreaLabel ?? string.Empty).Trim());
                insertGroup.Parameters.AddWithValue("$description", (transferGroup.Note ?? string.Empty).Trim());
                insertGroup.Parameters.AddWithValue("$priority", NormalizePriority(transferGroup.Priority));
                insertGroup.Parameters.AddWithValue("$enabled", transferGroup.Enabled ? 1 : 0);
                insertGroup.Parameters.AddWithValue("$group_key", groupKey);
                insertGroup.Parameters.AddWithValue("$created_at", StoredTimestamp.FormatLocal(DateTimeOffset.Now));
                insertGroup.Parameters.AddWithValue("$updated_at", StoredTimestamp.FormatLocal(DateTimeOffset.Now));
                groupId = Convert.ToInt64(await insertGroup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                await using (var lockCheck = connection.CreateCommand())
                {
                    lockCheck.Transaction = transaction;
                    lockCheck.CommandText = "SELECT locked FROM monitor_groups WHERE id = $id";
                    lockCheck.Parameters.AddWithValue("$id", groupId.Value);
                    if (Convert.ToInt32(await lockCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
                    {
                        throw new InvalidOperationException($"区域组 {groupKey} 已锁定，不能通过导入覆盖。");
                    }
                }

                await using var updateGroup = connection.CreateCommand();
                updateGroup.Transaction = transaction;
                updateGroup.CommandText = """
                    UPDATE monitor_groups
                    SET name = $name, area_label = $area_label, description = $description,
                        priority = $priority, group_kind = 'custom',
                        system_key = NULL, enabled = $enabled, updated_at = $updated_at
                    WHERE id = $id
                    """;
                updateGroup.Parameters.AddWithValue("$name", Require(transferGroup.Name, "group name"));
                updateGroup.Parameters.AddWithValue("$area_label", (transferGroup.AreaLabel ?? string.Empty).Trim());
                updateGroup.Parameters.AddWithValue("$description", (transferGroup.Note ?? string.Empty).Trim());
                updateGroup.Parameters.AddWithValue("$priority", NormalizePriority(transferGroup.Priority));
                updateGroup.Parameters.AddWithValue("$enabled", transferGroup.Enabled ? 1 : 0);
                updateGroup.Parameters.AddWithValue("$updated_at", StoredTimestamp.FormatLocal(DateTimeOffset.Now));
                updateGroup.Parameters.AddWithValue("$id", groupId.Value);
                await updateGroup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var deleteRules = connection.CreateCommand())
            {
                deleteRules.Transaction = transaction;
                deleteRules.CommandText = "DELETE FROM area_group_rules WHERE group_id = $group_id";
                deleteRules.Parameters.AddWithValue("$group_id", groupId.Value);
                await deleteRules.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var transferRule in transferGroup.Rules.OrderBy(rule => rule.RuleOrder))
            {
                var keywords = AreaGroupRuleNormalizer.NormalizeKeywords(string.Join("|", transferRule.Keywords));
                var rule = AreaGroupRuleNormalizer.Normalize(new AreaGroupRuleRecord(
                    0,
                    groupId.Value,
                    transferRule.RuleOrder,
                    transferRule.Building,
                    transferRule.Zuo,
                    transferRule.Floor,
                    AreaGroupRuleNormalizer.TryParseFloorValue(transferRule.Floor),
                    transferRule.Mode,
                    keywords,
                    transferRule.Note));
                await using var insertRule = connection.CreateCommand();
                insertRule.Transaction = transaction;
                insertRule.CommandText = """
                    INSERT INTO area_group_rules
                        (group_id, rule_order, building, zuo, floor_label, floor_value, match_mode, keywords, note, created_at, updated_at)
                    VALUES ($group_id, $rule_order, $building, $zuo, $floor_label, $floor_value, $match_mode, $keywords, $note, $created_at, $updated_at)
                    """;
                insertRule.Parameters.AddWithValue("$group_id", rule.GroupId);
                insertRule.Parameters.AddWithValue("$rule_order", rule.RuleOrder);
                insertRule.Parameters.AddWithValue("$building", rule.Building);
                insertRule.Parameters.AddWithValue("$zuo", rule.Zuo);
                insertRule.Parameters.AddWithValue("$floor_label", rule.FloorLabel);
                insertRule.Parameters.AddWithValue("$floor_value", (object?)rule.FloorValue ?? DBNull.Value);
                insertRule.Parameters.AddWithValue("$match_mode", rule.MatchMode);
                insertRule.Parameters.AddWithValue("$keywords", rule.KeywordText);
                insertRule.Parameters.AddWithValue("$note", rule.Note);
                insertRule.Parameters.AddWithValue("$created_at", StoredTimestamp.FormatLocal(DateTimeOffset.Now));
                insertRule.Parameters.AddWithValue("$updated_at", StoredTimestamp.FormatLocal(DateTimeOffset.Now));
                await insertRule.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AreaGroupRuleOrderMigration.RenumberGroupAsync(connection, transaction, groupId.Value, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteGroupAsync(long id, CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();
        await using var connection = OpenConnection(readOnly: false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var current = await LoadGroupRawAsync(connection, id, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"Group not found: {id}");
        if (current.Locked || current.GroupKind.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("系统区域不能删除");
        }

        if (await TableExistsAsync(connection, "device_watch_rules", cancellationToken).ConfigureAwait(false))
        {
            await using var reference = connection.CreateCommand();
            reference.CommandText = "SELECT COUNT(*) FROM device_watch_rules WHERE group_id = $id";
            reference.Parameters.AddWithValue("$id", id);
            var references = Convert.ToInt32(await reference.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (references > 0)
            {
                throw new InvalidOperationException("区域组仍被设备关注规则引用，请先解除引用后再删除。");
            }
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await TableExistsAsync(connection, "device_watch_rules", cancellationToken).ConfigureAwait(false))
        {
            // The reference check above intentionally makes this branch a no-op for normal deletion.
            // Keep the table check here for databases created before the watch-rule migration.
        }

        await using (var deleteGroup = connection.CreateCommand())
        {
            deleteGroup.Transaction = (SqliteTransaction)transaction;
            deleteGroup.CommandText = "DELETE FROM monitor_groups WHERE id = $id";
            deleteGroup.Parameters.AddWithValue("$id", id);
            await deleteGroup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AreaGroupItemRecord> SaveItemAsync(
        AreaGroupItemEdit edit,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("区域组成员已废弃，请使用区域组匹配规则。");
    }

    public async Task DeleteItemAsync(long id, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("区域组成员已废弃，请使用区域组匹配规则。");
    }

    private SqliteConnection OpenConnection(bool readOnly)
    {
        var mode = readOnly ? "ReadOnly" : "ReadWrite";
        var connection = new SqliteConnection($"Data Source={databasePathResolver()};Mode={mode}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void EnsureDatabaseExists()
    {
        var databasePath = databasePathResolver();
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Cannot find EMS SQLite database.", databasePath);
        }
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS monitor_groups (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                area_label TEXT NOT NULL DEFAULT '',
                description TEXT NOT NULL DEFAULT '',
                priority TEXT NOT NULL DEFAULT '重点',
                group_kind TEXT NOT NULL DEFAULT 'custom',
                system_key TEXT,
                locked INTEGER NOT NULL DEFAULT 0,
                enabled INTEGER NOT NULL DEFAULT 1,
                group_key TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60)),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60))
            );
            CREATE TABLE IF NOT EXISTS area_group_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                group_id INTEGER NOT NULL,
                rule_order INTEGER NOT NULL DEFAULT 0,
                building TEXT NOT NULL,
                zuo TEXT NOT NULL DEFAULT '-',
                floor_label TEXT NOT NULL DEFAULT '',
                floor_value REAL,
                match_mode TEXT NOT NULL,
                keywords TEXT NOT NULL,
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60)),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60)),
                FOREIGN KEY(group_id) REFERENCES monitor_groups(id) ON DELETE CASCADE
            );
                CREATE INDEX IF NOT EXISTS idx_area_group_rules_group_order
                    ON area_group_rules(group_id, rule_order, id);
                CREATE TABLE IF NOT EXISTS run_area_group_rules (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id INTEGER NOT NULL,
                    group_id INTEGER NOT NULL,
                    group_key TEXT NOT NULL DEFAULT '',
                    group_name TEXT NOT NULL DEFAULT '',
                    enabled INTEGER NOT NULL DEFAULT 1,
                    rule_order INTEGER NOT NULL,
                    building TEXT NOT NULL,
                    zuo TEXT NOT NULL DEFAULT '-',
                    floor_label TEXT NOT NULL DEFAULT '',
                    floor_value REAL,
                    match_mode TEXT NOT NULL,
                    keywords TEXT NOT NULL DEFAULT '',
                    note TEXT NOT NULL DEFAULT '',
                    FOREIGN KEY(run_id) REFERENCES collection_runs(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_run_area_group_rules_run_group
                    ON run_area_group_rules(run_id, group_id, rule_order, id);
                CREATE TABLE IF NOT EXISTS ems_schema_migrations (
                name TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS floor_catalog (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                building TEXT NOT NULL,
                floor_label TEXT NOT NULL,
                floor_value REAL NOT NULL,
                source TEXT NOT NULL DEFAULT 'manual',
                enabled INTEGER NOT NULL DEFAULT 1,
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60)),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60))
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_floor_catalog_key
            ON floor_catalog(building, floor_label);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, "monitor_groups", "group_key", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await EnsureGroupKeyIndexAsync(connection, cancellationToken).ConfigureAwait(false);
        await MigrateLegacyAreaGroupsAsync(connection, cancellationToken).ConfigureAwait(false);
        await AreaGroupRuleOrderMigration.ApplyAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(reader.GetOrdinal("name")), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition}";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureGroupKeyIndexAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_monitor_groups_group_key ON monitor_groups(group_key) WHERE group_key <> ''";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateLegacyAreaGroupsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string migrationName = "area-groups-rules-v1";
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM ems_schema_migrations WHERE name = $name";
        exists.Parameters.AddWithValue("$name", migrationName);
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            await CleanupLegacyAreaStorageAsync(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await TableExistsAsync(connection, "monitor_group_items", cancellationToken).ConfigureAwait(false))
        {
            // Legacy members reference monitor_groups without cascade delete.
            // Clear them before removing groups that are not retained for Watch.
            await using var deleteItems = connection.CreateCommand();
            deleteItems.Transaction = transaction;
            deleteItems.CommandText = "DELETE FROM monitor_group_items";
            await deleteItems.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (await TableExistsAsync(connection, "device_watch_rules", cancellationToken).ConfigureAwait(false))
        {
            await using var preserveWatchGroups = connection.CreateCommand();
            preserveWatchGroups.Transaction = transaction;
            preserveWatchGroups.CommandText = """
                UPDATE monitor_groups
                SET enabled = 0, group_kind = 'custom', system_key = NULL, locked = 0, group_key = ''
                WHERE EXISTS (
                    SELECT 1 FROM device_watch_rules
                    WHERE device_watch_rules.group_id = monitor_groups.id
                );
                DELETE FROM monitor_groups
                WHERE NOT EXISTS (
                    SELECT 1 FROM device_watch_rules
                    WHERE device_watch_rules.group_id = monitor_groups.id
                );
                DELETE FROM device_watch_rules
                WHERE NOT EXISTS (
                    SELECT 1 FROM monitor_groups
                    WHERE monitor_groups.id = device_watch_rules.group_id
                );
                """;
            await preserveWatchGroups.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var deleteGroups = connection.CreateCommand();
            deleteGroups.Transaction = transaction;
            deleteGroups.CommandText = "DELETE FROM monitor_groups";
            await deleteGroups.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteRules = connection.CreateCommand())
        {
            deleteRules.Transaction = transaction;
            deleteRules.CommandText = "DELETE FROM area_group_rules";
            await deleteRules.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await TableExistsAsync(connection, "monitor_group_items", cancellationToken).ConfigureAwait(false))
        {
            await using var dropItems = connection.CreateCommand();
            dropItems.Transaction = transaction;
            dropItems.CommandText = "DROP TABLE monitor_group_items";
            await dropItems.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await TableExistsAsync(connection, "legacy_area_api_state", cancellationToken).ConfigureAwait(false))
        {
            await using var deleteLegacyState = connection.CreateCommand();
            deleteLegacyState.Transaction = transaction;
            deleteLegacyState.CommandText = "DROP TABLE legacy_area_api_state";
            await deleteLegacyState.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "INSERT INTO ems_schema_migrations(name, applied_at) VALUES ($name, $applied_at)";
            mark.Parameters.AddWithValue("$name", migrationName);
            mark.Parameters.AddWithValue("$applied_at", StoredTimestamp.FormatLocal(DateTimeOffset.Now));
            await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CleanupLegacyAreaStorageAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var statements = new List<string>();
        if (await TableExistsAsync(connection, "monitor_group_items", cancellationToken).ConfigureAwait(false))
            statements.Add("DROP TABLE monitor_group_items");
        if (await TableExistsAsync(connection, "legacy_area_api_state", cancellationToken).ConfigureAwait(false))
            statements.Add("DROP TABLE legacy_area_api_state");
        if (statements.Count == 0)
            return;

        await using var cleanup = connection.CreateCommand();
        cleanup.CommandText = string.Join(';', statements);
        await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SyncFloorCatalogFromCurrentAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<(string Building, double Floor)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT building, floor
                FROM sub_areas
                WHERE floor IS NOT NULL
                GROUP BY building, floor
                ORDER BY building, floor
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((ReadString(reader, "building"), ReadDouble(reader, "floor")));
            }
        }

        var now = StoredTimestamp.FormatLocal(DateTimeOffset.Now);
        foreach (var row in rows)
        {
            await using var upsert = connection.CreateCommand();
            upsert.CommandText = """
                INSERT INTO floor_catalog (building, floor_label, floor_value, source, enabled, note, created_at, updated_at)
                VALUES ($building, $floor_label, $floor_value, 'discovered', 1, '', $created_at, $updated_at)
                ON CONFLICT(building, floor_label) DO UPDATE SET
                  floor_value = excluded.floor_value,
                  source = CASE
                    WHEN floor_catalog.source = 'manual' THEN 'manual+discovered'
                    WHEN floor_catalog.source = 'manual+discovered' THEN 'manual+discovered'
                    ELSE 'discovered'
                  END,
                  enabled = 1,
                  updated_at = excluded.updated_at
                """;
            upsert.Parameters.AddWithValue("$building", row.Building);
            upsert.Parameters.AddWithValue("$floor_label", FloorLabelFromValue(row.Floor));
            upsert.Parameters.AddWithValue("$floor_value", row.Floor);
            upsert.Parameters.AddWithValue("$created_at", now);
            upsert.Parameters.AddWithValue("$updated_at", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<FloorCatalogRecord?> LoadFloorByIdAsync(
        SqliteConnection connection,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, building, floor_label, floor_value, source, enabled, note
            FROM floor_catalog
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new FloorCatalogRecord(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                Building: ReadString(reader, "building"),
                FloorLabel: ReadString(reader, "floor_label"),
                FloorValue: ReadDouble(reader, "floor_value"),
                Source: ReadString(reader, "source"),
                Enabled: ReadInt32(reader, "enabled") != 0,
                Note: ReadString(reader, "note"))
            : null;
    }

    private static async Task<IReadOnlyList<AreaGroupRecord>> LoadGroupsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var groups = new List<AreaGroupRaw>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, name, area_label, description, priority, group_kind, system_key, locked,
                       enabled, group_key
                FROM monitor_groups
                WHERE COALESCE(group_key, '') <> ''
                ORDER BY enabled DESC, priority DESC, id
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                groups.Add(ReadRawGroup(reader));
            }
        }

        var rows = new List<AreaGroupRecord>();
        foreach (var group in groups)
        {
            var stats = await ComputeGroupStatsAsync(connection, group.Id, cancellationToken).ConfigureAwait(false);
            rows.Add(new AreaGroupRecord(
                group.Id,
                group.Name,
                group.AreaLabel,
                group.Description,
                group.Priority,
                group.GroupKind,
                group.SystemKey,
                group.Locked,
                group.Enabled,
                stats.ItemCount,
                stats.Total,
                stats.OnCount,
                stats.OffCount,
                stats.OfflineCount,
                stats.UnknownCount,
                stats.CoveredAreas,
                stats.PublicTotal,
                stats.PublicOnCount,
                stats.PublicOffCount,
                stats.PublicOfflineCount,
                stats.PublicUnknownCount,
                stats.PublicCoveredAreas,
                group.GroupKey));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<AreaGroupItemRecord>> LoadItemsAsync(
        SqliteConnection connection,
        long? groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT i.id, i.group_id, g.name AS group_name, i.target_type, i.building,
                   i.floor_label, i.floor_value, i.sub_area_text, i.card_name, i.note
            FROM monitor_group_items i
            JOIN monitor_groups g ON g.id = i.group_id
            {(groupId is null ? string.Empty : "WHERE i.group_id = $group_id")}
            ORDER BY g.id, i.building, i.floor_value, i.sub_area_text, i.card_name
            """;
        if (groupId is not null)
        {
            command.Parameters.AddWithValue("$group_id", groupId.Value);
        }

        var rows = new List<AreaGroupItemRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new AreaGroupItemRecord(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                GroupId: reader.GetInt64(reader.GetOrdinal("group_id")),
                GroupName: ReadString(reader, "group_name"),
                TargetType: ReadString(reader, "target_type"),
                Building: ReadString(reader, "building"),
                FloorLabel: ReadString(reader, "floor_label"),
                FloorValue: ReadNullableDouble(reader, "floor_value"),
                SubAreaText: ReadString(reader, "sub_area_text"),
                CardName: ReadString(reader, "card_name"),
                Note: ReadString(reader, "note")));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<AreaGroupRuleRecord>> LoadRulesAsync(
        SqliteConnection connection,
        long? groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, group_id, rule_order, building, zuo, floor_label, floor_value,
                   match_mode, keywords, note
            FROM area_group_rules
            {(groupId is null ? string.Empty : "WHERE group_id = $group_id")}
            ORDER BY group_id, rule_order, id
            """;
        if (groupId is not null)
        {
            command.Parameters.AddWithValue("$group_id", groupId.Value);
        }

        var rows = new List<AreaGroupRuleRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadRule(reader));
        }

        return rows;
    }

    private static async Task<AreaGroupRuleRecord?> LoadRuleByIdAsync(
        SqliteConnection connection,
        long id,
        CancellationToken cancellationToken)
    {
        var rows = await LoadRulesAsync(connection, null, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(rule => rule.Id == id);
    }

    private static async Task<int> NextRuleOrderAsync(
        SqliteConnection connection,
        long groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(rule_order), 0) + 1 FROM area_group_rules WHERE group_id = $group_id";
        command.Parameters.AddWithValue("$group_id", groupId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddRuleParameters(
        SqliteCommand command,
        AreaGroupRuleRecord rule,
        long id,
        string updatedAt)
    {
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$group_id", rule.GroupId);
        command.Parameters.AddWithValue("$rule_order", rule.RuleOrder);
        command.Parameters.AddWithValue("$building", rule.Building);
        command.Parameters.AddWithValue("$zuo", rule.Zuo);
        command.Parameters.AddWithValue("$floor_label", rule.FloorLabel);
        command.Parameters.AddWithValue("$floor_value", (object?)rule.FloorValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$match_mode", rule.MatchMode);
        command.Parameters.AddWithValue("$keywords", rule.KeywordText);
        command.Parameters.AddWithValue("$note", rule.Note);
        command.Parameters.AddWithValue("$updated_at", updatedAt);
    }

    private static AreaGroupRuleRecord ReadRule(SqliteDataReader reader)
    {
        return new AreaGroupRuleRecord(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            GroupId: reader.GetInt64(reader.GetOrdinal("group_id")),
            RuleOrder: ReadInt32(reader, "rule_order"),
            Building: ReadString(reader, "building"),
            Zuo: ReadString(reader, "zuo"),
            FloorLabel: ReadString(reader, "floor_label"),
            FloorValue: ReadNullableDouble(reader, "floor_value"),
            MatchMode: ReadString(reader, "match_mode"),
            Keywords: AreaGroupRuleNormalizer.NormalizeKeywords(ReadString(reader, "keywords")),
            Note: ReadString(reader, "note"));
    }

    private static async Task<GroupStats> ComputeGroupStatsAsync(
        SqliteConnection connection,
        long groupId,
        CancellationToken cancellationToken)
    {
        var group = await LoadGroupRawAsync(connection, groupId, cancellationToken).ConfigureAwait(false);
        var rules = await LoadRulesAsync(connection, groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return new GroupStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        if (!group.Enabled)
        {
            return new GroupStats(rules.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var hasCurrentDevices = await TableExistsAsync(connection, "sub_areas", cancellationToken).ConfigureAwait(false) &&
                                await TableExistsAsync(connection, "cards", cancellationToken).ConfigureAwait(false);
        if (rules.Count == 0 || !hasCurrentDevices)
        {
            return new GroupStats(rules.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var devices = await LoadCurrentDevicesForStatsAsync(connection, cancellationToken).ConfigureAwait(false);
        var matches = devices
            .Where(device => AreaGroupRuleMatcher.MatchesAny(device, rules))
            .ToArray();
        IReadOnlyList<DeviceRecord> publicMatches = [];
        return BuildGroupStats(rules.Count, matches, publicMatches);
    }

    private static GroupStats BuildGroupStats(
        int itemCount,
        IReadOnlyList<DeviceRecord> matches,
        IReadOnlyList<DeviceRecord> publicMatches)
    {
        return new GroupStats(
            ItemCount: itemCount,
            Total: matches.Count,
            OnCount: matches.Count(device => device.CommunicationState == DeviceCommunicationState.Running),
            OffCount: matches.Count(device => device.CommunicationState == DeviceCommunicationState.Stopped),
            OfflineCount: matches.Count(device => device.CommunicationState == DeviceCommunicationState.Offline),
            UnknownCount: matches.Count(device => device.CommunicationState == DeviceCommunicationState.Unknown),
            CoveredAreas: matches.Select(device => (device.Building, device.Floor, device.SubArea)).Distinct().Count(),
            PublicTotal: publicMatches.Count,
            PublicOnCount: publicMatches.Count(device => device.CommunicationState == DeviceCommunicationState.Running),
            PublicOffCount: publicMatches.Count(device => device.CommunicationState == DeviceCommunicationState.Stopped),
            PublicOfflineCount: publicMatches.Count(device => device.CommunicationState == DeviceCommunicationState.Offline),
            PublicUnknownCount: publicMatches.Count(device => device.CommunicationState == DeviceCommunicationState.Unknown),
            PublicCoveredAreas: publicMatches.Select(device => (device.Building, device.Floor, device.SubArea)).Distinct().Count());
    }

    private static async Task<IReadOnlyList<DeviceRecord>> LoadCurrentDevicesForStatsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, s.building, s.floor, s.text AS sub_area, s.x, s.y,
                   p.page_name, p.layout, c.name, c.switch, c.mode, c.indoor,
                   c.set_temp, c.fan, c.indicator, c.comm
            FROM sub_areas s
            JOIN pages p ON p.sub_area_id = s.id
            JOIN cards c ON c.page_id = p.id
            ORDER BY s.building, s.floor, s.sub_idx, p.id, c.name
            """;

        var rows = new List<DeviceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var floor = ReadNullableDouble(reader, "floor");
            var building = ReadString(reader, "building");
            var subArea = ReadString(reader, "sub_area");
            var x = ReadNullableDouble(reader, "x");
            var communication = ReadString(reader, "comm");
            rows.Add(new DeviceRecord(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                Building: building,
                Floor: floor,
                FloorLabel: DeviceFloorLabelFormatter.Format(floor, subArea),
                SubArea: subArea,
                X: x,
                Y: ReadNullableDouble(reader, "y"),
                PageName: ReadString(reader, "page_name"),
                Name: ReadString(reader, "name"),
                Layout: ReadString(reader, "layout"),
                SwitchState: ReadString(reader, "switch"),
                Mode: ReadString(reader, "mode"),
                IndoorTemperature: ReadString(reader, "indoor"),
                SetTemperature: ReadString(reader, "set_temp"),
                Fan: ReadString(reader, "fan"),
                Indicator: ReadString(reader, "indicator"),
                CommunicationText: communication,
                CommunicationState: DeviceCommunicationStateParser.Parse(communication),
                Zuo: DeviceZuoClassifier.Classify(building, x),
                ZuoSource: "db"));
        }

        return rows;
    }

    private static async Task<AreaGroupRaw?> LoadGroupRawAsync(
        SqliteConnection connection,
        long id,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, name, area_label, description, priority, group_kind, system_key, locked, enabled, group_key
            FROM monitor_groups
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRawGroup(reader) : null;
    }

    private static async Task<AreaGroupItemRaw?> LoadItemRawAsync(SqliteConnection connection, long id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, group_id
            FROM monitor_group_items
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AreaGroupItemRaw(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                GroupId: reader.GetInt64(reader.GetOrdinal("group_id")))
            : null;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<long?> FindGroupIdByNameAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM monitor_groups WHERE name = $name COLLATE NOCASE";
        command.Parameters.AddWithValue("$name", name);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long?> FindGroupIdByKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string groupKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM monitor_groups WHERE group_key = $group_key COLLATE NOCASE";
        command.Parameters.AddWithValue("$group_key", groupKey);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long?> FindItemIdAsync(
        SqliteConnection connection,
        long groupId,
        string targetType,
        string building,
        double? floorValue,
        string subArea,
        string cardName,
        long? excludeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = targetType switch
        {
            "name_contains" or "name_excludes" => """
                SELECT id FROM monitor_group_items
                WHERE group_id = $group_id
                  AND target_type = $target_type
                  AND building = $building
                  AND IFNULL(card_name, '') = IFNULL($card_name, '')
                  AND ABS(COALESCE(floor_value, -999999) - COALESCE($floor_value, -999998)) < 0.001
                  AND IFNULL(sub_area_text, '') = IFNULL($sub_area_text, '')
                  AND ($exclude_id IS NULL OR id <> $exclude_id)
                """,
            "device" => """
                SELECT id FROM monitor_group_items
                WHERE group_id = $group_id
                  AND target_type = 'device'
                  AND building = $building
                  AND IFNULL(card_name, '') = IFNULL($card_name, '')
                  AND ABS(COALESCE(floor_value, -999999) - COALESCE($floor_value, -999998)) < 0.001
                  AND IFNULL(sub_area_text, '') = IFNULL($sub_area_text, '')
                  AND ($exclude_id IS NULL OR id <> $exclude_id)
                """,
            "sub_area" => """
                SELECT id FROM monitor_group_items
                WHERE group_id = $group_id
                  AND target_type = 'sub_area'
                  AND building = $building
                  AND ABS(COALESCE(floor_value, -999999) - COALESCE($floor_value, -999998)) < 0.001
                  AND IFNULL(sub_area_text, '') = IFNULL($sub_area_text, '')
                  AND ($exclude_id IS NULL OR id <> $exclude_id)
                """,
            _ => """
                SELECT id FROM monitor_group_items
                WHERE group_id = $group_id
                  AND target_type = 'floor'
                  AND building = $building
                  AND ABS(COALESCE(floor_value, -999999) - COALESCE($floor_value, -999998)) < 0.001
                  AND ($exclude_id IS NULL OR id <> $exclude_id)
                """
        };
        command.Parameters.AddWithValue("$group_id", groupId);
        command.Parameters.AddWithValue("$target_type", targetType);
        command.Parameters.AddWithValue("$building", building);
        command.Parameters.AddWithValue("$floor_value", floorValue is null ? DBNull.Value : floorValue);
        command.Parameters.AddWithValue("$sub_area_text", NullIfEmpty(subArea));
        command.Parameters.AddWithValue("$card_name", NullIfEmpty(cardName));
        command.Parameters.AddWithValue("$exclude_id", excludeId is null ? DBNull.Value : excludeId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        (string Name, object Value) parameter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string CustomGroupExistsSql()
    {
        return """
            (
              EXISTS (
                SELECT 1
                FROM monitor_groups active_group
                WHERE active_group.id = $group_id
                  AND active_group.enabled = 1
              )
              AND (
                NOT EXISTS (
                  SELECT 1
                  FROM monitor_group_items positive
                  JOIN monitor_groups positive_group ON positive_group.id = positive.group_id
                  WHERE positive.group_id = $group_id
                    AND positive_group.enabled = 1
                    AND positive.target_type <> 'name_excludes'
                )
                OR EXISTS (
                  SELECT 1
                  FROM monitor_group_items mgi
                  JOIN monitor_groups mg ON mg.id = mgi.group_id
                  WHERE mgi.group_id = $group_id
                    AND mg.enabled = 1
                    AND mgi.building = s.building
                    AND (
                      (
                        mgi.target_type = 'device'
                        AND (mgi.card_name = c.name OR c.name LIKE mgi.card_name || '#%')
                        AND (
                          (mgi.floor_value IS NULL AND IFNULL(mgi.sub_area_text, '') = '')
                          OR (
                            (mgi.floor_value IS NULL OR ABS(COALESCE(s.floor, -999999) - COALESCE(mgi.floor_value, -999998)) < 0.001)
                            AND (IFNULL(mgi.sub_area_text, '') = '' OR IFNULL(mgi.sub_area_text, '') = IFNULL(s.text, ''))
                          )
                        )
                      )
                      OR (
                        mgi.target_type = 'sub_area'
                        AND ABS(COALESCE(s.floor, -999999) - COALESCE(mgi.floor_value, -999998)) < 0.001
                        AND IFNULL(mgi.sub_area_text, '') = IFNULL(s.text, '')
                      )
                      OR (
                        mgi.target_type = 'floor'
                        AND ABS(COALESCE(s.floor, -999999) - COALESCE(mgi.floor_value, -999998)) < 0.001
                      )
                      OR (
                        mgi.target_type = 'name_contains'
                        AND INSTR(LOWER(COALESCE(c.name, '')), LOWER(mgi.card_name)) > 0
                      )
                    )
                )
              )
              AND NOT EXISTS (
                SELECT 1
                FROM monitor_group_items negative
                JOIN monitor_groups negative_group ON negative_group.id = negative.group_id
                WHERE negative.group_id = $group_id
                  AND negative_group.enabled = 1
                  AND negative.target_type = 'name_excludes'
                  AND negative.building = s.building
                  AND INSTR(LOWER(COALESCE(c.name, '')), LOWER(negative.card_name)) > 0
                  AND (negative.floor_value IS NULL OR ABS(COALESCE(s.floor, -999999) - COALESCE(negative.floor_value, -999998)) < 0.001)
                  AND (IFNULL(negative.sub_area_text, '') = '' OR IFNULL(negative.sub_area_text, '') = IFNULL(s.text, ''))
              )
            )
            """;
    }

    public static string PublicSql()
    {
        return """
            (
              p.layout = 'group'
              OR (
                c.name NOT GLOB 'QL-[0-9]*'
                AND (
                  c.name LIKE '%GQ%'
                  OR c.name LIKE '%WSJ%'
                  OR c.name LIKE '%DTT%'
                  OR c.name LIKE '%FDT%'
                  OR c.name LIKE '%XFDT%'
                  OR c.name LIKE '%CSJ%'
                  OR c.name LIKE '%FWJ%'
                  OR c.name LIKE '%ZBS%'
                  OR c.name LIKE '%ZSG%'
                  OR c.name LIKE '%MD%'
                  OR c.name LIKE '%RDJHJF%'
                )
              )
            )
            """;
    }

    private static AreaGroupRaw ReadRawGroup(SqliteDataReader reader)
    {
        return new AreaGroupRaw(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            Name: ReadString(reader, "name"),
            AreaLabel: ReadString(reader, "area_label"),
            Description: ReadString(reader, "description"),
            Priority: ReadString(reader, "priority"),
            GroupKind: ReadString(reader, "group_kind"),
            SystemKey: ReadString(reader, "system_key"),
            Locked: ReadInt32(reader, "locked") != 0,
            Enabled: ReadInt32(reader, "enabled") != 0,
            GroupKey: ReadString(reader, "group_key"));
    }

    private static string Require(string value, string label)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException($"{label} is required.");
        }

        return normalized;
    }

    private static string NormalizePriority(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "重点" : normalized;
    }

    private static string NormalizeGroupKey(string value)
    {
        var normalized = Require(value, "group key");
        if (normalized.Length > 128 || normalized.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("group key 必须为 1-128 个不含空格的字符。", nameof(value));
        }

        return normalized;
    }

    private static string NormalizeTargetType(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized is "floor" or "sub_area" or "device" or "name_contains" or "name_excludes"
            ? normalized
            : "floor";
    }

    private static string NormalizeFloorLabel(string value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static double? ParseFloorValue(string floorLabel)
    {
        var normalized = NormalizeFloorLabel(floorLabel);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.StartsWith('B') && double.TryParse(normalized[1..^1], out var basement))
        {
            return -basement;
        }

        var trimmed = normalized.EndsWith('F') ? normalized[..^1] : normalized;
        return double.TryParse(trimmed, out var value) ? value : null;
    }

    private static double ReadDouble(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? 0 : reader.GetDouble(ordinal);
    }

    private static string FloorLabelFromValue(double? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return value < 0 ? $"B{Math.Abs(value.Value):0.#}F" : $"{value.Value:0.#}F";
    }

    private static object NullIfEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    }

    private static string ReadString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static int ReadInt32(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double? ReadNullableDouble(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private sealed record AreaGroupRaw(
        long Id,
        string Name,
        string AreaLabel,
        string Description,
        string Priority,
        string GroupKind,
        string SystemKey,
        bool Locked,
        bool Enabled,
        string GroupKey);

    private sealed record AreaGroupItemRaw(
        long Id,
        long GroupId);

    private sealed record GroupStats(
        int ItemCount,
        int Total,
        int OnCount,
        int OffCount,
        int OfflineCount,
        int UnknownCount,
        int CoveredAreas,
        int PublicTotal,
        int PublicOnCount,
        int PublicOffCount,
        int PublicOfflineCount,
        int PublicUnknownCount,
        int PublicCoveredAreas);
}
