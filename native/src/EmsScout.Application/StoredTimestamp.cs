using System.Globalization;

namespace EmsScout.Application;

/// <summary>
/// Parses timestamps persisted by EMS Scout. New data stores the local
/// computer time with its explicit offset; legacy values without an offset
/// are interpreted as UTC exactly once for compatibility.
/// </summary>
public static class StoredTimestamp
{
    public static string FormatLocal(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("O", CultureInfo.InvariantCulture);

    public static bool TryParse(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return !string.IsNullOrWhiteSpace(value) &&
            DateTimeOffset.TryParse(
                value.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces |
                DateTimeStyles.AssumeUniversal |
                DateTimeStyles.AdjustToUniversal,
                out timestamp);
    }
}
