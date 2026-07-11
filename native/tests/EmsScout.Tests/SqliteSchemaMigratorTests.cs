using System.Security.Cryptography;
using EmsScout.Infrastructure.Migrations;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class SqliteSchemaMigratorTests
{
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
            Assert.Equal([1, 2], created.AppliedVersions);
            Assert.Equal("absent", created.Before.DatabaseShape);
            Assert.True(created.After.IsCurrent);
            Assert.Equal("versioned-identity-v2", created.After.DatabaseShape);
            Assert.Equal(2, created.After.UserVersion);
            Assert.NotNull(created.IdentityReport);
            Assert.Equal(0, created.IdentityReport.CurrentCardCount);
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(databasePath)!, "*.partial"));

            await using (var connection = Open(databasePath, SqliteOpenMode.ReadOnly))
            {
                await connection.OpenAsync();
                Assert.Equal("ok", await ScalarStringAsync(connection, "PRAGMA quick_check"));
                Assert.Equal(2L, await ScalarInt64Async(connection, "PRAGMA user_version"));
                Assert.Equal(2L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM ems_schema_migrations"));
                Assert.Equal(
                    2L,
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

            Assert.Equal([1, 2], result.AppliedVersions);
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
