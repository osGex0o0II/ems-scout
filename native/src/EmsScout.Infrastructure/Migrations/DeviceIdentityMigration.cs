using System.Globalization;
using System.Text.Json;
using EmsScout.Application.Devices;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Migrations;

internal static class DeviceIdentityMigration
{
    private const string Unresolved = "unresolved";
    private const string AutoResolved = "auto_resolved";

    public static async Task<DeviceIdentityMigrationReport> ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var state = await IdentityState.LoadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var currentResult = await BackfillCurrentCardsAsync(connection, transaction, state, cancellationToken).ConfigureAwait(false);
        var runResult = await BackfillRunCardsAsync(
                connection,
                transaction,
                state,
                cancellationToken)
            .ConfigureAwait(false);
        var currentDevices = await LoadCurrentDevicesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var references = await BackfillUserReferencesAsync(
                connection,
                transaction,
                currentDevices,
                cancellationToken)
            .ConfigureAwait(false);

        return await BuildReportAsync(
                connection,
                transaction,
                references.Total,
                references.Resolved,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<CurrentBackfillResult> BackfillCurrentCardsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IdentityState state,
        CancellationToken cancellationToken)
    {
        var rows = new List<SourceRow>();
        await using (var command = CreateCommand(connection, transaction, """
                         SELECT c.id, NULL AS run_id, sa.building, sa.sub_idx, sa.floor, sa.text,
                                p.page_name, c.name, c.source_key, c.device_uid
                         FROM cards c
                         JOIN pages p ON p.id = c.page_id
                         JOIN sub_areas sa ON sa.id = p.sub_area_id
                         ORDER BY c.id
                         """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadSourceRow(reader));
            }
        }

