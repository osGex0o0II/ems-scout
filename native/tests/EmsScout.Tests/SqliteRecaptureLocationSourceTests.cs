using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class SqliteRecaptureLocationSourceTests
{
    [Fact]
    public async Task LoadsDistinctLocationsFromATemporaryDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ems-recapture-tests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "fixture.db");
        Directory.CreateDirectory(directory);
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE sub_areas (building TEXT, floor REAL, text TEXT, x INTEGER, y INTEGER);
                    INSERT INTO sub_areas VALUES ('5号', 2, '2F C座', 700, 30);
                    INSERT INTO sub_areas VALUES ('5号', 2, '2F C座 duplicate', 700, 30);
                    INSERT INTO sub_areas VALUES ('1号', -1, 'B1F', 100, 20);
                    INSERT INTO sub_areas VALUES ('1号', 1, 'missing coordinate', NULL, 20);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var source = new SqliteRecaptureLocationSource(() => databasePath);
            var locations = await source.LoadAsync();

            Assert.Collection(
                locations,
                first =>
                {
                    Assert.Equal("1号", first.Building);
                    Assert.Equal("整栋", first.Seat);
                    Assert.Equal("B1F", first.FloorLabel);
                    Assert.Equal((100, 20), (first.X, first.Y));
                },
                second =>
                {
                    Assert.Equal("5号", second.Building);
                    Assert.Equal("C座", second.Seat);
                    Assert.Equal("2F", second.FloorLabel);
                    Assert.Equal((700, 30), (second.X, second.Y));
                });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingConfiguredDatabaseProducesAnEmptyCatalog()
    {
        var source = new SqliteRecaptureLocationSource(() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"));

        Assert.Empty(await source.LoadAsync());
    }
}
