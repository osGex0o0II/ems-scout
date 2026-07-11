using System.Text.Json;
using EmsScout.Application.Devices;

namespace EmsScout.Infrastructure.Realtime;

public sealed class RealtimeLatestJsonSource(string rootPath, string outDirectory) : IRealtimeDetailSource
{
    public RealtimeLatestJsonSource(string rootPath, Func<string> outDirectoryResolver)
        : this(rootPath, string.Empty)
    {
        OutDirectoryResolver = outDirectoryResolver;
    }

    private Func<string> OutDirectoryResolver { get; } = () => outDirectory;

    public async Task<RealtimeDetailSet> LoadAsync(IReadOnlyList<string> buildings, CancellationToken cancellationToken = default)
    {
        var rows = new List<RealtimeDetailRecord>();
        foreach (var building in buildings.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct())
        {
            var file = LatestRealtimeFile(building);
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            await using var stream = File.OpenRead(file);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("rows", out var jsonRows) ||
                jsonRows.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var updatedAt = new DateTimeOffset(File.GetLastWriteTime(file));
            var sourceFile = Path.GetRelativePath(rootPath, file);
            var index = 0;
            foreach (var row in jsonRows.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(ReadRow(row, building, sourceFile, updatedAt, index));
                index++;
            }
        }

        return new RealtimeDetailSet(rows);
    }

    private string LatestRealtimeFile(string building)
    {
        var outDirectory = OutDirectoryResolver();
        var latest = Path.Combine(outDirectory, $"realtime_{building}_latest.json");
        if (File.Exists(latest))
        {
            return latest;
        }

        if (!Directory.Exists(outDirectory))
        {
            return string.Empty;
        }

        return Directory.EnumerateFiles(outDirectory, $"realtime_{building}_*.json")
            .Where(path => Path.GetFileName(path).Contains("_batch_", StringComparison.OrdinalIgnoreCase) ||
                           System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), $"^realtime_{System.Text.RegularExpressions.Regex.Escape(building)}_\\d{{8}}_\\d{{6}}\\.json$"))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()
            ?.FullName ?? string.Empty;
    }

    private static RealtimeDetailRecord ReadRow(
        JsonElement row,
        string fallbackBuilding,
        string sourceFile,
        DateTimeOffset updatedAt,
        int index)
    {
        var fields = ReadStringDictionary(row, "fields");
        var validFields = ReadBoolDictionary(row, "validFields");
        var building = ReadString(row, "building");
        if (string.IsNullOrWhiteSpace(building))
        {
            building = fallbackBuilding;
        }

        return new RealtimeDetailRecord(
            RowId: $"{sourceFile}#{index}",
            SourceFile: sourceFile,
            SourceUpdatedAt: updatedAt,
            Building: building,
            Floor: ReadNullableDouble(row, "floor"),
            SubArea: ReadFirstNonEmptyString(row, "subAreaText", "sub_area"),
            PageName: ReadFirstNonEmptyString(row, ["pageName", "page_name", "tab"], "default"),
            Name: ReadString(row, "name"),
            DevId: ReadString(row, "devId"),
            MeterId: ReadString(row, "meterId"),
            RtuId: ReadString(row, "rtuId"),
            FieldCount: ReadInt(row, "fieldCount", fields.Count),
            RealtimeTagCount: ReadInt(row, "realtimeTagCount", 0),
            RealtimeValidTagCount: ReadInt(row, "realtimeValidTagCount", 0),
            DefaultLike: ReadBool(row, "defaultLike"),
            Error: ReadString(row, "error"),
            CardComm: ReadString(row, "cardComm", "card_comm"),
            CardSwitch: ReadString(row, "cardSwitch", "card_switch"),
            CardIndicator: ReadString(row, "cardIndicator", "card_indicator"),
            Fields: fields,
            ValidFields: validFields);
    }

    private static Dictionary<string, string> ReadStringDictionary(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return property.EnumerateObject()
            .ToDictionary(item => item.Name, item => ValueToString(item.Value));
    }

    private static Dictionary<string, bool> ReadBoolDictionary(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var values = new Dictionary<string, bool>();
        foreach (var item in property.EnumerateObject())
        {
            if (item.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                values[item.Name] = item.Value.GetBoolean();
            }
        }

        return values;
    }

    private static string ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property))
            {
                return ValueToString(property);
            }
        }

        return string.Empty;
    }

    private static string ReadFirstNonEmptyString(JsonElement element, params string[] propertyNames)
    {
        return ReadFirstNonEmptyString(element, propertyNames, fallback: string.Empty);
    }

    private static string ReadFirstNonEmptyString(JsonElement element, string[] propertyNames, string fallback)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            var value = ValueToString(property);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static int ReadInt(JsonElement element, string propertyName, int fallback)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        if (property.TryGetInt32(out var value))
        {
            return value;
        }

        return int.TryParse(ValueToString(property), out value) ? value : fallback;
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
               property.GetBoolean();
    }

    private static double? ReadNullableDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.TryGetDouble(out var value) ? value : null;
    }

    private static string ValueToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }
}
