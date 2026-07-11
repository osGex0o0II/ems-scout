using EmsScout.Application.Devices;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteDeviceAnnotationService(Func<string> databasePathResolver) : IDeviceAnnotationService
{
    private static readonly HashSet<string> MatchOverrideActions =
    [
        "classify_only",
        "map_to_db",
        "create_virtual",
        "ignore_duplicate",
    ];

    public async Task SaveNoteAsync(
        DeviceAnnotationKey key,
        string note,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureAnnotationTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_notes (card_name, building, note, created_at, updated_at)
            VALUES ($card_name, $building, $note, $now, $now)
            ON CONFLICT(card_name, building) DO UPDATE SET note = excluded.note, updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$card_name", Require(key.CardName, "card name"));
        command.Parameters.AddWithValue("$building", NullIfEmpty(key.Building));
        command.Parameters.AddWithValue("$note", note ?? string.Empty);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddTagAsync(
        DeviceAnnotationKey key,
        string tag,
        CancellationToken cancellationToken = default)
    {
        var normalizedTag = Require(tag, "tag");
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureAnnotationTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO device_tags (card_name, building, tag)
            VALUES ($card_name, $building, $tag)
            """;
        command.Parameters.AddWithValue("$card_name", Require(key.CardName, "card name"));
        command.Parameters.AddWithValue("$building", NullIfEmpty(key.Building));
        command.Parameters.AddWithValue("$tag", normalizedTag);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteTagAsync(
        DeviceAnnotationKey key,
        string tag,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureAnnotationTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM device_tags
            WHERE card_name = $card_name
              AND IFNULL(building, '') = IFNULL($building, '')
              AND tag = $tag
            """;
        command.Parameters.AddWithValue("$card_name", Require(key.CardName, "card name"));
        command.Parameters.AddWithValue("$building", NullIfEmpty(key.Building));
        command.Parameters.AddWithValue("$tag", Require(tag, "tag"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RealtimeMatchOverride?> SaveRealtimeOverrideAsync(
        RealtimeOverrideEdit edit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureAnnotationTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await LoadExistingRealtimeOverrideAsync(connection, transaction, edit, cancellationToken).ConfigureAwait(false);
        var payload = BuildPayload(edit, existing);
        if (existing is not null && OverrideIsEmpty(payload))
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM realtime_match_overrides WHERE id = $id";
            delete.Parameters.AddWithValue("$id", existing.Id);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (existing is null && OverrideIsEmpty(payload))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        if (existing is not null)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE realtime_match_overrides
                SET building = $building,
                    dev_id = $dev_id,
                    floor_label = $floor_label,
                    sub_area = $sub_area,
                    page_name = $page_name,
                    realtime_name = $realtime_name,
                    action = $action,
                    target_card_id = $target_card_id,
                    zuo_override = $zuo_override,
                    area_type_override = $area_type_override,
                    note = $note,
                    updated_at = $updated_at
                WHERE id = $id
                """;
            AddOverrideParameters(update, payload, now);
            update.Parameters.AddWithValue("$id", existing.Id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var saved = await LoadOverrideByIdAsync(connection, transaction, existing.Id, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return saved;
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO realtime_match_overrides
              (building, dev_id, floor_label, sub_area, page_name, realtime_name, action,
               target_card_id, zuo_override, area_type_override, note, created_at, updated_at)
            VALUES ($building, $dev_id, $floor_label, $sub_area, $page_name, $realtime_name, $action,
                    $target_card_id, $zuo_override, $area_type_override, $note, $updated_at, $updated_at)
            RETURNING id
            """;
        AddOverrideParameters(insert, payload, now);
        var id = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var created = await LoadOverrideByIdAsync(
            connection,
            transaction,
            Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    private static async Task EnsureAnnotationTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await SqliteSchemaGuard.RequireCurrentAsync(
            connection,
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["device_tags"] = ["id", "card_name", "building", "device_uid", "tag", "created_at"],
                ["device_notes"] = ["id", "card_name", "building", "device_uid", "note", "created_at", "updated_at"],
                ["realtime_match_overrides"] = ["id", "building", "dev_id", "floor_label", "sub_area", "page_name", "realtime_name", "action", "target_card_id", "device_uid", "zuo_override", "area_type_override", "note", "created_at", "updated_at"],
            },
            ["ux_realtime_match_overrides_dev", "ux_realtime_match_overrides_identity"],
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RealtimeMatchOverride?> LoadExistingRealtimeOverrideAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RealtimeOverrideEdit edit,
        CancellationToken cancellationToken)
    {
        var building = KeyPart(edit.Building);
        var devId = KeyPart(edit.DevId);
        if (!string.IsNullOrWhiteSpace(devId))
        {
            await using var byDev = connection.CreateCommand();
            byDev.Transaction = transaction;
            byDev.CommandText = """
                SELECT id, building, dev_id, floor_label, sub_area, page_name, realtime_name,
                       action, target_card_id, zuo_override, area_type_override, note
                FROM realtime_match_overrides
                WHERE building = $building AND IFNULL(dev_id, '') = $dev_id
                ORDER BY id DESC
                LIMIT 1
                """;
            byDev.Parameters.AddWithValue("$building", building);
            byDev.Parameters.AddWithValue("$dev_id", devId);
            var row = await ReadOverrideAsync(byDev, cancellationToken).ConfigureAwait(false);
            if (row is not null)
            {
                return row;
            }
        }

        await using var byIdentity = connection.CreateCommand();
        byIdentity.Transaction = transaction;
        byIdentity.CommandText = """
            SELECT id, building, dev_id, floor_label, sub_area, page_name, realtime_name,
                   action, target_card_id, zuo_override, area_type_override, note
            FROM realtime_match_overrides
            WHERE building = $building
              AND IFNULL(floor_label, '') = IFNULL($floor_label, '')
              AND IFNULL(sub_area, '') = IFNULL($sub_area, '')
              AND IFNULL(page_name, '') = IFNULL($page_name, '')
              AND realtime_name = $realtime_name
            ORDER BY id DESC
            LIMIT 1
            """;
        byIdentity.Parameters.AddWithValue("$building", building);
        byIdentity.Parameters.AddWithValue("$floor_label", NullIfEmpty(NormalizeFloorLabel(edit.FloorLabel)));
        byIdentity.Parameters.AddWithValue("$sub_area", NullIfEmpty(edit.SubArea));
        byIdentity.Parameters.AddWithValue("$page_name", NullIfEmpty(NormalizePageName(edit.PageName)));
        byIdentity.Parameters.AddWithValue("$realtime_name", KeyPart(edit.RealtimeName));
        return await ReadOverrideAsync(byIdentity, cancellationToken).ConfigureAwait(false);
    }

    private static RealtimeOverridePayload BuildPayload(RealtimeOverrideEdit edit, RealtimeMatchOverride? existing)
    {
        var building = KeyPart(edit.Building);
        var name = KeyPart(edit.RealtimeName);
        if (string.IsNullOrWhiteSpace(building) || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("building and realtime name are required.");
        }

        var action = string.IsNullOrWhiteSpace(edit.Action)
            ? existing?.Action ?? "classify_only"
            : edit.Action.Trim();
        if (!MatchOverrideActions.Contains(action))
        {
            throw new ArgumentException("Unsupported realtime override action.");
        }

        return new RealtimeOverridePayload(
            Building: building,
            DevId: KeyPart(string.IsNullOrWhiteSpace(edit.DevId) ? existing?.DevId ?? string.Empty : edit.DevId),
            FloorLabel: NormalizeFloorLabel(string.IsNullOrWhiteSpace(edit.FloorLabel) ? existing?.FloorLabel ?? string.Empty : edit.FloorLabel),
            SubArea: string.IsNullOrWhiteSpace(edit.SubArea) ? existing?.SubArea ?? string.Empty : edit.SubArea.Trim(),
            PageName: NormalizePageName(string.IsNullOrWhiteSpace(edit.PageName) ? existing?.PageName ?? "default" : edit.PageName),
            RealtimeName: name,
            Action: action,
            TargetCardId: edit.TargetCardId ?? existing?.TargetCardId,
            ZuoOverride: edit.ZuoOverride is null ? existing?.ZuoOverride ?? string.Empty : NormalizeZuo(edit.ZuoOverride, building),
            AreaTypeOverride: edit.AreaTypeOverride is null ? existing?.AreaTypeOverride ?? string.Empty : NormalizeAreaType(edit.AreaTypeOverride),
            Note: edit.Note is null ? existing?.Note ?? string.Empty : edit.Note.Trim());
    }

    private static bool OverrideIsEmpty(RealtimeOverridePayload payload)
    {
        return payload.Action == "classify_only" &&
               payload.TargetCardId is null &&
               string.IsNullOrWhiteSpace(payload.ZuoOverride) &&
               string.IsNullOrWhiteSpace(payload.AreaTypeOverride) &&
               string.IsNullOrWhiteSpace(payload.Note);
    }

    private static void AddOverrideParameters(SqliteCommand command, RealtimeOverridePayload payload, string now)
    {
        command.Parameters.AddWithValue("$building", payload.Building);
        command.Parameters.AddWithValue("$dev_id", NullIfEmpty(payload.DevId));
        command.Parameters.AddWithValue("$floor_label", NullIfEmpty(payload.FloorLabel));
        command.Parameters.AddWithValue("$sub_area", NullIfEmpty(payload.SubArea));
        command.Parameters.AddWithValue("$page_name", NullIfEmpty(payload.PageName));
        command.Parameters.AddWithValue("$realtime_name", payload.RealtimeName);
        command.Parameters.AddWithValue("$action", payload.Action);
        command.Parameters.AddWithValue("$target_card_id", payload.TargetCardId is null ? DBNull.Value : payload.TargetCardId);
        command.Parameters.AddWithValue("$zuo_override", NullIfEmpty(payload.ZuoOverride));
        command.Parameters.AddWithValue("$area_type_override", NullIfEmpty(payload.AreaTypeOverride));
        command.Parameters.AddWithValue("$note", payload.Note);
        command.Parameters.AddWithValue("$updated_at", now);
    }

    private static async Task<RealtimeMatchOverride?> LoadOverrideByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, building, dev_id, floor_label, sub_area, page_name, realtime_name,
                   action, target_card_id, zuo_override, area_type_override, note
            FROM realtime_match_overrides
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        return await ReadOverrideAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RealtimeMatchOverride?> ReadOverrideAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new RealtimeMatchOverride(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            Building: SqliteValueReader.ReadString(reader, "building"),
            DevId: SqliteValueReader.ReadString(reader, "dev_id"),
            FloorLabel: SqliteValueReader.ReadString(reader, "floor_label"),
            SubArea: SqliteValueReader.ReadString(reader, "sub_area"),
            PageName: SqliteValueReader.ReadString(reader, "page_name"),
            RealtimeName: SqliteValueReader.ReadString(reader, "realtime_name"),
            Action: SqliteValueReader.ReadString(reader, "action"),
            TargetCardId: SqliteValueReader.ReadNullableInt64(reader, "target_card_id"),
            ZuoOverride: SqliteValueReader.ReadString(reader, "zuo_override"),
            AreaTypeOverride: SqliteValueReader.ReadString(reader, "area_type_override"),
            Note: SqliteValueReader.ReadString(reader, "note"));
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

    private static string KeyPart(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeFloorLabel(string value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string NormalizePageName(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
    }

    private static string NormalizeZuo(string value, string building)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.EndsWith('座') || normalized.EndsWith("号", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + "座";
    }

    private static string NormalizeAreaType(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized is "公区" or "非公区" or "未匹配" ? normalized : string.Empty;
    }

    private static object NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    }

    private sealed record RealtimeOverridePayload(
        string Building,
        string DevId,
        string FloorLabel,
        string SubArea,
        string PageName,
        string RealtimeName,
        string Action,
        long? TargetCardId,
        string ZuoOverride,
        string AreaTypeOverride,
        string Note);
}
