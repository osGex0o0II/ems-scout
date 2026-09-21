using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class DeviceDataRevisionMonitorTests
{
    [Fact]
    public void RepeatedReadsRemainStableUntilAnotherWalConnectionCommits()
    {
        var databasePath = CreateDatabase(1, useWal: true);
        using var monitor = new DeviceDataRevisionMonitor();
        var initialTimestamp = File.GetLastWriteTimeUtc(databasePath);

        var first = monitor.GetRevision(databasePath);
        var repeated = monitor.GetRevision(databasePath);
        Execute(databasePath, "UPDATE values_table SET value = 2 WHERE id = 1");
        File.SetLastWriteTimeUtc(databasePath, initialTimestamp);
        var changed = monitor.GetRevision(databasePath);

        Assert.Equal(first, repeated);
        Assert.True(changed > repeated);
        Assert.Equal(initialTimestamp, File.GetLastWriteTimeUtc(databasePath));
    }

    [Fact]
    public void SwitchingDatabasePathsAdvancesRevision()
    {
        var firstPath = CreateDatabase(1);
        var secondPath = CreateDatabase(2);
        using var monitor = new DeviceDataRevisionMonitor();

        var first = monitor.GetRevision(firstPath);
        var second = monitor.GetRevision(secondPath);

        Assert.True(second > first);
        Assert.Equal(second, monitor.GetRevision(secondPath));
    }

    [Fact]
    public void ReplacingDatabaseWithSameLengthAndTimestampAdvancesRevision()
    {
        var originalPath = CreateDatabase(1);
        var replacementPath = CreateDatabase(2);
        Assert.Equal(new FileInfo(originalPath).Length, new FileInfo(replacementPath).Length);
        var timestamp = File.GetLastWriteTimeUtc(originalPath);
        File.SetLastWriteTimeUtc(replacementPath, timestamp);
        using var monitor = new DeviceDataRevisionMonitor();
        var before = monitor.GetRevision(originalPath);

        File.Copy(replacementPath, originalPath, overwrite: true);
        File.SetLastWriteTimeUtc(originalPath, timestamp);
        var after = monitor.GetRevision(originalPath);

        Assert.True(after > before);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(originalPath));
    }

    [Fact]
    public void MissingDatabaseDropsStaleStateAndReopeningAdvancesRevision()
    {
        var databasePath = CreateDatabase(1);
        var activePath = CreateDatabase(2);
        using var monitor = new DeviceDataRevisionMonitor();
        var before = monitor.GetRevision(databasePath);
        var switched = monitor.GetRevision(activePath);

        // SQLite on Windows does not share delete access for an actively connected main
        // database. Switching paths must release that handle before the path can vanish.
        File.Delete(databasePath);
        Assert.Throws<FileNotFoundException>(() => monitor.GetRevision(databasePath));

        CreateDatabaseAt(databasePath, 3, useWal: false);
        var after = monitor.GetRevision(databasePath);
        Assert.True(switched > before);
        Assert.True(after > switched);
    }

    [Fact]
    public void GetRevisionAfterDisposeThrows()
    {
        var databasePath = CreateDatabase(1);
        var monitor = new DeviceDataRevisionMonitor();
        monitor.GetRevision(databasePath);

        monitor.Dispose();

        Assert.Throws<ObjectDisposedException>(() => monitor.GetRevision(databasePath));
        File.Delete(databasePath);
        Assert.False(File.Exists(databasePath));
        monitor.Dispose();
    }

    private static string CreateDatabase(int value, bool useWal = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-revision-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ac.db");
        CreateDatabaseAt(path, value, useWal);
        return path;
    }

    private static void CreateDatabaseAt(string path, int value, bool useWal)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            {(useWal ? "PRAGMA journal_mode=WAL;" : string.Empty)}
            CREATE TABLE values_table (id INTEGER PRIMARY KEY, value INTEGER NOT NULL);
            INSERT INTO values_table(id, value) VALUES (1, $value);
            """;
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