        var valid = new List<(SourceRow Row, SourceDescriptor Descriptor)>();
        foreach (var row in rows)
        {
            if (TryBuildDescriptor(row, out var descriptor, out var error))
            {
                valid.Add((row, descriptor));
                continue;
            }

            await UpdateCardIdentityAsync(connection, transaction, "cards", row.Id, null, null, cancellationToken).ConfigureAwait(false);
            await RecordAmbiguityAsync(
                    connection,
                    transaction,
                    "cards",
                    row.Id.ToString(CultureInfo.InvariantCulture),
                    "missing_source_identity_component",
                    Unresolved,
                    null,
                    SerializeIdentity(row, error),
                    [],
                    null,
                    error,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var group in valid.GroupBy(item => item.Descriptor.SourceKey, StringComparer.Ordinal))
        {
            var grouped = group.OrderBy(item => item.Row.Id).ToArray();
            var existingUids = grouped
                .Select(item => item.Row.ExistingDeviceUid)
                .Where(DeviceIdentityKeyBuilder.IsDeviceUid)
                .Select(uid => uid!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var assignedUids = new List<string>();
            var assignedSourceKeys = new List<string>();
            if (existingUids.Length > 1)
            {
                foreach (var (item, index) in grouped.Select((item, index) => (item, index)))
                {
                    var observation = item.Descriptor.WithOccurrence(index + 1);
                    await UpdateCardIdentityAsync(
                            connection,
                            transaction,
                            "cards",
                            item.Row.Id,
                            observation.SourceKey,
                            null,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await RecordAmbiguityAsync(
                        connection,
                        transaction,
                        "cards",
                        group.Key,
                        "conflicting_existing_device_uids",
                        Unresolved,
                        group.Key,
                        JsonSerializer.Serialize(new
                        {
                            baseSourceKey = group.Key,
                            cardIds = grouped.Select(item => item.Row.Id).ToArray(),
                            existingUids,
                            grouped[0].Descriptor.Building,
                            grouped[0].Descriptor.SubAreaIndex,
                            grouped[0].Descriptor.PageName,
                            grouped[0].Descriptor.DeviceName,
                        }),
                        existingUids,
                        null,
                        "Existing dual-write values disagree for observations of one base identity.",
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            string? sharedUid = existingUids.SingleOrDefault() ?? grouped[0].Descriptor.InitialDeviceUid;
            foreach (var (item, index) in grouped.Select((item, index) => (item, index)))
            {
                var observation = item.Descriptor.WithOccurrence(index + 1);
                var uid = await state.ResolveOrCreateAsync(
                        connection,
                        transaction,
                        observation,
                        sharedUid,
                        isCurrent: true,
                        firstRunId: null,
                        lastRunId: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                await UpdateCardIdentityAsync(
                        connection,
                        transaction,
                        "cards",
                        item.Row.Id,
                        observation.SourceKey,
                        uid,
                        cancellationToken)
                    .ConfigureAwait(false);
                assignedSourceKeys.Add(observation.SourceKey);
                if (uid is not null)
                {
                    sharedUid = uid;
                    assignedUids.Add(uid);
                }
            }

            if (grouped.Length > 1)
            {
                var resolvedUid = assignedUids.Distinct(StringComparer.Ordinal).Count() == 1
                    ? assignedUids[0]
                    : null;
                await RecordAmbiguityAsync(
                        connection,
                        transaction,
                        "cards",
                        group.Key,
                        "duplicate_source_observations",
                        resolvedUid is null ? Unresolved : AutoResolved,
                        group.Key,
                        JsonSerializer.Serialize(new
                        {
                            baseSourceKey = group.Key,
                            cardIds = grouped.Select(item => item.Row.Id).ToArray(),
                            sourceKeys = assignedSourceKeys,
                        }),
                        assignedUids.Distinct(StringComparer.Ordinal).ToArray(),
                        resolvedUid,
                        "Duplicate observations have unique occurrence source keys and share one base device UID.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new CurrentBackfillResult(rows.Count);
    }

    private static async Task<RunBackfillResult> BackfillRunCardsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IdentityState state,
        CancellationToken cancellationToken)
    {
        var rows = new List<SourceRow>();
        await using (var command = CreateCommand(connection, transaction, """
                         SELECT rc.id, rc.run_id, rsa.building, rsa.sub_idx, rsa.floor, rsa.text,
                                rp.page_name, rc.name, rc.source_key, rc.device_uid
                         FROM run_cards rc
                         JOIN run_pages rp ON rp.id = rc.run_page_id
                         JOIN run_sub_areas rsa ON rsa.id = rp.run_sub_area_id
                         ORDER BY rc.run_id, rc.id
                         """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadSourceRow(reader));
            }
        }

        var valid = new List<(SourceRow Row, SourceDescriptor Descriptor)>();
        foreach (var row in rows)
        {
            if (TryBuildDescriptor(row, out var descriptor, out var error))
            {
                valid.Add((row, descriptor));
                continue;
            }

            await UpdateCardIdentityAsync(connection, transaction, "run_cards", row.Id, null, null, cancellationToken).ConfigureAwait(false);
            await RecordAmbiguityAsync(
                    connection,
                    transaction,
                    "run_cards",
                    row.Id.ToString(CultureInfo.InvariantCulture),
                    "missing_source_identity_component",
                    Unresolved,
                    null,
                    SerializeIdentity(row, error),
                    [],
                    null,
                    error,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var observations = new List<(SourceRow Row, SourceDescriptor Descriptor)>();
        var duplicateGroups = new List<(long RunId, string BaseSourceKey, (SourceRow Row, SourceDescriptor Descriptor)[] Items)>();
        foreach (var group in valid.GroupBy(item => (RunId: item.Row.RunId!.Value, BaseSourceKey: item.Descriptor.SourceKey)))
        {
            var grouped = group.OrderBy(item => item.Row.Id).ToArray();
            var expanded = grouped
                .Select((item, index) => (item.Row, Descriptor: item.Descriptor.WithOccurrence(index + 1)))
                .ToArray();
            observations.AddRange(expanded);
            if (grouped.Length > 1)
            {
                duplicateGroups.Add((group.Key.RunId, group.Key.BaseSourceKey, expanded));
            }
        }

        var resolutions = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var sourceGroup in observations.GroupBy(item => item.Descriptor.SourceKey, StringComparer.Ordinal))
        {
            var first = sourceGroup.First();
            var firstRunId = sourceGroup.Min(item => item.Row.RunId!.Value);
            var lastRunId = sourceGroup.Max(item => item.Row.RunId!.Value);
            var preferredUid = sourceGroup
                .Select(item => item.Row.ExistingDeviceUid)
                .FirstOrDefault(DeviceIdentityKeyBuilder.IsDeviceUid) ?? first.Descriptor.InitialDeviceUid;
            resolutions[sourceGroup.Key] = await state.ResolveOrCreateAsync(
                    connection,
                    transaction,
                    first.Descriptor,
                    preferredUid,
                    isCurrent: false,
                    firstRunId,
                    lastRunId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var item in observations)
        {
            resolutions.TryGetValue(item.Descriptor.SourceKey, out var uid);

            await UpdateCardIdentityAsync(
                    connection,
                    transaction,
                    "run_cards",
                    item.Row.Id,
                    item.Descriptor.SourceKey,
                    uid,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var duplicate in duplicateGroups)
        {
            var uids = duplicate.Items
                .Select(item => resolutions.GetValueOrDefault(item.Descriptor.SourceKey))
                .Where(uid => uid is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var resolvedUid = uids.Length == 1 ? uids[0] : null;
            await RecordAmbiguityAsync(
                    connection,
                    transaction,
                    "run_cards",
                    $"run:{duplicate.RunId}:source:{duplicate.BaseSourceKey}",
                    "duplicate_source_observations",
                    resolvedUid is null ? Unresolved : AutoResolved,
                    duplicate.BaseSourceKey,
                    JsonSerializer.Serialize(new
                    {
                        duplicate.RunId,
                        duplicate.BaseSourceKey,
                        runCardIds = duplicate.Items.Select(item => item.Row.Id).ToArray(),
                        sourceKeys = duplicate.Items.Select(item => item.Descriptor.SourceKey).ToArray(),
                    }),
                    uids,
                    resolvedUid,
                    "Duplicate run observations have unique occurrence source keys and share one base device UID.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new RunBackfillResult(rows.Count);
    }

    private static async Task<UserReferenceResult> BackfillUserReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CurrentDevice> devices,
        CancellationToken cancellationToken)
    {
        var total = 0;
        var resolved = 0;
        foreach (var table in new[] { "device_notes", "device_tags", "manual_overrides" })
        {
            var result = await BackfillSimpleReferencesAsync(
                    connection,
                    transaction,
                    table,
                    devices,
                    cancellationToken)
                .ConfigureAwait(false);
            total += result.Total;
            resolved += result.Resolved;
        }

        var groupResult = await BackfillGroupItemsAsync(connection, transaction, devices, cancellationToken).ConfigureAwait(false);
        total += groupResult.Total;
        resolved += groupResult.Resolved;

        var overrideResult = await BackfillRealtimeOverridesAsync(connection, transaction, devices, cancellationToken).ConfigureAwait(false);
        total += overrideResult.Total;
        resolved += overrideResult.Resolved;
        return new UserReferenceResult(total, resolved);
    }

    private static async Task<UserReferenceResult> BackfillSimpleReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyList<CurrentDevice> devices,
        CancellationToken cancellationToken)
    {
        var rows = new List<SimpleReference>();
        await using (var command = CreateCommand(
                         connection,
                         transaction,
                         $"SELECT id, building, card_name, device_uid FROM {QuoteIdentifier(table)} ORDER BY id"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new SimpleReference(
                    reader.GetInt64(0),
                    ReadNullableString(reader, 1),
                    ReadNullableString(reader, 2),
                    ReadNullableString(reader, 3)));
            }
        }

        var resolved = 0;
        foreach (var row in rows)
        {
            var candidates = FindCandidates(devices, row.Building, row.CardName);
            if (candidates.Count == 1)
            {
                await UpdateUserDeviceUidAsync(connection, transaction, table, row.Id, candidates[0].DeviceUid, cancellationToken).ConfigureAwait(false);
                resolved++;
                continue;
            }

            await UpdateUserDeviceUidAsync(connection, transaction, table, row.Id, null, cancellationToken).ConfigureAwait(false);
            await RecordAmbiguityAsync(
                    connection,
                    transaction,
                    table,
                    row.Id.ToString(CultureInfo.InvariantCulture),
                    candidates.Count == 0 ? "legacy_reference_not_found" : "legacy_reference_not_unique",
                    Unresolved,
                    null,
                    JsonSerializer.Serialize(new { row.Building, row.CardName, row.ExistingDeviceUid }),
                    CandidateUids(candidates),
                    null,
                    candidates.Count == 0
                        ? "No current device matches the legacy building and card name."
                        : "More than one current device matches the legacy building and card name.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new UserReferenceResult(rows.Count, resolved);
    }

    private static async Task<UserReferenceResult> BackfillGroupItemsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CurrentDevice> devices,
        CancellationToken cancellationToken)
    {
        var rows = new List<GroupReference>();
        await using (var command = CreateCommand(connection, transaction, """
                         SELECT id, target_type, building, floor_label, floor_value,
                                sub_area_text, card_name, device_uid
                         FROM monitor_group_items
                         WHERE target_type = 'device' OR NULLIF(TRIM(card_name), '') IS NOT NULL
                         ORDER BY id
                         """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new GroupReference(
                    reader.GetInt64(0),
                    ReadNullableString(reader, 1),
                    ReadNullableString(reader, 2),
                    ReadNullableString(reader, 3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    ReadNullableString(reader, 5),
                    ReadNullableString(reader, 6),
                    ReadNullableString(reader, 7)));
            }
        }

        var resolved = 0;
        foreach (var row in rows)
        {
            var candidates = FindCandidates(devices, row.Building, row.CardName)
                .Where(device => row.FloorValue is null || NearlyEqual(device.Floor, row.FloorValue))
                .Where(device => string.IsNullOrWhiteSpace(row.FloorLabel) ||
                                 DeviceFloorLabelFormatter.Normalize(DeviceFloorLabelFormatter.Format(device.Floor, device.SubAreaText)) ==
                                 DeviceFloorLabelFormatter.Normalize(row.FloorLabel))
                .Where(device => string.IsNullOrWhiteSpace(row.SubAreaText) ||
                                 Normalize(row.SubAreaText) == Normalize(device.SubAreaText))
                .ToArray();
            if (candidates.Length == 1)
            {
                await UpdateUserDeviceUidAsync(connection, transaction, "monitor_group_items", row.Id, candidates[0].DeviceUid, cancellationToken).ConfigureAwait(false);
                resolved++;
                continue;
            }

            await UpdateUserDeviceUidAsync(connection, transaction, "monitor_group_items", row.Id, null, cancellationToken).ConfigureAwait(false);
            await RecordAmbiguityAsync(
                    connection,
                    transaction,
                    "monitor_group_items",
                    row.Id.ToString(CultureInfo.InvariantCulture),
                    candidates.Length == 0 ? "device_group_member_not_found" : "device_group_member_not_unique",
                    Unresolved,
                    null,
                    JsonSerializer.Serialize(row),
                    CandidateUids(candidates),
                    null,
                    "The device group member was not silently mapped.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new UserReferenceResult(rows.Count, resolved);
    }

    private static async Task<UserReferenceResult> BackfillRealtimeOverridesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CurrentDevice> devices,
        CancellationToken cancellationToken)
    {
        var rows = new List<RealtimeOverrideReference>();
        await using (var command = CreateCommand(connection, transaction, """
                         SELECT id, building, dev_id, floor_label, sub_area, page_name,
                                realtime_name, action, target_card_id, device_uid
                         FROM realtime_match_overrides
                         ORDER BY id
                         """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new RealtimeOverrideReference(
                    reader.GetInt64(0),
                    ReadNullableString(reader, 1),
                    ReadNullableString(reader, 2),
                    ReadNullableString(reader, 3),
                    ReadNullableString(reader, 4),
                    ReadNullableString(reader, 5),
                    ReadNullableString(reader, 6),
                    ReadNullableString(reader, 7),
                    reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    ReadNullableString(reader, 9)));
            }
        }

        var byCardId = devices.ToDictionary(device => device.CardId);
        var resolved = 0;
        foreach (var row in rows)
        {
            if (row.TargetCardId is long targetId && byCardId.TryGetValue(targetId, out var direct))
            {
                var directMatches = MatchesOverrideIdentity(direct, row, includePage: false);
                if (directMatches)
                {
                    await UpdateUserDeviceUidAsync(connection, transaction, "realtime_match_overrides", row.Id, direct.DeviceUid, cancellationToken).ConfigureAwait(false);
                    resolved++;
                    continue;
                }

                await UpdateUserDeviceUidAsync(connection, transaction, "realtime_match_overrides", row.Id, null, cancellationToken).ConfigureAwait(false);
                await RecordOverrideAmbiguityAsync(
                        connection,
                        transaction,
                        row,
                        "target_card_identity_conflict",
                        Unresolved,
                        [direct],
                        null,
                        "The stored target_card_id exists but conflicts with the override identity fields.",
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var candidatesBeforePage = FindCandidates(devices, row.Building, row.RealtimeName)
                .Where(device => MatchesOverrideIdentity(device, row, includePage: false))
                .ToArray();
            if (string.Equals(row.Action, "create_virtual", StringComparison.OrdinalIgnoreCase))
            {
                await UpdateUserDeviceUidAsync(connection, transaction, "realtime_match_overrides", row.Id, null, cancellationToken).ConfigureAwait(false);
                await RecordOverrideAmbiguityAsync(
                        connection,
                        transaction,
                        row,
                        "virtual_override_without_collected_device",
                        Unresolved,
                        candidatesBeforePage,
                        null,
                        "Virtual overrides remain without device_uid until a collected device or explicit registry identity exists.",
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var hasSpecificPage = !string.IsNullOrWhiteSpace(row.PageName) &&
                                  !row.PageName.Equals("default", StringComparison.OrdinalIgnoreCase);
            var strictCandidates = hasSpecificPage
                ? candidatesBeforePage.Where(device => Normalize(device.PageName) == Normalize(row.PageName)).ToArray()
                : candidatesBeforePage;
            var candidates = strictCandidates.Length == 0 && hasSpecificPage
                ? candidatesBeforePage
                : strictCandidates;
            if (candidates.Length == 1)
            {
                var reason = row.TargetCardId is not null
                    ? strictCandidates.Length == 0 && hasSpecificPage
                        ? "dangling_target_unique_after_page_mismatch"
                        : "dangling_target_unique_identity"
                    : "override_without_target_unique_identity";
                var note = strictCandidates.Length == 0 && hasSpecificPage
                    ? "The page field disagreed, but building, device name, floor and sub-area identified exactly one card."
                    : "Existing identity fields identified exactly one current card.";
                await UpdateUserDeviceUidAsync(connection, transaction, "realtime_match_overrides", row.Id, candidates[0].DeviceUid, cancellationToken).ConfigureAwait(false);
                await RecordOverrideAmbiguityAsync(
                        connection,
                        transaction,
                        row,
                        reason,
                        AutoResolved,
                        candidates,
                        candidates[0].DeviceUid,
                        note,
                        cancellationToken)
                    .ConfigureAwait(false);
                resolved++;
                continue;
            }

            await UpdateUserDeviceUidAsync(connection, transaction, "realtime_match_overrides", row.Id, null, cancellationToken).ConfigureAwait(false);
            await RecordOverrideAmbiguityAsync(
                    connection,
                    transaction,
                    row,
                    candidates.Length == 0 ? "override_identity_not_found" : "override_identity_not_unique",
                    Unresolved,
                    candidates,
                    null,
                    "The override was not silently mapped.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new UserReferenceResult(rows.Count, resolved);
    }

    private static bool MatchesOverrideIdentity(
        CurrentDevice device,
        RealtimeOverrideReference row,
        bool includePage)
    {
        if (!string.IsNullOrWhiteSpace(row.FloorLabel) &&
            DeviceFloorLabelFormatter.Normalize(DeviceFloorLabelFormatter.Format(device.Floor, device.SubAreaText)) !=
            DeviceFloorLabelFormatter.Normalize(row.FloorLabel))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(row.SubArea) && Normalize(device.SubAreaText) != Normalize(row.SubArea))
        {
            return false;
        }

        return !includePage || string.IsNullOrWhiteSpace(row.PageName) ||
               row.PageName.Equals("default", StringComparison.OrdinalIgnoreCase) ||
               Normalize(device.PageName) == Normalize(row.PageName);
    }

    private static async Task RecordOverrideAmbiguityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RealtimeOverrideReference row,
        string reason,
        string status,
        IReadOnlyCollection<CurrentDevice> candidates,
        string? resolvedUid,
        string note,
        CancellationToken cancellationToken)
    {
        await RecordAmbiguityAsync(
                connection,
                transaction,
                "realtime_match_overrides",
                row.Id.ToString(CultureInfo.InvariantCulture),
                reason,
                status,
                null,
                JsonSerializer.Serialize(row),
                CandidateUids(candidates),
                resolvedUid,
                note,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<CurrentDevice>> LoadCurrentDevicesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var devices = new List<CurrentDevice>();
        await using var command = CreateCommand(connection, transaction, """
            SELECT c.id, c.source_key, c.device_uid, sa.building, sa.sub_idx, sa.floor,
                   sa.text, p.page_name, c.name
            FROM cards c
            JOIN pages p ON p.id = c.page_id
            JOIN sub_areas sa ON sa.id = p.sub_area_id
            WHERE c.device_uid IS NOT NULL
            ORDER BY c.id
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            devices.Add(new CurrentDevice(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                ReadNullableString(reader, 6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return devices;
    }

    private static IReadOnlyList<CurrentDevice> FindCandidates(
        IReadOnlyList<CurrentDevice> devices,
        string? building,
        string? name)
    {
        var buildingKey = Normalize(building);
        var nameKey = Normalize(name);
        if (string.IsNullOrWhiteSpace(nameKey))
        {
            return [];
        }

        return devices
            .Where(device => string.IsNullOrWhiteSpace(buildingKey) || Normalize(device.Building) == buildingKey)
            .Where(device => Normalize(device.DeviceName) == nameKey)
            .ToArray();
    }

    private static async Task<DeviceIdentityMigrationReport> BuildReportAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int userReferenceCount,
        int userReferenceResolvedCount,
        CancellationToken cancellationToken)
    {
        var current = await ReadIdentityCountsAsync(connection, transaction, "cards", cancellationToken).ConfigureAwait(false);
        var runs = await ReadIdentityCountsAsync(connection, transaction, "run_cards", cancellationToken).ConfigureAwait(false);
        var registryCount = await ReadCountAsync(connection, transaction, "SELECT COUNT(*) FROM device_registry", cancellationToken).ConfigureAwait(false);
        var aliasCount = await ReadCountAsync(connection, transaction, "SELECT COUNT(*) FROM device_source_keys", cancellationToken).ConfigureAwait(false);
        var ambiguities = new List<DeviceIdentityAmbiguityRecord>();
        await using (var command = CreateCommand(connection, transaction, """
                         SELECT id, entity_table, entity_key, reason_code, status, source_key,
                                identity_json, candidate_device_uids, resolved_device_uid, resolution_note
                         FROM device_identity_ambiguities
                         ORDER BY id
                         """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var candidateJson = reader.GetString(7);
                ambiguities.Add(new DeviceIdentityAmbiguityRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    ReadNullableString(reader, 5),
                    reader.GetString(6),
                    JsonSerializer.Deserialize<string[]>(candidateJson) ?? [],
                    ReadNullableString(reader, 8),
                    reader.GetString(9)));
            }
        }

        return new DeviceIdentityMigrationReport(
            current.Total,
            current.SourceKeys,
            current.DeviceUids,
            runs.Total,
            runs.SourceKeys,
            runs.DeviceUids,
            registryCount,
            aliasCount,
            userReferenceCount,
            userReferenceResolvedCount,
            ambiguities);
    }

    private static async Task<IdentityCounts> ReadIdentityCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"SELECT COUNT(*), COUNT(source_key), COUNT(device_uid) FROM {QuoteIdentifier(table)}");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new IdentityCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static async Task<int> ReadCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static bool TryBuildDescriptor(
        SourceRow row,
        out SourceDescriptor descriptor,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(row.Building) || row.SubAreaIndex is null ||
            string.IsNullOrWhiteSpace(row.PageName) || string.IsNullOrWhiteSpace(row.DeviceName))
        {
            descriptor = null!;
            error = "building, sub_idx, page_name and device name are required for source_key v1.";
            return false;
        }

        var sourceIdentity = new DeviceSourceIdentity(
            row.Building,
            row.SubAreaIndex.Value,
            row.PageName,
            row.DeviceName);
        descriptor = new SourceDescriptor(
            DeviceIdentityKeyBuilder.BuildSourceKey(sourceIdentity),
            DeviceIdentityKeyBuilder.CreateInitialDeviceUid(sourceIdentity),
            DeviceIdentityKeyBuilder.NormalizeIdentityText(row.Building),
            row.SubAreaIndex.Value,
            DeviceIdentityKeyBuilder.NormalizeIdentityText(row.PageName),
            DeviceIdentityKeyBuilder.NormalizeIdentityText(row.DeviceName),
            Occurrence: 1);
        error = string.Empty;
        return true;
    }

    private static SourceRow ReadSourceRow(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.IsDBNull(1) ? null : reader.GetInt64(1),
        ReadNullableString(reader, 2),
        reader.IsDBNull(3) ? null : reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetDouble(4),
        ReadNullableString(reader, 5),
        ReadNullableString(reader, 6),
        ReadNullableString(reader, 7),
        ReadNullableString(reader, 8),
        ReadNullableString(reader, 9));

    private static async Task UpdateCardIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        long id,
        string? sourceKey,
        string? deviceUid,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"UPDATE {QuoteIdentifier(table)} SET source_key = $source_key, device_uid = $device_uid WHERE id = $id");
        command.Parameters.AddWithValue("$source_key", (object?)sourceKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$device_uid", (object?)deviceUid ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateUserDeviceUidAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        long id,
        string? deviceUid,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"UPDATE {QuoteIdentifier(table)} SET device_uid = $device_uid WHERE id = $id");
        command.Parameters.AddWithValue("$device_uid", (object?)deviceUid ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecordAmbiguityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityTable,
        string entityKey,
        string reasonCode,
        string status,
        string? sourceKey,
        string identityJson,
        IReadOnlyList<string> candidateUids,
        string? resolvedUid,
        string note,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO device_identity_ambiguities
                (migration_version, detected_at, entity_table, entity_key, reason_code, status,
                 source_key, identity_json, candidate_device_uids, resolved_device_uid,
                 resolution_note, resolved_at)
            VALUES
                (2, $detected_at, $entity_table, $entity_key, $reason_code, $status,
                 $source_key, $identity_json, $candidate_device_uids, $resolved_device_uid,
                 $resolution_note, $resolved_at)
            ON CONFLICT(entity_table, entity_key, reason_code) DO NOTHING
            """);
        command.Parameters.AddWithValue("$detected_at", now);
        command.Parameters.AddWithValue("$entity_table", entityTable);
        command.Parameters.AddWithValue("$entity_key", entityKey);
        command.Parameters.AddWithValue("$reason_code", reasonCode);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$source_key", (object?)sourceKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$identity_json", identityJson);
        command.Parameters.AddWithValue("$candidate_device_uids", JsonSerializer.Serialize(candidateUids));
        command.Parameters.AddWithValue("$resolved_device_uid", (object?)resolvedUid ?? DBNull.Value);
        command.Parameters.AddWithValue("$resolution_note", note);
        command.Parameters.AddWithValue("$resolved_at", status == AutoResolved ? now : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string[] CandidateUids(IEnumerable<CurrentDevice> candidates) =>
        candidates.Select(candidate => candidate.DeviceUid).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string SerializeIdentity(SourceRow row, string error) =>
        JsonSerializer.Serialize(new
        {
            row.Id,
            row.RunId,
            row.Building,
            row.SubAreaIndex,
            row.PageName,
            row.DeviceName,
            error,
        });

    private static string Normalize(string? value) =>
        DeviceIdentityKeyBuilder.NormalizeIdentityText(value);

    private static bool NearlyEqual(double? left, double? right) =>
        left is not null && right is not null && Math.Abs(left.Value - right.Value) < 0.000001;

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed class IdentityState
    {
        private readonly Dictionary<string, SourceAlias> aliases;
        private readonly Dictionary<string, string> registryPrimarySources;

        private IdentityState(
            Dictionary<string, SourceAlias> aliases,
            Dictionary<string, string> registryPrimarySources)
        {
            this.aliases = aliases;
            this.registryPrimarySources = registryPrimarySources;
        }

        public static async Task<IdentityState> LoadAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
        {
            var registry = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var command = CreateCommand(
                             connection,
                             transaction,
                             "SELECT device_uid, primary_source_key FROM device_registry"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    registry[reader.GetString(0)] = reader.GetString(1);
                }
            }

            var aliases = new Dictionary<string, SourceAlias>(StringComparer.Ordinal);
            await using (var command = CreateCommand(connection, transaction, """
                             SELECT source_key, device_uid, building, sub_idx, page_name, device_name,
                                    first_seen_run_id, last_seen_run_id, is_current
                             FROM device_source_keys
                             """))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var alias = new SourceAlias(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt64(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7),
                        reader.GetInt64(8) != 0);
                    aliases[alias.SourceKey] = alias;
                }
            }

            return new IdentityState(aliases, registry);
        }

        public async Task<string?> ResolveOrCreateAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SourceDescriptor descriptor,
            string? preferredUid,
            bool isCurrent,
            long? firstRunId,
            long? lastRunId,
            CancellationToken cancellationToken)
        {
            if (aliases.TryGetValue(descriptor.SourceKey, out var existingAlias))
            {
                if (!existingAlias.Matches(descriptor) ||
                    (DeviceIdentityKeyBuilder.IsDeviceUid(preferredUid) && preferredUid != existingAlias.DeviceUid))
                {
                    await RecordAmbiguityAsync(
                            connection,
                            transaction,
                            "device_source_keys",
                            descriptor.SourceKey,
                            "source_alias_conflict",
                            Unresolved,
                            descriptor.SourceKey,
                            JsonSerializer.Serialize(new { descriptor, existingAlias, preferredUid }),
                            [existingAlias.DeviceUid],
                            null,
                            "Existing registry alias conflicts with the captured source identity.",
                            cancellationToken)
                        .ConfigureAwait(false);
                    return null;
                }

                await UpdateAliasObservationAsync(
                        connection,
                        transaction,
                        descriptor.SourceKey,
                        isCurrent,
                        firstRunId,
                        lastRunId,
                        cancellationToken)
                    .ConfigureAwait(false);
                aliases[descriptor.SourceKey] = existingAlias.WithObservation(isCurrent, firstRunId, lastRunId);
                return existingAlias.DeviceUid;
            }

            var uid = DeviceIdentityKeyBuilder.IsDeviceUid(preferredUid)
                ? preferredUid!
                : DeviceIdentityKeyBuilder.CreateInitialDeviceUid(descriptor.SourceKey);
            if (registryPrimarySources.TryGetValue(uid, out var primarySource) &&
                !DeviceIdentityKeyBuilder.IsDeviceUid(preferredUid))
            {
                await RecordAmbiguityAsync(
                        connection,
                        transaction,
                        "device_registry",
                        uid,
                        "deterministic_device_uid_collision",
                        Unresolved,
                        descriptor.SourceKey,
                        JsonSerializer.Serialize(new { descriptor, primarySource }),
                        [uid],
                        null,
                        "The deterministic UID already belongs to a different primary source key.",
                        cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            if (!registryPrimarySources.ContainsKey(uid))
            {
                await using var insertRegistry = CreateCommand(connection, transaction, """
                    INSERT INTO device_registry (device_uid, primary_source_key, status, created_at, updated_at)
                    VALUES ($device_uid, $source_key, 'active', $created_at, $updated_at)
                    """);
                insertRegistry.Parameters.AddWithValue("$device_uid", uid);
                insertRegistry.Parameters.AddWithValue("$source_key", descriptor.SourceKey);
                insertRegistry.Parameters.AddWithValue("$created_at", now);
                insertRegistry.Parameters.AddWithValue("$updated_at", now);
                await insertRegistry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                registryPrimarySources[uid] = descriptor.SourceKey;
            }

            await using (var insertAlias = CreateCommand(connection, transaction, """
                             INSERT INTO device_source_keys
                                 (source_key, device_uid, building, sub_idx, page_name, device_name,
                                  first_seen_run_id, last_seen_run_id, is_current, created_at, updated_at)
                             VALUES
                                 ($source_key, $device_uid, $building, $sub_idx, $page_name, $device_name,
                                  $first_seen_run_id, $last_seen_run_id, $is_current, $created_at, $updated_at)
                             """))
            {
                insertAlias.Parameters.AddWithValue("$source_key", descriptor.SourceKey);
                insertAlias.Parameters.AddWithValue("$device_uid", uid);
                insertAlias.Parameters.AddWithValue("$building", descriptor.Building);
                insertAlias.Parameters.AddWithValue("$sub_idx", descriptor.SubAreaIndex);
                insertAlias.Parameters.AddWithValue("$page_name", descriptor.PageName);
                insertAlias.Parameters.AddWithValue("$device_name", descriptor.DeviceName);
                insertAlias.Parameters.AddWithValue("$first_seen_run_id", (object?)firstRunId ?? DBNull.Value);
                insertAlias.Parameters.AddWithValue("$last_seen_run_id", (object?)lastRunId ?? DBNull.Value);
                insertAlias.Parameters.AddWithValue("$is_current", isCurrent ? 1 : 0);
                insertAlias.Parameters.AddWithValue("$created_at", now);
                insertAlias.Parameters.AddWithValue("$updated_at", now);
                await insertAlias.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            aliases[descriptor.SourceKey] = new SourceAlias(
                descriptor.SourceKey,
                uid,
                descriptor.Building,
                descriptor.SubAreaIndex,
                descriptor.PageName,
                descriptor.DeviceName,
                firstRunId,
                lastRunId,
                isCurrent);
            return uid;
        }

        private static async Task UpdateAliasObservationAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sourceKey,
            bool isCurrent,
            long? firstRunId,
            long? lastRunId,
            CancellationToken cancellationToken)
        {
            await using var command = CreateCommand(connection, transaction, """
                UPDATE device_source_keys
                SET first_seen_run_id = CASE
                        WHEN $first_run_id IS NULL THEN first_seen_run_id
                        WHEN first_seen_run_id IS NULL OR $first_run_id < first_seen_run_id THEN $first_run_id
                        ELSE first_seen_run_id END,
                    last_seen_run_id = CASE
                        WHEN $last_run_id IS NULL THEN last_seen_run_id
                        WHEN last_seen_run_id IS NULL OR $last_run_id > last_seen_run_id THEN $last_run_id
                        ELSE last_seen_run_id END,
                    is_current = CASE WHEN $is_current = 1 THEN 1 ELSE is_current END,
                    updated_at = $updated_at
                WHERE source_key = $source_key
                """);
            command.Parameters.AddWithValue("$first_run_id", (object?)firstRunId ?? DBNull.Value);
            command.Parameters.AddWithValue("$last_run_id", (object?)lastRunId ?? DBNull.Value);
            command.Parameters.AddWithValue("$is_current", isCurrent ? 1 : 0);
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$source_key", sourceKey);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record SourceRow(
        long Id,
        long? RunId,
        string? Building,
        long? SubAreaIndex,
        double? Floor,
        string? SubAreaText,
        string? PageName,
        string? DeviceName,
        string? ExistingSourceKey,
        string? ExistingDeviceUid);

    private sealed record SourceDescriptor(
        string SourceKey,
        string InitialDeviceUid,
        string Building,
        long SubAreaIndex,
        string PageName,
        string DeviceName,
        int Occurrence)
    {
        public SourceDescriptor WithOccurrence(int occurrence)
        {
            if (occurrence == Occurrence)
            {
                return this;
            }

            var identity = new DeviceSourceIdentity(Building, SubAreaIndex, PageName, DeviceName, occurrence);
            return this with
            {
                SourceKey = DeviceIdentityKeyBuilder.BuildSourceKey(identity),
                Occurrence = occurrence,
            };
        }
    }

    private sealed record SourceAlias(
        string SourceKey,
        string DeviceUid,
        string Building,
        long SubAreaIndex,
        string PageName,
        string DeviceName,
        long? FirstSeenRunId,
        long? LastSeenRunId,
        bool IsCurrent)
    {
        public bool Matches(SourceDescriptor descriptor) =>
            SourceKey == descriptor.SourceKey && Building == descriptor.Building &&
            SubAreaIndex == descriptor.SubAreaIndex && PageName == descriptor.PageName &&
            DeviceName == descriptor.DeviceName;

        public SourceAlias WithObservation(bool isCurrent, long? firstRunId, long? lastRunId) => this with
        {
            FirstSeenRunId = MinNullable(FirstSeenRunId, firstRunId),
            LastSeenRunId = MaxNullable(LastSeenRunId, lastRunId),
            IsCurrent = IsCurrent || isCurrent,
        };

        private static long? MinNullable(long? left, long? right) =>
            left is null ? right : right is null ? left : Math.Min(left.Value, right.Value);

        private static long? MaxNullable(long? left, long? right) =>
            left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);
    }

    private sealed record CurrentDevice(
        long CardId,
        string SourceKey,
        string DeviceUid,
        string Building,
        long SubAreaIndex,
        double? Floor,
        string? SubAreaText,
        string PageName,
        string DeviceName);

    private sealed record SimpleReference(long Id, string? Building, string? CardName, string? ExistingDeviceUid);
    private sealed record GroupReference(long Id, string? TargetType, string? Building, string? FloorLabel, double? FloorValue, string? SubAreaText, string? CardName, string? ExistingDeviceUid);
    private sealed record RealtimeOverrideReference(long Id, string? Building, string? DevId, string? FloorLabel, string? SubArea, string? PageName, string? RealtimeName, string? Action, long? TargetCardId, string? ExistingDeviceUid);
    private sealed record CurrentBackfillResult(int Total);
    private sealed record RunBackfillResult(int Total);
    private sealed record UserReferenceResult(int Total, int Resolved);
    private sealed record IdentityCounts(int Total, int SourceKeys, int DeviceUids);
}
