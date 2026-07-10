using System.Globalization;

namespace EmsScout.Application.Devices;

public static class RealtimeKeyBuilder
{
    public static string ExactKey(RealtimeDetailRecord record)
    {
        return ExactKey(record.Building, record.Floor, record.SubArea, record.PageName, record.Name);
    }

    public static string ExactKey(string building, double? floor, string subArea, string pageName, string name)
    {
        var buildingKey = KeyPart(building);
        var nameKey = KeyPart(name);
        if (string.IsNullOrWhiteSpace(buildingKey) || string.IsNullOrWhiteSpace(nameKey))
        {
            return string.Empty;
        }

        return $"{buildingKey}|{PageKey(floor, subArea, pageName)}|{nameKey}";
    }

    public static string NameKey(string building, string name)
    {
        var buildingKey = KeyPart(building);
        var nameKey = KeyPart(name);
        return string.IsNullOrWhiteSpace(buildingKey) || string.IsNullOrWhiteSpace(nameKey)
            ? string.Empty
            : $"{buildingKey}|{nameKey}";
    }

    public static string NameFloorKey(string building, double? floor, string subArea, string name)
    {
        var buildingKey = KeyPart(building);
        var floorKey = KeyPart(DeviceFloorLabelFormatter.Format(floor, subArea));
        var nameKey = KeyPart(name);
        return string.IsNullOrWhiteSpace(buildingKey) ||
               string.IsNullOrWhiteSpace(floorKey) ||
               string.IsNullOrWhiteSpace(nameKey)
            ? string.Empty
            : $"{buildingKey}|{floorKey}|{nameKey}";
    }

    private static string PageKey(double? floor, string subArea, string pageName)
    {
        var floorKey = FloorKey(floor);
        var subAreaKey = KeyPart(string.IsNullOrWhiteSpace(subArea) ? floorKey : subArea);
        var pageKey = KeyPart(string.IsNullOrWhiteSpace(pageName) ? "default" : pageName);
        return $"{floorKey}|{subAreaKey}|{pageKey}";
    }

    private static string FloorKey(double? floor)
    {
        if (floor is null)
        {
            return string.Empty;
        }

        return floor.Value.ToString("0.########", CultureInfo.InvariantCulture).ToUpperInvariant() + "F";
    }

    private static string KeyPart(string value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }
}
