using EmsScout.Application.Settings;
using System.Diagnostics;
using System.Security.Cryptography;

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

    [Fact]
    public void PreviewAndClearDoNotFollowDirectoryJunctionsOutsideTheDataDirectory()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "ems-scout-log-cleanup", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(fixture, "data");
        var outside = Path.Combine(fixture, "outside");
        Directory.CreateDirectory(Path.Combine(data, "nested"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(data, "nested", "inside.log"), "inside");
        var sentinel = Path.Combine(outside, "sentinel.log");
        File.WriteAllText(sentinel, "must remain unchanged");
        var hashBefore = SHA256.HashData(File.ReadAllBytes(sentinel));
        CreateJunction(Path.Combine(data, "linked"), outside);
        CreateJunction(Path.Combine(data, "cycle"), data);
        var fileLink = Path.Combine(data, "linked-file.log");
        File.CreateSymbolicLink(fileLink, sentinel);

        var service = new LocalLogCleanupService(() => data);
        var preview = service.Preview();
        var result = service.Clear();

        Assert.Equal(1, preview.FileCount);
        Assert.Contains(preview.SkippedPaths, path => path.Contains("linked", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(preview.FailedPaths);
        Assert.Equal(1, result.DeletedCount);
        Assert.Contains(result.SkippedPaths, path => path.Contains("linked", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.SkippedPaths, path => path.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.SkippedPaths, path => path.Contains("linked-file", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(sentinel));
        Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(sentinel)));
        File.Delete(fileLink);
        DeleteJunction(Path.Combine(data, "linked"));
        DeleteJunction(Path.Combine(data, "cycle"));
        Directory.Delete(fixture, recursive: true);
    }

    [Fact]
    public void PreviewAndClearRejectACleanupRootBelowAnAncestorJunction()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "ems-scout-log-cleanup", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(fixture, "outside");
        var outsideChild = Path.Combine(outside, "child");
        var link = Path.Combine(fixture, "data-link");
        Directory.CreateDirectory(outsideChild);
        var sentinel = Path.Combine(outsideChild, "sentinel.log");
        File.WriteAllText(sentinel, "must remain unchanged");
        var hashBefore = SHA256.HashData(File.ReadAllBytes(sentinel));
        CreateJunction(link, outside);
        var service = new LocalLogCleanupService(() => Path.Combine(link, "child"));

        var preview = service.Preview();
        var result = service.Clear();

        Assert.Equal(0, preview.FileCount);
        Assert.NotEmpty(preview.SkippedPaths);
        Assert.Equal(0, result.DeletedCount);
        Assert.NotEmpty(result.SkippedPaths);
        Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(sentinel)));
        DeleteJunction(link);
        Directory.Delete(fixture, recursive: true);
    }

    [Fact]
    public void ClearDoesNotFollowADataDirectoryThatIsItselfAJunction()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "ems-scout-log-cleanup", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(fixture, "outside");
        var dataLink = Path.Combine(fixture, "data-link");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.log");
        File.WriteAllText(sentinel, "must remain unchanged");
        var hashBefore = SHA256.HashData(File.ReadAllBytes(sentinel));
        CreateJunction(dataLink, outside);

        var service = new LocalLogCleanupService(() => dataLink);
        var preview = service.Preview();
        var result = service.Clear();

        Assert.Equal(0, preview.FileCount);
        Assert.Equal(0, result.DeletedCount);
        Assert.NotEmpty(result.SkippedPaths);
        Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(sentinel)));
        DeleteJunction(dataLink);
        Directory.Delete(fixture, recursive: true);
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", link, target },
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static void DeleteJunction(string link)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "rmdir", link },
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
