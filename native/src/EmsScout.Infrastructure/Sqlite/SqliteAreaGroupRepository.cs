using EmsScout.Application.Groups;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteAreaGroupRepository(Func<string> databasePathResolver) : IAreaGroupRepository
{
    private static readonly string[] Buildings = ["1号", "2号", "3号", "4号", "5号", "6号"];

    public async Task<AreaGroupSet> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
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
                update.Parameters.AddWithValue("$area_label", NullIfEmpty(edit.AreaLabel));
                update.Parameters.AddWithValue("$description", NullIfEmpty(edit.Description));
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
                update.Parameters.AddWithValue("$area_label", NullIfEmpty(edit.AreaLabel));
                update.Parameters.AddWithValue("$description", NullIfEmpty(edit.Description));
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
        insert.Parameters.AddWithValue("$area_label", NullIfEmpty(edit.AreaLabel));
        insert.Parameters.AddWithValue("$description", NullIfEmpty(edit.Description));
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

    public async Task<AreaGroupItemRecord> SaveItemAsync(
        AreaGroupItemEdit edit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var group = await LoadGroupRawAsync(connection, edit.GroupId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Group not found: {edit.GroupId}");
        if (group.Locked || group.GroupKind.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("系统区域不需要手动添加成员");
        }

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
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM monitor_group_items WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteItemCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM monitor_group_items WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
            },
            ["idx_floor_catalog_key", "idx_monitor_group_items_group", "idx_monitor_group_items_target"],
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
                GroupName: SqliteValueReader.ReadString(reader, "group_name"),
                TargetType: SqliteValueReader.ReadString(reader, "target_type"),
                Building: SqliteValueReader.ReadString(reader, "building"),
                FloorLabel: SqliteValueReader.ReadString(reader, "floor_label"),
                FloorValue: SqliteValueReader.ReadNullableDouble(reader, "floor_value"),
                SubAreaText: SqliteValueReader.ReadString(reader, "sub_area_text"),
                CardName: SqliteValueReader.ReadString(reader, "card_name"),
                Note: SqliteValueReader.ReadString(reader, "note")));
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
                   SUM(c.comm = '开机') AS on_count,
                   SUM(c.comm = '关机') AS off_count,
                   SUM(c.comm = '离线') AS offline_count,
                   SUM(COALESCE(c.comm, '') NOT IN ('开机', '关机', '离线')) AS unknown_count,
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

    private sealed record GroupStats(
        int ItemCount,
        int Total,
        int OnCount,
        int OffCount,
        int OfflineCount,
        int UnknownCount,
        int CoveredAreas);
}
