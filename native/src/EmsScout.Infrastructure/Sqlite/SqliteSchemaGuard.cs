using System.Globalization;
using EmsScout.Infrastructure.Migrations;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

internal static class SqliteSchemaGuard
{
    public static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public static async Task RequireCurrentAsync(
        SqliteConnection connection,
        IReadOnlyDictionary<string, string[]> requiredColumns,
        IReadOnlyCollection<string> requiredIndexes,
        CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version";
            var value = await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var version = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (version < BaselineSchema.LatestVersion)
            {
                issues.Add($"user_version={version} (required >= {BaselineSchema.LatestVersion})");
            }
        }

        var tables = await ReadSchemaObjectsAsync(connection, "table", cancellationToken).ConfigureAwait(false);
        var indexes = await ReadSchemaObjectsAsync(connection, "index", cancellationToken).ConfigureAwait(false);
        foreach (var requirement in requiredColumns)
        {
            if (!tables.Contains(requirement.Key))
            {
                issues.Add("table " + requirement.Key);
                continue;
            }

            var actualColumns = await ReadColumnsAsync(connection, requirement.Key, cancellationToken).ConfigureAwait(false);
            issues.AddRange(requirement.Value
                .Where(column => !actualColumns.Contains(column))
                .Select(column => $"column {requirement.Key}.{column}"));
        }

        issues.AddRange(requiredIndexes
            .Where(index => !indexes.Contains(index))
            .Select(index => "index " + index));
        if (issues.Count > 0)
        {
            throw IncompleteSchema(connection.DataSource, issues);
        }
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actual.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return actual;
    }

    private static async Task<HashSet<string>> ReadSchemaObjectsAsync(
        SqliteConnection connection,
        string type,
        CancellationToken cancellationToken)
    {
        var objects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type";
        command.Parameters.AddWithValue("$type", type);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            objects.Add(reader.GetString(0));
        }

        return objects;
    }

    private static InvalidDataException IncompleteSchema(string databasePath, IReadOnlyCollection<string> issues) =>
        new(
            $"EMS database schema is not current: {string.Join("; ", issues)}. " +
            "Restart EMS Scout to migrate automatically, or run: " +
            $"dotnet run --project native/tools/EmsScout.SchemaTool/EmsScout.SchemaTool.csproj -- migrate --db \"{databasePath}\" --apply");

    private static string QuoteIdentifier(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
