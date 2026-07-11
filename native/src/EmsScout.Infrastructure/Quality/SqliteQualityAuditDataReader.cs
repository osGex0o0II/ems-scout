using System.Globalization;
using EmsScout.Application.Quality;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Quality;

internal sealed class SqliteQualityAuditDataReader
{
    public async Task<QualityAuditDataSet?> ReadAsync(
        string databasePath,
        QualityAuditSourceKind sourceKind,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var queryOnly = connection.CreateCommand())
        {
            queryOnly.CommandText = "PRAGMA query_only = ON";
            await queryOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = connection.BeginTransaction(deferred: true);
        var tables = await ReadTableNamesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var source = await ResolveSourceAsync(
                connection,
                transaction,
                tables,
                sourceKind,
                cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var subAreaColumns = await ReadColumnNamesAsync(
                connection,
                transaction,
                source.SubAreasTable,
                cancellationToken)
            .ConfigureAwait(false);
        var pageColumns = await ReadColumnNamesAsync(
                connection,
                transaction,
                source.PagesTable,
                cancellationToken)
            .ConfigureAwait(false);
        var cardColumns = await ReadColumnNamesAsync(
                connection,
                transaction,
                source.CardsTable,
                cancellationToken)
            .ConfigureAwait(false);

        ValidateRequiredColumns(source, subAreaColumns, pageColumns, cardColumns);
        var subAreas = await ReadSubAreasAsync(
                connection,
                transaction,
                source,
                subAreaColumns,
                cancellationToken)
            .ConfigureAwait(false);
        var pages = await ReadPagesAsync(
                connection,
                transaction,
                source,
                pageColumns,
                cancellationToken)
            .ConfigureAwait(false);
        var cards = await ReadCardsAsync(
                connection,
                transaction,
                source,
                cardColumns,
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new QualityAuditDataSet(source.RunId, subAreas, pages, cards);
    }

    private static async Task<QualityAuditSource?> ResolveSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlySet<string> tables,
        QualityAuditSourceKind sourceKind,
        CancellationToken cancellationToken)
    {
        if (sourceKind == QualityAuditSourceKind.Current)
        {
            return HasTables(tables, "sub_areas", "pages", "cards")
                ? new QualityAuditSource("sub_areas", "pages", "cards", "sub_area_id", "page_id", null)
                : null;
        }

        if (!HasTables(tables, "collection_runs", "run_sub_areas", "run_pages", "run_cards"))
        {
            return null;
        }

        var runColumns = await ReadColumnNamesAsync(
                connection,
                transaction,
                "collection_runs",
                cancellationToken)
            .ConfigureAwait(false);
        if (!HasColumns(runColumns, "id", "status", "completed_at"))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id
            FROM collection_runs
            WHERE status = 'completed'
            ORDER BY datetime(completed_at) DESC, id DESC
            LIMIT 1
            """;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
        {
            return null;
        }

        return new QualityAuditSource(
            "run_sub_areas",
            "run_pages",
            "run_cards",
            "run_sub_area_id",
            "run_page_id",
            Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                result.Add(reader.GetString(0));
            }
        }

        return result;
    }

    private static async Task<HashSet<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(1));
        }

        return result;
    }

    private static async Task<IReadOnlyList<QualityAuditSubArea>> ReadSubAreasAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QualityAuditSource source,
        IReadOnlySet<string> columns,
        CancellationToken cancellationToken)
    {
        var result = new List<QualityAuditSubArea>();
        await using var command = CreateReadCommand(
            connection,
            transaction,
            source,
            $$"""
            SELECT id,
                   {{ColumnOrNull(columns, "building")}},
                   {{ColumnOrNull(columns, "floor")}},
                   {{ColumnOrNull(columns, "text")}},
                   {{ColumnOrNull(columns, "sub_idx")}},
                   {{ColumnOrNull(columns, "x")}},
                   {{ColumnOrNull(columns, "y")}}
            FROM {{QuoteIdentifier(source.SubAreasTable)}}
            {0}
            ORDER BY id
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new QualityAuditSubArea(
                ReadInt64(reader, 0),
                ReadString(reader, 1),
                ReadDouble(reader, 2),
                ReadString(reader, 3),
                ReadInt32(reader, 4),
                ReadDouble(reader, 5),
                ReadDouble(reader, 6)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<QualityAuditPage>> ReadPagesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QualityAuditSource source,
        IReadOnlySet<string> columns,
        CancellationToken cancellationToken)
    {
        var result = new List<QualityAuditPage>();
        await using var command = CreateReadCommand(
            connection,
            transaction,
            source,
            $$"""
            SELECT id,
                   {{QuoteIdentifier(source.PageSubAreaColumn)}},
                   {{ColumnOrNull(columns, "page_name")}},
                   {{ColumnOrNull(columns, "count")}},
                   {{ColumnOrNull(columns, "raw_count")}},
                   {{ColumnOrNull(columns, "unique_count")}},
                   {{ColumnOrNull(columns, "layout")}},
                   {{ColumnOrNull(columns, "quality_reason")}}
            FROM {{QuoteIdentifier(source.PagesTable)}}
            {0}
            ORDER BY id
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new QualityAuditPage(
                ReadInt64(reader, 0),
                ReadInt64(reader, 1),
                ReadString(reader, 2),
                ReadInt32(reader, 3),
                ReadInt32(reader, 4),
                ReadInt32(reader, 5),
                ReadString(reader, 6),
                ReadString(reader, 7)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<QualityAuditCard>> ReadCardsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QualityAuditSource source,
        IReadOnlySet<string> columns,
        CancellationToken cancellationToken)
    {
        var result = new List<QualityAuditCard>();
        await using var command = CreateReadCommand(
            connection,
            transaction,
            source,
            $$"""
            SELECT id,
                   {{QuoteIdentifier(source.CardPageColumn)}},
                   {{ColumnOrNull(columns, "name")}},
                   {{ColumnOrNull(columns, "switch")}},
                   {{ColumnOrNull(columns, "mode")}},
                   {{ColumnOrNull(columns, "indoor")}},
                   {{ColumnOrNull(columns, "set_temp")}},
                   {{ColumnOrNull(columns, "fan")}},
                   {{ColumnOrNull(columns, "indicator")}},
                   {{ColumnOrNull(columns, "comm")}}
            FROM {{QuoteIdentifier(source.CardsTable)}}
            {0}
            ORDER BY id
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new QualityAuditCard(
                ReadInt64(reader, 0),
                ReadInt64(reader, 1),
                ReadString(reader, 2),
                ReadString(reader, 3),
                ReadString(reader, 4),
                ReadString(reader, 5),
                ReadString(reader, 6),
                ReadString(reader, 7),
                ReadString(reader, 8),
                ReadString(reader, 9)));
        }

        return result;
    }

    private static SqliteCommand CreateReadCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        QualityAuditSource source,
        string commandTemplate)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        var where = source.RunId is null ? string.Empty : "WHERE run_id = $runId";
        command.CommandText = string.Format(CultureInfo.InvariantCulture, commandTemplate, where);
        if (source.RunId is not null)
        {
            command.Parameters.AddWithValue("$runId", source.RunId.Value);
        }

