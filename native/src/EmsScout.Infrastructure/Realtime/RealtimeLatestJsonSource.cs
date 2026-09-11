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
        return await LoadAsync(buildings, expectedRunId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RealtimeDetailSet> LoadAsync(
        IReadOnlyList<string> buildings,
        long? expectedRunId,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<RealtimeDetailRecord>();
        long? sourceRunId = null;
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

            var fileRunId = ReadRunId(document.RootElement);
            if (expectedRunId is not null && fileRunId != expectedRunId)
            {
                var actual = fileRunId is null ? "缺失" : $"#{fileRunId}";
                return new RealtimeDetailSet(
                    [],
                    RealtimeDetailAvailability.MissingSnapshot,
                    $"实时详情批次不匹配：当前批次 #{expectedRunId}，文件批次 {actual}。请重新采集实时详情。",
                    fileRunId);
            }

            sourceRunId ??= fileRunId;
            if (sourceRunId != fileRunId)
            {
                return new RealtimeDetailSet(
                    [],
                    RealtimeDetailAvailability.Unavailable,
                    "实时详情文件来自多个批次，无法安全合并。请重新采集实时详情。",
                    null);
            }

            var updatedAt = ReadSourceUpdatedAt(document.RootElement, file);
            var sourceFile = Path.GetRelativePath(rootPath, file);
            foreach (var row in ReadRows(document.RootElement, building, sourceFile, updatedAt))
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(row);
            }
        }

        return new RealtimeDetailSet(rows, RealtimeDetailAvailability.Available, null, sourceRunId);
    }

    internal static long? ReadRunId(JsonElement root)
    {
        foreach (var propertyName in new[] { "runId", "run_id" })
        {
            if (root.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.Number &&
                property.TryGetInt64(out var runId) && runId > 0)
            {
                return runId;
            }

            if (root.TryGetProperty(propertyName, out property) &&
                property.ValueKind == JsonValueKind.String &&
                long.TryParse(property.GetString(), out var stringRunId) && stringRunId > 0)
            {
                return stringRunId;
            }
        }

        return null;
    }

    private static bool TryReadTimestamp(JsonElement element, string propertyName, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return DateTimeOffset.TryParse(value.GetString(), out timestamp);
    }

    private string LatestRealtimeFile(string building)
    {
        return FindLatestFile(OutDirectoryResolver(), building);
    }

    internal static string FindLatestFile(string outDirectory, string building)
    {
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

    internal static DateTimeOffset ReadSourceUpdatedAt(JsonElement root, string file)
    {
        foreach (var propertyName in new[] { "capturedAt", "completedAt", "collectedAt", "updatedAt" })
        {
            if (TryReadTimestamp(root, propertyName, out var timestamp))
            {
                return timestamp;
            }
        }

        if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "capturedAt", "completedAt", "generatedAt" })
            {
                if (TryReadTimestamp(summary, propertyName, out var timestamp))
                {
                    return timestamp;
                }
            }
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
    }

    internal static IReadOnlyList<RealtimeDetailRecord> ReadRows(
        JsonElement root,
        string fallbackBuilding,
        string sourceFile,
        DateTimeOffset updatedAt)
    {
        if (!root.TryGetProperty("rows", out var jsonRows) || jsonRows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<RealtimeDetailRecord>();
        var index = 0;
        foreach (var row in jsonRows.EnumerateArray())
        {
            rows.Add(ReadRow(row, fallbackBuilding, sourceFile, updatedAt, index));
            index++;
        }

        return rows;
    }

    internal static RealtimeDetailRecord ReadRow(
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
