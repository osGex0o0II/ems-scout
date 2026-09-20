namespace EmsScout.Application.Settings;

public sealed record LocalLogCleanupPreview(int FileCount, long TotalBytes);

public sealed record LocalLogCleanupResult(
    int DeletedCount,
    long DeletedBytes,
    IReadOnlyList<string> SkippedPaths,
    IReadOnlyList<string> FailedPaths)
{
    public bool IsComplete => SkippedPaths.Count == 0 && FailedPaths.Count == 0;
}

public sealed class LocalLogCleanupService(Func<string> dataDirectoryResolver)
{
    public LocalLogCleanupPreview Preview()
    {
        var files = EnumerateLogFiles().ToArray();
        return new LocalLogCleanupPreview(files.Length, files.Sum(GetLength));
    }

    public LocalLogCleanupResult Clear()
    {
        var root = ResolveRoot();
        if (!Directory.Exists(root))
        {
            return new LocalLogCleanupResult(0, 0, [], []);
        }

        var deleted = 0;
        long deletedBytes = 0;
        var skipped = new List<string>();
        var failed = new List<string>();
        foreach (var file in EnumerateLogFiles())
        {
            var relative = Path.GetRelativePath(root, file);
            try
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skipped.Add(relative);
                    continue;
                }

                var length = info.Length;
                File.Delete(file);
                deleted++;
                deletedBytes += length;
            }
            catch (IOException)
            {
                failed.Add(relative);
            }
            catch (UnauthorizedAccessException)
            {
                failed.Add(relative);
            }
        }

        return new LocalLogCleanupResult(deleted, deletedBytes, skipped, failed);
    }

    private IEnumerable<string> EnumerateLogFiles()
    {
        var root = ResolveRoot();
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories)
            .Where(path => IsInside(root, Path.GetFullPath(path)))
            .Where(path => !string.Equals(Path.GetFileName(path), "", StringComparison.Ordinal));
    }

    private string ResolveRoot()
    {
        var root = Path.GetFullPath(dataDirectoryResolver());
        if (Path.GetPathRoot(root)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException("禁止将磁盘根目录作为日志清理目录。");
        }

        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static long GetLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool IsInside(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