        return command;
    }

    private static void ValidateRequiredColumns(
        QualityAuditSource source,
        IReadOnlySet<string> subAreaColumns,
        IReadOnlySet<string> pageColumns,
        IReadOnlySet<string> cardColumns)
    {
        var subAreaRequired = source.RunId is null
            ? new[] { "id", "building" }
            : new[] { "id", "run_id", "building" };
        var pageRequired = source.RunId is null
            ? new[] { "id", source.PageSubAreaColumn }
            : new[] { "id", "run_id", source.PageSubAreaColumn };
        var cardRequired = source.RunId is null
            ? new[] { "id", source.CardPageColumn }
            : new[] { "id", "run_id", source.CardPageColumn };

        if (!HasColumns(subAreaColumns, subAreaRequired) ||
            !HasColumns(pageColumns, pageRequired) ||
            !HasColumns(cardColumns, cardRequired))
        {
            throw new InvalidDataException("SQLite quality source is missing required identity or relationship columns.");
        }
    }

    private static bool HasTables(IReadOnlySet<string> tables, params string[] required) =>
        required.All(tables.Contains);

    private static bool HasColumns(IReadOnlySet<string> columns, params string[] required) =>
        required.All(columns.Contains);

    private static string ColumnOrNull(IReadOnlySet<string> columns, string column) =>
        columns.Contains(column) ? QuoteIdentifier(column) : "NULL";

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static long ReadInt64(SqliteDataReader reader, int ordinal) =>
        Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static int? ReadInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static double? ReadDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static string? ReadString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private sealed record QualityAuditSource(
        string SubAreasTable,
        string PagesTable,
        string CardsTable,
        string PageSubAreaColumn,
        string CardPageColumn,
        long? RunId);
}

internal sealed record QualityAuditDataSet(
    long? RunId,
    IReadOnlyList<QualityAuditSubArea> SubAreas,
    IReadOnlyList<QualityAuditPage> Pages,
    IReadOnlyList<QualityAuditCard> Cards);

internal sealed record QualityAuditSubArea(
    long Id,
    string? Building,
    double? Floor,
    string? Text,
    int? SubIndex,
    double? X,
    double? Y);

internal sealed record QualityAuditPage(
    long Id,
    long SubAreaId,
    string? Name,
    int? Count,
    int? RawCount,
    int? UniqueCount,
    string? Layout,
    string? QualityReason);

internal sealed record QualityAuditCard(
    long Id,
    long PageId,
    string? Name,
    string? Switch,
    string? Mode,
    string? Indoor,
    string? SetTemp,
    string? Fan,
    string? Indicator,
    string? Communication);
