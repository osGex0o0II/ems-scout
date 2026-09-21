using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using EmsScout.Domain;

namespace EmsScout.Tests;

public sealed class SqliteDashboardSummaryRepositoryTests
{
    [Fact]
    public async Task AggregatesCommunicationStatesAndKeepsUnmappedValuesUnknown()
    {
        var path = Path.Combine(Path.GetTempPath(), "ems-scout-dashboard-summary-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE sub_areas (id INTEGER PRIMARY KEY, building TEXT, floor REAL, text TEXT);
                    CREATE TABLE pages (id INTEGER PRIMARY KEY, sub_area_id INTEGER, collected_at TEXT);
                    CREATE TABLE cards (id INTEGER PRIMARY KEY, page_id INTEGER, name TEXT, comm TEXT);
                    INSERT INTO sub_areas VALUES (1, '1号', 1, '1F A');
                    INSERT INTO pages VALUES (1, 1, '2026-09-22T08:00:00+08:00');
                    INSERT INTO cards VALUES (1, 1, 'A', 'ON');
                    INSERT INTO cards VALUES (2, 1, 'B', '离线');
                    INSERT INTO cards VALUES (3, 1, 'C', 'unmapped');
                    INSERT INTO cards VALUES (4, 1, 'D', '');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var result = await new SqliteDashboardSummaryRepository(path).LoadAsync(null);

            var building = Assert.Single(result.Summary.Buildings, item => item.Building == "1号");
            Assert.Equal(4, building.Total);
            Assert.Equal(1, building.Running);
            Assert.Equal(0, building.Stopped);
            Assert.Equal(1, building.Offline);
            Assert.Equal(2, building.Unknown);
            Assert.Equal(4, result.Summary.Total);
            Assert.Equal(DateTimeOffset.Parse("2026-09-22T08:00:00+08:00"), result.SourceUpdatedAt);
            Assert.NotEmpty(result.Revision);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
