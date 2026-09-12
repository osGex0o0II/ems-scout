using System.Text.Json;

namespace EmsScout.Application.Settings;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private AppSettings _current;

    public AppSettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EMS Scout",
            "settings.json"))
    {
    }

    public AppSettingsService(string settingsPath)
    {
        SettingsPath = settingsPath;
        _current = LoadFromDisk();
    }

    public string SettingsPath { get; }

    public AppSettings Current => _current.Clone();

    public AppSettings Load()
    {
        _current = LoadFromDisk();
        return Current;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var normalized = Normalize(settings);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(normalized, JsonOptions));
        _current = normalized;
    }

    public void Reset()
    {
        Save(new AppSettings());
    }

    private AppSettings LoadFromDisk()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(SettingsPath),
                JsonOptions);
            return Normalize(loaded ?? new AppSettings());
        }
        catch
        {
            return new AppSettings();
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        var output = settings.Clone();
        output.EmsUrl = string.IsNullOrWhiteSpace(output.EmsUrl)
            ? new AppSettings().EmsUrl
            : output.EmsUrl.Trim();
        output.EdgeCdpPort = Math.Clamp(output.EdgeCdpPort, 1, 65535);
        output.DataDirectory = string.IsNullOrWhiteSpace(output.DataDirectory)
            ? "out"
            : output.DataDirectory.Trim();
        if (output.DataDirectory.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            output.DataDirectory = "out";
        }
        output.ExportDirectory = string.IsNullOrWhiteSpace(output.ExportDirectory)
            ? "out/data-management-export"
            : output.ExportDirectory.Trim();
        // Browser startup is intentionally manual. Keep the legacy field for
        // settings-file compatibility, but never allow the removed auto mode.
        output.DefaultCollectionMode = "edge-cdp";
        output.CheckLoginBeforeCollection = true;
        output.LogLevel = NormalizeOption(output.LogLevel, "INFO", "ERROR", "INFO", "DEBUG");
        output.RealtimeBatchSize = Math.Clamp(output.RealtimeBatchSize, 1, 100);
        output.RealtimeReopenEvery = Math.Clamp(output.RealtimeReopenEvery, 0, 50);
        output.RealtimeTimeoutMs = Math.Clamp(output.RealtimeTimeoutMs, 3000, 120000);
        output.Theme = NormalizeOption(output.Theme, "system", "system", "light", "dark");
        output.PageTransitionStyle = NormalizeOption(output.PageTransitionStyle, "fade", "none", "fade", "slide");
        if (output.WindowPlacement is not null)
        {
            output.WindowPlacement.Width = Math.Max(1, output.WindowPlacement.Width);
            output.WindowPlacement.Height = Math.Max(1, output.WindowPlacement.Height);
        }
        output.TemperatureWarningThreshold = double.IsFinite(output.TemperatureWarningThreshold)
            ? Math.Clamp(output.TemperatureWarningThreshold, 0, 50)
            : 2.0;
        output.OfflineStatusColor = NormalizeHexColor(output.OfflineStatusColor, "#808080");
        output.TemperatureWarningColor = NormalizeHexColor(output.TemperatureWarningColor, "#D97706");
        output.DashboardNormalMode = NormalizeOption(
            output.DashboardNormalMode,
            "制冷",
            "制冷",
            "制热",
            "通风",
            "送暖",
            "地暖",
            "制热+地暖");
        output.DashboardTemperatureMin = NormalizeDashboardTemperature(output.DashboardTemperatureMin, 22);
        output.DashboardTemperatureMax = NormalizeDashboardTemperature(output.DashboardTemperatureMax, 26);
        if (output.DashboardTemperatureMin > output.DashboardTemperatureMax)
        {
            output.DashboardTemperatureMin = 22;
            output.DashboardTemperatureMax = 26;
        }
        return output;
    }

    private static double NormalizeDashboardTemperature(double value, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, 5, 40) : fallback;
    }

    private static string NormalizeHexColor(string? value, string fallback)
    {
        var candidate = value?.Trim();
        if (candidate is null || (candidate.Length != 7 && candidate.Length != 9) || candidate[0] != '#')
        {
            return fallback;
        }

        for (var index = 1; index < candidate.Length; index++)
        {
            if (!Uri.IsHexDigit(candidate[index]))
            {
                return fallback;
            }
        }

        return candidate.ToUpperInvariant();
    }

    private static string NormalizeOption(string? value, string fallback, params string[] allowed)
    {
        return allowed.FirstOrDefault(item => item.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? fallback;
    }
}
