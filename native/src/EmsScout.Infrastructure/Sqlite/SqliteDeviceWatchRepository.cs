using System.Globalization;
using EmsScout.Application.Devices;
using EmsScout.Application.Watch;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteDeviceWatchRepository(Func<string> databasePathResolver) : IDeviceWatchRepository
{
    public async Task<DeviceWatchEvaluation> EvaluateAsync(
        DeviceWatchQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!await HasHistoryAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return new DeviceWatchEvaluation([], [], new Dictionary<string, DeviceWatchState>(StringComparer.OrdinalIgnoreCase));
        }

        var rules = await LoadRulesAsync(connection, query, cancellationToken).ConfigureAwait(false);
        var incidents = new List<DeviceWatchIncident>();
        var states = new Dictionary<string, DeviceWatchState>(StringComparer.OrdinalIgnoreCase);
        var completedRules = new List<DeviceWatchRule>();

        foreach (var rule in rules)
        {
            var members = await LoadCurrentRuleMembersAsync(connection, rule.GroupId, cancellationToken).ConfigureAwait(false);
            var samples = await LoadRuleSamplesAsync(connection, rule.GroupId, cancellationToken).ConfigureAwait(false);
            var ruleIncidents = DetectIncidents(rule, samples).ToList();
            incidents.AddRange(ruleIncidents);

            var abnormalKeys = ruleIncidents
                .GroupBy(incident => incident.Device.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

            foreach (var member in members)
            {
                states[member.Key] = abnormalKeys.TryGetValue(member.Key, out var incident)
                    ? new DeviceWatchState(
                        IsWatched: true,
                        IsAbnormal: true,
                        RuleId: rule.Id,
                        GroupId: rule.GroupId,
                        RuleName: rule.Name,
                        StartAt: rule.StartAt,
                        EndAt: rule.EndAt,
                        Summary: "关注异常：" + incident.Summary,
                        Evidence: incident.Evidence)
                    : new DeviceWatchState(
                        IsWatched: true,
                        IsAbnormal: false,
                        RuleId: rule.Id,
                        GroupId: rule.GroupId,
                        RuleName: rule.Name,
                        StartAt: rule.StartAt,
                        EndAt: rule.EndAt,
                        Summary: "关注正常",
                        Evidence: "关注窗口内未检测到开关变化");
            }

            completedRules.Add(rule with
            {
                WatchedDevices = members.Select(member => member.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                AbnormalDevices = abnormalKeys.Count,
            });
        }

        return new DeviceWatchEvaluation(completedRules, incidents, states);
    }

    public async Task<DeviceWatchRule?> LoadRuleForGroupAsync(
        long groupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var rules = await LoadRulesAsync(
            connection,
            new DeviceWatchQuery(GroupId: groupId, IncludeDisabled: true),
            cancellationToken).ConfigureAwait(false);
        return rules.FirstOrDefault();
    }

    public async Task<DeviceWatchRule> SaveRuleAsync(
        DeviceWatchEdit edit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        if (edit.GroupId <= 0)
        {
            throw new ArgumentException("关注规则必须绑定自定义区域组。");
        }

        if (edit.EndAt <= edit.StartAt)
        {
            throw new ArgumentException("关注结束时间必须晚于开始时间。");
        }

        await EnsureCustomGroupAsync(connection, edit.GroupId, cancellationToken).ConfigureAwait(false);

        var name = string.IsNullOrWhiteSpace(edit.Name) ? "关注设备" : edit.Name.Trim();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (edit.Id is not null && edit.Id.Value > 0)
        {
            await EnsureRuleBelongsToGroupAsync(connection, edit.Id.Value, edit.GroupId, cancellationToken).ConfigureAwait(false);
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE device_watch_rules
                SET name = $name, start_at = $start_at, end_at = $end_at,
                    enabled = $enabled, note = $note, updated_at = $updated_at
                WHERE id = $id AND group_id = $group_id
                """;
            update.Parameters.AddWithValue("$group_id", edit.GroupId);
            update.Parameters.AddWithValue("$name", name);
            update.Parameters.AddWithValue("$start_at", edit.StartAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$end_at", edit.EndAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$enabled", edit.Enabled ? 1 : 0);
            update.Parameters.AddWithValue("$note", edit.Note ?? string.Empty);
            update.Parameters.AddWithValue("$updated_at", now);
            update.Parameters.AddWithValue("$id", edit.Id.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO device_watch_rules
                  (group_id, name, start_at, end_at, enabled, note, created_at, updated_at)
                VALUES ($group_id, $name, $start_at, $end_at, $enabled, $note, $created_at, $updated_at)
                ON CONFLICT(group_id) DO UPDATE SET
                  name = excluded.name,
                  start_at = excluded.start_at,
                  end_at = excluded.end_at,
                  enabled = excluded.enabled,
                  note = excluded.note,
                  updated_at = excluded.updated_at
                """;
            insert.Parameters.AddWithValue("$group_id", edit.GroupId);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$start_at", edit.StartAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$end_at", edit.EndAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$enabled", edit.Enabled ? 1 : 0);
            insert.Parameters.AddWithValue("$note", edit.Note ?? string.Empty);
            insert.Parameters.AddWithValue("$created_at", now);
            insert.Parameters.AddWithValue("$updated_at", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var saved = await LoadRulesAsync(
            connection,
            new DeviceWatchQuery(GroupId: edit.GroupId, IncludeDisabled: true),
            cancellationToken).ConfigureAwait(false);
        return saved.FirstOrDefault() ?? throw new InvalidOperationException("关注规则保存后未找到。");
    }

    public async Task DeleteRuleAsync(long id, long groupId, CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(databasePathResolver, SqliteOpenMode.ReadWrite);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM device_watch_rules WHERE id = $id AND group_id = $group_id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$group_id", groupId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureRuleBelongsToGroupAsync(
        SqliteConnection connection,
        long id,
        long groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_id FROM device_watch_rules WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            throw new InvalidOperationException("关注规则不存在或已被删除。");
        }

        var actualGroupId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (actualGroupId != groupId)
        {
            throw new InvalidOperationException("关注规则不属于当前分组，请刷新后重试。");
        }
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await SqliteSchemaGuard.RequireCurrentAsync(
            connection,
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["device_watch_rules"] = ["id", "group_id", "name", "start_at", "end_at", "enabled", "note", "created_at", "updated_at"],
                ["monitor_groups"] = ["id", "name", "enabled", "group_kind", "locked"],
                ["monitor_group_items"] = ["id", "group_id", "target_type", "building", "floor_value", "sub_area_text", "card_name"],
            },
            ["idx_device_watch_rules_enabled", "idx_monitor_group_items_group"],
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasHistoryAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        return await SqliteSchemaGuard.TableExistsAsync(connection, "collection_runs", cancellationToken).ConfigureAwait(false) &&
               await SqliteSchemaGuard.TableExistsAsync(connection, "run_cards", cancellationToken).ConfigureAwait(false) &&
               await SqliteSchemaGuard.TableExistsAsync(connection, "run_pages", cancellationToken).ConfigureAwait(false) &&
               await SqliteSchemaGuard.TableExistsAsync(connection, "run_sub_areas", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureCustomGroupAsync(
        SqliteConnection connection,
        long groupId,
        CancellationToken cancellationToken)
    {
        if (!await SqliteSchemaGuard.TableExistsAsync(connection, "monitor_groups", cancellationToken).ConfigureAwait(false))
        {
            throw new ArgumentException("关注规则必须绑定已存在的自定义区域组。");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT group_kind, locked
            FROM monitor_groups
            WHERE id = $group_id
            """;
        command.Parameters.AddWithValue("$group_id", groupId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ArgumentException("关注规则必须绑定已存在的自定义区域组。");
        }

        var groupKind = SqliteValueReader.ReadString(reader, "group_kind");
        var locked = SqliteValueReader.ReadInt32(reader, "locked") != 0;
        if (locked || !groupKind.Equals("custom", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("关注规则只能绑定自定义区域组。");
        }
    }

    private static async Task<IReadOnlyList<DeviceWatchRule>> LoadRulesAsync(
        SqliteConnection connection,
        DeviceWatchQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var clauses = new List<string>();
        if (!query.IncludeDisabled)
        {
            clauses.Add("r.enabled = 1");
            clauses.Add("g.enabled = 1");
        }

        if (query.GroupId is not null)
        {
            clauses.Add("r.group_id = $group_id");
            command.Parameters.AddWithValue("$group_id", query.GroupId.Value);
        }

        command.CommandText = $"""
            SELECT r.id, r.group_id, g.name AS group_name, r.name, r.start_at, r.end_at,
                   r.enabled, r.note
            FROM device_watch_rules r
            JOIN monitor_groups g ON g.id = r.group_id
            {(clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses))}
            ORDER BY datetime(r.start_at) DESC, r.id DESC
            """;
        var rows = new List<DeviceWatchRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DeviceWatchRule(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                GroupId: reader.GetInt64(reader.GetOrdinal("group_id")),
                GroupName: SqliteValueReader.ReadString(reader, "group_name"),
                Name: SqliteValueReader.ReadString(reader, "name"),
                StartAt: ReadDateTimeOffset(reader, "start_at"),
                EndAt: ReadDateTimeOffset(reader, "end_at"),
                Enabled: SqliteValueReader.ReadInt32(reader, "enabled") != 0,
                Note: SqliteValueReader.ReadString(reader, "note"),
                WatchedDevices: 0,
                AbnormalDevices: 0));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<DeviceWatchKey>> LoadCurrentRuleMembersAsync(
        SqliteConnection connection,
        long groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.building, s.floor, s.text AS sub_area, p.page_name, c.name
            FROM cards c
            JOIN pages p ON p.id = c.page_id
            JOIN sub_areas s ON s.id = p.sub_area_id
            WHERE {SqliteAreaGroupRepository.CustomGroupExistsSql()}
            ORDER BY s.building, s.floor, s.text, p.page_name, c.name
            """;
        command.Parameters.AddWithValue("$group_id", groupId);
        var rows = new Dictionary<string, DeviceWatchKey>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new DeviceWatchKey(
                SqliteValueReader.ReadString(reader, "building"),
                DeviceFloorLabelFormatter.Format(
                    SqliteValueReader.ReadNullableDouble(reader, "floor"),
                    SqliteValueReader.ReadString(reader, "sub_area")),
                SqliteValueReader.ReadString(reader, "sub_area"),
                SqliteValueReader.ReadString(reader, "page_name"),
                SqliteValueReader.ReadString(reader, "name"));
            rows[key.Key] = key;
        }

        return rows.Values.ToList();
    }

    private static async Task<IReadOnlyList<WatchSample>> LoadRuleSamplesAsync(
        SqliteConnection connection,
        long groupId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT cr.id AS run_id, cr.completed_at, rsa.building, rsa.floor, rsa.text AS sub_area,
                   rp.page_name, rc.name, rc.switch, rc.comm
            FROM collection_runs cr
            JOIN run_cards rc ON rc.run_id = cr.id
            JOIN run_pages rp ON rp.id = rc.run_page_id
            JOIN run_sub_areas rsa ON rsa.id = rp.run_sub_area_id
            WHERE cr.is_anomaly = 0
              AND EXISTS (
                SELECT 1
                FROM monitor_group_items mgi
                WHERE mgi.group_id = $group_id
                  AND mgi.building = rsa.building
                  AND (
                    (
                      mgi.target_type = 'device'
                      AND mgi.card_name = rc.name
                      AND (
                        (mgi.floor_value IS NULL AND IFNULL(mgi.sub_area_text, '') = '')
                        OR (
                          (mgi.floor_value IS NULL OR ABS(COALESCE(rsa.floor, -999999) - COALESCE(mgi.floor_value, -999998)) < 0.001)
                          AND (IFNULL(mgi.sub_area_text, '') = '' OR IFNULL(mgi.sub_area_text, '') = IFNULL(rsa.text, ''))
                        )
                      )
                    )
                    OR (
                      mgi.target_type = 'sub_area'
                      AND ABS(COALESCE(rsa.floor, -999999) - COALESCE(mgi.floor_value, -999998)) < 0.001
                      AND IFNULL(mgi.sub_area_text, '') = IFNULL(rsa.text, '')
                    )
                    OR (
                      mgi.target_type = 'floor'
                      AND ABS(COALESCE(rsa.floor, -999999) - COALESCE(mgi.floor_value, -999998)) < 0.001
                    )
                  )
              )
            ORDER BY rsa.building, rsa.floor, rsa.text, rp.page_name, rc.name, datetime(cr.completed_at), cr.id
            """;
        command.Parameters.AddWithValue("$group_id", groupId);
        var rows = new List<WatchSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new DeviceWatchKey(
                SqliteValueReader.ReadString(reader, "building"),
                DeviceFloorLabelFormatter.Format(
                    SqliteValueReader.ReadNullableDouble(reader, "floor"),
                    SqliteValueReader.ReadString(reader, "sub_area")),
                SqliteValueReader.ReadString(reader, "sub_area"),
                SqliteValueReader.ReadString(reader, "page_name"),
                SqliteValueReader.ReadString(reader, "name"));
            rows.Add(new WatchSample(
                RunId: reader.GetInt64(reader.GetOrdinal("run_id")),
                CompletedAt: ReadDateTimeOffset(reader, "completed_at"),
                Key: key,
                State: NormalizePowerState(SqliteValueReader.ReadString(reader, "switch"), SqliteValueReader.ReadString(reader, "comm"))));
        }

        return rows;
    }

    private static IEnumerable<DeviceWatchIncident> DetectIncidents(
        DeviceWatchRule rule,
        IReadOnlyList<WatchSample> samples)
    {
        foreach (var group in samples
                     .Where(sample => sample.State is "ON" or "OFF")
                     .GroupBy(sample => sample.Key.Key, StringComparer.OrdinalIgnoreCase))
        {
            WatchSample? previous = null;
            foreach (var sample in group.OrderBy(item => item.CompletedAt).ThenBy(item => item.RunId))
            {
                if (sample.CompletedAt < rule.StartAt)
                {
                    previous = sample;
                    continue;
                }

                if (sample.CompletedAt > rule.EndAt)
                {
                    break;
                }

                if (previous is not null && !string.Equals(previous.State, sample.State, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new DeviceWatchIncident(
                        RuleId: rule.Id,
                        GroupId: rule.GroupId,
                        GroupName: rule.GroupName,
                        Device: sample.Key,
                        PreviousState: previous.State,
                        CurrentState: sample.State,
                        PreviousAt: previous.CompletedAt,
                        CurrentAt: sample.CompletedAt,
                        PreviousRunId: previous.RunId,
                        CurrentRunId: sample.RunId);
                }

                previous = sample;
            }
        }
    }

    private static string NormalizePowerState(string switchState, string comm)
    {
        var sw = (switchState ?? string.Empty).Trim().ToUpperInvariant();
        if (sw is "ON" or "OFF")
        {
            return sw;
        }

        return (comm ?? string.Empty).Trim() switch
        {
            "开机" => "ON",
            "关机" => "OFF",
            _ => string.Empty,
        };
    }

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, string column)
    {
        var value = SqliteValueReader.ReadString(reader, column);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    private sealed record WatchSample(
        long RunId,
        DateTimeOffset CompletedAt,
        DeviceWatchKey Key,
        string State);
}
