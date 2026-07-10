using System.Globalization;

namespace EmsScout.Application.Devices;

public static class DeviceTemperatureRules
{
    public const string Any = "all";
    public const string AbnormalOrMissing = "abnormal";
    public const string Above28 = "above_28";
    public const string Below18 = "below_18";

    private const double TemperatureMin = 5;
    private const double TemperatureMax = 40;

    public static bool IsAbnormalOrMissing(string value)
    {
        return !TryRead(value, out var number) || number < TemperatureMin || number > TemperatureMax;
    }

    public static bool MatchesMode(string value, string? mode)
    {
        return mode?.Trim() switch
        {
            null or "" or Any => true,
            AbnormalOrMissing => IsAbnormalOrMissing(value),
            Above28 => TryRead(value, out var number) && number > 28,
            Below18 => TryRead(value, out var number) && number < 18,
            _ => true,
        };
    }

    public static bool TryRead(string value, out double number)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value ?? string.Empty, "-?\\d+(?:\\.\\d+)?");
        return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
}
