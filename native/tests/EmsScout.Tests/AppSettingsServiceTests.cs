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
            CheckLoginBeforeCollection = false,
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
        Assert.Equal("edge-cdp", loaded.DefaultCollectionMode);
        Assert.True(loaded.CheckLoginBeforeCollection);
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
    public void MigratesLegacyDataDirectoryToCompleteOutDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ems-scout-settings-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var service = new AppSettingsService(settingsPath);

        service.Save(new AppSettings { DataDirectory = "data" });

        var loaded = new AppSettingsService(settingsPath).Load();

        Assert.Equal("out", loaded.DataDirectory);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void PersistsDataTableAppearanceSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ems-scout-settings-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var service = new AppSettingsService(settingsPath);

        service.Save(new AppSettings
        {
            TemperatureWarningThreshold = 3.5,
            OfflineStatusColor = "#667788",
            TemperatureWarningColor = "#D97706",
        });

        var loaded = new AppSettingsService(settingsPath).Load();

        Assert.Equal(3.5, loaded.TemperatureWarningThreshold);
        Assert.Equal("#667788", loaded.OfflineStatusColor);
        Assert.Equal("#D97706", loaded.TemperatureWarningColor);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void PersistsWindowStartupAndIntegrationSettingsInTheSettingsDocument()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ems-scout-settings-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var service = new AppSettingsService(settingsPath);

        service.Save(new AppSettings());

        var json = File.ReadAllText(settingsPath);
        Assert.Contains("\"PageTransitionStyle\"", json);
        Assert.DoesNotContain("\"Language\"", json);
        Assert.Contains("\"SaveWindowPlacement\"", json);
        Assert.Contains("\"StartMinimized\"", json);
        Assert.Contains("\"LaunchAtLogin\"", json);
        Assert.Contains("\"ShowInSendTo\"", json);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void NormalizesRuntimePreferenceOptionsAndWindowPlacement()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ems-scout-settings-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var service = new AppSettingsService(settingsPath);

        service.Save(new AppSettings
        {
            PageTransitionStyle = "spring",
            WindowPlacement = new WindowPlacementState { Width = 0, Height = -1 },
        });

        var loaded = service.Load();

        Assert.Equal("fade", loaded.PageTransitionStyle);
        Assert.NotNull(loaded.WindowPlacement);
        Assert.Equal(1, loaded.WindowPlacement!.Width);
        Assert.Equal(1, loaded.WindowPlacement.Height);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public void NullOptionValuesFallBackToSafeDefaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ems-scout-settings-tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var service = new AppSettingsService(settingsPath);

        service.Save(new AppSettings
        {
            LogLevel = null!,
            Theme = null!,
            PageTransitionStyle = null!,
        });

        var loaded = service.Load();

        Assert.Equal("INFO", loaded.LogLevel);
        Assert.Equal("system", loaded.Theme);
        Assert.Equal("fade", loaded.PageTransitionStyle);

        Directory.Delete(tempDir, recursive: true);
    }
}
