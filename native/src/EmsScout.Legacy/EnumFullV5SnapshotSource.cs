using System.Globalization;
using System.Text.Json;
using EmsScout.Application;
using EmsScout.Domain;

namespace EmsScout.Legacy;

public sealed class EnumFullV5SnapshotSource(string path) : IInventorySnapshotSource
{
    public EnumFullV5SnapshotSource(Func<string> pathResolver)
        : this(string.Empty)
    {
        PathResolver = pathResolver;
    }

    private Func<string> PathResolver { get; } = () => path;

    public async Task<InventorySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = PathResolver();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Cannot find legacy enum_full_v5.json.", path);
        }

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var cards = new List<AirConditionerCard>();
        foreach (var building in document.RootElement.GetProperty("buildings").EnumerateArray())
        {
            var buildingName = ReadString(building, "building");
            foreach (var subArea in EnumerateArray(building, "subAreas"))
            {
                var subAreaText = ReadString(subArea, "text");
                var floor = ReadNullableInt32(subArea, "floor");
                foreach (var page in EnumerateArray(subArea, "pages"))
                {
                    var pageName = ReadString(page, "page");
                    foreach (var card in EnumerateArray(page, "cards"))
                    {
                        var comm = ReadString(card, "comm");
                        cards.Add(new AirConditionerCard(
                            Building: buildingName,
                            SubArea: subAreaText,
                            Floor: floor,
                            Page: pageName,
                            Name: ReadString(card, "name"),
                            SwitchState: ReadString(card, "switch"),
                            Mode: ReadString(card, "mode"),
                            IndoorTemperature: ReadNullableDouble(card, "indoor"),
                            SetTemperature: ReadNullableDouble(card, "setTemp"),
                            Fan: ReadString(card, "fan"),
                            Indicator: ReadString(card, "indicator"),
                            CommunicationState: DeviceCommunicationStateParser.Parse(comm)));
                    }
                }
            }
        }

        var updatedAt = new DateTimeOffset(File.GetLastWriteTime(path));
        return new InventorySnapshot(path, updatedAt, cards);
    }

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
            : [];
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static int? ReadNullableInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static double? ReadNullableDouble(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }
}
