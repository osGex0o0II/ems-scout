using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Infrastructure.Migrations;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteAreaGroupRepository(Func<string> databasePathResolver) : IAreaGroupRepository
{
    private static readonly string[] Buildings = ["1号", "2号", "3号", "4号", "5号", "6号"];

    public async Task<AreaGroupSet> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureSystemGroupsAsync(connection, cancellationToken).ConfigureAwait(false);
        var groups = await LoadGroupsAsync(connection, cancellationToken).ConfigureAwait(false);
        var items = await LoadItemsAsync(connection, null, cancellationToken).ConfigureAwait(false);
        return new AreaGroupSet(groups, items);
    }

    public async Task<IReadOnlyList<AreaGroupTargetOption>> LoadTargetOptionsAsync(
        string building,
        string floorLabel,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadOnly);
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
                var optionFloor = SqliteValueReader.ReadNullableDouble(reader, "floor");
                subAreas.Add(new AreaGroupTargetOption(
                    Type: "sub_area",
                    Building: SqliteValueReader.ReadString(reader, "building"),
                    FloorLabel: FloorLabelFromValue(optionFloor),
                    FloorValue: optionFloor,
                    SubAreaText: SqliteValueReader.ReadString(reader, "sub_area_text"),
                    CardName: string.Empty,
                    Count: SqliteValueReader.ReadInt32(reader, "count")));
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
                var optionFloor = SqliteValueReader.ReadNullableDouble(reader, "floor");
                devices.Add(new AreaGroupTargetOption(
                    Type: "device",
                    Building: SqliteValueReader.ReadString(reader, "building"),
                    FloorLabel: FloorLabelFromValue(optionFloor),
                    FloorValue: optionFloor,
                    SubAreaText: SqliteValueReader.ReadString(reader, "sub_area_text"),
                    CardName: SqliteValueReader.ReadString(reader, "card_name"),
                    Count: SqliteValueReader.ReadInt32(reader, "count")));
            }
        }

        return subAreas.Concat(devices).ToList();
    }

    public async Task<IReadOnlyList<FloorCatalogRecord>> LoadFloorsAsync(
        string building,
        bool includeDisabled = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
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
                Building: SqliteValueReader.ReadString(reader, "building"),
                FloorLabel: SqliteValueReader.ReadString(reader, "floor_label"),
                FloorValue: ReadDouble(reader, "floor_value"),
                Source: SqliteValueReader.ReadString(reader, "source"),
                Enabled: SqliteValueReader.ReadInt32(reader, "enabled") != 0,
                Note: SqliteValueReader.ReadString(reader, "note")));
        }

        return rows;
    }

    public async Task<FloorCatalogRecord> SaveFloorAsync(
        FloorCatalogEdit edit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var building = Require(edit.Building, "building");
        if (!Buildings.Contains(building, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid building: " + building);
        }

        var floorLabel = NormalizeFloorLabel(Require(edit.FloorLabel, "floor"));
        var floorValue = ParseFloorValue(floorLabel) ?? throw new ArgumentException("Invalid floor: " + floorLabel);
        var now = DateTimeOffset.UtcNow.ToString("O");
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
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE floor_catalog
            SET enabled = 0, updated_at = $updated_at
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScheduleGroupRecord>> LoadScheduleGroupsAsync(
        long areaGroupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var ids = new List<(long Id, string Name, string Description, bool Enabled)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, description, enabled FROM schedule_groups WHERE area_group_id = $area_group_id ORDER BY name, id";
            command.Parameters.AddWithValue("$area_group_id", areaGroupId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0));
            }
        }

        var result = new List<ScheduleGroupRecord>();
        foreach (var group in ids)
        {
            result.Add(new ScheduleGroupRecord(
                group.Id,
                areaGroupId,
                group.Name,
                group.Description,
                group.Enabled,
                await LoadScheduleRulesAsync(connection, group.Id, cancellationToken).ConfigureAwait(false),
                await LoadScheduleMembersAsync(connection, group.Id, cancellationToken).ConfigureAwait(false)));
        }

        return result;
    }

    public async Task<ScheduleGroupRecord> SaveScheduleGroupAsync(
        ScheduleGroupEdit edit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        _ = await LoadGroupRawAsync(connection, edit.AreaGroupId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("区域组不存在，请刷新后重试。");
        var name = Require(edit.Name, "schedule group name");
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (edit.Id is not null)
        {
            await using var ownership = connection.CreateCommand();
            ownership.CommandText = "SELECT area_group_id FROM schedule_groups WHERE id = $id";
            ownership.Parameters.AddWithValue("$id", edit.Id.Value);
            var owner = await ownership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (owner is null || Convert.ToInt64(owner) != edit.AreaGroupId)
            {
                throw new InvalidOperationException("时间组不属于当前区域组，请刷新后重试。");
            }
        }

        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.CommandText = "SELECT id FROM schedule_groups WHERE area_group_id = $area_group_id AND name = $name COLLATE NOCASE AND id <> $id LIMIT 1";
            duplicate.Parameters.AddWithValue("$area_group_id", edit.AreaGroupId);
            duplicate.Parameters.AddWithValue("$name", name);
            duplicate.Parameters.AddWithValue("$id", edit.Id ?? 0);
            if (await duplicate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException("当前区域组中已存在同名时间组。");
            }
        }

        long id;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = edit.Id is null
                ? "INSERT INTO schedule_groups (area_group_id, name, description, enabled, created_at, updated_at) VALUES ($area_group_id, $name, $description, $enabled, $created_at, $updated_at); SELECT last_insert_rowid();"
                : "UPDATE schedule_groups SET name = $name, description = $description, enabled = $enabled, updated_at = $updated_at WHERE id = $id; SELECT $id;";
            command.Parameters.AddWithValue("$area_group_id", edit.AreaGroupId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$description", edit.Description ?? string.Empty);
            command.Parameters.AddWithValue("$enabled", edit.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$created_at", now);
            command.Parameters.AddWithValue("$updated_at", now);
            command.Parameters.AddWithValue("$id", edit.Id ?? 0);
            id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        return (await LoadScheduleGroupsAsync(edit.AreaGroupId, cancellationToken).ConfigureAwait(false)).First(group => group.Id == id);
    }

    public async Task DeleteScheduleGroupAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var table in new[] { "schedule_intervals", "schedule_rules", "schedule_group_members", "schedule_groups" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = table switch
            {
                "schedule_intervals" => "DELETE FROM schedule_intervals WHERE rule_id IN (SELECT id FROM schedule_rules WHERE schedule_group_id = $id)",
                "schedule_rules" => "DELETE FROM schedule_rules WHERE schedule_group_id = $id",
                "schedule_group_members" => "DELETE FROM schedule_group_members WHERE schedule_group_id = $id",
                _ => "DELETE FROM schedule_groups WHERE id = $id",
            };
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScheduleRuleRecord> SaveScheduleRuleAsync(
        ScheduleRuleEdit edit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!DateOnly.TryParseExact(edit.CalendarDate, "yyyy-MM-dd", out var parsedDate))
        {
            throw new ArgumentException("日期格式必须为 yyyy-MM-dd。");
        }

        var date = parsedDate.ToString("yyyy-MM-dd");
        var status = edit.ExpectedStatus is "enabled" or "not_open"
            ? edit.ExpectedStatus
            : throw new ArgumentException("未知的计划状态。");
        var intervals = NormalizeScheduleIntervals(status, edit.Intervals);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var transaction = connection.BeginTransaction(deferred: false);
        long? existingId = edit.Id;
        if (existingId is not null)
        {
            await using var ownership = connection.CreateCommand();
            ownership.Transaction = transaction;
            ownership.CommandText = "SELECT schedule_group_id FROM schedule_rules WHERE id = $id";
            ownership.Parameters.AddWithValue("$id", existingId.Value);
            var owner = await ownership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (owner is null || Convert.ToInt64(owner) != edit.ScheduleGroupId)
            {
                throw new InvalidOperationException("日期规则不属于当前时间组，请刷新后重试。");
            }
        }
        else
        {
            await using var sameDate = connection.CreateCommand();
            sameDate.Transaction = transaction;
            sameDate.CommandText = "SELECT id FROM schedule_rules WHERE schedule_group_id = $group_id AND calendar_date = $date LIMIT 1";
            sameDate.Parameters.AddWithValue("$group_id", edit.ScheduleGroupId);
            sameDate.Parameters.AddWithValue("$date", date);
            var value = await sameDate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            existingId = value is null ? null : Convert.ToInt64(value);
        }

        long id;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = existingId is null
                ? "INSERT INTO schedule_rules (schedule_group_id, calendar_date, expected_status, note, created_at, updated_at) VALUES ($group_id, $date, $status, $note, $created_at, $updated_at); SELECT last_insert_rowid();"
                : "UPDATE schedule_rules SET calendar_date = $date, expected_status = $status, note = $note, updated_at = $updated_at WHERE id = $id; SELECT $id;";
            command.Parameters.AddWithValue("$group_id", edit.ScheduleGroupId);
            command.Parameters.AddWithValue("$date", date);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$note", edit.Note ?? string.Empty);
            command.Parameters.AddWithValue("$created_at", now);
            command.Parameters.AddWithValue("$updated_at", now);
            command.Parameters.AddWithValue("$id", existingId ?? 0);
            id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM schedule_intervals WHERE rule_id = $rule_id";
            delete.Parameters.AddWithValue("$rule_id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var interval in intervals)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO schedule_intervals (rule_id, start_time, end_time, created_at) VALUES ($rule_id, $start_time, $end_time, $created_at)";
            insert.Parameters.AddWithValue("$rule_id", id);
            insert.Parameters.AddWithValue("$start_time", interval.StartTime);
            insert.Parameters.AddWithValue("$end_time", interval.EndTime);
            insert.Parameters.AddWithValue("$created_at", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        await using var reload = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(reload, cancellationToken).ConfigureAwait(false);
        return (await LoadScheduleRulesAsync(reload, edit.ScheduleGroupId, cancellationToken).ConfigureAwait(false)).First(rule => rule.Id == id);
    }

    public async Task<IReadOnlyList<ScheduleRuleRecord>> SaveScheduleRulesAsync(
        ScheduleRuleBatchEdit edit,
        CancellationToken cancellationToken = default)
    {
        var dates = NormalizeScheduleDates(edit.CalendarDates);
        if (dates.Count == 0)
        {
            throw new ArgumentException("请至少选择一个日期。");
        }

        var status = edit.ExpectedStatus is "enabled" or "not_open"
            ? edit.ExpectedStatus
            : throw new ArgumentException("未知的计划状态。");
        var intervals = NormalizeScheduleIntervals(status, edit.Intervals);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using (var ownership = connection.CreateCommand())
        {
            ownership.Transaction = transaction;
            ownership.CommandText = "SELECT COUNT(*) FROM schedule_groups WHERE id = $id";
            ownership.Parameters.AddWithValue("$id", edit.ScheduleGroupId);
            if (Convert.ToInt64(await ownership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1)
            {
                throw new InvalidOperationException("时间组不存在，请刷新后重试。");
            }
        }

        foreach (var date in dates)
        {
            long ruleId;
            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO schedule_rules
                        (schedule_group_id, calendar_date, expected_status, note, created_at, updated_at)
                    VALUES ($group_id, $date, $status, $note, $created_at, $updated_at)
                    ON CONFLICT(schedule_group_id, calendar_date) DO UPDATE SET
                        expected_status = excluded.expected_status,
                        note = excluded.note,
                        updated_at = excluded.updated_at
                    RETURNING id
                    """;
                upsert.Parameters.AddWithValue("$group_id", edit.ScheduleGroupId);
                upsert.Parameters.AddWithValue("$date", date);
                upsert.Parameters.AddWithValue("$status", status);
                upsert.Parameters.AddWithValue("$note", edit.Note ?? string.Empty);
                upsert.Parameters.AddWithValue("$created_at", now);
                upsert.Parameters.AddWithValue("$updated_at", now);
                ruleId = Convert.ToInt64(await upsert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            }

            await using (var deleteIntervals = connection.CreateCommand())
            {
                deleteIntervals.Transaction = transaction;
                deleteIntervals.CommandText = "DELETE FROM schedule_intervals WHERE rule_id = $rule_id";
                deleteIntervals.Parameters.AddWithValue("$rule_id", ruleId);
                await deleteIntervals.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var interval in intervals)
            {
                await using var insertInterval = connection.CreateCommand();
                insertInterval.Transaction = transaction;
                insertInterval.CommandText = "INSERT INTO schedule_intervals (rule_id, start_time, end_time, created_at) VALUES ($rule_id, $start_time, $end_time, $created_at)";
                insertInterval.Parameters.AddWithValue("$rule_id", ruleId);
                insertInterval.Parameters.AddWithValue("$start_time", interval.StartTime);
                insertInterval.Parameters.AddWithValue("$end_time", interval.EndTime);
                insertInterval.Parameters.AddWithValue("$created_at", now);
                await insertInterval.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var dateSet = dates.ToHashSet(StringComparer.Ordinal);
        return (await LoadScheduleRulesAsync(connection, edit.ScheduleGroupId, cancellationToken).ConfigureAwait(false))
            .Where(rule => dateSet.Contains(rule.CalendarDate))
            .ToArray();
    }

    public async Task DeleteScheduleRuleAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var sql in new[] { "DELETE FROM schedule_intervals WHERE rule_id = $id", "DELETE FROM schedule_rules WHERE id = $id" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteScheduleRulesAsync(
        long scheduleGroupId,
        IReadOnlyList<string> calendarDates,
        CancellationToken cancellationToken = default)
    {
        var dates = NormalizeScheduleDates(calendarDates);
        if (dates.Count == 0)
        {
            return;
        }

        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var parameters = new List<string>(dates.Count);
        for (var index = 0; index < dates.Count; index++)
        {
            parameters.Add("$date_" + index);
        }
        var dateList = string.Join(",", parameters);

        await using (var deleteIntervals = connection.CreateCommand())
        {
            deleteIntervals.Transaction = transaction;
            deleteIntervals.CommandText = $"DELETE FROM schedule_intervals WHERE rule_id IN (SELECT id FROM schedule_rules WHERE schedule_group_id = $group_id AND calendar_date IN ({dateList}))";
            deleteIntervals.Parameters.AddWithValue("$group_id", scheduleGroupId);
            for (var index = 0; index < dates.Count; index++)
            {
                deleteIntervals.Parameters.AddWithValue(parameters[index], dates[index]);
            }
            await deleteIntervals.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteRules = connection.CreateCommand())
        {
            deleteRules.Transaction = transaction;
            deleteRules.CommandText = $"DELETE FROM schedule_rules WHERE schedule_group_id = $group_id AND calendar_date IN ({dateList})";
            deleteRules.Parameters.AddWithValue("$group_id", scheduleGroupId);
            for (var index = 0; index < dates.Count; index++)
            {
                deleteRules.Parameters.AddWithValue(parameters[index], dates[index]);
            }
            await deleteRules.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScheduleMemberRecord> SaveScheduleMemberAsync(
        ScheduleMemberEdit edit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var targetType = NormalizeTargetType(edit.TargetType);
        var building = Require(edit.Building, "building");
        if (!Buildings.Contains(building, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid building: " + building);
        }

        var floorLabel = string.IsNullOrWhiteSpace(edit.FloorLabel) ? string.Empty : NormalizeFloorLabel(edit.FloorLabel);
        var floorValue = ParseFloorValue(floorLabel);
        var subArea = (edit.SubAreaText ?? string.Empty).Trim();
        var cardName = (edit.CardName ?? string.Empty).Trim();
        ValidateTarget(targetType, floorValue, subArea, cardName);
        var memberStatus = edit.ExpectedStatus is "not_open" or "normal"
            ? edit.ExpectedStatus
            : throw new ArgumentException("未知的成员预期状态。");
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using (var groupOwnership = connection.CreateCommand())
        {
            groupOwnership.Transaction = transaction;
            groupOwnership.CommandText = "SELECT area_group_id FROM schedule_groups WHERE id = $id";
            groupOwnership.Parameters.AddWithValue("$id", edit.ScheduleGroupId);
            var ownerValue = await groupOwnership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (ownerValue is null)
            {
                throw new InvalidOperationException("时间组不存在，请刷新后重试。");
            }

            if (edit.AreaGroupItemId is not null)
            {
                await using var itemOwnership = connection.CreateCommand();
                itemOwnership.Transaction = transaction;
                itemOwnership.CommandText = "SELECT group_id FROM monitor_group_items WHERE id = $id";
                itemOwnership.Parameters.AddWithValue("$id", edit.AreaGroupItemId.Value);
                var itemOwner = await itemOwnership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (itemOwner is null || Convert.ToInt64(itemOwner) != Convert.ToInt64(ownerValue))
                {
                    throw new InvalidOperationException("时间组成员必须来自当前区域组。");
                }
            }
        }

        long? existingId = edit.Id;
        if (existingId is not null)
        {
            await using var ownership = connection.CreateCommand();
            ownership.Transaction = transaction;
            ownership.CommandText = "SELECT schedule_group_id FROM schedule_group_members WHERE id = $id";
            ownership.Parameters.AddWithValue("$id", existingId.Value);
            var owner = await ownership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (owner is null || Convert.ToInt64(owner) != edit.ScheduleGroupId)
            {
                throw new InvalidOperationException("成员不属于当前时间组，请刷新后重试。");
            }
        }
        else if (edit.AreaGroupItemId is not null)
        {
            await using var duplicate = connection.CreateCommand();
            duplicate.Transaction = transaction;
            duplicate.CommandText = "SELECT id FROM schedule_group_members WHERE schedule_group_id = $group_id AND area_group_item_id = $item_id LIMIT 1";
            duplicate.Parameters.AddWithValue("$group_id", edit.ScheduleGroupId);
            duplicate.Parameters.AddWithValue("$item_id", edit.AreaGroupItemId.Value);
            var value = await duplicate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            existingId = value is null ? null : Convert.ToInt64(value);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = existingId is null
            ? "INSERT INTO schedule_group_members (schedule_group_id, area_group_item_id, target_type, building, floor_label, floor_value, sub_area_text, card_name, device_uid, expected_status, note, created_at, updated_at) VALUES ($group_id, $item_id, $type, $building, $floor_label, $floor_value, $sub_area, $card_name, $device_uid, $status, $note, $created_at, $updated_at); SELECT last_insert_rowid();"
            : "UPDATE schedule_group_members SET area_group_item_id = $item_id, target_type = $type, building = $building, floor_label = $floor_label, floor_value = $floor_value, sub_area_text = $sub_area, card_name = $card_name, device_uid = $device_uid, expected_status = $status, note = $note, updated_at = $updated_at WHERE id = $id; SELECT $id;";
        command.Parameters.AddWithValue("$group_id", edit.ScheduleGroupId);
        command.Parameters.AddWithValue("$item_id", edit.AreaGroupItemId is null ? DBNull.Value : edit.AreaGroupItemId.Value);
        command.Parameters.AddWithValue("$type", targetType);
        command.Parameters.AddWithValue("$building", building);
        command.Parameters.AddWithValue("$floor_label", NullIfEmpty(floorLabel));
        command.Parameters.AddWithValue("$floor_value", floorValue is null ? DBNull.Value : floorValue.Value);
        command.Parameters.AddWithValue("$sub_area", NullIfEmpty(subArea));
        command.Parameters.AddWithValue("$card_name", NullIfEmpty(cardName));
        command.Parameters.AddWithValue("$device_uid", NullIfEmpty(edit.DeviceUid));
        command.Parameters.AddWithValue("$status", memberStatus);
        command.Parameters.AddWithValue("$note", edit.Note ?? string.Empty);
        command.Parameters.AddWithValue("$created_at", now);
        command.Parameters.AddWithValue("$updated_at", now);
        command.Parameters.AddWithValue("$id", existingId ?? 0);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var group = await LoadScheduleGroupsAsync(await ReadScheduleAreaGroupIdAsync(connection, id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return group.SelectMany(item => item.Members).First(member => member.Id == id);
    }

    public async Task DeleteScheduleMemberAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM schedule_group_members WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScheduleAuditRecord>> EvaluateSchedulesAsync(
        long? runId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var observedAt = await ResolveScheduleObservationTimeAsync(connection, runId, at, cancellationToken).ConfigureAwait(false);
        var localObservation = observedAt.ToLocalTime();
        var date = localObservation.ToString("yyyy-MM-dd");
        var raw = new List<ScheduleAuditRaw>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT ag.name AS area_group_name, sg.name AS schedule_group_name,
                       r.calendar_date, r.expected_status AS rule_status,
                       m.target_type, m.building, COALESCE(m.floor_label, '') AS floor_label,
                       m.floor_value, COALESCE(m.sub_area_text, '') AS sub_area_text,
                       COALESCE(m.card_name, '') AS card_name, COALESCE(m.device_uid, '') AS device_uid,
                       m.expected_status AS member_status, COALESCE(m.note, '') AS note,
                       COALESCE(si.start_time, '') AS start_time, COALESCE(si.end_time, '') AS end_time
                FROM schedule_groups sg
                JOIN monitor_groups ag ON ag.id = sg.area_group_id
                JOIN schedule_rules r ON r.schedule_group_id = sg.id AND r.calendar_date = $date
                JOIN schedule_group_members m ON m.schedule_group_id = sg.id
                LEFT JOIN schedule_intervals si ON si.rule_id = r.id
                WHERE sg.enabled = 1 AND ag.enabled = 1
                ORDER BY ag.name, sg.name, m.building, m.floor_value, m.card_name, si.start_time
                """;
            command.Parameters.AddWithValue("$date", date);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                raw.Add(new ScheduleAuditRaw(
                    SqliteValueReader.ReadString(reader, "area_group_name"),
                    SqliteValueReader.ReadString(reader, "schedule_group_name"),
                    SqliteValueReader.ReadString(reader, "calendar_date"),
                    SqliteValueReader.ReadString(reader, "rule_status"),
                    SqliteValueReader.ReadString(reader, "target_type"),
                    SqliteValueReader.ReadString(reader, "building"),
                    SqliteValueReader.ReadString(reader, "floor_label"),
                    SqliteValueReader.ReadNullableDouble(reader, "floor_value"),
                    SqliteValueReader.ReadString(reader, "sub_area_text"),
                    SqliteValueReader.ReadString(reader, "card_name"),
                    SqliteValueReader.ReadString(reader, "device_uid"),
                    SqliteValueReader.ReadString(reader, "member_status"),
                    SqliteValueReader.ReadString(reader, "note"),
                    SqliteValueReader.ReadString(reader, "start_time"),
                    SqliteValueReader.ReadString(reader, "end_time")));
            }
        }

        var result = new List<ScheduleAuditRecord>();
        foreach (var group in raw.GroupBy(item => new
        {
            item.AreaGroupName,
            item.ScheduleGroupName,
            item.CalendarDate,
            item.RuleStatus,
            item.TargetType,
            item.Building,
            item.FloorLabel,
            item.FloorValue,
            item.SubAreaText,
            item.CardName,
            item.DeviceUid,
            item.MemberStatus,
            item.Note,
        }))
        {
            var intervals = group
                .Where(item => !string.IsNullOrWhiteSpace(item.StartTime) && !string.IsNullOrWhiteSpace(item.EndTime))
                .Select(item => $"{item.StartTime}-{item.EndTime}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var configuredTarget = string.Join(" / ", new[] { group.Key.Building, group.Key.FloorLabel, group.Key.SubAreaText, group.Key.CardName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var devices = await ReadScheduleActualDevicesAsync(connection, runId, group.Key.TargetType, group.Key.Building,
                group.Key.FloorValue, group.Key.SubAreaText, group.Key.CardName, group.Key.DeviceUid, configuredTarget, cancellationToken).ConfigureAwait(false);
            var expectedNotOpen = group.Key.MemberStatus == "not_open" || group.Key.RuleStatus == "not_open";
            var inWindow = intervals.Any(interval => IsWithinInterval(localObservation.TimeOfDay, interval));
            foreach (var device in devices)
            {
                var resultCode = expectedNotOpen
                    ? device.ActualStatus == "开机" ? "unexpected_running" : "not_open"
                    : inWindow
                        ? device.ActualStatus == "开机" ? "ok" : "not_enabled"
                        : device.ActualStatus == "开机" ? "unexpected_running" : "outside_window";
                var detail = resultCode switch
                {
                    "not_enabled" => $"在计划启用时间内，实际状态为 {device.ActualStatus}。来源：{group.Key.AreaGroupName} / {group.Key.ScheduleGroupName}。",
                    "unexpected_running" => $"当前不应启用，但实际状态为开机。来源：{group.Key.AreaGroupName} / {group.Key.ScheduleGroupName}。",
                    "not_open" => "人工标记为未开放，当前未运行。",
                    "outside_window" => "当前不在计划启用时间段内，设备未运行。",
                    _ => "实际状态符合计划。",
                };
                result.Add(new ScheduleAuditRecord(
                    group.Key.AreaGroupName, group.Key.ScheduleGroupName, group.Key.CalendarDate,
                    intervals.Length == 0 ? "全天不启用" : string.Join("、", intervals),
                    group.Key.TargetType, device.TargetLabel, localObservation.ToString("yyyy-MM-dd HH:mm"),
                    expectedNotOpen ? "未开放" : inWindow ? "应启用" : "不应启用",
                    device.ActualStatus, resultCode, detail));
            }
        }

        return result;
    }

    private static async Task<IReadOnlyList<ScheduleObservedDevice>> ReadScheduleActualDevicesAsync(
        SqliteConnection connection,
        long? runId,
        string targetType,
        string building,
        double? floorValue,
        string subAreaText,
        string cardName,
        string deviceUid,
        string configuredTarget,
        CancellationToken cancellationToken)
    {
        var cardTable = runId is null ? "cards c JOIN pages p ON p.id = c.page_id JOIN sub_areas s ON s.id = p.sub_area_id" : "run_cards c JOIN run_pages p ON p.id = c.run_page_id JOIN run_sub_areas s ON s.id = p.run_sub_area_id";
        var runClause = runId is null ? string.Empty : " AND c.run_id = $run_id";
        var targetClause = targetType switch
        {
            "device" => "AND c.name = $card_name AND ABS(COALESCE(s.floor, -999999) - COALESCE($floor_value, -999999)) < 0.001 AND (NULLIF($sub_area_text, '') IS NULL OR s.text = $sub_area_text) AND (NULLIF($device_uid, '') IS NULL OR c.device_uid = $device_uid)",
            "sub_area" => "AND ABS(COALESCE(s.floor, -999999) - COALESCE($floor_value, -999999)) < 0.001 AND s.text = $sub_area_text",
            _ => "AND ABS(COALESCE(s.floor, -999999) - COALESCE($floor_value, -999999)) < 0.001",
        };
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT s.building, s.floor, COALESCE(s.text, '') AS sub_area_text, COALESCE(c.name, '') AS card_name, COALESCE(c.device_uid, '') AS device_uid, COALESCE(c.comm, '') AS comm, COALESCE(c.switch, '') AS device_switch FROM {cardTable} WHERE s.building = $building {targetClause}{runClause}";
        command.Parameters.AddWithValue("$building", building);
        command.Parameters.AddWithValue("$floor_value", floorValue is null ? DBNull.Value : floorValue.Value);
        command.Parameters.AddWithValue("$sub_area_text", subAreaText);
        command.Parameters.AddWithValue("$card_name", cardName);
        command.Parameters.AddWithValue("$device_uid", deviceUid);
        if (runId is not null) command.Parameters.AddWithValue("$run_id", runId.Value);
        var rows = new List<(string Building, double? Floor, string SubArea, string CardName, string DeviceUid, string Comm, string Switch)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((
                SqliteValueReader.ReadString(reader, "building"),
                SqliteValueReader.ReadNullableDouble(reader, "floor"),
                SqliteValueReader.ReadString(reader, "sub_area_text"),
                SqliteValueReader.ReadString(reader, "card_name"),
                SqliteValueReader.ReadString(reader, "device_uid"),
                SqliteValueReader.ReadString(reader, "comm"),
                SqliteValueReader.ReadString(reader, "device_switch")));
        }

        if (rows.Count == 0)
        {
            return [new ScheduleObservedDevice(configuredTarget, "未采集")];
        }

        return rows
            .GroupBy(row => string.IsNullOrWhiteSpace(row.DeviceUid)
                ? $"{row.Building}|{row.Floor}|{row.SubArea}|{row.CardName}"
                : row.DeviceUid,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var statuses = group.Select(item => DeviceOperatingStatusResolver.ResolveText(item.Comm, item.Switch)).ToArray();
                var status = statuses.Contains("开机") ? "开机"
                    : statuses.Contains("离线") ? "离线"
                    : statuses.Contains("关机") ? "关机" : "未知";
                var label = string.Join(" / ", new[]
                {
                    first.Building, FloorLabelFromValue(first.Floor), first.SubArea, first.CardName,
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                return new ScheduleObservedDevice(label, status);
            })
            .OrderBy(device => device.TargetLabel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<DateTimeOffset> ResolveScheduleObservationTimeAsync(
        SqliteConnection connection,
        long? runId,
        DateTimeOffset fallback,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = runId is null
            ? "SELECT completed_at FROM collection_runs WHERE status = 'completed' ORDER BY completed_at DESC, id DESC LIMIT 1"
            : "SELECT completed_at FROM collection_runs WHERE id = $run_id AND status = 'completed' LIMIT 1";
        if (runId is not null) command.Parameters.AddWithValue("$run_id", runId.Value);
        var value = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static bool IsWithinInterval(TimeSpan now, string value)
    {
        var parts = value.Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && TimeOnly.TryParse(parts[0], out var start) && TimeOnly.TryParse(parts[1], out var end) &&
               now >= start.ToTimeSpan() && now < end.ToTimeSpan();
    }

    private static IReadOnlyList<string> NormalizeScheduleDates(IReadOnlyList<string> dates)
    {
        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in dates ?? [])
        {
            if (!DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", out var date))
            {
                throw new ArgumentException($"日期格式必须为 yyyy-MM-dd：{value}");
            }

            normalized.Add(date.ToString("yyyy-MM-dd"));
        }

        return normalized.ToArray();
    }

    private static IReadOnlyList<ScheduleIntervalEdit> NormalizeScheduleIntervals(
        string status,
        IReadOnlyList<ScheduleIntervalEdit> intervals)
    {
        if (status == "not_open")
        {
            if (intervals.Count > 0)
            {
                throw new ArgumentException("全天不启用不能同时配置启用时间段。");
            }

            return [];
        }

        var normalized = new List<(TimeOnly Start, TimeOnly End)>();
        foreach (var interval in intervals)
        {
            if (!TimeOnly.TryParseExact(interval.StartTime, "HH:mm", out var start) ||
                !TimeOnly.TryParseExact(interval.EndTime, "HH:mm", out var end) || start >= end)
            {
                throw new ArgumentException("启用时间段必须使用 HH:mm-HH:mm，且结束时间晚于开始时间。");
            }

            normalized.Add((start, end));
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException("按时间启用至少需要一个时间段。");
        }

        normalized.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < normalized.Count; index++)
        {
            if (normalized[index].Start < normalized[index - 1].End)
            {
                throw new ArgumentException("启用时间段不能重叠。");
            }
        }

        return normalized
            .Distinct()
            .Select(interval => new ScheduleIntervalEdit(interval.Start.ToString("HH:mm"), interval.End.ToString("HH:mm")))
            .ToArray();
    }

    public async Task<AreaGroupRecord> SaveGroupAsync(
        AreaGroupEdit edit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var name = Require(edit.Name, "group name");
        var priority = NormalizePriority(edit.Priority);
        var now = DateTimeOffset.UtcNow.ToString("O");

        if (edit.Id is not null)
        {
            var current = await LoadGroupRawAsync(connection, edit.Id.Value, cancellationToken).ConfigureAwait(false)
                          ?? throw new InvalidOperationException($"Group not found: {edit.Id.Value}");
            await using var update = connection.CreateCommand();
            if (current.Locked)
            {
                update.CommandText = """
                    UPDATE monitor_groups
                    SET area_label = $area_label, description = $description, enabled = $enabled, updated_at = $updated_at
                    WHERE id = $id
                    """;
                update.Parameters.AddWithValue("$area_label", edit.AreaLabel ?? string.Empty);
                update.Parameters.AddWithValue("$description", edit.Description ?? string.Empty);
                update.Parameters.AddWithValue("$enabled", edit.Enabled ? 1 : 0);
                update.Parameters.AddWithValue("$updated_at", now);
                update.Parameters.AddWithValue("$id", edit.Id.Value);
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
                        priority = $priority, enabled = $enabled, updated_at = $updated_at
                    WHERE id = $id
                    """;
                update.Parameters.AddWithValue("$name", name);
                update.Parameters.AddWithValue("$area_label", edit.AreaLabel ?? string.Empty);
                update.Parameters.AddWithValue("$description", edit.Description ?? string.Empty);
                update.Parameters.AddWithValue("$priority", priority);
                update.Parameters.AddWithValue("$enabled", edit.Enabled ? 1 : 0);
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

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO monitor_groups (name, area_label, description, priority, enabled, created_at, updated_at)
            VALUES ($name, $area_label, $description, $priority, $enabled, $created_at, $updated_at)
            RETURNING id
            """;
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$area_label", edit.AreaLabel ?? string.Empty);
        insert.Parameters.AddWithValue("$description", edit.Description ?? string.Empty);
        insert.Parameters.AddWithValue("$priority", priority);
        insert.Parameters.AddWithValue("$enabled", edit.Enabled ? 1 : 0);
        insert.Parameters.AddWithValue("$created_at", now);
        insert.Parameters.AddWithValue("$updated_at", now);
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        return (await LoadGroupsAsync(connection, cancellationToken).ConfigureAwait(false)).First(group => group.Id == id);
    }

    public async Task DeleteGroupAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var current = await LoadGroupRawAsync(connection, id, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException($"Group not found: {id}");
        if (current.Locked || current.GroupKind.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("系统区域不能删除");
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await DeleteSchedulesForAreaGroupAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (await SqliteSchemaGuard.TableExistsAsync(connection, "device_watch_rules", cancellationToken).ConfigureAwait(false))
        {
            await using var deleteWatchRules = connection.CreateCommand();
            deleteWatchRules.Transaction = (SqliteTransaction)transaction;
            deleteWatchRules.CommandText = "DELETE FROM device_watch_rules WHERE group_id = $id";
            deleteWatchRules.Parameters.AddWithValue("$id", id);
            await deleteWatchRules.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteItems = connection.CreateCommand())
        {
            deleteItems.Transaction = (SqliteTransaction)transaction;
            deleteItems.CommandText = "DELETE FROM monitor_group_items WHERE group_id = $id";
            deleteItems.Parameters.AddWithValue("$id", id);
            await deleteItems.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task DeleteSchedulesForAreaGroupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long areaGroupId,
        CancellationToken cancellationToken)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM schedule_intervals WHERE rule_id IN (SELECT r.id FROM schedule_rules r JOIN schedule_groups sg ON sg.id = r.schedule_group_id WHERE sg.area_group_id = $id)",
                     "DELETE FROM schedule_rules WHERE schedule_group_id IN (SELECT id FROM schedule_groups WHERE area_group_id = $id)",
                     "DELETE FROM schedule_group_members WHERE schedule_group_id IN (SELECT id FROM schedule_groups WHERE area_group_id = $id)",
                     "DELETE FROM schedule_groups WHERE area_group_id = $id",
                 })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", areaGroupId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<AreaGroupItemRecord> SaveItemAsync(
        AreaGroupItemEdit edit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var group = await LoadGroupRawAsync(connection, edit.GroupId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Group not found: {edit.GroupId}");
        var targetType = NormalizeTargetType(edit.TargetType);
        var building = Require(edit.Building, "building");
        if (!Buildings.Contains(building, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid building: " + building);
        }

        var floorLabel = string.IsNullOrWhiteSpace(edit.FloorLabel) ? string.Empty : NormalizeFloorLabel(edit.FloorLabel);
        var floorValue = string.IsNullOrWhiteSpace(floorLabel) ? null : ParseFloorValue(floorLabel);
        var subArea = (edit.SubAreaText ?? string.Empty).Trim();
        var cardName = (edit.CardName ?? string.Empty).Trim();
        if (targetType == "floor" && floorValue is null)
        {
            throw new ArgumentException("floor target requires floor label.");
        }

        if (targetType == "sub_area" && (floorValue is null || string.IsNullOrWhiteSpace(subArea)))
        {
            throw new ArgumentException("sub_area target requires floor and sub area.");
        }

        if (targetType == "device" && (floorValue is null || string.IsNullOrWhiteSpace(subArea) || string.IsNullOrWhiteSpace(cardName)))
        {
            throw new ArgumentException("device target requires floor, sub area and card name.");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existingItem = edit.Id is null
            ? null
            : await LoadItemRawAsync(connection, edit.Id.Value, cancellationToken).ConfigureAwait(false)
              ?? throw new InvalidOperationException($"Group item not found: {edit.Id.Value}");
        if (existingItem is not null && existingItem.GroupId != edit.GroupId)
        {
            throw new InvalidOperationException("分组成员不属于当前区域组。");
        }

        var duplicateId = await FindItemIdAsync(
            connection,
            edit.GroupId,
            targetType,
            building,
            floorValue,
            subArea,
            cardName,
            edit.Id,
            cancellationToken).ConfigureAwait(false);
        if (duplicateId is not null)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE monitor_group_items
                SET target_type = $target_type,
                    building = $building,
                    floor_label = $floor_label,
                    floor_value = $floor_value,
                    sub_area_text = $sub_area_text,
                    card_name = $card_name,
                    note = $note,
                    updated_at = $updated_at
                WHERE id = $id
                """;
            update.Parameters.AddWithValue("$target_type", targetType);
            update.Parameters.AddWithValue("$building", building);
            update.Parameters.AddWithValue("$floor_label", NullIfEmpty(floorLabel));
            update.Parameters.AddWithValue("$floor_value", floorValue is null ? DBNull.Value : floorValue);
            update.Parameters.AddWithValue("$sub_area_text", NullIfEmpty(subArea));
            update.Parameters.AddWithValue("$card_name", NullIfEmpty(cardName));
            update.Parameters.AddWithValue("$note", edit.Note ?? string.Empty);
            update.Parameters.AddWithValue("$updated_at", now);
            update.Parameters.AddWithValue("$id", duplicateId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (edit.Id is not null)
            {
                await MoveScheduleMembersAsync(
                    connection, transaction, edit.Id.Value, duplicateId.Value,
                    targetType, building, floorLabel, floorValue, subArea, cardName, cancellationToken).ConfigureAwait(false);
                await DeleteItemCoreAsync(connection, transaction, edit.Id.Value, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (await LoadItemsAsync(connection, edit.GroupId, cancellationToken).ConfigureAwait(false))
                .First(item => item.Id == duplicateId.Value);
        }

        if (edit.Id is not null)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE monitor_group_items
                SET target_type = $target_type,
                    building = $building,
                    floor_label = $floor_label,
                    floor_value = $floor_value,
                    sub_area_text = $sub_area_text,
                    card_name = $card_name,
                    note = $note,
                    updated_at = $updated_at
                WHERE id = $id
                """;
            update.Parameters.AddWithValue("$target_type", targetType);
            update.Parameters.AddWithValue("$building", building);
            update.Parameters.AddWithValue("$floor_label", NullIfEmpty(floorLabel));
            update.Parameters.AddWithValue("$floor_value", floorValue is null ? DBNull.Value : floorValue);
            update.Parameters.AddWithValue("$sub_area_text", NullIfEmpty(subArea));
            update.Parameters.AddWithValue("$card_name", NullIfEmpty(cardName));
            update.Parameters.AddWithValue("$note", edit.Note ?? string.Empty);
            update.Parameters.AddWithValue("$updated_at", now);
            update.Parameters.AddWithValue("$id", edit.Id.Value);
            var changes = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changes == 0)
            {
                throw new InvalidOperationException($"Group item not found: {edit.Id.Value}");
            }

            await UpdateScheduleMembersFromAreaItemAsync(
                connection, transaction, edit.Id.Value, targetType, building, floorLabel,
                floorValue, subArea, cardName, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (await LoadItemsAsync(connection, edit.GroupId, cancellationToken).ConfigureAwait(false))
                .First(item => item.Id == edit.Id.Value);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO monitor_group_items
              (group_id, target_type, building, floor_label, floor_value, sub_area_text, card_name, note, created_at, updated_at)
            VALUES ($group_id, $target_type, $building, $floor_label, $floor_value, $sub_area_text, $card_name, $note, $created_at, $updated_at)
            RETURNING id
            """;
        insert.Parameters.AddWithValue("$group_id", edit.GroupId);
        insert.Parameters.AddWithValue("$target_type", targetType);
        insert.Parameters.AddWithValue("$building", building);
        insert.Parameters.AddWithValue("$floor_label", NullIfEmpty(floorLabel));
        insert.Parameters.AddWithValue("$floor_value", floorValue is null ? DBNull.Value : floorValue);
        insert.Parameters.AddWithValue("$sub_area_text", NullIfEmpty(subArea));
        insert.Parameters.AddWithValue("$card_name", NullIfEmpty(cardName));
        insert.Parameters.AddWithValue("$note", edit.Note ?? string.Empty);
        insert.Parameters.AddWithValue("$created_at", now);
        insert.Parameters.AddWithValue("$updated_at", now);
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await LoadItemsAsync(connection, edit.GroupId, cancellationToken).ConfigureAwait(false))
            .First(item => item.Id == id);
    }

    public async Task DeleteItemAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await DeleteItemCoreAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteItemCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        CancellationToken cancellationToken)
    {
        await using (var scheduleMembers = connection.CreateCommand())
        {
            scheduleMembers.Transaction = transaction;
            scheduleMembers.CommandText = "DELETE FROM schedule_group_members WHERE area_group_item_id = $id";
            scheduleMembers.Parameters.AddWithValue("$id", id);
            await scheduleMembers.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM monitor_group_items WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ScheduleRuleRecord>> LoadScheduleRulesAsync(
        SqliteConnection connection,
        long scheduleGroupId,
        CancellationToken cancellationToken)
    {
        var rawRules = new List<(long Id, long GroupId, string Date, string Status, string Note)>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, schedule_group_id, calendar_date, expected_status, note FROM schedule_rules WHERE schedule_group_id = $group_id ORDER BY calendar_date, id";
        command.Parameters.AddWithValue("$group_id", scheduleGroupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rawRules.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        var rules = new List<ScheduleRuleRecord>();
        foreach (var raw in rawRules)
        {
            rules.Add(new ScheduleRuleRecord(
                raw.Id,
                raw.GroupId,
                raw.Date,
                raw.Status,
                raw.Note,
                await LoadScheduleIntervalsAsync(connection, raw.Id, cancellationToken).ConfigureAwait(false)));
        }
        return rules;
    }

    private static async Task<IReadOnlyList<ScheduleIntervalRecord>> LoadScheduleIntervalsAsync(
        SqliteConnection connection,
        long ruleId,
        CancellationToken cancellationToken)
    {
        var result = new List<ScheduleIntervalRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, rule_id, start_time, end_time FROM schedule_intervals WHERE rule_id = $rule_id ORDER BY start_time, id";
        command.Parameters.AddWithValue("$rule_id", ruleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ScheduleIntervalRecord(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<ScheduleMemberRecord>> LoadScheduleMembersAsync(
        SqliteConnection connection,
        long scheduleGroupId,
        CancellationToken cancellationToken)
    {
        var result = new List<ScheduleMemberRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, schedule_group_id, area_group_item_id, target_type, building, COALESCE(floor_label, ''), floor_value, COALESCE(sub_area_text, ''), COALESCE(card_name, ''), COALESCE(device_uid, ''), expected_status, note FROM schedule_group_members WHERE schedule_group_id = $group_id ORDER BY building, floor_value, sub_area_text, card_name, id";
        command.Parameters.AddWithValue("$group_id", scheduleGroupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ScheduleMemberRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11)));
        }
        return result;
    }

    private static async Task<long> ReadScheduleAreaGroupIdAsync(
        SqliteConnection connection,
        long memberId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sg.area_group_id FROM schedule_group_members m JOIN schedule_groups sg ON sg.id = m.schedule_group_id WHERE m.id = $id";
        command.Parameters.AddWithValue("$id", memberId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task EnsureSystemGroupsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var definition in new[]
                 {
                     (Name: "公区", Label: "公区", Key: "public", Description: "规则识别的公区；可维护人工成员，并在日期管理中设置计划。"),
                     (Name: "非公区", Label: "非公区", Key: "non_public", Description: "规则识别的非公区；可维护人工成员，并在日期管理中设置计划。"),
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO monitor_groups
                    (name, area_label, description, priority, group_kind, system_key, locked, enabled, created_at, updated_at)
                VALUES
                    ($name, $area_label, $description, '重点', 'system', $system_key, 1, 1, $created_at, $updated_at)
                ON CONFLICT(name) DO UPDATE SET
                    area_label = excluded.area_label,
                    description = CASE WHEN monitor_groups.description = '' THEN excluded.description ELSE monitor_groups.description END,
                    group_kind = 'system',
                    system_key = excluded.system_key,
                    locked = 1,
                    enabled = 1,
                    updated_at = excluded.updated_at
                """;
            command.Parameters.AddWithValue("$name", definition.Name);
            command.Parameters.AddWithValue("$area_label", definition.Label);
            command.Parameters.AddWithValue("$description", definition.Description);
            command.Parameters.AddWithValue("$system_key", definition.Key);
            command.Parameters.AddWithValue("$created_at", now);
            command.Parameters.AddWithValue("$updated_at", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpdateScheduleMembersFromAreaItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long areaGroupItemId,
        string targetType,
        string building,
        string floorLabel,
        double? floorValue,
        string subArea,
        string cardName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE schedule_group_members
            SET target_type = $target_type, building = $building, floor_label = $floor_label,
                floor_value = $floor_value, sub_area_text = $sub_area_text,
                card_name = $card_name, updated_at = $updated_at
            WHERE area_group_item_id = $item_id
            """;
        command.Parameters.AddWithValue("$target_type", targetType);
        command.Parameters.AddWithValue("$building", building);
        command.Parameters.AddWithValue("$floor_label", NullIfEmpty(floorLabel));
        command.Parameters.AddWithValue("$floor_value", floorValue is null ? DBNull.Value : floorValue.Value);
        command.Parameters.AddWithValue("$sub_area_text", NullIfEmpty(subArea));
        command.Parameters.AddWithValue("$card_name", NullIfEmpty(cardName));
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$item_id", areaGroupItemId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MoveScheduleMembersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceAreaGroupItemId,
        long targetAreaGroupItemId,
        string targetType,
        string building,
        string floorLabel,
        double? floorValue,
        string subArea,
        string cardName,
        CancellationToken cancellationToken)
    {
        await using (var removeDuplicates = connection.CreateCommand())
        {
            removeDuplicates.Transaction = transaction;
            removeDuplicates.CommandText = """
                DELETE FROM schedule_group_members
                WHERE area_group_item_id = $source_id
                  AND schedule_group_id IN (
                      SELECT schedule_group_id FROM schedule_group_members WHERE area_group_item_id = $target_id
                  )
                """;
            removeDuplicates.Parameters.AddWithValue("$source_id", sourceAreaGroupItemId);
            removeDuplicates.Parameters.AddWithValue("$target_id", targetAreaGroupItemId);
            await removeDuplicates.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var move = connection.CreateCommand())
        {
            move.Transaction = transaction;
            move.CommandText = "UPDATE schedule_group_members SET area_group_item_id = $target_id WHERE area_group_item_id = $source_id";
            move.Parameters.AddWithValue("$target_id", targetAreaGroupItemId);
            move.Parameters.AddWithValue("$source_id", sourceAreaGroupItemId);
            await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpdateScheduleMembersFromAreaItemAsync(
            connection, transaction, targetAreaGroupItemId, targetType, building, floorLabel,
            floorValue, subArea, cardName, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await SqliteSchemaGuard.RequireCurrentAsync(
            connection,
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["monitor_groups"] = ["id", "name", "area_label", "description", "priority", "group_kind", "system_key", "locked", "enabled", "created_at", "updated_at"],
                ["monitor_group_items"] = ["id", "group_id", "target_type", "building", "floor_label", "floor_value", "sub_area_text", "card_name", "device_uid", "note", "created_at", "updated_at"],
                ["floor_catalog"] = ["id", "building", "floor_label", "floor_value", "source", "enabled", "note", "created_at", "updated_at"],
                ["schedule_groups"] = ["id", "area_group_id", "name", "description", "enabled", "created_at", "updated_at"],
                ["schedule_rules"] = ["id", "schedule_group_id", "calendar_date", "expected_status", "note", "created_at", "updated_at"],
                ["schedule_intervals"] = ["id", "rule_id", "start_time", "end_time", "created_at"],
                ["schedule_group_members"] = ["id", "schedule_group_id", "area_group_item_id", "target_type", "building", "floor_label", "floor_value", "sub_area_text", "card_name", "device_uid", "expected_status", "note", "created_at", "updated_at"],
            },
            ["idx_floor_catalog_key", "idx_monitor_group_items_group", "idx_monitor_group_items_target", "idx_schedule_groups_area", "idx_schedule_rules_group_date", "idx_schedule_intervals_rule", "idx_schedule_members_group", "idx_schedule_members_target", "ux_schedule_groups_area_name", "ux_schedule_members_area_item", "idx_schedule_members_area_item", "ux_schedule_intervals_window"],
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task SyncFloorCatalogFromCurrentAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction(deferred: false);
        var rows = new List<(string Building, double Floor)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
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
                rows.Add((SqliteValueReader.ReadString(reader, "building"), ReadDouble(reader, "floor")));
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var row in rows)
        {
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
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

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
                Building: SqliteValueReader.ReadString(reader, "building"),
                FloorLabel: SqliteValueReader.ReadString(reader, "floor_label"),
                FloorValue: ReadDouble(reader, "floor_value"),
                Source: SqliteValueReader.ReadString(reader, "source"),
                Enabled: SqliteValueReader.ReadInt32(reader, "enabled") != 0,
                Note: SqliteValueReader.ReadString(reader, "note"))
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
                       enabled
                FROM monitor_groups
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
                stats.CoveredAreas));
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
                   i.floor_label, i.floor_value, i.sub_area_text, i.card_name, i.note, COALESCE(i.device_uid, '') AS device_uid
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
                GroupName: SqliteValueReader.ReadString(reader, "group_name"),
                TargetType: SqliteValueReader.ReadString(reader, "target_type"),
                Building: SqliteValueReader.ReadString(reader, "building"),
                FloorLabel: SqliteValueReader.ReadString(reader, "floor_label"),
                FloorValue: SqliteValueReader.ReadNullableDouble(reader, "floor_value"),
                SubAreaText: SqliteValueReader.ReadString(reader, "sub_area_text"),
                CardName: SqliteValueReader.ReadString(reader, "card_name"),
                Note: SqliteValueReader.ReadString(reader, "note"),
                DeviceUid: SqliteValueReader.ReadString(reader, "device_uid")));
        }

        return rows;
    }

    private static async Task<GroupStats> ComputeGroupStatsAsync(
        SqliteConnection connection,
        long groupId,
        CancellationToken cancellationToken)
    {
        var group = await LoadGroupRawAsync(connection, groupId, cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return new GroupStats(0, 0, 0, 0, 0, 0, 0);
        }

        var itemCount = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM monitor_group_items WHERE group_id = $id", ("$id", groupId), cancellationToken).ConfigureAwait(false);
        var sql = """
            SELECT COUNT(*) AS total,
                   SUM(TRIM(COALESCE(c.comm, '')) = '开机' OR
                       (TRIM(COALESCE(c.comm, '')) NOT IN ('离线', '关机', '开机') AND UPPER(TRIM(COALESCE(c.switch, ''))) = 'ON')) AS on_count,
                   SUM(TRIM(COALESCE(c.comm, '')) = '关机' OR
                       (TRIM(COALESCE(c.comm, '')) NOT IN ('离线', '关机', '开机') AND UPPER(TRIM(COALESCE(c.switch, ''))) = 'OFF')) AS off_count,
                   SUM(TRIM(COALESCE(c.comm, '')) = '离线') AS offline_count,
                   SUM(TRIM(COALESCE(c.comm, '')) NOT IN ('离线', '关机', '开机') AND
                       UPPER(TRIM(COALESCE(c.switch, ''))) NOT IN ('ON', 'OFF')) AS unknown_count,
                   COUNT(DISTINCT s.building || ':' || COALESCE(s.floor, '') || ':' || COALESCE(s.text, '')) AS covered_areas
            FROM sub_areas s
            JOIN pages p ON p.sub_area_id = s.id
            JOIN cards c ON c.page_id = p.id
            WHERE 
            """;
        if (group.GroupKind.Equals("system", StringComparison.OrdinalIgnoreCase) && group.SystemKey == "public")
        {
            return await ReadStatsAsync(connection, sql + PublicSql(), itemCount, cancellationToken).ConfigureAwait(false);
        }

        if (group.GroupKind.Equals("system", StringComparison.OrdinalIgnoreCase) && group.SystemKey == "non_public")
        {
            return await ReadStatsAsync(connection, sql + "NOT (" + PublicSql() + ")", itemCount, cancellationToken).ConfigureAwait(false);
        }

        return await ReadStatsAsync(
            connection,
            sql + CustomGroupExistsSql(),
            itemCount,
            cancellationToken,
            ("$group_id", groupId)).ConfigureAwait(false);
    }

    private static async Task<GroupStats> ReadStatsAsync(
        SqliteConnection connection,
        string sql,
        long itemCount,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new GroupStats((int)itemCount, 0, 0, 0, 0, 0, 0);
        }

        return new GroupStats(
            ItemCount: (int)itemCount,
            Total: SqliteValueReader.ReadInt32(reader, "total"),
            OnCount: SqliteValueReader.ReadInt32(reader, "on_count"),
            OffCount: SqliteValueReader.ReadInt32(reader, "off_count"),
            OfflineCount: SqliteValueReader.ReadInt32(reader, "offline_count"),
            UnknownCount: SqliteValueReader.ReadInt32(reader, "unknown_count"),
            CoveredAreas: SqliteValueReader.ReadInt32(reader, "covered_areas"));
    }

    private static async Task<AreaGroupRaw?> LoadGroupRawAsync(SqliteConnection connection, long id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, area_label, description, priority, group_kind, system_key, locked, enabled
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

    private static async Task<long?> FindGroupIdByNameAsync(SqliteConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM monitor_groups WHERE name = $name COLLATE NOCASE";
        command.Parameters.AddWithValue("$name", name);
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
            EXISTS (
                SELECT 1
                FROM monitor_group_items mgi
                JOIN monitor_groups mg ON mg.id = mgi.group_id
                WHERE mgi.group_id = $group_id
                  AND mg.enabled = 1
                  AND mgi.building = s.building
                  AND (
                    (
                      mgi.target_type = 'device'
                      AND mgi.card_name = c.name
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
            Name: SqliteValueReader.ReadString(reader, "name"),
            AreaLabel: SqliteValueReader.ReadString(reader, "area_label"),
            Description: SqliteValueReader.ReadString(reader, "description"),
            Priority: SqliteValueReader.ReadString(reader, "priority"),
            GroupKind: SqliteValueReader.ReadString(reader, "group_kind"),
            SystemKey: SqliteValueReader.ReadString(reader, "system_key"),
            Locked: SqliteValueReader.ReadInt32(reader, "locked") != 0,
            Enabled: SqliteValueReader.ReadInt32(reader, "enabled") != 0);
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

    private static string NormalizeTargetType(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized is "floor" or "sub_area" or "device" ? normalized : "floor";
    }

    private static void ValidateTarget(string targetType, double? floorValue, string subArea, string cardName)
    {
        if (targetType == "floor" && floorValue is null)
        {
            throw new ArgumentException("floor target requires floor label.");
        }

        if (targetType == "sub_area" && (floorValue is null || string.IsNullOrWhiteSpace(subArea)))
        {
            throw new ArgumentException("sub_area target requires floor and sub area.");
        }

        if (targetType == "device" && (floorValue is null || string.IsNullOrWhiteSpace(subArea) || string.IsNullOrWhiteSpace(cardName)))
        {
            throw new ArgumentException("device target requires floor, sub area and card name.");
        }
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

    private sealed record AreaGroupRaw(
        long Id,
        string Name,
        string AreaLabel,
        string Description,
        string Priority,
        string GroupKind,
        string SystemKey,
        bool Locked,
        bool Enabled);

    private sealed record AreaGroupItemRaw(
        long Id,
        long GroupId);

    private sealed record ScheduleAuditRaw(
        string AreaGroupName,
        string ScheduleGroupName,
        string CalendarDate,
        string RuleStatus,
        string TargetType,
        string Building,
        string FloorLabel,
        double? FloorValue,
        string SubAreaText,
        string CardName,
        string DeviceUid,
        string MemberStatus,
        string Note,
        string StartTime,
        string EndTime);

    private sealed record ScheduleObservedDevice(string TargetLabel, string ActualStatus);

    private sealed record GroupStats(
        int ItemCount,
        int Total,
        int OnCount,
        int OffCount,
        int OfflineCount,
        int UnknownCount,
        int CoveredAreas);
}
