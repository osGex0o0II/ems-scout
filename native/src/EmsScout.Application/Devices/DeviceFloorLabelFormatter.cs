using System.Globalization;
using System.Text.RegularExpressions;

namespace EmsScout.Application.Devices;

public static class DeviceFloorLabelFormatter
{
    public static string Format(double? floor, string? subArea)
    {
        var raw = (subArea ?? string.Empty).Trim().ToUpperInvariant();
        if (raw == "BM")
        {
            return "BM";
        }

        var leading = Regex.Match(raw, @"^(?:B\d+(?:\.\d+)?F|\d+(?:\.\d+)?F)\b");
        if (leading.Success)
        {
            return Normalize(leading.Value);
        }

        var exact = Regex.Match(raw, @"\b(?:B\d+(?:\.\d+)?F|\d+(?:\.\d+)?F)\b");
        if (exact.Success)
        {
            return Normalize(exact.Value);
        }

        if (floor is null)
        {
            return string.IsNullOrWhiteSpace(raw) ? "--" : raw;
        }

        var absolute = Math.Abs(floor.Value).ToString("0.#", CultureInfo.InvariantCulture);
        return floor.Value < 0 ? $"B{absolute}F" : $"{absolute}F";
    }

    public static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }
}
