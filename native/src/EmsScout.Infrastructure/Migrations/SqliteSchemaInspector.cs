using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Migrations;

internal sealed record SchemaColumnInfo(
    string Name,
    string DeclaredType,
    bool NotNull,
    string? DefaultValue,
    int PrimaryKeyOrdinal);

internal sealed record SchemaIndexInfo(
    string Name,
    string Table,
    bool IsUnique,
    string Sql);

internal sealed record SchemaSnapshot(
    int UserVersion,
    string JournalMode,
    string QuickCheck,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, SchemaColumnInfo>> Tables,
    IReadOnlyDictionary<string, SchemaIndexInfo> Indexes,
    IReadOnlySet<int> RecordedMigrationVersions,
    int UnresolvedIdentityAmbiguities,
    int AutoResolvedIdentityAmbiguities);

internal static class SqliteSchemaInspector
{
    public static async Task<SchemaSnapshot> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var userVersion = Convert.ToInt32(
            await ExecuteScalarAsync(connection, transaction, "PRAGMA user_version", cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        var journalMode = Convert.ToString(
            await ExecuteScalarAsync(connection, transaction, "PRAGMA journal_mode", cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        var quickCheck = await ReadQuickCheckAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        var tableNames = new List<string>();
        var indexes = new Dictionary<string, SchemaIndexInfo>(StringComparer.OrdinalIgnoreCase);
        await using (var command = CreateCommand(
                         connection,
                         transaction,
                         "SELECT type, name, tbl_name, COALESCE(sql, '') FROM sqlite_schema WHERE type IN ('table', 'index') AND name NOT LIKE 'sqlite_%' ORDER BY type, name"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var type = reader.GetString(0);
                var name = reader.GetString(1);
                if (type.Equals("table", StringComparison.OrdinalIgnoreCase))
                {
                    tableNames.Add(name);
                    continue;
                }

                var sql = reader.GetString(3);
                indexes[name] = new SchemaIndexInfo(
                    name,
                    reader.GetString(2),
                    sql.StartsWith("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase),
                    sql);
            }
        }

        var tables = new Dictionary<string, IReadOnlyDictionary<string, SchemaColumnInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tableName in tableNames)
        {
            var columns = new Dictionary<string, SchemaColumnInfo>(StringComparer.OrdinalIgnoreCase);
            await using var command = CreateCommand(
                connection,
                transaction,
                "SELECT name, type, [notnull], dflt_value, pk FROM pragma_table_info($table) ORDER BY cid");
            command.Parameters.AddWithValue("$table", tableName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var column = new SchemaColumnInfo(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.GetInt64(2) != 0,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4));
                columns[column.Name] = column;
            }

            tables[tableName] = columns;
        }

        var recordedVersions = new HashSet<int>();
        if (tables.TryGetValue("ems_schema_migrations", out var ledgerColumns) &&
            ledgerColumns.ContainsKey("version"))
        {
            await using var command = CreateCommand(
                connection,
                transaction,
                "SELECT version FROM ems_schema_migrations ORDER BY version");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                recordedVersions.Add(reader.GetInt32(0));
            }
        }

        var unresolvedAmbiguities = 0;
        var autoResolvedAmbiguities = 0;
        if (tables.TryGetValue("device_identity_ambiguities", out var ambiguityColumns) &&
            ambiguityColumns.ContainsKey("status"))
        {
            await using var command = CreateCommand(
                connection,
                transaction,
                "SELECT status, COUNT(*) FROM device_identity_ambiguities GROUP BY status");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var count = reader.GetInt32(1);
                switch (reader.GetString(0))
                {
                    case "unresolved":
                        unresolvedAmbiguities = count;
                        break;
                    case "auto_resolved":
                        autoResolvedAmbiguities = count;
                        break;
                }
            }
        }

        return new SchemaSnapshot(
            userVersion,
            journalMode,
            quickCheck,
            tables,
            indexes,
            recordedVersions,
            unresolvedAmbiguities,
            autoResolvedAmbiguities);
    }

    private static async Task<string> ReadQuickCheckAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var results = new List<string>();
        await using var command = CreateCommand(connection, transaction, "PRAGMA quick_check");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(reader.GetString(0));
        }

        return results.Count == 0 ? "no result" : string.Join(" | ", results);
    }

    private static async Task<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }
}
