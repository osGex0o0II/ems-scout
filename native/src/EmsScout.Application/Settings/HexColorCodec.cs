using System.Globalization;

namespace EmsScout.Application.Settings;

public readonly record struct RgbaColor(byte Alpha, byte Red, byte Green, byte Blue);

public static class HexColorCodec
{
    public static RgbaColor Decode(string? value, RgbaColor fallback)
    {
        var candidate = value?.Trim();
        if (candidate is null || (candidate.Length != 7 && candidate.Length != 9) || candidate[0] != '#')
        {
            return fallback;
        }

        var channelStart = candidate.Length == 9 ? 3 : 1;
        var alpha = (byte)255;
        if (!TryParseChannel(candidate, channelStart, out var red) ||
            !TryParseChannel(candidate, channelStart + 2, out var green) ||
            !TryParseChannel(candidate, channelStart + 4, out var blue) ||
            (candidate.Length == 9 && !TryParseChannel(candidate, 1, out alpha)))
        {
            return fallback;
        }

        return new RgbaColor(alpha, red, green, blue);
    }

    public static string Encode(RgbaColor color) =>
        $"#{color.Alpha:X2}{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    private static bool TryParseChannel(string value, int start, out byte channel) =>
        byte.TryParse(value.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out channel);
}
