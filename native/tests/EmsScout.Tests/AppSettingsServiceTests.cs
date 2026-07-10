using EmsScout.Application.Settings;

namespace EmsScout.Tests;

public sealed class AppSettingsServiceTests
{
    [Fact]
    public void SavesLoadsAndNormalizesSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ems-scout-settings-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var service = new AppSettingsService(settingsPath);

        service.Save(new AppSettings
        {
            EmsUrl = "  http://example.local/ui  ",
            EdgeCdpPort = 70000,
            DataDirectory = "  data-out  ",
            ExportDirectory = "",
            DefaultCollectionMode = "auto-launch",
            LogLevel = "debug",
            Theme = "dark",
            SaveNdjsonLog = false,
            ReduceMotion = true,
        });

        var loaded = new AppSettingsService(settingsPath).Load();

        Assert.True(File.Exists(settingsPath));
        Assert.Equal("http://example.local/ui", loaded.EmsUrl);
        Assert.Equal(65535, loaded.EdgeCdpPort);
        Assert.Equal("data-out", loaded.DataDirectory);
        Assert.Equal("out/data-management-export", loaded.ExportDirectory);
        Assert.Equal("auto-launch", loaded.DefaultCollectionMode);
        Assert.Equal("DEBUG", loaded.LogLevel);
        Assert.Equal("dark", loaded.Theme);
        Assert.False(loaded.SaveNdjsonLog);
        Assert.True(loaded.ReduceMotion);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void ResetRestoresDefaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ems-scout-settings-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var service = new AppSettingsService(settingsPath);

        service.Save(new AppSettings { EdgeCdpPort = 1000, Theme = "dark" });
        service.Reset();

        var loaded = service.Load();
        Assert.Equal(9222, loaded.EdgeCdpPort);
        Assert.Equal("system", loaded.Theme);

        Directory.Delete(tempDir, recursive: true);
    }
}
