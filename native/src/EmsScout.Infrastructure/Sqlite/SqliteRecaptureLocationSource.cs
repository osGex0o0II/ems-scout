using EmsScout.Application.Collection;
using EmsScout.Application.Devices;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteRecaptureLocationSource(Func<string> databasePathResolver) : IRecaptureLocationSource
{
    public async Task<IReadOnlyList<RecaptureLocation>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var databasePath = Path.GetFullPath(databasePathResolver());
        if (!File.Exists(databasePath))
        {
            return [];
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT building, floor, text, x, y
            FROM sub_areas
            WHERE building IS NOT NULL
              AND floor IS NOT NULL
              AND x IS NOT NULL
              AND y IS NOT NULL
            ORDER BY building, floor, x, y
            """;

        var locations = new List<RecaptureLocation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var building = reader.GetString(0).Trim();
            var floor = reader.GetDouble(1);
            var subArea = reader.IsDBNull(2) ? null : reader.GetString(2);
            var x = reader.GetInt32(3);
            var y = reader.GetInt32(4);
            locations.Add(new RecaptureLocation(
                building,
                RecaptureLocationCatalog.ResolveSeat(building, x),
                DeviceFloorLabelFormatter.Format(floor, subArea),
                floor,
                x,
                y));
        }

        return locations
            .DistinctBy(location => (location.Building, location.Floor, location.X, location.Y))
            .OrderBy(location => location.Building, StringComparer.Ordinal)
            .ThenBy(location => location.Floor)
            .ThenBy(location => location.X)
            .ThenBy(location => location.Y)
            .ToArray();
    }
}
