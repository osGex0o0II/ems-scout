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
        foreach (var markerPath in GetMarkerPaths())
        {
            if (!File.Exists(markerPath))
            {
                continue;
            }

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

    private static IEnumerable<string> GetMarkerPaths()
    {
        // Packaged WinUI processes can redirect LocalApplicationData into the
        // package LocalCache. The installer writes the authoritative marker in
        // the user's profile, so read that location first to avoid stale package
        // cache data selecting an older checkout.
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, "AppData", "Local", "EMS Scout", WorkspaceMarkerFileName);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "EMS Scout", WorkspaceMarkerFileName);
        }
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
