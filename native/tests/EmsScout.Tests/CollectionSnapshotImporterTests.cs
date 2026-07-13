using EmsScout.Infrastructure.Importing;

namespace EmsScout.Tests;

public sealed class CollectionSnapshotImporterTests
{
    [Fact]
    public async Task FreshApplyCreatesCurrentDatabaseAndImportsSnapshot()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "nested", "ac.db");
        var snapshot = CollectionSnapshotTestFixture.Write(
            root,
            "fresh-apply-1",
            new SnapshotFixtureBuilding("1号", "1-0101-KT", Communication: "开机", RawCount: 2));

        var report = await new CollectionSnapshotImporter().ImportAsync(new(
            snapshot,
            database,
            Apply: true));

        Assert.True(File.Exists(database));
        Assert.False(report.ReadOnly);
        Assert.Equal("apply", report.Operation);
        Assert.Null(report.MigrationBackupPath);
        Assert.NotNull(report.DatabaseBefore);
        Assert.Equal(0, report.DatabaseBefore!.UniqueCardCount);
        Assert.NotNull(report.DatabaseAfter);
        Assert.Equal(1, report.DatabaseAfter!.UniqueCardCount);
        Assert.Equal(2, report.DatabaseAfter.RawCardCount);
        Assert.True(report.Buildings[0].AfterMatches);

        await using var connection = CollectionImportDatabaseFixture.Open(database);
        await connection.OpenAsync();
        Assert.Equal(5, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "PRAGMA user_version"));
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "SELECT COUNT(*) FROM cards"));
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "SELECT COUNT(*) FROM run_cards"));
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "SELECT COUNT(*) FROM device_registry"));
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "SELECT COUNT(*) FROM device_source_keys WHERE is_current=1"));
    }

    [Fact]
    public async Task FreshApplyFailureLeavesMigratedDatabaseWithoutPartialImport()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "fresh-failure.db");
        var snapshot = CollectionSnapshotTestFixture.Write(
            root,
            "fresh-failure-1",
            new SnapshotFixtureBuilding("1号", "1-0101-KT"));
        var node = CollectionSnapshotTestFixture.ReadNode(snapshot);
        node["buildings"]![0]!["subAreas"]![0]!["pages"]![0]!["cards"]![0]!["deviceUid"] =
            "duid1_" + new string('a', 64);
        CollectionSnapshotTestFixture.WriteNode(snapshot, node, recomputeArtifact: true);

        var error = await Assert.ThrowsAsync<CollectionSnapshotContractException>(
            () => new CollectionSnapshotImporter().ImportAsync(new(snapshot, database, Apply: true)));

        Assert.Contains("not present in device_registry", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(database));
        await using var connection = CollectionImportDatabaseFixture.Open(database);
        await connection.OpenAsync();
        Assert.Equal(5, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "PRAGMA user_version"));
        Assert.Equal(0, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "SELECT COUNT(*) FROM cards"));
        Assert.Equal(0, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "SELECT COUNT(*) FROM collection_runs"));
        Assert.Equal(0, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "SELECT COUNT(*) FROM device_registry"));
        Assert.Equal(0, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "SELECT COUNT(*) FROM device_source_keys"));
    }

    [Fact]
    public async Task ShadowImportRequiresExistingDatabaseWithoutCreatingIt()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "missing.db");
        var snapshot = CollectionSnapshotTestFixture.Write(
            root,
            "shadow-missing-1",
            new SnapshotFixtureBuilding("1号", "1-0101-KT"));

        var error = await Assert.ThrowsAsync<FileNotFoundException>(
            () => new CollectionSnapshotImporter().ImportAsync(new(snapshot, database)));

        Assert.Contains("Shadow import requires an existing SQLite database", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(database));
    }

    [Fact]
    public async Task InvalidSnapshotFailsBeforeTouchingExistingDatabase()
    {
        var root = TempDirectory();
        var database = await CollectionImportDatabaseFixture.CreateMigratedAsync(
            root,
            ("1号", "KEEP-KT", "default", "关机"));
        var snapshot = CollectionSnapshotTestFixture.Write(
            root,
            "invalid-before-db-1",
            new SnapshotFixtureBuilding("1号", "REPLACE-KT"));
        var node = CollectionSnapshotTestFixture.ReadNode(snapshot);
        node["buildings"]![0]!["subAreas"]![0]!["pages"]![0]!["cards"]![0]!["comm"] = "开机";
        CollectionSnapshotTestFixture.WriteNode(snapshot, node, recomputeArtifact: false);
        var before = File.ReadAllBytes(database);

        await Assert.ThrowsAsync<CollectionSnapshotContractException>(() =>
            new CollectionSnapshotImporter().ImportAsync(new(snapshot, database, Apply: true)));

        Assert.Equal(before, File.ReadAllBytes(database));
        await using var verify = CollectionImportDatabaseFixture.Open(database);
        await verify.OpenAsync();
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM cards WHERE name='KEEP-KT'"));
        Assert.Equal(0, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM cards WHERE name='REPLACE-KT'"));
    }

    [Fact]
    public async Task FreshApplyRejectsMigrationBackupWithoutCreatingFiles()
    {
        var root = TempDirectory();
        var database = Path.Combine(root, "missing.db");
        var backup = Path.Combine(root, "backup.db");
        var snapshot = CollectionSnapshotTestFixture.Write(
            root,
            "fresh-backup-1",
            new SnapshotFixtureBuilding("1号", "1-0101-KT"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new CollectionSnapshotImporter().ImportAsync(new(
                snapshot,
                database,
                Apply: true,
                MigrationBackupPath: backup)));

        Assert.Contains("cannot be requested", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(database));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public async Task ShadowImportIsReadOnlyAndReportsParity()
    {
        var root = TempDirectory();
        var database = await CollectionImportDatabaseFixture.CreateMigratedAsync(
            root,
            ("1号", "1-0101-KT", "default", "关机"));
        var snapshot = CollectionSnapshotTestFixture.Write(
            root,
            "shadow-1",
            new SnapshotFixtureBuilding("1号", "1-0101-KT"));
        var before = File.ReadAllBytes(database);

        var report = await new CollectionSnapshotImporter().ImportAsync(new(snapshot, database));

        Assert.True(report.ReadOnly);
        Assert.Equal("shadow", report.Operation);
        Assert.Equal(["1号"], report.ImportedBuildings);
        Assert.True(report.Buildings[0].BeforeMatches);
        Assert.Equal(before, File.ReadAllBytes(database));
    }

    [Fact]
    public async Task ExactReplayReturnsOriginalRunWithoutDuplicatingAnyRows()
    {
        var root = TempDirectory();
        var database = await CollectionImportDatabaseFixture.CreateMigratedAsync(
            root,
            ("1号", "OLD-KT", "default", "关机"));
        await SeedProtectedDataAsync(database);
        var snapshot = CollectionSnapshotTestFixture.Write(
            root,
            "idempotent-full-1",
            new SnapshotFixtureBuilding("1号", "NEW-KT", Communication: "开机"));
        var importer = new CollectionSnapshotImporter();

        var first = await importer.ImportAsync(new(snapshot, database, Apply: true));
        var beforeReplay = await TableCountsAsync(database);
        var replay = await importer.ImportAsync(new(snapshot, database, Apply: true));
        var afterReplay = await TableCountsAsync(database);

        Assert.Equal(first.RunId, replay.RunId);
        Assert.Equal("apply", replay.Operation);
        Assert.True(Assert.Single(replay.Buildings).AfterMatches);
        Assert.Equal(beforeReplay, afterReplay);
        Assert.Equal(first.UserDataAfter, replay.UserDataAfter);
    }

    [Fact]
    public async Task SameWorkflowWithDifferentArtifactIsRejectedWithoutChangingCurrentData()
    {
        var root = TempDirectory();
        var database = await CollectionImportDatabaseFixture.CreateMigratedAsync(
            root,
            ("1号", "OLD-KT", "default", "关机"));
        var firstSnapshot = CollectionSnapshotTestFixture.Write(
            Path.Combine(root, "first"),
            "idempotency-conflict-1",
            new SnapshotFixtureBuilding("1号", "FIRST-KT"));
        var conflictingSnapshot = CollectionSnapshotTestFixture.Write(
            Path.Combine(root, "conflict"),
            "idempotency-conflict-1",
            new SnapshotFixtureBuilding("1号", "SECOND-KT"));
        var importer = new CollectionSnapshotImporter();
        await importer.ImportAsync(new(firstSnapshot, database, Apply: true));
        var beforeConflict = await TableCountsAsync(database);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            importer.ImportAsync(new(conflictingSnapshot, database, Apply: true)));

        Assert.Contains("different artifact", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeConflict, await TableCountsAsync(database));
        await using var verify = CollectionImportDatabaseFixture.Open(database);
        await verify.OpenAsync();
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM cards WHERE name='FIRST-KT'"));
        Assert.Equal(0, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM cards WHERE name='SECOND-KT'"));
    }

    [Fact]
    public async Task ExactReplayIsRejectedWhenCurrentDataNoLongerMatchesOriginalRun()
    {
        var root = TempDirectory();
        var database = await CollectionImportDatabaseFixture.CreateMigratedAsync(
            root,
            ("1号", "OLD-KT", "default", "关机"));
        var snapshot = CollectionSnapshotTestFixture.Write(
            root,
            "idempotency-current-conflict-1",
            new SnapshotFixtureBuilding("1号", "IMPORTED-KT"));
        var importer = new CollectionSnapshotImporter();
        await importer.ImportAsync(new(snapshot, database, Apply: true));
        await using (var mutate = CollectionImportDatabaseFixture.Open(database))
        {
            await mutate.OpenAsync();
            await CollectionImportDatabaseFixture.ExecuteAsync(
                mutate,
                "UPDATE cards SET mode='后续批次值' WHERE name='IMPORTED-KT'");
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            importer.ImportAsync(new(snapshot, database, Apply: true)));

        Assert.Contains("current data no longer matches", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = CollectionImportDatabaseFixture.Open(database);
        await verify.OpenAsync();
        Assert.Equal("后续批次值", await CollectionImportDatabaseFixture.ScalarStringAsync(
            verify,
            "SELECT mode FROM cards WHERE name='IMPORTED-KT'"));
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM collection_runs"));
    }

    [Fact]
    public async Task SelectedBuildingReplayIsNoOpAndDoesNotTouchOtherBuildings()
    {
        var root = TempDirectory();
        var database = await CollectionImportDatabaseFixture.CreateMigratedAsync(
            root,
            ("1号", "OLD-1-KT", "default", "关机"),
            ("2号", "KEEP-2-KT", "default", "关机"));
        var snapshot = CollectionSnapshotTestFixture.Write(
            root,
            "idempotent-partial-1",
            new SnapshotFixtureBuilding("1号", "NEW-1-KT", Communication: "开机"),
            new SnapshotFixtureBuilding("2号", "UNUSED-2-KT", Communication: "离线"));
        var importer = new CollectionSnapshotImporter();

        var first = await importer.ImportAsync(new(snapshot, database, Buildings: ["1号"], Apply: true));
        var replay = await importer.ImportAsync(new(snapshot, database, Buildings: ["1号"], Apply: true));

        Assert.Equal(first.RunId, replay.RunId);
        await using var verify = CollectionImportDatabaseFixture.Open(database);
        await verify.OpenAsync();
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM cards c JOIN pages p ON p.id=c.page_id JOIN sub_areas sa ON sa.id=p.sub_area_id WHERE sa.building='1号' AND c.name='NEW-1-KT'"));
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM cards c JOIN pages p ON p.id=c.page_id JOIN sub_areas sa ON sa.id=p.sub_area_id WHERE sa.building='2号' AND c.name='KEEP-2-KT'"));
        Assert.Equal(0, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM cards WHERE name='UNUSED-2-KT'"));
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM collection_runs"));
    }

    [Fact]
    public async Task ApplyReplacesOnlySelectedBuildingsAndPreservesProtectedDataAndManualFloorSettings()
    {
        var root = TempDirectory();
        var database = await CollectionImportDatabaseFixture.CreateMigratedAsync(
            root,
            ("1号", "1-0101-KT", "default", "关机"),
            ("2号", "2-0101-KT", "default", "关机"));
        await SeedProtectedDataAsync(database);
        var snapshot = CollectionSnapshotTestFixture.Write(
            root,
            "selected-1",
            new SnapshotFixtureBuilding("1号", "1-0101-KT", Communication: "开机", RawCount: 2),
            new SnapshotFixtureBuilding("2号", "2-0101-KT", Communication: "离线"));

        var report = await new CollectionSnapshotImporter().ImportAsync(new(
            snapshot,
            database,
            Buildings: ["1号"],
            Apply: true));

        Assert.False(report.ReadOnly);
        Assert.Equal("selected_buildings", report.ReplacementMode);
        Assert.Equal(["1号"], report.ImportedBuildings);
        Assert.Equal(2, report.SnapshotSelected.RawCardCount);
        Assert.Equal(1, report.SnapshotSelected.UniqueCardCount);
        Assert.Equal(1, report.SnapshotSelected.DeduplicatedObservationCount);
        Assert.True(report.Buildings[0].AfterMatches);
        Assert.Equal(report.UserDataBefore, report.UserDataAfter);

        await using var connection = CollectionImportDatabaseFixture.Open(database);
        await connection.OpenAsync();
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "SELECT COUNT(*) FROM cards c JOIN pages p ON p.id=c.page_id JOIN sub_areas sa ON sa.id=p.sub_area_id WHERE sa.building='1号'"));
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "SELECT COUNT(*) FROM cards c JOIN pages p ON p.id=c.page_id JOIN sub_areas sa ON sa.id=p.sub_area_id WHERE sa.building='2号' AND c.name='2-0101-KT'"));
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "SELECT COUNT(*) FROM run_cards WHERE run_id=(SELECT MAX(id) FROM collection_runs)"));
        Assert.Equal("keep note", await CollectionImportDatabaseFixture.ScalarStringAsync(connection, "SELECT note FROM device_notes LIMIT 1"));
        Assert.Equal(0, await CollectionImportDatabaseFixture.ScalarLongAsync(connection, "SELECT enabled FROM floor_catalog WHERE building='1号' AND floor_label='1F'"));
        Assert.Equal("manual floor", await CollectionImportDatabaseFixture.ScalarStringAsync(connection, "SELECT note FROM floor_catalog WHERE building='1号' AND floor_label='1F'"));
    }

    [Fact]
    public async Task DeviceMovingPageKeepsRegistryUidAndNote()
    {
        var root = TempDirectory();
        var database = await CollectionImportDatabaseFixture.CreateMigratedAsync(
            root,
            ("1号", "1-0101-KT", "old-page", "关机"));
        await using (var seed = CollectionImportDatabaseFixture.Open(database))
        {
            await seed.OpenAsync();
            await CollectionImportDatabaseFixture.ExecuteAsync(seed, """
                INSERT INTO device_notes (card_name, building, device_uid, note, created_at, updated_at)
                SELECT c.name, sa.building, c.device_uid, 'move-safe', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM cards c JOIN pages p ON p.id=c.page_id JOIN sub_areas sa ON sa.id=p.sub_area_id
                WHERE c.name='1-0101-KT'
                """);
        }
        string originalUid;
        await using (var before = CollectionImportDatabaseFixture.Open(database))
        {
            await before.OpenAsync();
            originalUid = (await CollectionImportDatabaseFixture.ScalarStringAsync(before, "SELECT device_uid FROM cards LIMIT 1"))!;
        }
        var snapshot = CollectionSnapshotTestFixture.Write(
            root,
            "move-page-1",
            new SnapshotFixtureBuilding("1号", "1-0101-KT", PageName: "new-page"));

        await new CollectionSnapshotImporter().ImportAsync(new(snapshot, database, Apply: true));

        await using var verify = CollectionImportDatabaseFixture.Open(database);
        await verify.OpenAsync();
        Assert.Equal(originalUid, await CollectionImportDatabaseFixture.ScalarStringAsync(verify, "SELECT device_uid FROM cards LIMIT 1"));
        Assert.Equal(originalUid, await CollectionImportDatabaseFixture.ScalarStringAsync(verify, "SELECT device_uid FROM device_notes LIMIT 1"));
        Assert.Equal("move-safe", await CollectionImportDatabaseFixture.ScalarStringAsync(verify, "SELECT note FROM device_notes LIMIT 1"));
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(verify, "SELECT COUNT(*) FROM device_source_keys WHERE device_uid=(SELECT device_uid FROM cards LIMIT 1) AND is_current=1 AND page_name='new-page'"));
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(verify, "SELECT COUNT(*) FROM device_source_keys WHERE device_uid=(SELECT device_uid FROM cards LIMIT 1) AND is_current=0 AND page_name='old-page' COLLATE NOCASE"));
    }

    [Fact]
    public async Task InsertFailureRollsBackCurrentRunAndIdentityWrites()
    {
        var root = TempDirectory();
        var database = await CollectionImportDatabaseFixture.CreateMigratedAsync(
            root,
            ("1号", "OLD-KT", "default", "关机"));
        long runsBefore;
        await using (var setup = CollectionImportDatabaseFixture.Open(database))
        {
            await setup.OpenAsync();
            runsBefore = await CollectionImportDatabaseFixture.ScalarLongAsync(setup, "SELECT COUNT(*) FROM collection_runs");
            await CollectionImportDatabaseFixture.ExecuteAsync(setup, """
                CREATE TRIGGER fail_import_card
                BEFORE INSERT ON cards
                WHEN NEW.name = 'FAIL-KT'
                BEGIN
                  SELECT RAISE(ABORT, 'forced import failure');
                END
                """);
        }
        var snapshot = CollectionSnapshotTestFixture.Write(
            root,
            "rollback-1",
            new SnapshotFixtureBuilding("1号", "FAIL-KT"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => new CollectionSnapshotImporter().ImportAsync(new(snapshot, database, Apply: true)));

        await using var verify = CollectionImportDatabaseFixture.Open(database);
        await verify.OpenAsync();
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(verify, "SELECT COUNT(*) FROM cards WHERE name='OLD-KT'"));
        Assert.Equal(0, await CollectionImportDatabaseFixture.ScalarLongAsync(verify, "SELECT COUNT(*) FROM cards WHERE name='FAIL-KT'"));
        Assert.Equal(runsBefore, await CollectionImportDatabaseFixture.ScalarLongAsync(verify, "SELECT COUNT(*) FROM collection_runs"));
        Assert.Equal(0, await CollectionImportDatabaseFixture.ScalarLongAsync(verify, "SELECT COUNT(*) FROM device_source_keys WHERE device_name='FAIL-KT'"));
    }

    [Fact]
    public async Task CancellationAfterCurrentDeleteRollsBackCardsRunsAndIdentityAliases()
    {
        var root = TempDirectory();
        var database = await CollectionImportDatabaseFixture.CreateMigratedAsync(
            root,
            ("1号", "OLD-KT", "default", "关机"));
        var snapshot = CollectionSnapshotTestFixture.Write(
            root,
            "cancel-rollback-1",
            new SnapshotFixtureBuilding("1号", "NEW-KT"));
        long runsBefore;
        await using (var before = CollectionImportDatabaseFixture.Open(database))
        {
            await before.OpenAsync();
            runsBefore = await CollectionImportDatabaseFixture.ScalarLongAsync(
                before,
                "SELECT COUNT(*) FROM collection_runs");
        }
        using var cancellation = new CancellationTokenSource();
        var checkpoints = new List<string>();
        var importer = new CollectionSnapshotImporter(
            reader: null,
            migrator: null,
            faultCheckpoint: (checkpoint, _) =>
            {
                checkpoints.Add(checkpoint);
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            importer.ImportAsync(new(snapshot, database, Apply: true), cancellation.Token));

        Assert.Equal(["after_current_delete"], checkpoints);
        await using var verify = CollectionImportDatabaseFixture.Open(database);
        await verify.OpenAsync();
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM cards WHERE name='OLD-KT'"));
        Assert.Equal(0, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM cards WHERE name='NEW-KT'"));
        Assert.Equal(runsBefore, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM collection_runs"));
        Assert.Equal(1, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM device_source_keys WHERE device_name='OLD-KT' AND is_current=1"));
        Assert.Equal(0, await CollectionImportDatabaseFixture.ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM device_source_keys WHERE device_name='NEW-KT'"));
    }

    private static async Task SeedProtectedDataAsync(string database)
    {
        await using var connection = CollectionImportDatabaseFixture.Open(database);
        await connection.OpenAsync();
        await CollectionImportDatabaseFixture.ExecuteAsync(connection, """
            INSERT INTO device_notes (card_name, building, device_uid, note, created_at, updated_at)
            SELECT c.name, sa.building, c.device_uid, 'keep note', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            FROM cards c JOIN pages p ON p.id=c.page_id JOIN sub_areas sa ON sa.id=p.sub_area_id
            WHERE sa.building='1号' LIMIT 1;
            INSERT INTO device_tags (card_name, building, device_uid, tag, created_at)
            SELECT c.name, sa.building, c.device_uid, 'keep-tag', CURRENT_TIMESTAMP
            FROM cards c JOIN pages p ON p.id=c.page_id JOIN sub_areas sa ON sa.id=p.sub_area_id
            WHERE sa.building='1号' LIMIT 1;
            INSERT INTO manual_overrides (card_name, building, device_uid, field, value, reason, created_at, updated_at)
            SELECT c.name, sa.building, c.device_uid, 'mode', '制冷', 'keep', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            FROM cards c JOIN pages p ON p.id=c.page_id JOIN sub_areas sa ON sa.id=p.sub_area_id
            WHERE sa.building='1号' LIMIT 1;
            INSERT INTO monitor_groups (name, area_label, description, priority, group_kind, locked, enabled)
            VALUES ('keep-group', '', '', '重点', 'custom', 0, 1);
            INSERT INTO monitor_group_items (group_id, target_type, building, card_name, device_uid, note)
            SELECT 1, 'device', sa.building, c.name, c.device_uid, 'keep-item'
            FROM cards c JOIN pages p ON p.id=c.page_id JOIN sub_areas sa ON sa.id=p.sub_area_id
            WHERE sa.building='1号' LIMIT 1;
            INSERT INTO realtime_match_overrides
                (building, realtime_name, action, device_uid, note)
            SELECT sa.building, c.name, 'map_to_db', c.device_uid, 'keep-override'
            FROM cards c JOIN pages p ON p.id=c.page_id JOIN sub_areas sa ON sa.id=p.sub_area_id
            WHERE sa.building='1号' LIMIT 1;
            INSERT INTO device_watch_rules (group_id, name, start_at, end_at, enabled, note)
            VALUES (1, 'keep-watch', '00:00', '23:59', 1, 'keep-watch-note');
            INSERT INTO floor_catalog
                (building, floor_label, floor_value, source, enabled, note, created_at, updated_at)
            VALUES ('1号', '1F', 1, 'manual', 0, 'manual floor', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT(building, floor_label) DO UPDATE SET
                source='manual', enabled=0, note='manual floor';
            """);
    }

    private static async Task<IReadOnlyDictionary<string, long>> TableCountsAsync(string database)
    {
        await using var connection = CollectionImportDatabaseFixture.Open(database);
        await connection.OpenAsync();
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in new[]
                 {
                     "buildings", "sub_areas", "pages", "cards", "collection_runs",
                     "run_buildings", "run_sub_areas", "run_pages", "run_cards",
                     "device_registry", "device_source_keys", "device_notes", "device_tags",
                     "monitor_groups", "monitor_group_items", "realtime_match_overrides",
                     "device_watch_rules",
                 })
        {
            result[table] = await CollectionImportDatabaseFixture.ScalarLongAsync(
                connection,
                $"SELECT COUNT(*) FROM {table}");
        }

        return result;
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ems-snapshot-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
