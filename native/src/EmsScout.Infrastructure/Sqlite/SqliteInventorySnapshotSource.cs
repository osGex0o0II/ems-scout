using System.Globalization;
using EmsScout.Application;
using EmsScout.Domain;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteInventorySnapshotSource(Func<string> databasePathResolver) : IInventorySnapshotSource
{
    public async Task<InventorySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = Path.GetFullPath(databasePathResolver());
        await using var connection = await SqliteDatabase.OpenExistingAsync(
            () => databasePath,
            SqliteOpenMode.ReadOnly,
            SqliteCacheMode.Private,
            pooling: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var cards = new List<AirConditionerCard>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT sa.building, sa.text AS sub_area, sa.floor, p.page_name,
                       c.name, c.switch, c.mode, c.indoor, c.set_temp, c.fan,
                       c.indicator, c.comm
                FROM cards c
                JOIN pages p ON p.id = c.page_id
                JOIN sub_areas sa ON sa.id = p.sub_area_id
                ORDER BY sa.building, sa.floor, sa.sub_idx, p.id, c.id
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var floor = SqliteValueReader.ReadNullableDouble(reader, "floor");
                cards.Add(new AirConditionerCard(
                    Building: SqliteValueReader.ReadString(reader, "building"),
                    SubArea: SqliteValueReader.ReadString(reader, "sub_area"),
                    Floor: floor,
                    Page: SqliteValueReader.ReadString(reader, "page_name"),
                    Name: SqliteValueReader.ReadString(reader, "name"),
                    SwitchState: SqliteValueReader.ReadString(reader, "switch"),
                    Mode: SqliteValueReader.ReadString(reader, "mode"),
                    IndoorTemperature: ParseNullableDouble(SqliteValueReader.ReadString(reader, "indoor")),
                    SetTemperature: ParseNullableDouble(SqliteValueReader.ReadString(reader, "set_temp")),
                    Fan: SqliteValueReader.ReadString(reader, "fan"),
                    Indicator: SqliteValueReader.ReadString(reader, "indicator"),
                    CommunicationState: DeviceCommunicationStateParser.Parse(SqliteValueReader.ReadString(reader, "comm"))));
            }
        }

        var updatedAt = await ReadLatestImportTimeAsync(connection, cancellationToken).ConfigureAwait(false)
                        ?? new DateTimeOffset(File.GetLastWriteTimeUtc(databasePath), TimeSpan.Zero);
        return new InventorySnapshot(databasePath, updatedAt, cards);
    }

    private static async Task<DateTimeOffset?> ReadLatestImportTimeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await SqliteSchemaGuard.TableExistsAsync(
                connection,
                "collection_runs",
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(NULLIF(imported_at, ''), completed_at)
            FROM collection_runs
            WHERE status = 'completed'
            ORDER BY datetime(COALESCE(NULLIF(imported_at, ''), completed_at)) DESC, id DESC
            LIMIT 1
            """;
        var value = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static double? ParseNullableDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
}
