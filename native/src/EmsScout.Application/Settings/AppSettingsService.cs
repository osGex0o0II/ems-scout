using System.Text.Json;

namespace EmsScout.Application.Settings;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object sync = new();
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

    public AppSettings Current
    {
        get
        {
            lock (sync) return _current.Clone();
        }
    }

    public AppSettings Load()
    {
        lock (sync)
        {
            _current = LoadFromDisk();
            return _current.Clone();
        }
    }

    public void Save(AppSettings settings)
    {
        var normalized = Normalize(settings);
        lock (sync)
        {
            var fullPath = Path.GetFullPath(SettingsPath);
            var directory = Path.GetDirectoryName(fullPath)
                            ?? throw new InvalidOperationException("Settings path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
                File.Move(temporaryPath, fullPath, overwrite: true);
                _current = normalized;
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
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
            ? AppStorageDefaults.DataDirectory
            : output.DataDirectory.Trim();
        output.ExportDirectory = string.IsNullOrWhiteSpace(output.ExportDirectory)
            ? AppStorageDefaults.ExportDirectory
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
