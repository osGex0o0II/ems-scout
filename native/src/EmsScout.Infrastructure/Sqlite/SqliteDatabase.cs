using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

internal static class SqliteDatabase
{
    public static SqliteConnection OpenExisting(
        Func<string> databasePathResolver,
        SqliteOpenMode mode,
        SqliteCacheMode cache = SqliteCacheMode.Default,
        bool pooling = true)
    {
        var connection = CreateExistingConnection(databasePathResolver, mode, cache, pooling);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static async Task<SqliteConnection> OpenExistingAsync(
        Func<string> databasePathResolver,
        SqliteOpenMode mode,
        SqliteCacheMode cache = SqliteCacheMode.Default,
        bool pooling = true,
        CancellationToken cancellationToken = default)
    {
        var connection = CreateExistingConnection(databasePathResolver, mode, cache, pooling);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static SqliteConnection CreateExistingConnection(
        Func<string> databasePathResolver,
        SqliteOpenMode mode,
        SqliteCacheMode cache,
        bool pooling)
    {
        ArgumentNullException.ThrowIfNull(databasePathResolver);
        var configuredPath = databasePathResolver();
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("EMS SQLite database path is not configured.");
        }

        var databasePath = Path.GetFullPath(configuredPath);
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Cannot find EMS SQLite database.", databasePath);
        }

        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Cache = cache,
            Pooling = pooling,
        }.ToString());
    }
}
