namespace EmsScout.Application.Devices;

public static class DeviceAreaClassifier
{
    public const string PublicArea = "公区";
    public const string PrivateArea = "非公区";

    private static readonly string[] PublicKeywords =
    [
        "GQ",
        "WSJ",
        "DTT",
        "FDT",
        "XFDT",
        "CSJ",
        "FWJ",
        "ZBS",
        "ZSG",
        "MD",
        "RDJHJF",
    ];

    public static string Classify(string? name, string? layout)
    {
        if (string.Equals(layout?.Trim(), "group", StringComparison.OrdinalIgnoreCase))
        {
            return PublicArea;
        }

        var deviceName = (name ?? string.Empty).Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(deviceName, "^QL-\\d"))
        {
            return PrivateArea;
        }

        return PublicKeywords.Any(keyword => deviceName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            ? PublicArea
            : PrivateArea;
    }
}
