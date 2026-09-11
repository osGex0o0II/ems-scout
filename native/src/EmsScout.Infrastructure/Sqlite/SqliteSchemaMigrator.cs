using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteSchemaMigrator(Func<string> databasePathResolver)
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = databasePathResolver();
        if (!File.Exists(databasePath))
        {
            return;
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await EnsureCollectionRunColumnsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureQualityColumnsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureRealtimeSnapshotSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureCollectionRunColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "collection_runs", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "source", "TEXT NOT NULL DEFAULT '采集导入'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "data_version", "TEXT NOT NULL DEFAULT 'v1.0.0'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "operator_name", "TEXT NOT NULL DEFAULT '本机'", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "collection_runs", "restored_from_run_id", "INTEGER", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureQualityColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await AddColumnIfMissingAsync(connection, transaction, "pages", "quality_reason", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "run_pages", "quality_reason", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "pages", "collected_at", "TEXT", cancellationToken).ConfigureAwait(false);
        await AddColumnIfMissingAsync(connection, transaction, "run_pages", "collected_at", "TEXT", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureRealtimeSnapshotSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "collection_runs", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS run_realtime_details (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL,
                source_row_id TEXT NOT NULL,
                building TEXT NOT NULL,
                floor REAL,
                sub_area TEXT,
                page_name TEXT,
                name TEXT,
                source_file TEXT,
                source_updated_at TEXT,
                payload_json TEXT NOT NULL,
                FOREIGN KEY(run_id) REFERENCES collection_runs(id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_run_realtime_details_row
                ON run_realtime_details(run_id, source_row_id);
            CREATE INDEX IF NOT EXISTS idx_run_realtime_details_run_building
                ON run_realtime_details(run_id, building);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, tableName, cancellationToken).ConfigureAwait(false) ||
            await ColumnExistsAsync(connection, transaction, tableName, columnName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
