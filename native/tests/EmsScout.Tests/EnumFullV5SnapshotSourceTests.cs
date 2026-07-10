using EmsScout.Domain;
using EmsScout.Legacy;

namespace EmsScout.Tests;

public sealed class EnumFullV5SnapshotSourceTests
{
    [Fact]
    public async Task LoadsCardsFromLegacyEnumJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ems-scout-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
        {
          "buildings": [
            {
              "building": "1号",
              "subAreas": [
                {
                  "floor": -1,
                  "text": "B1F",
                  "pages": [
                    {
                      "page": "default",
                      "cards": [
                        { "name": "DXBCGQ-1", "switch": "OFF", "mode": "制冷", "indoor": "26.3", "setTemp": "26", "fan": "自动", "indicator": "3bdc38eda0ae77f26807b2b6cdde4456.png", "comm": "关机" },
                        { "name": "DXBCGQ-2", "switch": "ON", "mode": "制冷", "indoor": "25.1", "setTemp": "24", "fan": "中", "indicator": "56f45bb314d74cc8da6c6c8e5942d08d.png", "comm": "开机" }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """);

        try
        {
            var snapshot = await new EnumFullV5SnapshotSource(path).LoadAsync();
            var summary = new InventorySummarizer().Summarize(snapshot.Cards);

            Assert.Equal(2, summary.Total);
            Assert.Equal(1, summary.Running);
            Assert.Equal(1, summary.Stopped);
            Assert.Equal("DXBCGQ-1", snapshot.Cards[0].Name);
            Assert.Equal(DeviceCommunicationState.Stopped, snapshot.Cards[0].CommunicationState);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CurrentWorkspaceEnumJsonMatchesMigrationBaseline()
    {
        var root = LocateRepositoryRoot();
        var path = Path.Combine(root, "out", "enum_full_v5.json");
        Assert.True(File.Exists(path), $"Missing baseline file: {path}");

        var snapshot = await new EnumFullV5SnapshotSource(path).LoadAsync();
        var summary = new InventorySummarizer().Summarize(snapshot.Cards);

        Assert.Equal(6568, summary.Total);
        Assert.Equal(1843, summary.Running);
        Assert.Equal(3196, summary.Stopped);
        Assert.Equal(1527, summary.Offline);
        Assert.Equal(2, summary.Unknown);
        Assert.Equal(6, summary.Buildings.Count);
        Assert.Equal(1493, summary.Buildings.Single(x => x.Building == "1号").Total);
        Assert.Equal(2480, summary.Buildings.Single(x => x.Building == "6号").Total);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "out")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
}
