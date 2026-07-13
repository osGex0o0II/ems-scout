using System.Text;

namespace EmsScout.Application.Settings;

public sealed record AppLaunchOptions(string? SettingsPathOverride)
{
    public const string SettingsPathArgumentPrefix = "--ui-validation-settings-base64=";

    public static AppLaunchOptions Parse(string? arguments)
    {
        var token = (arguments ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(item => item.StartsWith(SettingsPathArgumentPrefix, StringComparison.Ordinal));
        if (token is null)
        {
            return new AppLaunchOptions((string?)null);
        }

        var encodedPath = token[SettingsPathArgumentPrefix.Length..];
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            throw new InvalidDataException("The UI validation settings path is missing.");
        }

        try
        {
            var settingsPath = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPath));
            return new AppLaunchOptions(Path.GetFullPath(settingsPath));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("The UI validation settings path is invalid.", ex);
        }
    }
}
