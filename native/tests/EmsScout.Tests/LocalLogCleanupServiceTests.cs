using EmsScout.Application.Settings;

namespace EmsScout.Tests;

public sealed class LocalLogCleanupServiceTests
{
    [Fact]
    public void PreviewAndClearRemoveOnlyLocalLogFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-log-cleanup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllText(Path.Combine(root, "app.log"), "abc");
        File.WriteAllText(Path.Combine(root, "nested", "collector.ndjson.log"), "12345");
        File.WriteAllText(Path.Combine(root, "ac.db"), "sqlite");
        File.WriteAllText(Path.Combine(root, "quality_report.json"), "report");
        File.WriteAllText(Path.Combine(root, "data.ndjson"), "data");

        var service = new LocalLogCleanupService(() => root);

        var preview = service.Preview();
        var result = service.Clear();

        Assert.Equal(2, preview.FileCount);
        Assert.Equal(8, preview.TotalBytes);
        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(8, result.DeletedBytes);
        Assert.False(File.Exists(Path.Combine(root, "app.log")));
        Assert.False(File.Exists(Path.Combine(root, "nested", "collector.ndjson.log")));
        Assert.True(File.Exists(Path.Combine(root, "ac.db")));
        Assert.True(File.Exists(Path.Combine(root, "quality_report.json")));
        Assert.True(File.Exists(Path.Combine(root, "data.ndjson")));
    }

    [Fact]
    public void MissingDataDirectoryIsAnEmptySuccessfulCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-log-cleanup", Guid.NewGuid().ToString("N"));
        var result = new LocalLogCleanupService(() => root).Clear();

        Assert.Equal(0, result.DeletedCount);
        Assert.Empty(result.SkippedPaths);
        Assert.Empty(result.FailedPaths);
    }
}
