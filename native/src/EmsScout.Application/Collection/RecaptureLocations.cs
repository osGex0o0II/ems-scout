using System.Globalization;

namespace EmsScout.Application.Collection;

public sealed record RecaptureLocation(
    string Building,
    string Seat,
    string FloorLabel,
    double Floor,
    int X,
    int Y);

public sealed record RecaptureLocationOption(string Value, string Label);

public interface IRecaptureLocationSource
{
    Task<IReadOnlyList<RecaptureLocation>> LoadAsync(CancellationToken cancellationToken = default);
}

public static class RecaptureLocationCatalog
{
    private static readonly string[] BuildingOrder = ["1号", "2号", "3号", "4号", "5号", "6号"];

    public static string ResolveSeat(string building, int x) => building switch
    {
        "5号" when x <= 400 => "A座",
        "5号" when x <= 616 => "B座",
        "5号" when x <= 874 => "C座",
        "5号" when x <= 1120 => "D座",
        "5号" when x <= 1424 => "E座",
        "5号" => "F座",
        "6号" when x <= 650 => "A座",
        "6号" when x <= 1220 => "B座",
        "6号" => "C座",
        _ => "整栋",
    };

    public static IReadOnlyList<RecaptureLocationOption> BuildingOptions(
        IReadOnlyList<RecaptureLocation> locations) => locations
        .Select(location => location.Building)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(BuildingIndex)
        .ThenBy(value => value, StringComparer.Ordinal)
        .Select(value => new RecaptureLocationOption(value, value + "楼"))
        .ToArray();

    public static IReadOnlyList<RecaptureLocationOption> SeatOptions(
        IReadOnlyList<RecaptureLocation> locations,
        string building)
    {
        var seats = locations
            .Where(location => location.Building == building)
            .Select(location => location.Seat)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (seats.Length == 1 && seats[0] == "整栋")
        {
            return [new("整栋", "整栋")];
        }

        return [new(string.Empty, "全部座号"), .. seats.Select(value => new RecaptureLocationOption(value, value))];
    }

    public static IReadOnlyList<RecaptureLocationOption> FloorOptions(
        IReadOnlyList<RecaptureLocation> locations,
        string building,
        string? seat)
    {
        var floors = locations
            .Where(location => location.Building == building &&
                               (string.IsNullOrEmpty(seat) || location.Seat == seat))
            .GroupBy(location => location.FloorLabel, StringComparer.Ordinal)
            .Select(group => new { Label = group.Key, Floor = group.Min(location => location.Floor) })
            .OrderBy(item => item.Floor)
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .Select(item => new RecaptureLocationOption(item.Label, item.Label))
            .ToArray();
        return [new(string.Empty, "全部楼层"), .. floors];
    }

    public static string BuildTargetArgument(
        IReadOnlyList<RecaptureLocation> locations,
        string? building,
        string? seat,
        string? floorLabel) => string.Join(",", locations
            .Where(location =>
                !string.IsNullOrWhiteSpace(building) &&
                location.Building == building &&
                (string.IsNullOrEmpty(seat) || location.Seat == seat) &&
                (string.IsNullOrEmpty(floorLabel) || location.FloorLabel == floorLabel))
            .DistinctBy(location => (location.Building, location.X, location.Y))
            .OrderBy(location => location.Floor)
            .ThenBy(location => location.X)
            .ThenBy(location => location.Y)
            .Select(location => string.Create(
                CultureInfo.InvariantCulture,
                $"{location.Building}:{location.X}:{location.Y}")));

    private static int BuildingIndex(string building)
    {
        var index = Array.IndexOf(BuildingOrder, building);
        return index < 0 ? int.MaxValue : index;
    }
}
