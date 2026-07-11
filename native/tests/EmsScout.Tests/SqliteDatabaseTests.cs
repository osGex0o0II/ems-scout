using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class SqliteDatabaseTests
{
    [Fact]
    public void OpenExistingEscapesThePathAndResolvesItOnce()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ems-scout-sqlite;connection-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "ac;current.db");
        CreateDatabase(databasePath);
        var resolverCalls = 0;

        try
        {
            using var connection = SqliteDatabase.OpenExisting(
                () =>
                {
                    resolverCalls++;
                    return databasePath;
                },
                SqliteOpenMode.ReadOnly,
                SqliteCacheMode.Private,
                pooling: false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM probe";

            Assert.Equal("ok", command.ExecuteScalar());
            Assert.Equal(1, resolverCalls);
            Assert.Equal(Path.GetFileName(databasePath), Path.GetFileName(connection.DataSource));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadOnlyConnectionRejectsWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "ac.db");
        CreateDatabase(databasePath);

        try
        {
            using var connection = SqliteDatabase.OpenExisting(
                () => databasePath,
                SqliteOpenMode.ReadOnly,
                pooling: false);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO probe (value) VALUES ('write')";

            var error = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            Assert.Equal(8, error.SqliteErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingDatabaseIsNotCreated()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "ems-scout-sqlite-tests",
            Guid.NewGuid().ToString("N"),
            "ac.db");

        var error = Assert.Throws<FileNotFoundException>(() =>
            SqliteDatabase.OpenExisting(() => databasePath, SqliteOpenMode.ReadWrite));

        Assert.Equal(Path.GetFullPath(databasePath), error.FileName);
        Assert.False(File.Exists(databasePath));
    }

    private static void CreateDatabase(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE probe (value TEXT NOT NULL); INSERT INTO probe VALUES ('ok');";
        command.ExecuteNonQuery();
    }
}
