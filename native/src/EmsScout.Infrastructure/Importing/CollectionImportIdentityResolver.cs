using System.Globalization;
using EmsScout.Application.Devices;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Importing;

internal sealed record ResolvedSnapshotIdentity(
    string SourceKey,
    string DeviceUid,
    string Building,
    long SubAreaIndex,
    string PageName,
    string DeviceName);

internal static class CollectionImportIdentityResolver
{
    public static async Task<IReadOnlyDictionary<string, ResolvedSnapshotIdentity>> ResolveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CollectionSnapshotV1 snapshot,
        IReadOnlySet<string> importedBuildings,
        CancellationToken cancellationToken)
    {
        var registry = await LoadRegistryAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var aliases = await LoadAliasesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var aliasesBySource = aliases.ToDictionary(alias => alias.SourceKey, StringComparer.Ordinal);
        var registryByPrimarySource = registry.Values
            .GroupBy(item => item.PrimarySourceKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.DeviceUid).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var assignedUids = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolved = new Dictionary<string, ResolvedSnapshotIdentity>(StringComparer.Ordinal);

        foreach (var building in snapshot.Buildings.Where(building => importedBuildings.Contains(building.Building)))
            foreach (var subArea in building.SubAreas)
                foreach (var page in subArea.Pages)
                    foreach (var card in page.Cards)
                    {
                        var descriptor = new ResolvedSnapshotIdentity(
                            card.SourceKey,
                            string.Empty,
                            building.Building,
                            subArea.Idx,
                            page.Page,
                            card.Name);
                        var uid = ResolveOne(
                            descriptor,
                            card.DeviceUid,
                            registry,
                            aliases,
                            aliasesBySource,
                            registryByPrimarySource);
                        if (assignedUids.TryGetValue(uid, out var otherSourceKey) &&
                            !otherSourceKey.Equals(card.SourceKey, StringComparison.Ordinal))
                        {
                            throw new CollectionSnapshotContractException(
                                $"Identity resolution assigned deviceUid {uid} to both {otherSourceKey} and {card.SourceKey}; " +
                                "an explicit identity decision is required.");
                        }
                        assignedUids[uid] = card.SourceKey;
                        resolved[card.SourceKey] = descriptor with { DeviceUid = uid };
                    }

        return resolved;
    }

    public static async Task DeactivateCurrentAliasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> importedBuildings,
        bool replaceAll,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = replaceAll
            ? "UPDATE device_source_keys SET is_current = 0, updated_at = $updated_at WHERE is_current <> 0"
            : $"""
               UPDATE device_source_keys
               SET is_current = 0, updated_at = $updated_at
               WHERE is_current <> 0 AND building IN ({string.Join(", ", importedBuildings.Select((_, index) => "$building" + index))})
               """;
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        for (var index = 0; index < importedBuildings.Count; index++)
        {
            command.Parameters.AddWithValue("$building" + index, importedBuildings[index]);
        }
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ResolvedSnapshotIdentity identity,
        long runId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using (var registry = connection.CreateCommand())
        {
            registry.Transaction = transaction;
            registry.CommandText = """
                INSERT INTO device_registry
                    (device_uid, primary_source_key, status, created_at, updated_at)
                VALUES
                    ($device_uid, $source_key, 'active', $now, $now)
                ON CONFLICT(device_uid) DO UPDATE SET
                    status = 'active',
                    updated_at = excluded.updated_at
                """;
            registry.Parameters.AddWithValue("$device_uid", identity.DeviceUid);
            registry.Parameters.AddWithValue("$source_key", identity.SourceKey);
            registry.Parameters.AddWithValue("$now", now);
            await registry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var alias = connection.CreateCommand();
        alias.Transaction = transaction;
        alias.CommandText = """
            INSERT INTO device_source_keys
                (source_key, device_uid, building, sub_idx, page_name, device_name,
                 first_seen_run_id, last_seen_run_id, is_current, created_at, updated_at)
            VALUES
                ($source_key, $device_uid, $building, $sub_idx, $page_name, $device_name,
                 $run_id, $run_id, 1, $now, $now)
            ON CONFLICT(source_key) DO UPDATE SET
                device_uid = excluded.device_uid,
                building = excluded.building,
                sub_idx = excluded.sub_idx,
                page_name = excluded.page_name,
                device_name = excluded.device_name,
                first_seen_run_id = COALESCE(device_source_keys.first_seen_run_id, excluded.first_seen_run_id),
                last_seen_run_id = excluded.last_seen_run_id,
                is_current = 1,
                updated_at = excluded.updated_at
            """;
        alias.Parameters.AddWithValue("$source_key", identity.SourceKey);
        alias.Parameters.AddWithValue("$device_uid", identity.DeviceUid);
        alias.Parameters.AddWithValue("$building", identity.Building);
        alias.Parameters.AddWithValue("$sub_idx", identity.SubAreaIndex);
        alias.Parameters.AddWithValue("$page_name", identity.PageName);
        alias.Parameters.AddWithValue("$device_name", identity.DeviceName);
        alias.Parameters.AddWithValue("$run_id", runId);
        alias.Parameters.AddWithValue("$now", now);
        await alias.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveOne(
        ResolvedSnapshotIdentity descriptor,
        string? snapshotUid,
        IReadOnlyDictionary<string, RegistryRow> registry,
        IReadOnlyList<AliasRow> aliases,
        IReadOnlyDictionary<string, AliasRow> aliasesBySource,
        IReadOnlyDictionary<string, string[]> registryByPrimarySource)
    {
        if (aliasesBySource.TryGetValue(descriptor.SourceKey, out var exactAlias))
        {
            EnsureAliasMatches(exactAlias, descriptor);
            EnsureRegistered(exactAlias.DeviceUid, registry, descriptor.SourceKey);
            if (snapshotUid is not null && !snapshotUid.Equals(exactAlias.DeviceUid, StringComparison.Ordinal))
            {
                throw new CollectionSnapshotContractException(
                    $"Snapshot deviceUid {snapshotUid} conflicts with exact source alias {descriptor.SourceKey} -> {exactAlias.DeviceUid}.");
            }
            return exactAlias.DeviceUid;
        }

        if (registryByPrimarySource.TryGetValue(descriptor.SourceKey, out var primaryUids))
        {
            if (primaryUids.Length != 1)
            {
                throw new CollectionSnapshotContractException(
                    $"Registry primary source {descriptor.SourceKey} maps to {primaryUids.Length} device UIDs.");
            }
            if (snapshotUid is not null && !snapshotUid.Equals(primaryUids[0], StringComparison.Ordinal))
            {
                throw new CollectionSnapshotContractException(
                    $"Snapshot deviceUid {snapshotUid} conflicts with registry primary source {primaryUids[0]}.");
            }
            return primaryUids[0];
        }

        if (snapshotUid is not null)
        {
            EnsureRegistered(snapshotUid, registry, descriptor.SourceKey);
            return snapshotUid;
        }

        var normalizedBuilding = DeviceIdentityKeyBuilder.NormalizeIdentityText(descriptor.Building);
        var normalizedName = DeviceIdentityKeyBuilder.NormalizeIdentityText(descriptor.DeviceName);
        var candidates = aliases
            .Where(alias =>
                DeviceIdentityKeyBuilder.NormalizeIdentityText(alias.Building) == normalizedBuilding &&
                DeviceIdentityKeyBuilder.NormalizeIdentityText(alias.DeviceName) == normalizedName)
            .Select(alias => alias.DeviceUid)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var candidate in candidates) EnsureRegistered(candidate, registry, descriptor.SourceKey);
        if (candidates.Length == 1) return candidates[0];
        if (candidates.Length > 1)
        {
            throw new CollectionSnapshotContractException(
                $"New source {descriptor.SourceKey} has {candidates.Length} registry candidates for " +
                $"{descriptor.Building}/{descriptor.DeviceName}; an explicit deviceUid is required.");
        }

        var initialUid = DeviceIdentityKeyBuilder.CreateInitialDeviceUid(descriptor.SourceKey);
        if (registry.ContainsKey(initialUid))
        {
            throw new CollectionSnapshotContractException(
                $"Deterministic deviceUid {initialUid} already exists for another source; import refuses to relink silently.");
        }
        return initialUid;
    }

    private static void EnsureAliasMatches(AliasRow alias, ResolvedSnapshotIdentity descriptor)
    {
        if (alias.SubAreaIndex != descriptor.SubAreaIndex ||
            DeviceIdentityKeyBuilder.NormalizeIdentityText(alias.Building) != DeviceIdentityKeyBuilder.NormalizeIdentityText(descriptor.Building) ||
            DeviceIdentityKeyBuilder.NormalizeIdentityText(alias.PageName) != DeviceIdentityKeyBuilder.NormalizeIdentityText(descriptor.PageName) ||
            DeviceIdentityKeyBuilder.NormalizeIdentityText(alias.DeviceName) != DeviceIdentityKeyBuilder.NormalizeIdentityText(descriptor.DeviceName))
        {
            throw new CollectionSnapshotContractException(
                $"Exact source alias {descriptor.SourceKey} has conflicting identity components.");
        }
    }

    private static void EnsureRegistered(
        string deviceUid,
        IReadOnlyDictionary<string, RegistryRow> registry,
        string sourceKey)
    {
        if (!DeviceIdentityKeyBuilder.IsDeviceUid(deviceUid) || !registry.ContainsKey(deviceUid))
        {
            throw new CollectionSnapshotContractException(
                $"Source {sourceKey} references deviceUid {deviceUid}, but it is not present in device_registry.");
        }
    }

    private static async Task<Dictionary<string, RegistryRow>> LoadRegistryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, RegistryRow>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT device_uid, primary_source_key, status FROM device_registry";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new RegistryRow(reader.GetString(0), reader.GetString(1), reader.GetString(2));
            rows[row.DeviceUid] = row;
        }
        return rows;
    }

    private static async Task<List<AliasRow>> LoadAliasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<AliasRow>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_key, device_uid, building, sub_idx, page_name, device_name
            FROM device_source_keys
            ORDER BY source_key
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5)));
        }
        return rows;
    }

    private sealed record RegistryRow(string DeviceUid, string PrimarySourceKey, string Status);

    private sealed record AliasRow(
        string SourceKey,
        string DeviceUid,
        string Building,
        long SubAreaIndex,
        string PageName,
        string DeviceName);
}
