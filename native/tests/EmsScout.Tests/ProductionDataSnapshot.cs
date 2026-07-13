namespace EmsScout.Tests;

internal static class ProductionDataSnapshot
{
    private static readonly Lazy<Snapshot> Current = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string DatabasePath => Current.Value.DatabasePath;

    public static string RepositoryRoot => Current.Value.RepositoryRoot;

    private static Snapshot Create()
    {
        CleanupStaleSnapshots();
        var repositoryRoot = LocateRepositoryRoot();
        var configuredDatabase = Environment.GetEnvironmentVariable("EMS_PRODUCTION_DB_PATH");
        var sourceDatabase = string.IsNullOrWhiteSpace(configuredDatabase)
            ? Path.Combine(repositoryRoot, "out", "ac.db")
            : Path.GetFullPath(configuredDatabase);
        if (!File.Exists(sourceDatabase))
        {
            throw new FileNotFoundException(
                "Missing production database fixture. Set EMS_PRODUCTION_DB_PATH to a complete evidence database or place it at out/ac.db.",
                sourceDatabase);
        }

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "ems-scout-production-snapshot-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        var databasePath = Path.Combine(temporaryRoot, "ac.db");
        File.Copy(sourceDatabase, databasePath);

        var sourceWal = sourceDatabase + "-wal";
        if (File.Exists(sourceWal) && new FileInfo(sourceWal).Length > 0)
        {
            File.Copy(sourceWal, databasePath + "-wal");
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(temporaryRoot);
        return new Snapshot(repositoryRoot, databasePath);
    }

    private static string LocateRepositoryRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("EMS_PRODUCTION_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(current.FullName, "out")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Cannot locate EMS repository root.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Process exit cleanup is best effort; test data is confined to the system temp directory.
        }
    }

    private static void CleanupStaleSnapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-production-snapshot-tests");
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed record Snapshot(string RepositoryRoot, string DatabasePath);
}
