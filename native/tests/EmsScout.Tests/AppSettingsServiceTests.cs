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
        Assert.Equal(AppStorageDefaults.ExportDirectory, loaded.ExportDirectory);
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

    [Fact]
    public void NewSettingsUseWritableLocalApplicationDataDirectories()
    {
        var settings = new AppSettings();

        Assert.True(Path.IsPathRooted(settings.DataDirectory));
        Assert.Equal(AppStorageDefaults.DataDirectory, settings.DataDirectory);
        Assert.Equal(AppStorageDefaults.ExportDirectory, settings.ExportDirectory);
        Assert.StartsWith(AppStorageDefaults.ProductDirectory, settings.DataDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentSavesAlwaysPublishACompleteJsonDocument()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ems-scout-settings-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var service = new AppSettingsService(settingsPath);

        var writers = Enumerable.Range(0, 4).Select(writer => Task.Run(() =>
        {
            for (var index = 0; index < 75; index++)
            {
                service.Save(new AppSettings
                {
                    EmsUrl = $"http://writer-{writer}.local/ui/{index}",
                    DataDirectory = $"data-{writer}",
                    ExportDirectory = $"export-{writer}",
                });
            }
        }));

        await Task.WhenAll(writers);
        var loaded = new AppSettingsService(settingsPath).Load();
        var suffix = loaded.DataDirectory["data-".Length..];
        Assert.Equal("export-" + suffix, loaded.ExportDirectory);
        Assert.StartsWith("http://writer-" + suffix + ".local/", loaded.EmsUrl, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(tempDir, "*.tmp", SearchOption.AllDirectories));
        Directory.Delete(tempDir, recursive: true);
    }
}
