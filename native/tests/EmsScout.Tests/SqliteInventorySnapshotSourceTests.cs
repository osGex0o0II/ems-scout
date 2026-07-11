using EmsScout.Domain;
using EmsScout.Infrastructure.Sqlite;

namespace EmsScout.Tests;

public sealed class SqliteInventorySnapshotSourceTests
{
    [Fact]
    public async Task LoadsCurrentCardsFromTheVersionedDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-inventory-tests", Guid.NewGuid().ToString("N"));
        var databasePath = await CollectionImportDatabaseFixture.CreateMigratedAsync(
            root,
            ("1号", "1-0101-KT", "default", "开机"),
            ("2号", "2-0201-KT", "二页", "离线"));

        try
        {
            var snapshot = await new SqliteInventorySnapshotSource(() => databasePath).LoadAsync();
            var summary = new InventorySummarizer().Summarize(snapshot.Cards);

            Assert.Equal(Path.GetFullPath(databasePath), snapshot.SourcePath);
            Assert.Equal(2, summary.Total);
            Assert.Equal(1, summary.Running);
            Assert.Equal(1, summary.Offline);
            Assert.Equal("1-0101-KT", snapshot.Cards[0].Name);
            Assert.Equal(DeviceCommunicationState.Running, snapshot.Cards[0].CommunicationState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingDatabaseFailsWithoutCreatingAFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "ems-scout-inventory-tests", Guid.NewGuid().ToString("N"), "ac.db");
        var source = new SqliteInventorySnapshotSource(() => path);

        await Assert.ThrowsAsync<FileNotFoundException>(() => source.LoadAsync());

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task UsesLatestCompletedImportTimeInsteadOfDatabaseFileTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-inventory-tests", Guid.NewGuid().ToString("N"));
        var databasePath = await CollectionImportDatabaseFixture.CreateMigratedAsync(
            root,
            ("1号", "1-0101-KT", "default", "关机"));
        var expected = DateTimeOffset.Parse("2026-07-11T08:30:00Z");

        try
        {
            await using (var connection = CollectionImportDatabaseFixture.Open(databasePath))
            {
                await connection.OpenAsync();
                await CollectionImportDatabaseFixture.ExecuteAsync(connection, """
                    INSERT INTO collection_runs
                        (run_key, completed_at, imported_at, status, scope, buildings)
                    VALUES
                        ('older', '2026-07-10T08:00:00Z', '2026-07-10T08:01:00Z', 'completed', 'full', '["1号"]'),
                        ('latest', '2026-07-11T08:00:00Z', '2026-07-11T08:30:00Z', 'completed', 'full', '["1号"]'),
                        ('backup', '2026-07-12T08:00:00Z', '2026-07-12T08:30:00Z', 'backup', 'full', '["1号"]');
                    """);
            }

            File.SetLastWriteTimeUtc(databasePath, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var snapshot = await new SqliteInventorySnapshotSource(() => databasePath).LoadAsync();

            Assert.Equal(expected, snapshot.SourceUpdatedAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreservesFractionalBasementAndInlineFloorValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-inventory-tests", Guid.NewGuid().ToString("N"));
        var databasePath = await CollectionImportDatabaseFixture.CreateMigratedAsync(
            root,
            ("2号", "2-2BC-2M001-KT-1", "default", "未知"),
            ("3号", "B1-CARD", "default", "离线"),
            ("6号", "BM-CARD", "default", "关机"));

        try
        {
            await using (var connection = CollectionImportDatabaseFixture.Open(databasePath))
            {
                await connection.OpenAsync();
                await CollectionImportDatabaseFixture.ExecuteAsync(connection, """
                    UPDATE sub_areas SET floor = 2.5, text = '2.5F' WHERE building = '2号';
                    UPDATE sub_areas SET floor = -1, text = 'B1F' WHERE building = '3号';
                    UPDATE sub_areas SET floor = -2, text = 'BM' WHERE building = '6号';
                    """);
            }

            var snapshot = await new SqliteInventorySnapshotSource(() => databasePath).LoadAsync();

            Assert.Equal(2.5, snapshot.Cards.Single(card => card.Building == "2号").Floor);
            Assert.Equal(-1, snapshot.Cards.Single(card => card.Building == "3号").Floor);
            Assert.Equal(-2, snapshot.Cards.Single(card => card.Building == "6号").Floor);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
