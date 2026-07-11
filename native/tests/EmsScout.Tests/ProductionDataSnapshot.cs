namespace EmsScout.Tests;

internal static class ProductionDataSnapshot
{
    private static readonly Lazy<Snapshot> Current = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string DatabasePath => Current.Value.DatabasePath;

    public static string RepositoryRoot => Current.Value.RepositoryRoot;

    private static Snapshot Create()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var sourceDatabase = Path.Combine(repositoryRoot, "out", "ac.db");
        if (!File.Exists(sourceDatabase))
        {
            throw new FileNotFoundException("Missing production database fixture.", sourceDatabase);
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

    private sealed record Snapshot(string RepositoryRoot, string DatabasePath);
}
