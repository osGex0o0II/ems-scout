using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Migrations;

public sealed class SqliteSchemaMigrator
{
    private const int MaxBackupConsistencyAttempts = 3;
    private readonly SqliteSchemaAuditor auditor;

    public SqliteSchemaMigrator(SqliteSchemaAuditor? auditor = null)
    {
        this.auditor = auditor ?? new SqliteSchemaAuditor();
    }

    public async Task<SchemaMigrationResult> CreateNewAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizeNewDatabasePath(databasePath);
        EnsureCreationTargetAvailable(fullPath);

        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new ArgumentException("Database path has no parent directory.", nameof(databasePath));
        Directory.CreateDirectory(directory);
        EnsureCreationTargetAvailable(fullPath);

        var partialPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.partial");
        DeviceIdentityMigrationReport identityReport;
        try
        {
            await using (var connection = OpenReadWriteCreate(partialPath))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await ConfigureWriteConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
                await EnsureDeleteJournalModeAsync(connection, cancellationToken).ConfigureAwait(false);

                await using var transaction = connection.BeginTransaction(deferred: false);
                try
                {
                    await ExecuteAsync(connection, transaction, FreshCoreSql.Text, cancellationToken).ConfigureAwait(false);
                    await ApplyV1BaselineAsync(
                            connection,
                            transaction,
                            "fresh-empty-v0",
                            "not-required:fresh-create",
                            cancellationToken)
                        .ConfigureAwait(false);
                    identityReport = await ApplyV2IdentityAsync(
                            connection,
                            transaction,
                            "fresh-empty-v0",
                            "not-required:fresh-create",
                            cancellationToken)
                        .ConfigureAwait(false);
                    await ExecuteAsync(
                            connection,
                            transaction,
                            $"PRAGMA user_version = {BaselineSchema.LatestVersion}",
                            cancellationToken)
                        .ConfigureAwait(false);

                    var transactionalAudit = await auditor
                        .AuditConnectionAsync(partialPath, connection, transaction, cancellationToken)
                        .ConfigureAwait(false);
                    if (!transactionalAudit.IsCurrent)
                    {
                        throw new InvalidOperationException(
                            "Fresh schema verification failed before commit: " +
                            string.Join("; ", transactionalAudit.Issues
                                .Where(issue => issue.Severity == SchemaIssueSeverity.Error)
                                .Select(issue => issue.Message)));
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Preserve the creation failure; disposing the transaction is the final rollback fallback.
                    }

                    throw;
                }
            }

            if (File.Exists(partialPath + "-wal") || File.Exists(partialPath + "-shm"))
            {
                throw new InvalidDataException("Fresh database retained an unexpected SQLite WAL sidecar.");
            }

            var after = await auditor.AuditAsync(partialPath, cancellationToken).ConfigureAwait(false);
            if (!after.IsCurrent || !after.QuickCheck.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Fresh database failed final schema or quick_check verification: " +
                    string.Join("; ", after.Issues.Select(issue => issue.Message)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureCreationTargetAvailable(fullPath);
            File.Move(partialPath, fullPath);

            return new SchemaMigrationResult(
                fullPath,
                BackupPath: null,
                AppliedVersions: [BaselineSchema.V1Version, BaselineSchema.V2Version],
                AppliedChanges:
                [
                    new SchemaPendingChange(
                        SchemaChangeKind.CreateTable,
                        "complete-schema",
                        "Create a complete empty core, baseline, and identity schema."),
                ],
                CreateAbsentAudit(fullPath),
                after with { DatabasePath = fullPath })
            {
                IdentityReport = identityReport,
            };
        }
        catch (OperationCanceledException)
        {
            DeletePartialDatabase(partialPath);
            throw;
        }
        catch (Exception ex)
        {
            DeletePartialDatabase(partialPath);
            throw new SchemaMigrationException(
                "Fresh database creation failed; no database was published at the target path.",
                innerException: ex);
        }
    }

