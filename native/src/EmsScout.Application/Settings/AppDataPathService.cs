namespace EmsScout.Application.Settings;

public sealed class AppDataPathService(
    string workspaceRoot,
    AppSettingsService settingsService)
{
    public string WorkspaceRoot { get; } = workspaceRoot;

    public string DataDirectory => Capture().DataDirectory;

    public string ExportDirectory => Capture().ExportDirectory;

    public string CollectionSnapshotPath => Capture().CollectionSnapshotPath;

    public string DatabasePath => Capture().DatabasePath;

    public string QualityOutputDirectory => Capture().QualityOutputDirectory;

    public AppDataPathSnapshot Capture() => Capture(settingsService.Load());

    public AppDataPathSnapshot Capture(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(
            Path.GetFullPath(ResolveWorkspacePath(settings.DataDirectory)),
            Path.GetFullPath(ResolveWorkspacePath(settings.ExportDirectory)));
    }

    public IReadOnlyDictionary<string, string> BuildDataEnvironment() => Capture().BuildDataEnvironment();

    public string ResolveWorkspacePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(WorkspaceRoot, path);
    }
}

public sealed record AppDataPathSnapshot(string DataDirectory, string ExportDirectory)
{
    public string CollectionSnapshotPath => Path.Combine(DataDirectory, "collection_snapshot_v1.json");

    public string DatabasePath => Path.Combine(DataDirectory, "ac.db");

    public string QualityOutputDirectory => DataDirectory;

    public IReadOnlyDictionary<string, string> BuildDataEnvironment()
    {
        Directory.CreateDirectory(DataDirectory);
        return new Dictionary<string, string>
        {
            ["EMS_OUT_DIR"] = DataDirectory,
            ["EMS_JSON_PATH"] = Path.Combine(DataDirectory, "enum_full_v5.json"),
            ["EMS_SNAPSHOT_PATH"] = CollectionSnapshotPath,
            ["EMS_DB_PATH"] = DatabasePath,
            ["EMS_QUALITY_OUT"] = QualityOutputDirectory,
        };
    }
}
