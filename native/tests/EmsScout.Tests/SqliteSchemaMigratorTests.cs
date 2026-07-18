using System.Security.Cryptography;
using EmsScout.Application.Groups;
using EmsScout.Infrastructure.Migrations;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class SqliteSchemaMigratorTests
{
    [Fact]
    public async Task FreshDatabaseAppliesAreaGroupReconciliationV6()
    {
        var directory = CreateTempDirectory();
        var databasePath = Path.Combine(directory, "ac.db");
        try
        {
            var result = await new SqliteSchemaMigrator().CreateNewAsync(databasePath);

            Assert.Equal([1, 2, 3, 4, 5, 6], result.AppliedVersions);
            await using var connection = Open(databasePath, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync();
            Assert.Equal(6L, await ScalarInt64Async(connection, "PRAGMA user_version"));
            Assert.Equal(4L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('area_group_rules', 'area_group_members', 'area_group_exceptions', 'area_group_change_requests')"));
            Assert.Equal(2L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM monitor_groups WHERE system_key IN ('public', 'non_public') AND group_kind = 'custom' AND locked = 0"));
            Assert.Equal(1L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM area_group_rules WHERE rule_type = 'area_public' AND group_id = (SELECT id FROM monitor_groups WHERE system_key = 'public')"));
            Assert.Equal(1L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM area_group_rules WHERE rule_type = 'area_non_public' AND group_id = (SELECT id FROM monitor_groups WHERE system_key = 'non_public')"));
            Assert.Equal(1L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'ux_area_group_change_pending'"));
            Assert.Equal(1L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'device_watch_rules'"));
            Assert.Equal(1L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'schedule_groups'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingPresetGroupsBecomeEditableAndDeletedPresetDoesNotReseed()
    {
        var directory = CreateTempDirectory();
        var databasePath = Path.Combine(directory, "ac.db");
        try
        {
            await new SqliteSchemaMigrator().CreateNewAsync(databasePath);
            long publicGroupId;
            await using (var connection = Open(databasePath, SqliteOpenMode.ReadOnly))
            {
                await connection.OpenAsync();
                publicGroupId = await ScalarInt64Async(
                    connection,
                    "SELECT id FROM monitor_groups WHERE system_key = 'public'");
                Assert.Equal(0L, await ScalarInt64Async(
                    connection,
                    "SELECT locked FROM monitor_groups WHERE id = " + publicGroupId));
            }

            var repository = new SqliteAreaGroupRepository(() => databasePath);
            await repository.DeleteGroupAsync(publicGroupId);
            var reloaded = await repository.LoadAsync();

            Assert.DoesNotContain(reloaded.Groups, group => group.SystemKey == "public");
            Assert.Contains(reloaded.Groups, group => group.SystemKey == "non_public");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task V6MigrationKeepsSharedNoUidDeviceIdentityStableAcrossGroups()
    {
        var directory = CreateTempDirectory();
        var databasePath = Path.Combine(directory, "ac.db");
        try
        {
            await new SqliteSchemaMigrator().CreateNewAsync(databasePath);
            await using (var connection = Open(databasePath, SqliteOpenMode.ReadWrite))
            {
                await connection.OpenAsync();
                await ExecuteAsync(connection, """
                    DROP TABLE area_group_change_requests;
                    DROP TABLE area_group_exceptions;
                    DROP TABLE area_group_members;
                    DROP TABLE area_group_rules;
                    DELETE FROM ems_schema_migrations WHERE version = 6;
                    PRAGMA user_version = 5;
                    INSERT INTO monitor_groups
                        (id, name, area_label, description, priority, group_kind, system_key, locked, enabled)
                    VALUES
                        (10, '旧甲组', '', '', '重点', 'custom', NULL, 0, 1),
                        (11, '旧乙组', '', '', '重点', 'custom', NULL, 0, 1),
                        (12, '旧 UID 恢复组', '', '', '重点', 'custom', NULL, 0, 1);
                    INSERT INTO buildings (building, sub_area_count, menu_clicked) VALUES ('1号', 1, 'test');
                    INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y)
                    VALUES (10, '1号', 1, '1F SAME', 1, 100, 100);
                    INSERT INTO pages (id, sub_area_id, page_name, layout)
                    VALUES (10, 10, 'default', 'grid');
                    INSERT INTO cards
                        (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
                    VALUES
                        (10, 10, 'NOUID-AHU', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'shared-source', ''),
                        (11, 10, 'UID-AHU', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'uid-source', 'UID-CURRENT');
                    INSERT INTO monitor_group_items
                        (group_id, target_type, building, floor_label, floor_value, sub_area_text, card_name, device_uid, note)
                    VALUES
                        (10, 'device', '1号', '1F', 1, '1F SAME', 'NOUID-AHU', '', '甲组旧成员'),
                        (11, 'device', '1号', '1F', 1, '1F SAME', 'NOUID-AHU', '', '乙组旧成员'),
                        (12, 'device', '1号', '1F', 1, '1F SAME', 'UID-AHU', '', 'V1 旧成员');
                    """);
            }

            var migrated = await new SqliteSchemaMigrator().MigrateAsync(databasePath);

            Assert.Equal([6], migrated.AppliedVersions);
            await using var verify = Open(databasePath, SqliteOpenMode.ReadOnly);
            await verify.OpenAsync();
            Assert.Equal(2L, await ScalarInt64Async(verify,
                "SELECT COUNT(*) FROM area_group_members WHERE group_id IN (10, 11)"));
            Assert.Equal(1L, await ScalarInt64Async(verify,
                "SELECT COUNT(DISTINCT identity_key) FROM area_group_members WHERE group_id IN (10, 11)"));
            Assert.Equal(0L, await ScalarInt64Async(verify,
                "SELECT COUNT(*) FROM area_group_members WHERE group_id IN (10, 11) AND occurrence <> 1"));
            var currentUid = await ScalarStringAsync(verify, "SELECT device_uid FROM cards WHERE id = 11");
            Assert.False(string.IsNullOrWhiteSpace(currentUid));
            Assert.Equal("uid:" + currentUid!.ToUpperInvariant(), await ScalarStringAsync(verify,
                "SELECT identity_key FROM area_group_members WHERE group_id = 12"));
            Assert.Equal(currentUid, await ScalarStringAsync(verify,
                "SELECT device_uid FROM area_group_members WHERE group_id = 12"));
            Assert.Equal(1L, await ScalarInt64Async(verify,
                "SELECT occurrence FROM area_group_members WHERE group_id = 12"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task V6MigrationDoesNotExpandBlankUidItemsAcrossMixedIdentityMatches()
    {
        var directory = CreateTempDirectory();
        var databasePath = Path.Combine(directory, "ac.db");
        try
        {
            await new SqliteSchemaMigrator().CreateNewAsync(databasePath);
            await using (var connection = Open(databasePath, SqliteOpenMode.ReadWrite))
            {
                await connection.OpenAsync();
                await ExecuteAsync(connection, """
                    DROP TABLE area_group_change_requests;
                    DROP TABLE area_group_exceptions;
                    DROP TABLE area_group_members;
                    DROP TABLE area_group_rules;
                    DELETE FROM ems_schema_migrations WHERE version = 6;
                    PRAGMA user_version = 5;
                    INSERT INTO monitor_groups
                        (id, name, area_label, description, priority, group_kind, system_key, locked, enabled)
                    VALUES
                        (20, '旧甲组', '', '', '重点', 'custom', NULL, 0, 1),
                        (21, '旧乙组', '', '', '重点', 'custom', NULL, 0, 1);
                    INSERT INTO buildings (building, sub_area_count, menu_clicked) VALUES ('1号', 1, 'test');
                    INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y)
                    VALUES (20, '1号', 1, '1F MIXED', 1, 100, 100);
                    INSERT INTO pages (id, sub_area_id, page_name, layout)
                    VALUES (20, 20, 'default', 'grid');
                    INSERT INTO cards
                        (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm, source_key, device_uid)
                    VALUES
                        (20, 20, 'MIXED-AHU', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'a-uid', 'UID-MIXED'),
                        (21, 20, 'MIXED-AHU', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'b-no-uid', ''),
                        (22, 20, 'MIXED-AHU', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机', 'c-no-uid', '');
                    CREATE TRIGGER preserve_mixed_no_uid
                    AFTER UPDATE OF device_uid ON cards
                    WHEN NEW.id IN (21, 22) AND COALESCE(NEW.device_uid, '') <> ''
                    BEGIN
                        UPDATE cards SET device_uid = NULL WHERE id = NEW.id;
                    END;
                    INSERT INTO monitor_group_items
                        (group_id, target_type, building, floor_label, floor_value, sub_area_text, card_name, device_uid, note)
                    VALUES
                        (20, 'device', '1号', '1F', 1, '1F MIXED', 'MIXED-AHU', '', '甲组旧成员'),
                        (21, 'device', '1号', '1F', 1, '1F MIXED', 'MIXED-AHU', '', '乙组旧成员');
                    CREATE TRIGGER preserve_mixed_member_no_uid
                    AFTER UPDATE OF device_uid ON monitor_group_items
                    WHEN NEW.group_id IN (20, 21) AND COALESCE(NEW.device_uid, '') <> ''
                    BEGIN
                        UPDATE monitor_group_items SET device_uid = NULL WHERE id = NEW.id;
                    END;
                    """);
            }

            var migrated = await new SqliteSchemaMigrator().MigrateAsync(databasePath);

            Assert.Equal([6], migrated.AppliedVersions);
            string migratedIdentity;
            string selectedSourceKey;
            await using (var verify = Open(databasePath, SqliteOpenMode.ReadOnly))
            {
                await verify.OpenAsync();
                Assert.Equal(2L, await ScalarInt64Async(verify,
                    "SELECT COUNT(*) FROM area_group_members WHERE group_id IN (20, 21)"));
                Assert.Equal(2L, await ScalarInt64Async(verify, """
                    SELECT COUNT(*)
                    FROM area_group_members
                    WHERE group_id IN (20, 21)
                      AND device_uid = ''
                      AND occurrence = 1
                    """));
                Assert.Equal(1L, await ScalarInt64Async(verify,
                    "SELECT COUNT(DISTINCT identity_key) FROM area_group_members WHERE group_id IN (20, 21)"));
                selectedSourceKey = (await ScalarStringAsync(verify, """
                    SELECT source_key
                    FROM cards
                    WHERE id IN (21, 22) AND COALESCE(TRIM(device_uid), '') = ''
                    ORDER BY source_key, id
                    LIMIT 1
                    """))!;
                Assert.Equal(selectedSourceKey, await ScalarStringAsync(verify,
                    "SELECT source_key FROM area_group_members WHERE group_id = 20"));
                migratedIdentity = (await ScalarStringAsync(verify,
                    "SELECT identity_key FROM area_group_members WHERE group_id = 20"))!;
                Assert.Equal(
                    $"legacy:1号|1F|1F MIXED|DEFAULT|MIXED-AHU|{selectedSourceKey.ToUpperInvariant()}|1",
                    migratedIdentity);
            }

            var noUidRuntimeDevice = Assert.Single(
                await new SqliteAreaGroupRepository(() => databasePath).LoadTargetOptionsAsync("1号", "1F"),
                option =>
                    option.Type == "device" &&
                    option.CardName == "MIXED-AHU" &&
                    option.DeviceUid == "" &&
                    option.SourceKey == selectedSourceKey);
            Assert.Equal(1, noUidRuntimeDevice.Occurrence);
            var runtimeMember = await new SqliteAreaGroupReconciliationRepository(() => databasePath)
                .AddManualMemberAsync(new AreaGroupManualMemberEdit(
                    20, "", noUidRuntimeDevice.Building, noUidRuntimeDevice.FloorLabel,
                    noUidRuntimeDevice.FloorValue, noUidRuntimeDevice.SubAreaText, noUidRuntimeDevice.PageName,
                    noUidRuntimeDevice.CardName, noUidRuntimeDevice.SourceKey, noUidRuntimeDevice.Occurrence,
                    "运行时身份对照"));
            Assert.Equal(migratedIdentity, runtimeMember.IdentityKey);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SchemaRepairDoesNotReseedDeletedOrRenamedPresets()
    {
        var directory = CreateTempDirectory();
        var databasePath = Path.Combine(directory, "ac.db");
        try
        {
            await new SqliteSchemaMigrator().CreateNewAsync(databasePath);
            await using (var connection = Open(databasePath, SqliteOpenMode.ReadWrite))
            {
                await connection.OpenAsync();
                await ExecuteAsync(connection, """
                    DELETE FROM area_group_rules WHERE group_id = (SELECT id FROM monitor_groups WHERE system_key = 'public');
                    DELETE FROM monitor_groups WHERE system_key = 'public';
                    UPDATE monitor_groups SET name = '普通办公区', area_label = '办公区' WHERE system_key = 'non_public';
                    DROP INDEX idx_area_group_rules_group;
                    """);
            }

            var repaired = await new SqliteSchemaMigrator().MigrateAsync(databasePath);

            Assert.True(repaired.After.IsCurrent);
            await using var verify = Open(databasePath, SqliteOpenMode.ReadOnly);
            await verify.OpenAsync();
            Assert.Equal(0L, await ScalarInt64Async(
                verify, "SELECT COUNT(*) FROM monitor_groups WHERE system_key = 'public'"));
            Assert.Equal(1L, await ScalarInt64Async(
                verify, "SELECT COUNT(*) FROM monitor_groups WHERE system_key = 'non_public' AND name = '普通办公区' AND area_label = '办公区'"));
            Assert.Equal(1L, await ScalarInt64Async(
                verify, "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'idx_area_group_rules_group'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateNewAsyncPublishesCurrentEmptyDatabaseAndMigrationIsByteStable()
    {
        var directory = CreateTempDirectory();
        var databasePath = Path.Combine(directory, "nested", "ac.db");
        try
        {
            var migrator = new SqliteSchemaMigrator();
            var created = await migrator.CreateNewAsync(databasePath);

            Assert.True(File.Exists(databasePath));
            Assert.Null(created.BackupPath);
            Assert.Equal([1, 2, 3, 4, 5, 6], created.AppliedVersions);
            Assert.Equal("absent", created.Before.DatabaseShape);
            Assert.True(created.After.IsCurrent);
            Assert.Equal("versioned-identity-v2", created.After.DatabaseShape);
            Assert.Equal(6, created.After.UserVersion);
            Assert.NotNull(created.IdentityReport);
            Assert.Equal(0, created.IdentityReport.CurrentCardCount);
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(databasePath)!, "*.partial"));

            await using (var connection = Open(databasePath, SqliteOpenMode.ReadOnly))
            {
                await connection.OpenAsync();
                Assert.Equal("ok", await ScalarStringAsync(connection, "PRAGMA quick_check"));
                Assert.Equal(6L, await ScalarInt64Async(connection, "PRAGMA user_version"));
                Assert.Equal(6L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM ems_schema_migrations"));
                Assert.Equal(
                    6L,
                    await ScalarInt64Async(
                        connection,
                        "SELECT COUNT(*) FROM ems_schema_migrations WHERE backup_path = 'not-required:fresh-create'"));
                Assert.Equal(0L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM cards"));
                Assert.Equal(0L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM run_cards"));
                Assert.Equal(0L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM device_registry"));
            }

            var beforeHash = await Sha256Async(databasePath);
            var second = await migrator.MigrateAsync(databasePath);
            var afterHash = await Sha256Async(databasePath);

            Assert.Null(second.BackupPath);
            Assert.Empty(second.AppliedVersions);
            Assert.Empty(second.AppliedChanges);
            Assert.Equal(beforeHash, afterHash);
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(databasePath)!, "*.backup.db"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateNewAsyncRefusesExistingTargetWithoutChangingIt()
    {
        var directory = CreateTempDirectory();
        var databasePath = Path.Combine(directory, "ac.db");
        var original = new byte[] { 1, 3, 3, 7 };
        await File.WriteAllBytesAsync(databasePath, original);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new SqliteSchemaMigrator().CreateNewAsync(databasePath));
            Assert.Equal(original, await File.ReadAllBytesAsync(databasePath));
            Assert.Single(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateAsyncStillRejectsExistingDatabaseWithMissingCoreTables()
    {
        var directory = CreateTempDirectory();
        var databasePath = Path.Combine(directory, "ac.db");
        try
        {
            await using (var connection = Open(databasePath, SqliteOpenMode.ReadWriteCreate))
            {
                await connection.OpenAsync();
                await ExecuteAsync(connection, "CREATE TABLE unrelated (id INTEGER PRIMARY KEY)");
            }

            var error = await Assert.ThrowsAsync<SchemaMigrationException>(
                () => new SqliteSchemaMigrator().MigrateAsync(databasePath));

            Assert.Contains("Required core table", error.Message, StringComparison.Ordinal);
            Assert.Null(error.BackupPath);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.backup.db"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateAsyncRejectsCorruptFileWithoutChangingOrReplacingIt()
    {
        var directory = CreateTempDirectory();
        var databasePath = Path.Combine(directory, "ac.db");
        var original = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        await File.WriteAllBytesAsync(databasePath, original);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => new SqliteSchemaMigrator().MigrateAsync(databasePath));

            Assert.Equal(original, await File.ReadAllBytesAsync(databasePath));
            Assert.Equal([databasePath], Directory.EnumerateFiles(directory).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VerificationFailureRollsBackMigrationAndRetainsRecoveryBackup()
    {
        var directory = CreateTempDirectory();
        var databasePath = Path.Combine(directory, "ac.db");
        try
        {
            await CreateArchivedCoreFixtureAsync(databasePath);
            await using (var connection = Open(databasePath, SqliteOpenMode.ReadWrite))
            {
                await connection.OpenAsync();
                await ExecuteAsync(connection, """
                    UPDATE sub_areas SET sub_idx = 0;
                    CREATE TABLE device_registry (
                        device_uid TEXT PRIMARY KEY,
                        primary_source_key TEXT NOT NULL,
                        status TEXT NOT NULL DEFAULT 'active',
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    CREATE TRIGGER fail_identity_migration
                    BEFORE INSERT ON device_registry
                    BEGIN
                      SELECT RAISE(ABORT, 'forced identity migration failure');
                    END;
                    """);
            }
            var beforeHash = await Sha256Async(databasePath);

            var error = await Assert.ThrowsAsync<SchemaMigrationException>(
                () => new SqliteSchemaMigrator().MigrateAsync(databasePath));

            Assert.NotNull(error.BackupPath);
            Assert.True(File.Exists(error.BackupPath));
            Assert.Equal(beforeHash, await Sha256Async(databasePath));
            await using var verify = Open(databasePath, SqliteOpenMode.ReadOnly);
            await verify.OpenAsync();
            Assert.Equal(0L, await ScalarInt64Async(verify, "PRAGMA user_version"));
            Assert.Equal(0L, await ScalarInt64Async(
                verify,
                "SELECT COUNT(*) FROM pragma_table_info('buildings') WHERE name='updated_at'"));
            Assert.Equal(0L, await ScalarInt64Async(
                verify,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='ems_schema_migrations'"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ArchivedCoreWithoutSubAreaIndexKeepsIdentityExplicitlyUnresolved()
    {
        var directory = CreateTempDirectory();
        var databasePath = Path.Combine(directory, "ac.db");
        try
        {
            await CreateArchivedCoreFixtureAsync(databasePath);

            var result = await new SqliteSchemaMigrator().MigrateAsync(databasePath);

            Assert.Equal([1, 2, 3, 4, 5, 6], result.AppliedVersions);
            Assert.NotNull(result.BackupPath);
            Assert.True(File.Exists(result.BackupPath));
            Assert.NotNull(result.IdentityReport);
            Assert.Equal(1, result.IdentityReport.CurrentCardCount);
            Assert.Equal(0, result.IdentityReport.CurrentSourceKeyCount);
            Assert.Equal(0, result.IdentityReport.CurrentDeviceUidCount);
            var finding = Assert.Single(result.IdentityReport.Ambiguities);
            Assert.Equal("cards", finding.EntityTable);
            Assert.Equal("missing_source_identity_component", finding.ReasonCode);
            Assert.Equal("unresolved", finding.Status);
            Assert.Contains("sub_idx", finding.ResolutionNote, StringComparison.Ordinal);

            await using var connection = Open(databasePath, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync();
            Assert.Equal(1L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM cards WHERE source_key IS NULL AND device_uid IS NULL"));
            Assert.Equal(1L, await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM device_identity_ambiguities WHERE status = 'unresolved'"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CreateArchivedCoreFixtureAsync(string databasePath)
    {
        await using var connection = Open(databasePath, SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE buildings (
                building TEXT PRIMARY KEY,
                sub_area_count INTEGER,
                menu_clicked TEXT
            );
            CREATE TABLE sub_areas (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                building TEXT NOT NULL,
                sub_idx INTEGER,
                floor INTEGER,
                text TEXT,
                x INTEGER,
                y INTEGER
            );
            CREATE TABLE pages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sub_area_id INTEGER NOT NULL,
                page_name TEXT,
                count INTEGER,
                on_href TEXT,
                off_href TEXT,
                layout TEXT,
                err TEXT
            );
            CREATE TABLE cards (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                page_id INTEGER NOT NULL,
                name TEXT,
                switch TEXT,
                mode TEXT,
                indoor TEXT,
                set_temp TEXT,
                fan TEXT,
                comm TEXT
            );
            INSERT INTO buildings(building, sub_area_count, menu_clicked) VALUES ('1号', 1, 'true');
            INSERT INTO sub_areas(id, building, sub_idx, floor, text, x, y)
                VALUES (10, '1号', NULL, 1, '1F', 10, 20);
            INSERT INTO pages(id, sub_area_id, page_name, count, layout)
                VALUES (20, 10, '一页', 1, 'grid');
            INSERT INTO cards(id, page_id, name, switch, mode, indoor, set_temp, fan, comm)
                VALUES (30, 20, '1F-101-KT', 'OFF', '制冷', '25', '24', '中', '关机');
            """);
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ems-schema-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
