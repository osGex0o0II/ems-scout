using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EmsScout.Application.Devices;
using EmsScout.Infrastructure.Migrations;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Importing;

public sealed class CollectionSnapshotImporter
{
    private static readonly string[] UserDataTables =
    [
        "device_notes",
        "device_tags",
        "monitor_groups",
        "monitor_group_items",
        "manual_overrides",
        "realtime_match_overrides",
        "device_watch_rules",
        "area_group_rules",
        "area_group_members",
        "area_group_exceptions",
    ];

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly CollectionSnapshotReader reader;
    private readonly SqliteSchemaMigrator migrator;
    private readonly Func<string, CancellationToken, ValueTask>? faultCheckpoint;

    public CollectionSnapshotImporter(
        CollectionSnapshotReader? reader = null,
        SqliteSchemaMigrator? migrator = null)
    {
        this.reader = reader ?? new CollectionSnapshotReader();
        this.migrator = migrator ?? new SqliteSchemaMigrator();
    }

    internal CollectionSnapshotImporter(
        CollectionSnapshotReader? reader,
        SqliteSchemaMigrator? migrator,
        Func<string, CancellationToken, ValueTask> faultCheckpoint)
        : this(reader, migrator)
    {
        this.faultCheckpoint = faultCheckpoint ?? throw new ArgumentNullException(nameof(faultCheckpoint));
    }

    public async Task<CollectionImportParityReport> ValidateAsync(
        string snapshotPath,
        IReadOnlyList<string>? buildings = null,
        CancellationToken cancellationToken = default)
    {
        var read = await reader.ReadAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var importedBuildings = ResolveImportedBuildings(read.Snapshot, buildings);
        var snapshotInventory = BuildSnapshotInventory(read.Snapshot, importedBuildings);
        var issues = SnapshotApplyIssues(read.Snapshot).ToArray();
        return new(
            CollectionImportReportContract.Version,
            Operation: "validate",
            ReadOnly: true,
            ApplyReady: issues.Length == 0,
            read.Snapshot.WorkflowId,
            read.SourcePath,
            DatabasePath: null,
            read.Snapshot.Scope.Mode,
            ReplacementMode(read.Snapshot, buildings),
            importedBuildings,
            read.Snapshot.Counts,
            read.ArtifactVerification,
            snapshotInventory,
            DatabaseBefore: null,
            DatabaseAfter: null,
            Buildings: snapshotInventory.Buildings.Select(building =>
                new BuildingParity(building.Building, building, null, null, false, false)).ToArray(),
            UserDataBefore: [],
            UserDataAfter: [],
            issues,
            RunId: null,
            MigrationBackupPath: null);
    }