    public async Task<SchemaMigrationResult> MigrateAsync(
        string databasePath,
        string? backupPath = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = SqliteSchemaAuditor.NormalizeExistingDatabasePath(databasePath);
        var initialAudit = await auditor.AuditAsync(fullPath, cancellationToken).ConfigureAwait(false);
        EnsureCanMigrate(initialAudit);

        if (initialAudit.IsCurrent)
        {
            return new SchemaMigrationResult(
                fullPath,
                BackupPath: null,
                AppliedVersions: [],
                AppliedChanges: [],
                initialAudit,
                initialAudit);
        }

        await using var connection = SqliteSchemaAuditor.OpenReadWrite(fullPath);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureWriteConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        for (var attempt = 1; attempt <= MaxBackupConsistencyAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? completedBackupPath = null;
            var migrationStarted = false;
            try
            {
                var versionBeforeBackup = await ReadDataVersionAsync(connection, null, cancellationToken).ConfigureAwait(false);
                completedBackupPath = CreateWalSafeBackup(connection, fullPath, backupPath, cancellationToken);
                var versionAfterBackup = await ReadDataVersionAsync(connection, null, cancellationToken).ConfigureAwait(false);
                if (versionBeforeBackup != versionAfterBackup)
                {
                    DeleteBackup(completedBackupPath);
                    continue;
                }

                await using var transaction = connection.BeginTransaction(deferred: false);
                try
                {
                    var lockedDataVersion = await ReadDataVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    if (lockedDataVersion != versionAfterBackup)
                    {
                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        DeleteBackup(completedBackupPath);
                        continue;
                    }

                    var lockedAudit = await auditor
                        .AuditConnectionAsync(fullPath, connection, transaction, cancellationToken)
                        .ConfigureAwait(false);
                    EnsureCanMigrate(lockedAudit);

                    if (lockedAudit.IsCurrent)
                    {
                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        DeleteBackup(completedBackupPath);
                        return new SchemaMigrationResult(
                            fullPath,
                            BackupPath: null,
                            AppliedVersions: [],
                            AppliedChanges: [],
                            lockedAudit,
                            lockedAudit);
                    }

                    migrationStarted = true;
                    await ApplyV1BaselineAsync(
                            connection,
                            transaction,
                            lockedAudit.DatabaseShape,
                            completedBackupPath,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var identityReport = await ApplyV2IdentityAsync(
                            connection,
                            transaction,
                            lockedAudit.DatabaseShape,
                            completedBackupPath,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await ExecuteAsync(
                            connection,
                            transaction,
                            $"PRAGMA user_version = {BaselineSchema.LatestVersion}",
                            cancellationToken)
                        .ConfigureAwait(false);

                    var after = await auditor
                        .AuditConnectionAsync(fullPath, connection, transaction, cancellationToken)
                        .ConfigureAwait(false);
                    if (!after.IsCurrent)
                    {
                        throw new InvalidOperationException(
                            "Schema migration verification failed before commit: " +
                            string.Join("; ", after.Issues.Where(issue => issue.Severity == SchemaIssueSeverity.Error).Select(issue => issue.Message)));
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new SchemaMigrationResult(
                        fullPath,
                        completedBackupPath,
                        PendingVersions(lockedAudit.UserVersion),
                        lockedAudit.PendingChanges,
                        lockedAudit,
                        after)
                    {
                        IdentityReport = identityReport,
                    };
                }
                catch
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Preserve the original migration failure. Disposing the transaction is the final rollback fallback.
                    }

                    throw;
                }
            }
            catch (Exception ex)
            {
                if (!migrationStarted && completedBackupPath is not null)
                {
                    DeleteBackup(completedBackupPath);
                    completedBackupPath = null;
                }

                if (ex is SchemaMigrationException migrationException)
                {
                    if (migrationStarted && migrationException.BackupPath is null)
                    {
                        throw new SchemaMigrationException(migrationException.Message, completedBackupPath, migrationException);
                    }

                    throw;
                }

                throw new SchemaMigrationException(
                    "Schema migration failed; the database transaction was rolled back.",
                    migrationStarted ? completedBackupPath : null,
                    ex);
            }
        }

        throw new SchemaMigrationException(
            $"Database changed during each of {MaxBackupConsistencyAttempts} online backup attempts; no migration was applied.");
    }

    private static void EnsureCanMigrate(SchemaAuditReport report)
    {
        if (report.CanMigrate)
        {
            return;
        }

        var errors = report.Issues
            .Where(issue => issue.Severity == SchemaIssueSeverity.Error)
            .Select(issue => issue.Message);
        throw new SchemaMigrationException("Database is not eligible for schema migration: " + string.Join("; ", errors));
    }

    private static async Task ConfigureWriteConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureDeleteJournalModeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = DELETE";
        var mode = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Fresh database journal normalization failed: " + (mode ?? "no result"));
        }
    }

    private static async Task<long> ReadDataVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA data_version";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ApplyV1BaselineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceShape,
        string backupPath,
        CancellationToken cancellationToken)
    {
        await ApplyAdditiveColumnsAsync(
                connection,
                transaction,
                BaselineSchema.V1AdditiveColumns,
                cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, BaselineSql.Text, cancellationToken).ConfigureAwait(false);
        await RecordMigrationAsync(
                connection,
                transaction,
                BaselineSchema.V1Version,
                "v1-baseline",
                sourceShape,
                backupPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<DeviceIdentityMigrationReport> ApplyV2IdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceShape,
        string backupPath,
        CancellationToken cancellationToken)
    {
        await ApplyAdditiveColumnsAsync(
                connection,
                transaction,
                BaselineSchema.V2AdditiveColumns,
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, IdentitySql.Text, cancellationToken).ConfigureAwait(false);
        var report = await DeviceIdentityMigration.ApplyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await RecordMigrationAsync(
                connection,
                transaction,
                BaselineSchema.V2Version,
                "v2-identity",
                sourceShape,
                backupPath,
                cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    private static async Task ApplyAdditiveColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<AdditiveColumn> additions,
        CancellationToken cancellationToken)
    {
        foreach (var addition in additions)
        {
            if (await TableExistsAsync(connection, transaction, addition.Table, cancellationToken).ConfigureAwait(false) &&
                !await ColumnExistsAsync(connection, transaction, addition.Table, addition.Name, cancellationToken).ConfigureAwait(false))
            {
                await ExecuteAsync(
                        connection,
                        transaction,
                        $"ALTER TABLE {QuoteIdentifier(addition.Table)} ADD COLUMN {QuoteIdentifier(addition.Name)} {addition.SqlDefinition}",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task RecordMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        string name,
        string sourceShape,
        string backupPath,
        CancellationToken cancellationToken)
    {
        await using var ledger = connection.CreateCommand();
        ledger.Transaction = transaction;
        ledger.CommandText = """
            INSERT OR IGNORE INTO ems_schema_migrations
                (version, name, applied_at, source_shape, backup_path, tool_version)
            VALUES
                ($version, $name, $applied_at, $source_shape, $backup_path, $tool_version)
            """;
        ledger.Parameters.AddWithValue("$version", version);
        ledger.Parameters.AddWithValue("$name", name);
        ledger.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToString("O"));
        ledger.Parameters.AddWithValue("$source_shape", sourceShape);
        ledger.Parameters.AddWithValue("$backup_path", backupPath);
        ledger.Parameters.AddWithValue(
            "$tool_version",
            typeof(SqliteSchemaMigrator).Assembly.GetName().Version?.ToString() ?? "unknown");
        await ledger.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CreateWalSafeBackup(
        SqliteConnection source,
        string databasePath,
        string? requestedBackupPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var finalPath = ResolveBackupPath(databasePath, requestedBackupPath);
        var directory = Path.GetDirectoryName(finalPath)
                        ?? throw new InvalidOperationException("Backup path has no parent directory: " + finalPath);
        Directory.CreateDirectory(directory);

        if (File.Exists(finalPath) || File.Exists(finalPath + "-wal") || File.Exists(finalPath + "-shm"))
        {
            throw new IOException("Backup destination or SQLite sidecar already exists: " + finalPath);
        }

        var partialPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = partialPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            };
            using (var destination = new SqliteConnection(builder.ToString()))
            {
                destination.Open();
                source.BackupDatabase(destination);
                using (var normalize = destination.CreateCommand())
                {
                    normalize.CommandText = "PRAGMA journal_mode = DELETE";
                    var mode = Convert.ToString(normalize.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Backup journal normalization failed: " + (mode ?? "no result"));
                    }
                }

                using var check = destination.CreateCommand();
                check.CommandText = "PRAGMA quick_check";
                var result = Convert.ToString(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Backup quick_check failed: " + (result ?? "no result"));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            DeleteIfExists(partialPath + "-wal");
            DeleteIfExists(partialPath + "-shm");
            File.Move(partialPath, finalPath);
            return finalPath;
        }
        catch
        {
            DeleteIfExists(partialPath);
            DeleteIfExists(partialPath + "-wal");
            DeleteIfExists(partialPath + "-shm");

            throw;
        }
    }

    private static string ResolveBackupPath(string databasePath, string? requestedBackupPath)
    {
        var finalPath = string.IsNullOrWhiteSpace(requestedBackupPath)
            ? Path.Combine(
                Path.GetDirectoryName(databasePath) ?? Directory.GetCurrentDirectory(),
                $"{Path.GetFileNameWithoutExtension(databasePath)}.pre-v2-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.backup{Path.GetExtension(databasePath)}")
            : Path.GetFullPath(requestedBackupPath);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (Path.GetFullPath(databasePath).Equals(Path.GetFullPath(finalPath), comparison))
        {
            throw new ArgumentException("Backup path must be different from the source database path.", nameof(requestedBackupPath));
        }

        return Path.GetFullPath(finalPath);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name = $column COLLATE NOCASE LIMIT 1";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string NormalizeNewDatabasePath(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        return Path.GetFullPath(databasePath);
    }

    private static void EnsureCreationTargetAvailable(string databasePath)
    {
        if (File.Exists(databasePath) ||
            Directory.Exists(databasePath) ||
            File.Exists(databasePath + "-wal") ||
            File.Exists(databasePath + "-shm"))
        {
            throw new IOException("Fresh database target or SQLite sidecar already exists: " + databasePath);
        }
    }

    private static SqliteConnection OpenReadWriteCreate(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        return new SqliteConnection(builder.ToString());
    }

    private static SchemaAuditReport CreateAbsentAudit(string databasePath) =>
        new(
            databasePath,
            DateTimeOffset.UtcNow,
            "absent",
            UserVersion: 0,
            BaselineSchema.LatestVersion,
            JournalMode: "not-created",
            QuickCheck: "not-run",
            UsedImmutableReadOnlyFallback: false,
            CanMigrate: true,
            IsCurrent: false,
            Tables: [],
            Issues: [],
            PendingChanges: []);

    private static int[] PendingVersions(int currentVersion) =>
        Enumerable.Range(
                Math.Max(BaselineSchema.V1Version, currentVersion + 1),
                Math.Max(0, BaselineSchema.LatestVersion - Math.Max(BaselineSchema.V1Version, currentVersion + 1) + 1))
            .ToArray();

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteBackup(string backupPath)
    {
        DeleteIfExists(backupPath);
        DeleteIfExists(backupPath + "-wal");
        DeleteIfExists(backupPath + "-shm");
    }

    private static void DeletePartialDatabase(string databasePath)
    {
        DeleteIfExists(databasePath);
        DeleteIfExists(databasePath + "-wal");
        DeleteIfExists(databasePath + "-shm");
    }
}
