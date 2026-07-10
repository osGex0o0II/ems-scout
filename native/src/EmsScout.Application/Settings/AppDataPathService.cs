namespace EmsScout.Application.Settings;

public sealed class AppDataPathService(
    string workspaceRoot,
    AppSettingsService settingsService)
{
    public string WorkspaceRoot { get; } = workspaceRoot;

    public string DataDirectory
    {
        get
        {
            var settings = settingsService.Load();
            return ResolveWorkspacePath(settings.DataDirectory);
        }
    }

    public string ExportDirectory
    {
        get
        {
            var settings = settingsService.Load();
            return ResolveWorkspacePath(settings.ExportDirectory);
        }
    }

    public string EnumJsonPath => Path.Combine(DataDirectory, "enum_full_v5.json");

    public string DatabasePath => Path.Combine(DataDirectory, "ac.db");

    public string QualityOutputDirectory => DataDirectory;

    public IReadOnlyDictionary<string, string> BuildDataEnvironment()
    {
        var dataDirectory = DataDirectory;
        Directory.CreateDirectory(dataDirectory);
        return new Dictionary<string, string>
        {
            ["EMS_OUT_DIR"] = dataDirectory,
            ["EMS_JSON_PATH"] = Path.Combine(dataDirectory, "enum_full_v5.json"),
            ["EMS_DB_PATH"] = Path.Combine(dataDirectory, "ac.db"),
            ["EMS_QUALITY_OUT"] = dataDirectory,
        };
    }

    public string ResolveWorkspacePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(WorkspaceRoot, path);
    }
}