    public async Task<CollectionImportParityReport> ShadowAsync(
        CollectionImportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DatabasePath))
        {
            throw new ArgumentException("Database path is required for shadow import.", nameof(request));
        }

        var read = await reader.ReadAsync(request.SnapshotPath, cancellationToken).ConfigureAwait(false);
        var databasePath = NormalizeExistingDatabasePath(
            request.DatabasePath,
            "Shadow import requires an existing SQLite database; target was not found.");
        var importedBuildings = ResolveImportedBuildings(read.Snapshot, request.Buildings);
        var snapshotInventory = BuildSnapshotInventory(read.Snapshot, importedBuildings);

        await using var connection = OpenConnection(databasePath, readOnly: true);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var schema = await ReadSchemaStateAsync(connection, null, cancellationToken).ConfigureAwait(false);
        var databaseInventory = schema.CoreReady
            ? await ReadDatabaseInventoryAsync(connection, null, importedBuildings, cancellationToken).ConfigureAwait(false)
            : null;
        var userData = await ReadUserDataStateAsync(connection, null, cancellationToken).ConfigureAwait(false);
        var snapshotIssues = SnapshotApplyIssues(read.Snapshot).ToArray();
        var issues = snapshotIssues.Concat(schema.Issues).Distinct(StringComparer.Ordinal).ToArray();
        return BuildReport(
            operation: "shadow",
            readOnly: true,
            applyReady: snapshotIssues.Length == 0 && schema.ApplyReady,
            read,
            databasePath,
            importedBuildings,
            request.Buildings,
            snapshotInventory,
            databaseInventory,
            databaseInventory,
            userData,
            userData,
            issues,
            runId: null,
            migrationBackupPath: null);
    }

    public async Task<CollectionImportParityReport> ImportAsync(
        CollectionImportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.Apply) return await ShadowAsync(request, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.DatabasePath))
        {
            throw new ArgumentException("Database path is required for apply.", nameof(request));
        }

        var read = await reader.ReadAsync(request.SnapshotPath, cancellationToken).ConfigureAwait(false);
        var importedBuildings = ResolveImportedBuildings(read.Snapshot, request.Buildings);
        var snapshotIssues = SnapshotApplyIssues(read.Snapshot).ToArray();
        if (snapshotIssues.Length > 0)
        {
            throw new CollectionSnapshotContractException(
                "CollectionSnapshot cannot be applied: " + string.Join("; ", snapshotIssues));
        }

        var databasePath = Path.GetFullPath(request.DatabasePath);
        SchemaMigrationResult migration;
        if (File.Exists(databasePath))
        {
            migration = await migrator
                .MigrateAsync(databasePath, request.MigrationBackupPath, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            if (request.MigrationBackupPath is not null)
            {
                throw new InvalidOperationException(
                    "A migration backup cannot be requested when creating a new SQLite database.");
            }

            migration = await migrator
                .CreateNewAsync(databasePath, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var connection = OpenConnection(databasePath, readOnly: false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureWriteConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        var schema = await ReadSchemaStateAsync(connection, null, cancellationToken).ConfigureAwait(false);
        if (!schema.ApplyReady)
        {
            throw new InvalidOperationException(
                "Database is not ready for identity-safe CollectionSnapshot import: " +
                string.Join("; ", schema.Issues));
        }

        var snapshotInventory = BuildSnapshotInventory(read.Snapshot, importedBuildings);
        ImportInventorySummary databaseBefore;
        IReadOnlyList<UserDataTableState> userDataBefore;
        IReadOnlyList<UserDataTableState> userDataAfterTransaction;
        IReadOnlyDictionary<string, ResolvedSnapshotIdentity> resolvedIdentities;
        long runId;

        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            try
            {
                databaseBefore = await ReadDatabaseInventoryAsync(
                        connection, transaction, importedBuildings, cancellationToken)
                    .ConfigureAwait(false);
                userDataBefore = await ReadUserDataStateAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
                var runKey = BuildRunKey(read.Snapshot.WorkflowId, importedBuildings);
                var existingImport = await LoadExistingImportAsync(
                        connection, transaction, runKey, cancellationToken)
                    .ConfigureAwait(false);
                if (existingImport is not null)
                {
                    if (!string.Equals(
                            existingImport.ArtifactSha256,
                            read.Snapshot.Artifact.Sha256,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Idempotency conflict for {runKey}: the workflow/building selection was already imported with a different artifact.");
                    }

                    if (!await CurrentMatchesRunAsync(
                            connection,
                            transaction,
                            existingImport.RunId,
                            importedBuildings,
                            cancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            $"Idempotency conflict for {runKey}: current data no longer matches the original imported run.");
                    }

                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return BuildReport(
                        operation: "apply",
                        readOnly: false,
                        applyReady: true,
                        read,
                        databasePath,
                        importedBuildings,
                        request.Buildings,
                        snapshotInventory,
                        databaseBefore,
                        databaseBefore,
                        userDataBefore,
                        userDataBefore,
                        issues: [],
                        runId: existingImport.RunId,
                        migrationBackupPath: migration.BackupPath);
                }
                resolvedIdentities = await CollectionImportIdentityResolver.ResolveAsync(
                        connection,
                        transaction,
                        read.Snapshot,
                        importedBuildings.ToHashSet(StringComparer.Ordinal),
                        cancellationToken)
                    .ConfigureAwait(false);

                await EnsureRunKeyAvailableAsync(
                        connection, transaction, BuildRunKey(read.Snapshot.WorkflowId, importedBuildings), cancellationToken)
                    .ConfigureAwait(false);
                var replaceAll = ReplacementMode(read.Snapshot, request.Buildings) == "full";
                await CollectionImportIdentityResolver.DeactivateCurrentAliasesAsync(
                        connection, transaction, importedBuildings, replaceAll, cancellationToken)
                    .ConfigureAwait(false);
                await DeleteCurrentAsync(
                        connection,
                        transaction,
                        importedBuildings,
                        replaceAll,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (faultCheckpoint is not null)
                {
                    await faultCheckpoint("after_current_delete", cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                runId = await InsertSnapshotAsync(
                        connection,
                        transaction,
                        read,
                        importedBuildings,
                        snapshotInventory,
                        resolvedIdentities,
                        cancellationToken)
                    .ConfigureAwait(false);
                await SyncFloorCatalogAsync(
                        connection, transaction, read.Snapshot, importedBuildings, cancellationToken)
                    .ConfigureAwait(false);
                await SqliteAreaGroupReconciliationRepository.ReconcileAsync(
                        connection, transaction, runId, importedBuildings, cancellationToken)
                    .ConfigureAwait(false);
                if (faultCheckpoint is not null)
                {
                    await faultCheckpoint("after_area_group_reconciliation", cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                userDataAfterTransaction = await ReadUserDataStateAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
                EnsureUserDataUnchanged(userDataBefore, userDataAfterTransaction);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original import exception.
                }
                throw;
            }
        }

        await using var verifyConnection = OpenConnection(databasePath, readOnly: true);
        await verifyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var databaseAfter = await ReadDatabaseInventoryAsync(
                verifyConnection, null, importedBuildings, cancellationToken)
            .ConfigureAwait(false);
        var userDataAfter = await ReadUserDataStateAsync(verifyConnection, null, cancellationToken)
            .ConfigureAwait(false);
        EnsureUserDataUnchanged(userDataBefore, userDataAfter);

        return BuildReport(
            operation: "apply",
            readOnly: false,
            applyReady: true,
            read,
            databasePath,
            importedBuildings,
            request.Buildings,
            snapshotInventory,
            databaseBefore,
            databaseAfter,
            userDataBefore,
            userDataAfter,
            issues: [],
            runId,
            migration.BackupPath);
    }

    private static CollectionImportParityReport BuildReport(
        string operation,
        bool readOnly,
        bool applyReady,
        CollectionSnapshotReadResult read,
        string databasePath,
        IReadOnlyList<string> importedBuildings,
        IReadOnlyList<string>? requestedBuildings,
        ImportInventorySummary snapshotInventory,
        ImportInventorySummary? databaseBefore,
        ImportInventorySummary? databaseAfter,
        IReadOnlyList<UserDataTableState> userDataBefore,
        IReadOnlyList<UserDataTableState> userDataAfter,
        IReadOnlyList<string> issues,
        long? runId,
        string? migrationBackupPath)
    {
        var beforeByBuilding = databaseBefore?.Buildings.ToDictionary(item => item.Building, StringComparer.Ordinal);
        var afterByBuilding = databaseAfter?.Buildings.ToDictionary(item => item.Building, StringComparer.Ordinal);
        var parity = snapshotInventory.Buildings.Select(snapshotBuilding =>
        {
            BuildingInventory? before = null;
            BuildingInventory? after = null;
            if (beforeByBuilding is not null &&
                beforeByBuilding.TryGetValue(snapshotBuilding.Building, out var beforeValue)) before = beforeValue;
            if (afterByBuilding is not null &&
                afterByBuilding.TryGetValue(snapshotBuilding.Building, out var afterValue)) after = afterValue;
            return new BuildingParity(
                snapshotBuilding.Building,
                snapshotBuilding,
                before,
                after,
                before is not null && InventoryMatches(snapshotBuilding, before),
                after is not null && InventoryMatches(snapshotBuilding, after));
        }).ToArray();

        return new(
            CollectionImportReportContract.Version,
            operation,
            readOnly,
            applyReady,
            read.Snapshot.WorkflowId,
            read.SourcePath,
            databasePath,
            read.Snapshot.Scope.Mode,
            ReplacementMode(read.Snapshot, requestedBuildings),
            importedBuildings,
            read.Snapshot.Counts,
            read.ArtifactVerification,
            snapshotInventory,
            databaseBefore,
            databaseAfter,
            parity,
            userDataBefore,
            userDataAfter,
            issues,
            runId,
            migrationBackupPath);
    }

    private static IReadOnlyList<string> ResolveImportedBuildings(
        CollectionSnapshotV1 snapshot,
        IReadOnlyList<string>? requestedBuildings)
    {
        var payloadOrder = snapshot.Buildings.Select(building => building.Building).ToArray();
        if (requestedBuildings is null || requestedBuildings.Count == 0) return payloadOrder;

        var requested = requestedBuildings
            .Select(building => building.Trim())
            .Where(building => building.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var missing = requested.Except(payloadOrder, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new CollectionSnapshotContractException(
                "Requested import building(s) are absent from the snapshot: " + string.Join(", ", missing));
        }
        if (requested.Count == 0)
        {
            throw new CollectionSnapshotContractException("The requested building filter is empty.");
        }
        return payloadOrder.Where(requested.Contains).ToArray();
    }

    private static string ReplacementMode(CollectionSnapshotV1 snapshot, IReadOnlyList<string>? requestedBuildings) =>
        snapshot.Scope.Mode == "full" && (requestedBuildings is null || requestedBuildings.Count == 0)
            ? "full"
            : "selected_buildings";

    private static IEnumerable<string> SnapshotApplyIssues(CollectionSnapshotV1 snapshot)
    {
        if (snapshot.Quality.Decision == "rejected") yield return "Snapshot quality decision is rejected.";
        if (snapshot.Buildings.SelectMany(building => building.SubAreas)
            .SelectMany(subArea => subArea.Pages)
            .Any(page => page.Quality.Decision == "rejected"))
        {
            yield return "Snapshot contains one or more rejected pages.";
        }
        if (snapshot.Scope.Mode is "sub_area" or "recapture")
        {
            yield return $"Apply does not support {snapshot.Scope.Mode} scope because replacing a partial building would discard uncollected devices.";
        }
    }

    private static ImportInventorySummary BuildSnapshotInventory(
        CollectionSnapshotV1 snapshot,
        IReadOnlyList<string> selectedBuildings)
    {
        var selected = selectedBuildings.ToHashSet(StringComparer.Ordinal);
        var rows = snapshot.Buildings.Where(building => selected.Contains(building.Building)).Select(building =>
        {
            var pages = building.SubAreas.SelectMany(subArea => subArea.Pages).ToArray();
            var cards = pages.SelectMany(page => page.Cards).ToArray();
            return new BuildingInventory(
                building.Building,
                Exists: true,
                building.SubAreas.Count,
                pages.Length,
                pages.Sum(page => page.RawCount),
                cards.Length,
                pages.Sum(page => page.RawCount - page.UniqueCount),
                CountStatuses(cards.Select(card => card.Comm)));
        }).ToArray();
        return Summarize(rows);
    }

    private static ImportInventorySummary Summarize(IReadOnlyList<BuildingInventory> buildings) => new(
        buildings.Count(building => building.Exists),
        buildings.Sum(building => building.SubAreaCount),
        buildings.Sum(building => building.PageCount),
        buildings.Sum(building => building.RawCardCount),
        buildings.Sum(building => building.UniqueCardCount),
        buildings.Sum(building => building.DeduplicatedObservationCount),
        new(
            buildings.Sum(building => building.Statuses.Running),
            buildings.Sum(building => building.Statuses.Stopped),
            buildings.Sum(building => building.Statuses.Offline),
            buildings.Sum(building => building.Statuses.Unknown)),
        buildings);

    private static ImportStatusCounts CountStatuses(IEnumerable<string?> states)
    {
        var running = 0;
        var stopped = 0;
        var offline = 0;
        var unknown = 0;
        foreach (var state in states)
        {
            switch (state)
            {
                case "开机": running++; break;
                case "关机": stopped++; break;
                case "离线": offline++; break;
                default: unknown++; break;
            }
        }
        return new(running, stopped, offline, unknown);
    }

    private static bool InventoryMatches(BuildingInventory expected, BuildingInventory actual) =>
        expected.SubAreaCount == actual.SubAreaCount &&
        expected.PageCount == actual.PageCount &&
        expected.RawCardCount == actual.RawCardCount &&
        expected.UniqueCardCount == actual.UniqueCardCount &&
        expected.DeduplicatedObservationCount == actual.DeduplicatedObservationCount &&
        expected.Statuses == actual.Statuses;

    private static async Task<ImportInventorySummary> ReadDatabaseInventoryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken)
    {
        var rows = new List<BuildingInventory>(buildings.Count);
        foreach (var building in buildings)
        {
            var exists = await ScalarLongAsync(
                    connection, transaction, "SELECT COUNT(*) FROM buildings WHERE building = $building", building, cancellationToken)
                .ConfigureAwait(false) > 0;
            var subAreas = await ScalarLongAsync(
                    connection, transaction, "SELECT COUNT(*) FROM sub_areas WHERE building = $building", building, cancellationToken)
                .ConfigureAwait(false);
            var pages = await ScalarLongAsync(
                    connection,
                    transaction,
                    "SELECT COUNT(*) FROM pages p JOIN sub_areas sa ON sa.id = p.sub_area_id WHERE sa.building = $building",
                    building,
                    cancellationToken)
                .ConfigureAwait(false);

            int uniqueCards;
            ImportStatusCounts statuses;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT COUNT(*) AS unique_cards,
                           SUM(CASE WHEN c.comm = '开机' THEN 1 ELSE 0 END) AS running,
                           SUM(CASE WHEN c.comm = '关机' THEN 1 ELSE 0 END) AS stopped,
                           SUM(CASE WHEN c.comm = '离线' THEN 1 ELSE 0 END) AS offline,
                           SUM(CASE WHEN COALESCE(c.comm, '') NOT IN ('开机', '关机', '离线') THEN 1 ELSE 0 END) AS unknown
                    FROM cards c
                    JOIN pages p ON p.id = c.page_id
                    JOIN sub_areas sa ON sa.id = p.sub_area_id
                    WHERE sa.building = $building
                    """;
                command.Parameters.AddWithValue("$building", building);
                await using var cardReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await cardReader.ReadAsync(cancellationToken).ConfigureAwait(false);
                uniqueCards = DbInt(cardReader, 0);
                statuses = new(
                    DbInt(cardReader, 1),
                    DbInt(cardReader, 2),
                    DbInt(cardReader, 3),
                    DbInt(cardReader, 4));
            }

            await using var rawCommand = connection.CreateCommand();
            rawCommand.Transaction = transaction;
            rawCommand.CommandText = """
                SELECT COALESCE(SUM(COALESCE(p.raw_count, p.unique_count, p.count, 0)), 0),
                       COALESCE(SUM(MAX(COALESCE(p.raw_count, p.count, 0) - COALESCE(p.unique_count, p.count, 0), 0)), 0)
                FROM pages p
                JOIN sub_areas sa ON sa.id = p.sub_area_id
                WHERE sa.building = $building
                """;
            rawCommand.Parameters.AddWithValue("$building", building);
            await using var rawReader = await rawCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await rawReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            rows.Add(new(
                building,
                exists,
                checked((int)subAreas),
                checked((int)pages),
                DbInt(rawReader, 0),
                uniqueCards,
                DbInt(rawReader, 1),
                statuses));
        }
        return Summarize(rows);
    }

    private static int DbInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : checked(Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture));

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string building,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$building", building);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<ImportSchemaState> ReadSchemaStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var userVersion = checked((int)await ScalarLongAsync(connection, transaction, "PRAGMA user_version", cancellationToken)
            .ConfigureAwait(false));
        var quickCheck = await ScalarStringAsync(connection, transaction, "PRAGMA quick_check", cancellationToken)
            .ConfigureAwait(false) ?? "no result";
        var coreReady = await HasColumnsAsync(connection, transaction, "buildings", ["building", "sub_area_count", "menu_clicked", "updated_at"], cancellationToken) &&
                        await HasColumnsAsync(connection, transaction, "sub_areas", ["id", "building", "sub_idx", "floor", "text", "x", "y"], cancellationToken) &&
                        await HasColumnsAsync(connection, transaction, "pages", ["id", "sub_area_id", "page_name", "count", "raw_count", "unique_count", "duplicate_names", "on_href", "off_href", "layout", "quality_reason", "err"], cancellationToken) &&
                        await HasColumnsAsync(connection, transaction, "cards", ["id", "page_id", "name", "switch", "mode", "indoor", "set_temp", "fan", "indicator", "comm"], cancellationToken);
        var historyReady = await HasColumnsAsync(connection, transaction, "collection_runs", ["id", "run_key", "started_at", "completed_at", "imported_at", "status", "scope", "buildings", "json_path", "db_snapshot_path", "card_count", "on_count", "off_count", "offline_count", "unknown_count", "quality_summary", "is_anomaly", "note"], cancellationToken) &&
                           await HasColumnsAsync(connection, transaction, "run_buildings", ["id", "run_id", "building", "sub_area_count", "menu_clicked", "updated_at"], cancellationToken) &&
                           await HasColumnsAsync(connection, transaction, "run_sub_areas", ["id", "run_id", "source_sub_area_id", "building", "sub_idx", "floor", "floor_label", "text", "x", "y"], cancellationToken) &&
                           await HasColumnsAsync(connection, transaction, "run_pages", ["id", "run_id", "run_sub_area_id", "source_page_id", "page_name", "count", "raw_count", "unique_count", "duplicate_names", "on_href", "off_href", "layout", "quality_reason", "err"], cancellationToken) &&
                           await HasColumnsAsync(connection, transaction, "run_cards", ["id", "run_id", "run_page_id", "source_card_id", "name", "switch", "mode", "indoor", "set_temp", "fan", "indicator", "comm"], cancellationToken);
        var identityReady = await HasColumnsAsync(connection, transaction, "cards", ["source_key", "device_uid"], cancellationToken) &&
                            await HasColumnsAsync(connection, transaction, "run_cards", ["source_key", "device_uid"], cancellationToken) &&
                            await HasColumnsAsync(connection, transaction, "device_registry", ["device_uid", "primary_source_key", "status", "created_at", "updated_at"], cancellationToken) &&
                            await HasColumnsAsync(connection, transaction, "device_source_keys", ["source_key", "device_uid", "building", "sub_idx", "page_name", "device_name", "first_seen_run_id", "last_seen_run_id", "is_current", "created_at", "updated_at"], cancellationToken);
        var nullCurrent = identityReady
            ? await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM cards WHERE source_key IS NULL OR device_uid IS NULL OR source_key = '' OR device_uid = ''", cancellationToken).ConfigureAwait(false)
            : -1;
        var nullRun = identityReady
            ? await ScalarLongAsync(connection, transaction, "SELECT COUNT(*) FROM run_cards WHERE source_key IS NULL OR device_uid IS NULL OR source_key = '' OR device_uid = ''", cancellationToken).ConfigureAwait(false)
            : -1;

        var blocking = new List<string>();
        var warnings = new List<string>();
        if (userVersion < 2) blocking.Add($"Database user_version={userVersion}; identity import requires version 2 or later.");
        if (!quickCheck.Equals("ok", StringComparison.OrdinalIgnoreCase)) blocking.Add("SQLite quick_check failed: " + quickCheck);
        if (!coreReady) blocking.Add("Database core current tables are incomplete.");
        if (!historyReady) blocking.Add("Database run snapshot tables are incomplete.");
        if (!identityReady) blocking.Add("Identity registry and cards/run_cards source_key/device_uid columns are required; import will not downgrade.");
        if (nullCurrent > 0) blocking.Add($"Current cards contain {nullCurrent} unresolved identity row(s).");
        if (nullRun > 0) warnings.Add($"Historical run cards contain {nullRun} unresolved identity row(s); they are preserved but do not block a new current import.");
        return new(userVersion, quickCheck, coreReady, historyReady, identityReady, nullCurrent, nullRun, blocking, warnings);
    }

    private static async Task<bool> HasColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        IReadOnlyList<string> required,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) columns.Add(reader.GetString(1));
        return required.All(columns.Contains);
    }

    private static async Task<IReadOnlyList<UserDataTableState>> ReadUserDataStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var states = new List<UserDataTableState>(UserDataTables.Length);
        foreach (var table in UserDataTables)
        {
            if (!await TableExistsAsync(connection, transaction, table, cancellationToken).ConfigureAwait(false))
            {
                states.Add(new(table, false, 0, null));
                continue;
            }

            using var payload = new MemoryStream();
            long rowCount = 0;
            using (var writer = new Utf8JsonWriter(payload, new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = false,
            }))
            {
                writer.WriteStartArray();
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                var columns = table == "area_group_members"
                    ? "id, group_id, identity_key, device_uid, member_origin, note, created_at"
                    : "*";
                command.CommandText = $"SELECT {columns} FROM \"{table}\" ORDER BY rowid";
                await using var data = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await data.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rowCount++;
                    writer.WriteStartArray();
                    for (var ordinal = 0; ordinal < data.FieldCount; ordinal++) WriteDatabaseValue(writer, data.GetValue(ordinal));
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            states.Add(new(table, true, rowCount, CollectionSnapshotCanonicalJson.ComputeSha256(payload.ToArray())));
        }
        return states;
    }

    private static void WriteDatabaseValue(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case DBNull:
                writer.WriteNullValue();
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            case long integer:
                writer.WriteNumberValue(integer);
                break;
            case int integer:
                writer.WriteNumberValue(integer);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static void EnsureUserDataUnchanged(
        IReadOnlyList<UserDataTableState> before,
        IReadOnlyList<UserDataTableState> after)
    {
        if (!before.SequenceEqual(after))
        {
            throw new InvalidOperationException(
                "CollectionSnapshot import changed protected notes/groups/overrides/watch data; transaction was rejected.");
        }
    }

    private static async Task DeleteCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> buildings,
        bool replaceAll,
        CancellationToken cancellationToken)
    {
        if (replaceAll)
        {
            foreach (var table in new[] { "cards", "pages", "sub_areas", "buildings" })
            {
                await ExecuteAsync(connection, transaction, $"DELETE FROM {table}", cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        foreach (var building in buildings)
        {
            await ExecuteAsync(connection, transaction, """
                DELETE FROM cards
                WHERE page_id IN (
                    SELECT p.id FROM pages p JOIN sub_areas sa ON sa.id = p.sub_area_id WHERE sa.building = $building
                )
                """, cancellationToken, ("$building", building)).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                DELETE FROM pages
                WHERE sub_area_id IN (SELECT id FROM sub_areas WHERE building = $building)
                """, cancellationToken, ("$building", building)).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "DELETE FROM sub_areas WHERE building = $building", cancellationToken, ("$building", building)).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "DELETE FROM buildings WHERE building = $building", cancellationToken, ("$building", building)).ConfigureAwait(false);
        }
    }

    private static async Task<long> InsertSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CollectionSnapshotReadResult read,
        IReadOnlyList<string> importedBuildings,
        ImportInventorySummary inventory,
        IReadOnlyDictionary<string, ResolvedSnapshotIdentity> resolvedIdentities,
        CancellationToken cancellationToken)
    {
        var snapshot = read.Snapshot;
        var imported = importedBuildings.ToHashSet(StringComparer.Ordinal);
        var runKey = BuildRunKey(snapshot.WorkflowId, importedBuildings);
        var importedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var completedAt = snapshot.CompletedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var scope = imported.Count == snapshot.Buildings.Count && snapshot.Scope.Mode == "full" ? "full" : "partial";
        var qualitySummary = JsonSerializer.Serialize(new
        {
            contractVersion = snapshot.ContractVersion,
            workflowId = snapshot.WorkflowId,
            scopeMode = snapshot.Scope.Mode,
            decision = snapshot.Quality.Decision,
            findingCount = snapshot.Quality.Findings.Count,
            retryCount = snapshot.Quality.Retries.Count,
            artifactSha256 = snapshot.Artifact.Sha256,
            rawCardCount = inventory.RawCardCount,
            uniqueCardCount = inventory.UniqueCardCount,
            deduplicatedObservationCount = inventory.DeduplicatedObservationCount,
        }, CompactJson);

        var runId = await InsertAndGetIdAsync(connection, transaction, """
            INSERT INTO collection_runs
                (run_key, started_at, completed_at, imported_at, status, scope, buildings,
                 json_path, db_snapshot_path, card_count, on_count, off_count, offline_count,
                 unknown_count, quality_summary, is_anomaly, note)
            VALUES
                ($run_key, NULL, $completed_at, $imported_at, 'completed', $scope, $buildings,
                 $json_path, NULL, $card_count, $on_count, $off_count, $offline_count,
                 $unknown_count, $quality_summary, 0, $note)
            """, cancellationToken,
            ("$run_key", runKey),
            ("$completed_at", completedAt),
            ("$imported_at", importedAt),
            ("$scope", scope),
            ("$buildings", JsonSerializer.Serialize(importedBuildings, CompactJson)),
            ("$json_path", read.SourcePath),
            ("$card_count", inventory.UniqueCardCount),
            ("$on_count", inventory.Statuses.Running),
            ("$off_count", inventory.Statuses.Stopped),
            ("$offline_count", inventory.Statuses.Offline),
            ("$unknown_count", inventory.Statuses.Unknown),
            ("$quality_summary", qualitySummary),
            ("$note", $"CollectionSnapshot v1 import; workflowId={snapshot.WorkflowId}; scope={snapshot.Scope.Mode}"))
            .ConfigureAwait(false);

        foreach (var building in snapshot.Buildings.Where(building => imported.Contains(building.Building)))
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO buildings (building, sub_area_count, menu_clicked, updated_at)
                VALUES ($building, $sub_area_count, $menu_clicked, $updated_at)
                """, cancellationToken,
                ("$building", building.Building),
                ("$sub_area_count", building.SubAreas.Count),
                ("$menu_clicked", DbValue(building.MenuClicked)),
                ("$updated_at", completedAt)).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO run_buildings (run_id, building, sub_area_count, menu_clicked, updated_at)
                VALUES ($run_id, $building, $sub_area_count, $menu_clicked, $updated_at)
                """, cancellationToken,
                ("$run_id", runId),
                ("$building", building.Building),
                ("$sub_area_count", building.SubAreas.Count),
                ("$menu_clicked", DbValue(building.MenuClicked)),
                ("$updated_at", completedAt)).ConfigureAwait(false);

            foreach (var subArea in building.SubAreas)
            {
                var subAreaId = await InsertAndGetIdAsync(connection, transaction, """
                    INSERT INTO sub_areas (building, sub_idx, floor, text, x, y)
                    VALUES ($building, $sub_idx, $floor, $text, $x, $y)
                    """, cancellationToken,
                    ("$building", building.Building),
                    ("$sub_idx", subArea.Idx),
                    ("$floor", DbValue(subArea.Floor)),
                    ("$text", subArea.Text),
                    ("$x", DbValue(subArea.X)),
                    ("$y", DbValue(subArea.Y))).ConfigureAwait(false);
                var runSubAreaId = await InsertAndGetIdAsync(connection, transaction, """
                    INSERT INTO run_sub_areas
                        (run_id, source_sub_area_id, building, sub_idx, floor, floor_label, text, x, y)
                    VALUES
                        ($run_id, $source_sub_area_id, $building, $sub_idx, $floor, $floor_label, $text, $x, $y)
                    """, cancellationToken,
                    ("$run_id", runId),
                    ("$source_sub_area_id", subAreaId),
                    ("$building", building.Building),
                    ("$sub_idx", subArea.Idx),
                    ("$floor", DbValue(subArea.Floor)),
                    ("$floor_label", DbValue(subArea.FloorLabel)),
                    ("$text", subArea.Text),
                    ("$x", DbValue(subArea.X)),
                    ("$y", DbValue(subArea.Y))).ConfigureAwait(false);

                foreach (var page in subArea.Pages)
                {
                    var duplicateJson = JsonSerializer.Serialize(page.Duplicates, CompactJson);
                    var onHref = SourceString(page.SourceEvidence.OnHref);
                    var offHref = SourceString(page.SourceEvidence.OffHref);
                    var error = SourceString(page.SourceEvidence.Err);
                    var pageId = await InsertAndGetIdAsync(connection, transaction, """
                        INSERT INTO pages
                            (sub_area_id, page_name, count, raw_count, unique_count, duplicate_names,
                             on_href, off_href, layout, quality_reason, err)
                        VALUES
                            ($sub_area_id, $page_name, $count, $raw_count, $unique_count, $duplicate_names,
                             $on_href, $off_href, $layout, $quality_reason, $err)
                        """, cancellationToken,
                        ("$sub_area_id", subAreaId),
                        ("$page_name", page.Page),
                        ("$count", page.UniqueCount),
                        ("$raw_count", page.RawCount),
                        ("$unique_count", page.UniqueCount),
                        ("$duplicate_names", duplicateJson),
                        ("$on_href", DbValue(onHref)),
                        ("$off_href", DbValue(offHref)),
                        ("$layout", DbValue(page.Layout)),
                        ("$quality_reason", page.Quality.Reason),
                        ("$err", DbValue(error))).ConfigureAwait(false);
                    var runPageId = await InsertAndGetIdAsync(connection, transaction, """
                        INSERT INTO run_pages
                            (run_id, run_sub_area_id, source_page_id, page_name, count, raw_count,
                             unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err)
                        VALUES
                            ($run_id, $run_sub_area_id, $source_page_id, $page_name, $count, $raw_count,
                             $unique_count, $duplicate_names, $on_href, $off_href, $layout, $quality_reason, $err)
                        """, cancellationToken,
                        ("$run_id", runId),
                        ("$run_sub_area_id", runSubAreaId),
                        ("$source_page_id", pageId),
                        ("$page_name", page.Page),
                        ("$count", page.UniqueCount),
                        ("$raw_count", page.RawCount),
                        ("$unique_count", page.UniqueCount),
                        ("$duplicate_names", duplicateJson),
                        ("$on_href", DbValue(onHref)),
                        ("$off_href", DbValue(offHref)),
                        ("$layout", DbValue(page.Layout)),
                        ("$quality_reason", page.Quality.Reason),
                        ("$err", DbValue(error))).ConfigureAwait(false);

                    foreach (var card in page.Cards)
                    {
                        if (!resolvedIdentities.TryGetValue(card.SourceKey, out var resolvedIdentity))
                        {
                            throw new InvalidOperationException("Missing resolved identity for " + card.SourceKey);
                        }
                        var deviceUid = resolvedIdentity.DeviceUid;
                        await CollectionImportIdentityResolver.UpsertAsync(
                                connection, transaction, resolvedIdentity, runId, cancellationToken)
                            .ConfigureAwait(false);
                        var cardId = await InsertAndGetIdAsync(connection, transaction, """
                            INSERT INTO cards
                                (page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm,
                                 source_key, device_uid)
                            VALUES
                                ($page_id, $name, $switch, $mode, $indoor, $set_temp, $fan, $indicator, $comm,
                                 $source_key, $device_uid)
                            """, cancellationToken,
                            ("$page_id", pageId),
                            ("$name", card.Name),
                            ("$switch", DbValue(card.Switch)),
                            ("$mode", DbValue(card.Mode)),
                            ("$indoor", DbValue(NumberText(card.Indoor))),
                            ("$set_temp", DbValue(NumberText(card.SetTemp))),
                            ("$fan", DbValue(card.Fan)),
                            ("$indicator", DbValue(card.Indicator)),
                            ("$comm", card.Comm),
                            ("$source_key", card.SourceKey),
                            ("$device_uid", deviceUid)).ConfigureAwait(false);
                        await ExecuteAsync(connection, transaction, """
                            INSERT INTO run_cards
                                (run_id, run_page_id, source_card_id, name, switch, mode, indoor, set_temp,
                                 fan, indicator, comm, source_key, device_uid)
                            VALUES
                                ($run_id, $run_page_id, $source_card_id, $name, $switch, $mode, $indoor, $set_temp,
                                 $fan, $indicator, $comm, $source_key, $device_uid)
                            """, cancellationToken,
                            ("$run_id", runId),
                            ("$run_page_id", runPageId),
                            ("$source_card_id", cardId),
                            ("$name", card.Name),
                            ("$switch", DbValue(card.Switch)),
                            ("$mode", DbValue(card.Mode)),
                            ("$indoor", DbValue(NumberText(card.Indoor))),
                            ("$set_temp", DbValue(NumberText(card.SetTemp))),
                            ("$fan", DbValue(card.Fan)),
                            ("$indicator", DbValue(card.Indicator)),
                            ("$comm", card.Comm),
                            ("$source_key", card.SourceKey),
                            ("$device_uid", deviceUid)).ConfigureAwait(false);
                    }
                }
            }
        }
        return runId;
    }

    private static async Task SyncFloorCatalogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CollectionSnapshotV1 snapshot,
        IReadOnlyList<string> importedBuildings,
        CancellationToken cancellationToken)
    {
        var imported = importedBuildings.ToHashSet(StringComparer.Ordinal);
        var floors = snapshot.Buildings
            .Where(building => imported.Contains(building.Building))
            .SelectMany(building => building.SubAreas
                .Where(subArea => subArea.Floor.HasValue)
                .Select(subArea => (building.Building, Floor: subArea.Floor!.Value)))
            .Distinct()
            .OrderBy(item => item.Building, StringComparer.Ordinal)
            .ThenBy(item => item.Floor)
            .ToArray();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var floor in floors)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO floor_catalog
                    (building, floor_label, floor_value, source, enabled, note, created_at, updated_at)
                VALUES
                    ($building, $floor_label, $floor_value, 'discovered', 1, '', $now, $now)
                ON CONFLICT(building, floor_label) DO UPDATE SET
                    floor_value = excluded.floor_value,
                    source = CASE
                        WHEN floor_catalog.source = 'manual' THEN 'manual+discovered'
                        WHEN floor_catalog.source = 'manual+discovered' THEN 'manual+discovered'
                        ELSE 'discovered'
                    END,
                    updated_at = excluded.updated_at
                """;
            command.Parameters.AddWithValue("$building", floor.Building);
            command.Parameters.AddWithValue("$floor_label", FloorLabel(floor.Floor));
            command.Parameters.AddWithValue("$floor_value", floor.Floor);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string FloorLabel(double floor) => floor switch
    {
        -2 => "BM",
        < 0 => $"B{Math.Abs(floor).ToString("0.#", CultureInfo.InvariantCulture)}F",
        _ => floor.ToString("0.#", CultureInfo.InvariantCulture) + "F",
    };

    private static string BuildRunKey(string workflowId, IReadOnlyList<string> buildings)
    {
        var payload = Encoding.UTF8.GetBytes(workflowId + "\n" + string.Join("\n", buildings));
        return "cs1_" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()[..32];
    }

    private static async Task EnsureRunKeyAvailableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM collection_runs WHERE run_key = $run_key";
        command.Parameters.AddWithValue("$run_key", runKey);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
        {
            throw new InvalidOperationException("This workflow/building selection has already been imported: " + runKey);
        }
    }

    private static async Task<ExistingImport?> LoadExistingImportAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, quality_summary FROM collection_runs WHERE run_key = $run_key";
        command.Parameters.AddWithValue("$run_key", runKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var artifactSha256 = string.Empty;
        if (!reader.IsDBNull(1))
        {
            try
            {
                using var summary = JsonDocument.Parse(reader.GetString(1));
                if (summary.RootElement.TryGetProperty("artifactSha256", out var artifact) &&
                    artifact.ValueKind == JsonValueKind.String)
                {
                    artifactSha256 = artifact.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                // A legacy or malformed summary cannot prove an idempotent replay.
            }
        }

        return new ExistingImport(reader.GetInt64(0), artifactSha256);
    }

    private static async Task<bool> CurrentMatchesRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken)
    {
        var buildingParameters = string.Join(",", buildings.Select((_, index) => "$building" + index));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH current_rows AS (
                SELECT sa.building, sa.sub_idx, sa.floor, sa.text, sa.x, sa.y,
                       p.page_name, p.raw_count, p.unique_count, p.layout, p.quality_reason,
                       c.name, c.switch, c.mode, c.indoor, c.set_temp, c.fan, c.indicator,
                       c.comm, c.source_key, c.device_uid
                FROM cards c
                JOIN pages p ON p.id = c.page_id
                JOIN sub_areas sa ON sa.id = p.sub_area_id
                WHERE sa.building IN ({buildingParameters})
            ),
            run_rows AS (
                SELECT sa.building, sa.sub_idx, sa.floor, sa.text, sa.x, sa.y,
                       p.page_name, p.raw_count, p.unique_count, p.layout, p.quality_reason,
                       c.name, c.switch, c.mode, c.indoor, c.set_temp, c.fan, c.indicator,
                       c.comm, c.source_key, c.device_uid
                FROM run_cards c
                JOIN run_pages p ON p.id = c.run_page_id
                JOIN run_sub_areas sa ON sa.id = p.run_sub_area_id
                WHERE c.run_id = $run_id AND sa.building IN ({buildingParameters})
            )
            SELECT
                EXISTS(SELECT * FROM current_rows EXCEPT SELECT * FROM run_rows) OR
                EXISTS(SELECT * FROM run_rows EXCEPT SELECT * FROM current_rows)
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        for (var index = 0; index < buildings.Count; index++)
        {
            command.Parameters.AddWithValue("$building" + index, buildings[index]);
        }

        var differs = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return differs == 0;
    }

    private static async Task<long> InsertAndGetIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql + "; SELECT last_insert_rowid();";
        AddParameters(command, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameters(SqliteCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static string? NumberText(double? value) =>
        value?.ToString("G17", CultureInfo.InvariantCulture);

    private static string? SourceString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw new CollectionSnapshotContractException("Source evidence is not scalar."),
    };

    private sealed record ExistingImport(long RunId, string ArtifactSha256);

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task ConfigureWriteConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteConnection OpenConnection(string databasePath, bool readOnly)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        return new(builder.ToString());
    }

    private static string NormalizeExistingDatabasePath(string databasePath, string? missingMessage = null)
    {
        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(missingMessage ?? "SQLite database not found.", fullPath);
        }
        return fullPath;
    }
}
