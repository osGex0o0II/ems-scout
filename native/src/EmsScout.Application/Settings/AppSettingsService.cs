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
        output.ExportDirectory = string.IsNullOrWhiteSpace(output.ExportDirectory)
            ? "out/data-management-export"
            : output.ExportDirectory.Trim();
        output.DefaultCollectionMode = NormalizeOption(output.DefaultCollectionMode, "edge-cdp", "edge-cdp", "auto-launch");
        output.LogLevel = NormalizeOption(output.LogLevel, "INFO", "ERROR", "INFO", "DEBUG");
        output.Theme = NormalizeOption(output.Theme, "system", "system", "light", "dark");
        return output;
    }

    private static string NormalizeOption(string value, string fallback, params string[] allowed)
    {
        return allowed.FirstOrDefault(item => item.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? fallback;
    }
}
