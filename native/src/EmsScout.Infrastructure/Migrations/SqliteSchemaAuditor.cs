using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Migrations;

public sealed class SqliteSchemaAuditor
{
    private const int SqliteReadOnly = 8;
    private const int SqliteCannotOpen = 14;

    public async Task<SchemaAuditReport> AuditAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizeExistingDatabasePath(databasePath);
        try
        {
            await using var connection = OpenReadOnly(fullPath, immutable: false);
            var snapshot = await SqliteSchemaInspector.ReadAsync(connection, null, cancellationToken).ConfigureAwait(false);
            return Evaluate(fullPath, snapshot, usedImmutableFallback: false);
        }
        catch (SqliteException ex) when (CanUseImmutableFallback(fullPath, ex))
        {
            await using var connection = OpenReadOnly(fullPath, immutable: true);
            var snapshot = await SqliteSchemaInspector.ReadAsync(connection, null, cancellationToken).ConfigureAwait(false);
            return Evaluate(fullPath, snapshot, usedImmutableFallback: true);
        }
    }

    internal async Task<SchemaAuditReport> AuditConnectionAsync(
        string databasePath,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var snapshot = await SqliteSchemaInspector.ReadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        return Evaluate(databasePath, snapshot, usedImmutableFallback: false);
    }

    internal static string NormalizeExistingDatabasePath(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Cannot find EMS SQLite database.", fullPath);
        }

        return fullPath;
    }

    private static SchemaAuditReport Evaluate(
        string databasePath,
        SchemaSnapshot snapshot,
        bool usedImmutableFallback)
    {
        var issues = new List<SchemaAuditIssue>();
        var pending = new List<SchemaPendingChange>();

        if (!snapshot.QuickCheck.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new SchemaAuditIssue(
                SchemaIssueSeverity.Error,
                "SQLITE_QUICK_CHECK_FAILED",
                "PRAGMA quick_check returned: " + snapshot.QuickCheck));
        }

        if (usedImmutableFallback)
        {
            issues.Add(new SchemaAuditIssue(
                SchemaIssueSeverity.Info,
                "IMMUTABLE_READ_ONLY_FALLBACK",
                "The checkpointed WAL-format database required SQLite immutable mode for a read-only audit; the source file was not modified."));
        }

        if (snapshot.UserVersion > BaselineSchema.LatestVersion)
        {
            issues.Add(new SchemaAuditIssue(
                SchemaIssueSeverity.Error,
                "FUTURE_USER_VERSION",
                $"Database user_version {snapshot.UserVersion} is newer than supported version {BaselineSchema.LatestVersion}."));
        }

        foreach (var expectedTable in BaselineSchema.ExpectedColumns.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!snapshot.Tables.TryGetValue(expectedTable.Key, out var actualColumns))
            {
                if (BaselineSchema.CoreTables.Contains(expectedTable.Key))
                {
                    issues.Add(new SchemaAuditIssue(
                        SchemaIssueSeverity.Error,
                        "MISSING_CORE_TABLE",
                        $"Required core table '{expectedTable.Key}' is missing. Existing-database migration will not synthesize or rebuild core capture tables."));
                }
                else
                {
                    pending.Add(new SchemaPendingChange(
                        SchemaChangeKind.CreateTable,
                        expectedTable.Key,
                        $"Create additive schema table '{expectedTable.Key}'."));
                }

                continue;
            }

            foreach (var expectedColumn in expectedTable.Value)
            {
                if (actualColumns.ContainsKey(expectedColumn))
                {
                    continue;
                }

                if (BaselineSchema.TryGetAdditiveColumn(expectedTable.Key, expectedColumn, out var addition))
                {
                    pending.Add(new SchemaPendingChange(
                        SchemaChangeKind.AddColumn,
                        $"{expectedTable.Key}.{expectedColumn}",
                        $"Add column '{expectedColumn} {addition.SqlDefinition}' to '{expectedTable.Key}'."));
                }
                else
                {
                    issues.Add(new SchemaAuditIssue(
                        SchemaIssueSeverity.Error,
                        "UNSUPPORTED_COLUMN_GAP",
                        $"Table '{expectedTable.Key}' exists but required column '{expectedColumn}' is missing; the supported migrations cannot infer its data safely."));
                }
            }
        }

        foreach (var expectedIndex in BaselineSchema.ExpectedIndexes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!snapshot.Tables.ContainsKey(expectedIndex.Value.Table))
            {
                continue;
            }

            if (!snapshot.Indexes.TryGetValue(expectedIndex.Key, out var actualIndex))
            {
                pending.Add(new SchemaPendingChange(
                    SchemaChangeKind.CreateIndex,
                    expectedIndex.Key,
                    $"Create additive index '{expectedIndex.Key}' on '{expectedIndex.Value.Table}'."));
                continue;
            }

            if (!actualIndex.Table.Equals(expectedIndex.Value.Table, StringComparison.OrdinalIgnoreCase) ||
                actualIndex.IsUnique != expectedIndex.Value.IsUnique)
            {
                issues.Add(new SchemaAuditIssue(
                    SchemaIssueSeverity.Error,
                    "INDEX_DEFINITION_CONFLICT",
                    $"Index '{expectedIndex.Key}' conflicts with the supported schema definition; migration will not drop or replace it."));
            }
        }

        var ledgerExists = snapshot.Tables.ContainsKey("ems_schema_migrations");
        for (var version = BaselineSchema.V1Version; version <= BaselineSchema.LatestVersion; version++)
        {
            if (ledgerExists && !snapshot.RecordedMigrationVersions.Contains(version))
            {
                pending.Add(new SchemaPendingChange(
                    SchemaChangeKind.RecordMigration,
                    $"ems_schema_migrations:{version}",
                    $"Record successful application of schema migration v{version}."));
            }
        }

        if (snapshot.UserVersion < BaselineSchema.LatestVersion)
        {
            pending.Add(new SchemaPendingChange(
                SchemaChangeKind.SetUserVersion,
                "PRAGMA user_version",
                $"Set PRAGMA user_version to {BaselineSchema.LatestVersion}."));
        }
        else if (snapshot.UserVersion == BaselineSchema.LatestVersion && pending.Count > 0)
        {
            issues.Add(new SchemaAuditIssue(
                SchemaIssueSeverity.Warning,
                "VERSION_SCHEMA_DRIFT",
                "user_version is current, but additive schema objects are missing. An explicit migrate command can repair this drift."));
        }

        AddKnownShapeVarianceIssues(snapshot, issues);
        if (snapshot.UnresolvedIdentityAmbiguities > 0)
        {
            issues.Add(new SchemaAuditIssue(
                SchemaIssueSeverity.Warning,
                "UNRESOLVED_DEVICE_IDENTITIES",
                $"{snapshot.UnresolvedIdentityAmbiguities} device identity ambiguities require explicit resolution."));
        }

        var hasErrors = issues.Any(issue => issue.Severity == SchemaIssueSeverity.Error);
        var isCurrent = !hasErrors && pending.Count == 0 &&
                        snapshot.UserVersion == BaselineSchema.LatestVersion &&
                        Enumerable.Range(
                                BaselineSchema.V1Version,
                                BaselineSchema.LatestVersion - BaselineSchema.V1Version + 1)
                            .All(snapshot.RecordedMigrationVersions.Contains);

        return new SchemaAuditReport(
            databasePath,
            DateTimeOffset.UtcNow,
            DetectShape(snapshot),
            snapshot.UserVersion,
            BaselineSchema.LatestVersion,
            snapshot.JournalMode,
            snapshot.QuickCheck,
            usedImmutableFallback,
            CanMigrate: !hasErrors,
            IsCurrent: isCurrent,
            snapshot.Tables.Keys.Order(StringComparer.Ordinal).ToArray(),
            issues,
            pending)
        {
            UnresolvedIdentityAmbiguities = snapshot.UnresolvedIdentityAmbiguities,
            AutoResolvedIdentityAmbiguities = snapshot.AutoResolvedIdentityAmbiguities,
        };
    }

    private static string DetectShape(SchemaSnapshot snapshot)
    {
        if (snapshot.UserVersion > BaselineSchema.LatestVersion)
        {
            return $"future-version-v{snapshot.UserVersion}";
        }

        if (snapshot.UserVersion >= BaselineSchema.V2Version &&
            snapshot.RecordedMigrationVersions.Contains(BaselineSchema.V1Version) &&
            snapshot.RecordedMigrationVersions.Contains(BaselineSchema.V2Version))
        {
            return "versioned-identity-v2";
        }

        if (snapshot.UserVersion >= BaselineSchema.V1Version &&
            snapshot.RecordedMigrationVersions.Contains(BaselineSchema.V1Version))
        {
            return "versioned-baseline-v1";
        }

        var nonCoreTables = snapshot.Tables.Keys.Count(table => !BaselineSchema.CoreTables.Contains(table));
        if (nonCoreTables == 0 && BaselineSchema.CoreTables.All(snapshot.Tables.ContainsKey))
        {
            return "archived-core-v0";
        }

        var hasHistory = snapshot.Tables.ContainsKey("collection_runs") &&
                         snapshot.Tables.ContainsKey("run_pages") &&
                         snapshot.Tables.ContainsKey("run_cards");
        var hasWatchRules = snapshot.Tables.ContainsKey("device_watch_rules");
        var hasPageQuality = HasColumn(snapshot, "pages", "quality_reason") &&
                             HasColumn(snapshot, "run_pages", "quality_reason");

        if (hasHistory && !hasWatchRules && !hasPageQuality)
        {
            return "workflow-extensions-v0";
        }

        if (hasHistory && hasWatchRules && !hasPageQuality)
        {
            return "pre-quality-v0";
        }

        if (hasHistory && hasWatchRules && hasPageQuality)
        {
            return "current-unversioned-v0";
        }

        return "mixed-unversioned-v0";
    }

    private static void AddKnownShapeVarianceIssues(
        SchemaSnapshot snapshot,
        ICollection<SchemaAuditIssue> issues)
    {
        if (snapshot.Tables.TryGetValue("floor_catalog", out var floorCatalog) &&
            floorCatalog.TryGetValue("floor_value", out var floorValue) &&
            !floorValue.NotNull)
        {
            issues.Add(new SchemaAuditIssue(
                SchemaIssueSeverity.Info,
                "KNOWN_FLOOR_VALUE_NULLABILITY",
                "floor_catalog.floor_value is nullable in this known schema variant. Migration preserves it because rebuilding populated tables is out of scope."));
        }

        if (snapshot.Tables.TryGetValue("sub_areas", out var subAreas) &&
            subAreas.TryGetValue("floor", out var floor) &&
            floor.DeclaredType.Equals("INT", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new SchemaAuditIssue(
                SchemaIssueSeverity.Info,
                "KNOWN_FLOOR_AFFINITY_VARIANCE",
                "sub_areas.floor uses the legacy INT declaration. Migration preserves the column and its data; SQLite numeric reads remain compatible."));
        }
    }

    private static bool HasColumn(SchemaSnapshot snapshot, string table, string column) =>
        snapshot.Tables.TryGetValue(table, out var columns) && columns.ContainsKey(column);

    private static bool CanUseImmutableFallback(string databasePath, SqliteException exception) =>
        exception.SqliteErrorCode is SqliteReadOnly or SqliteCannotOpen &&
        !File.Exists(databasePath + "-wal") &&
        !File.Exists(databasePath + "-shm");

    private static SqliteConnection OpenReadOnly(string databasePath, bool immutable)
    {
        var dataSource = immutable
            ? new Uri(databasePath).AbsoluteUri + "?immutable=1"
            : databasePath;
        var connection = CreateConnection(dataSource, SqliteOpenMode.ReadOnly);
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

    internal static SqliteConnection OpenReadWrite(string databasePath) =>
        CreateConnection(databasePath, SqliteOpenMode.ReadWrite);

    private static SqliteConnection CreateConnection(string dataSource, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        return new SqliteConnection(builder.ToString());
    }
}
