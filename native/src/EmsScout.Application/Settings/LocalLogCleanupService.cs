namespace EmsScout.Application.Settings;

public sealed record LocalLogCleanupPreview(int FileCount, long TotalBytes)
{
    public IReadOnlyList<string> SkippedPaths { get; init; } = [];

    public IReadOnlyList<string> FailedPaths { get; init; } = [];
}

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
        var scan = ScanLogFiles();
        return new LocalLogCleanupPreview(scan.Files.Count, scan.Files.Sum(GetLength))
        {
            SkippedPaths = scan.SkippedPaths,
            FailedPaths = scan.FailedPaths,
        };
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
        var scan = ScanLogFiles();
        var skipped = new List<string>(scan.SkippedPaths);
        var failed = new List<string>(scan.FailedPaths);
        foreach (var file in scan.Files)
        {
            var relative = Path.GetRelativePath(root, file);
            try
            {
                if (!IsSafeFile(root, file))
                {
                    skipped.Add(relative);
                    continue;
                }

                var info = new FileInfo(file);
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

    private LogFileScan ScanLogFiles()
    {
        var root = ResolveRoot();
        if (!Directory.Exists(root))
        {
            return new LogFileScan([], [], []);
        }

        try
        {
            var reparsePoint = FindReparsePoint(root);
            if (reparsePoint is not null)
            {
                return new LogFileScan([], [reparsePoint], []);
            }
        }
        catch (IOException)
        {
            return new LogFileScan([], [], ["."]);
        }
        catch (UnauthorizedAccessException)
        {
            return new LogFileScan([], [], ["."]);
        }

        var files = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (IOException)
            {
                failed.Add(Path.GetRelativePath(root, directory.FullName));
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                failed.Add(Path.GetRelativePath(root, directory.FullName));
                continue;
            }

            foreach (var entry in entries)
            {
                string relative;
                try
                {
                    relative = Path.GetRelativePath(root, entry.FullName);
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skipped.Add(relative);
                        continue;
                    }

                    if (entry is DirectoryInfo child)
                    {
                        pending.Push(child);
                    }
                    else if (entry is FileInfo file &&
                             file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(file.FullName);
                    }
                }
                catch (IOException)
                {
                    failed.Add(Path.GetRelativePath(root, entry.FullName));
                }
                catch (UnauthorizedAccessException)
                {
                    failed.Add(Path.GetRelativePath(root, entry.FullName));
                }
            }
        }

        return new LogFileScan(files, skipped, failed);
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

    private static bool IsSafeFile(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        return FindReparsePoint(fullPath) is null;
    }

    private static string? FindReparsePoint(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(fullPath)!;
        var current = volumeRoot;
        foreach (var segment in Path.GetRelativePath(volumeRoot, fullPath)
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                break;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return current;
            }
        }

        return null;
    }

    private sealed record LogFileScan(
        IReadOnlyList<string> Files,
        IReadOnlyList<string> SkippedPaths,
        IReadOnlyList<string> FailedPaths);
}
