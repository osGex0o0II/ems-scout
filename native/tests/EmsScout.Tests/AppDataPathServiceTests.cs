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
        Assert.Equal(Path.Combine(workspaceRoot, "runtime-data", "collection_snapshot_v1.json"), paths.CollectionSnapshotPath);
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
        Assert.Equal(Path.Combine(dataDirectory, "collection_snapshot_v1.json"), environment["EMS_SNAPSHOT_PATH"]);
        Assert.Equal(Path.Combine(dataDirectory, "ac.db"), environment["EMS_DB_PATH"]);
        Assert.Equal(dataDirectory, environment["EMS_QUALITY_OUT"]);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void CapturedPathsRemainInternallyConsistentAfterSettingsSwitch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ems-scout-path-tests", Guid.NewGuid().ToString("N"));
        var settings = new AppSettingsService(Path.Combine(tempDir, "settings.json"));
        settings.Save(new AppSettings { DataDirectory = "data-a", ExportDirectory = "export-a" });
        var paths = new AppDataPathService(Path.Combine(tempDir, "workspace"), settings);
        var captured = paths.Capture();

        settings.Save(new AppSettings { DataDirectory = "data-b", ExportDirectory = "export-b" });
        var environment = captured.BuildDataEnvironment();

        Assert.EndsWith(Path.Combine("workspace", "data-a"), captured.DataDirectory, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(captured.DataDirectory, "ac.db"), captured.DatabasePath);
        Assert.Equal(Path.Combine(captured.DataDirectory, "collection_snapshot_v1.json"), captured.CollectionSnapshotPath);
        Assert.All(environment.Values, value =>
            Assert.StartsWith(captured.DataDirectory, value, StringComparison.Ordinal));
        Assert.EndsWith(Path.Combine("workspace", "data-b"), paths.Capture().DataDirectory, StringComparison.Ordinal);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ConcurrentSettingsSwitchNeverProducesMixedPathSnapshot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ems-scout-path-tests", Guid.NewGuid().ToString("N"));
        var settings = new AppSettingsService(Path.Combine(tempDir, "settings.json"));
        settings.Save(new AppSettings { DataDirectory = "data-a", ExportDirectory = "export-a" });
        var paths = new AppDataPathService(Path.Combine(tempDir, "workspace"), settings);

        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 100; index++)
            {
                var suffix = index % 2 == 0 ? "a" : "b";
                settings.Save(new AppSettings
                {
                    DataDirectory = "data-" + suffix,
                    ExportDirectory = "export-" + suffix,
                });
            }
        });
        var reader = Task.Run(() =>
        {
            for (var index = 0; index < 300; index++)
            {
                var snapshot = paths.Capture();
                var dataSuffix = Path.GetFileName(snapshot.DataDirectory)[^1];
                var exportSuffix = Path.GetFileName(snapshot.ExportDirectory)[^1];
                Assert.Equal(dataSuffix, exportSuffix);
                Assert.Equal(Path.Combine(snapshot.DataDirectory, "ac.db"), snapshot.DatabasePath);
            }
        });

        await Task.WhenAll(writer, reader);
        Assert.Empty(Directory.EnumerateFiles(tempDir, "*.tmp", SearchOption.AllDirectories));
        Directory.Delete(tempDir, recursive: true);
    }
}
