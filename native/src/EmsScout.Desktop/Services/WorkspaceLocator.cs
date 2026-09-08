namespace EmsScout.Desktop.Services;

public static class WorkspaceLocator
{
    private const string WorkspaceEnvironmentVariable = "EMS_SCOUT_WORKSPACE";
    private const string WorkspaceMarkerFileName = "workspace-root.txt";

    public static string LocateRepositoryRoot()
    {
        foreach (var candidate in GetCandidates())
        {
            var repository = FindRepositoryRoot(candidate);
            if (repository is not null)
            {
                return repository;
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EMS Scout",
            "workspace");
    }

    private static IEnumerable<string> GetCandidates()
    {
        var markerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EMS Scout",
            WorkspaceMarkerFileName);
        if (File.Exists(markerPath))
        {
            var marker = File.ReadAllText(markerPath).Trim();
            if (!string.IsNullOrWhiteSpace(marker))
            {
                yield return marker;
            }
        }

        var configured = Environment.GetEnvironmentVariable(WorkspaceEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        yield return Environment.CurrentDirectory;
        yield return AppContext.BaseDirectory;
    }

    private static string? FindRepositoryRoot(string candidate)
    {
        try
        {
            var directory = new DirectoryInfo(candidate);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "out")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }
}
