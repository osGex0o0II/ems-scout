using EmsScout.Application.Settings;

namespace EmsScout.Tests;

public sealed class AppDataPathServiceTests
{
    [Fact]
    public void ResolvesConfiguredRelativePathsFromWorkspaceRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ems-scout-path-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var workspaceRoot = Path.Combine(tempDir, "workspace");
        var service = new AppSettingsService(settingsPath);
        service.Save(new AppSettings
        {
            DataDirectory = "runtime-data",
            ExportDirectory = "runtime-export",
        });

        var paths = new AppDataPathService(workspaceRoot, service);

        Assert.Equal(Path.Combine(workspaceRoot, "runtime-data"), paths.DataDirectory);
        Assert.Equal(Path.Combine(workspaceRoot, "runtime-data", "enum_full_v5.json"), paths.EnumJsonPath);
        Assert.Equal(Path.Combine(workspaceRoot, "runtime-data", "ac.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(workspaceRoot, "runtime-export"), paths.ExportDirectory);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void BuildDataEnvironmentCreatesDirectoryAndKeepsPathsConsistent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ems-scout-path-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var workspaceRoot = Path.Combine(tempDir, "workspace");
        var service = new AppSettingsService(settingsPath);
        service.Save(new AppSettings { DataDirectory = "configured-out" });

        var paths = new AppDataPathService(workspaceRoot, service);
        var environment = paths.BuildDataEnvironment();
        var dataDirectory = Path.Combine(workspaceRoot, "configured-out");

        Assert.True(Directory.Exists(dataDirectory));
        Assert.Equal(dataDirectory, environment["EMS_OUT_DIR"]);
        Assert.Equal(Path.Combine(dataDirectory, "enum_full_v5.json"), environment["EMS_JSON_PATH"]);
        Assert.Equal(Path.Combine(dataDirectory, "ac.db"), environment["EMS_DB_PATH"]);
        Assert.Equal(dataDirectory, environment["EMS_QUALITY_OUT"]);

        Directory.Delete(tempDir, recursive: true);
    }
}
