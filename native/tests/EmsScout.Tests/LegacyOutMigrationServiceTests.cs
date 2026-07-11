using EmsScout.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class LegacyOutMigrationServiceTests
{
    [Fact]
    public async Task MigratesCommittedWalDataAndOnlyCurrentCompatibilityArtifacts()
    {
        var root = CreateTemporaryDirectory();
        var sourceDirectory = Path.Combine(root, "workspace", "out");
        var destinationDirectory = Path.Combine(root, "local-app-data", "data");
        Directory.CreateDirectory(sourceDirectory);
        var sourceDatabasePath = Path.Combine(sourceDirectory, "ac.db");

        await using var writer = await OpenWalDatabaseAsync(sourceDatabasePath);
        await ExecuteAsync(writer, "CREATE TABLE cards (id INTEGER PRIMARY KEY, name TEXT NOT NULL);");
        await ExecuteAsync(writer, "INSERT INTO cards(name) VALUES ('WAL-CARD');");
        Assert.True(File.Exists(sourceDatabasePath + "-wal"));

        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "enum_full_v5.json"), "{\"source\":\"legacy\"}");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "collection_snapshot_v1.json"), "{\"contractVersion\":\"ems.collection-snapshot/v1\"}");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "realtime_1_latest.json"), "{\"rows\":[]}");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "quality_report.txt"), "legacy report");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "enum_2026-07-11.log"), "legacy log");

        try
        {
            var service = new LegacyOutMigrationService();
            var result = await service.MigrateIfNeededAsync(sourceDirectory, destinationDirectory);

            Assert.Equal(LegacyOutMigrationStatus.Migrated, result.Status);
            Assert.Equal(4, result.Artifacts.Count);
            Assert.All(result.Artifacts, artifact => Assert.Equal(64, artifact.Sha256.Length));
            Assert.True(File.Exists(result.MarkerPath));
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "enum_full_v5.json")));
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "collection_snapshot_v1.json")));
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "realtime_1_latest.json")));
            Assert.False(File.Exists(Path.Combine(destinationDirectory, "quality_report.txt")));
            Assert.False(File.Exists(Path.Combine(destinationDirectory, "enum_2026-07-11.log")));

            await using var migrated = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = result.DestinationDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await migrated.OpenAsync();
            await using var command = migrated.CreateCommand();
            command.CommandText = "SELECT name FROM cards;";
            Assert.Equal("WAL-CARD", await command.ExecuteScalarAsync());

            var firstHash = await File.ReadAllBytesAsync(result.DestinationDatabasePath);
            var second = await service.MigrateIfNeededAsync(sourceDirectory, destinationDirectory);
            Assert.Equal(LegacyOutMigrationStatus.DestinationAlreadyInitialized, second.Status);
            Assert.Equal(firstHash, await File.ReadAllBytesAsync(result.DestinationDatabasePath));
        }
        finally
        {
            await writer.CloseAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotCreateDestinationWhenLegacyDatabaseIsMissing()
    {
        var root = CreateTemporaryDirectory();
        var sourceDirectory = Path.Combine(root, "out");
        var destinationDirectory = Path.Combine(root, "local", "data");
        Directory.CreateDirectory(sourceDirectory);

        try
        {
            var result = await new LegacyOutMigrationService()
                .MigrateIfNeededAsync(sourceDirectory, destinationDirectory);

            Assert.Equal(LegacyOutMigrationStatus.SourceMissing, result.Status);
            Assert.False(Directory.Exists(destinationDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingDestinationDatabaseIsNeverOverwrittenOrValidatedAgainstSource()
    {
        var root = CreateTemporaryDirectory();
        var sourceDirectory = Path.Combine(root, "out");
        var destinationDirectory = Path.Combine(root, "local", "data");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "ac.db"), "not a SQLite database");
        var destinationPath = Path.Combine(destinationDirectory, "ac.db");
        await File.WriteAllTextAsync(destinationPath, "owned by current app");

        try
        {
            var result = await new LegacyOutMigrationService()
                .MigrateIfNeededAsync(sourceDirectory, destinationDirectory);

            Assert.Equal(LegacyOutMigrationStatus.DestinationAlreadyInitialized, result.Status);
            Assert.Equal("owned by current app", await File.ReadAllTextAsync(destinationPath));
            Assert.False(File.Exists(Path.Combine(destinationDirectory, LegacyOutMigrationService.MarkerFileName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SameSourceAndDestinationIsANoOp()
    {
        var root = CreateTemporaryDirectory();
        var dataDirectory = Path.Combine(root, "out");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "ac.db"), "existing");

        try
        {
            var result = await new LegacyOutMigrationService()
                .MigrateIfNeededAsync(dataDirectory, dataDirectory);

            Assert.Equal(LegacyOutMigrationStatus.SourceAndDestinationAreSame, result.Status);
            Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(dataDirectory, "ac.db")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<SqliteConnection> OpenWalDatabaseAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;");
        await ExecuteAsync(connection, "PRAGMA wal_autocheckpoint=0;");
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ems-scout-legacy-migration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
