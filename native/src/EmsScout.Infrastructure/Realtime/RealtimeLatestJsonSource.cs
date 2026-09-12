using System.Text.Json;
using EmsScout.Application;
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
        return await LoadCoreAsync(buildings, expectedRunId: null, requireBatchMetadata: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RealtimeDetailSet> LoadAsync(
        IReadOnlyList<string> buildings,
        long? expectedRunId,
        CancellationToken cancellationToken = default)
    {
        return await LoadCoreAsync(buildings, expectedRunId, requireBatchMetadata: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RealtimeDetailSet> LoadCoreAsync(
        IReadOnlyList<string> buildings,
        long? expectedRunId,
        bool requireBatchMetadata,
        CancellationToken cancellationToken)
    {
        var requestedBuildings = buildings
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rows = new List<RealtimeDetailRecord>();
        long? sourceRunId = null;
        foreach (var building in requestedBuildings)
        {
            var file = LatestRealtimeFile(building);
            if (string.IsNullOrWhiteSpace(file))
            {
                return new RealtimeDetailSet(
                    [],
                    RealtimeDetailAvailability.MissingSnapshot,
                    $"缺少 {building} 的实时详情文件，请重新采集实时详情。",
                    sourceRunId);
            }

            JsonDocument document;
            try
            {
                await using var stream = File.OpenRead(file);
                document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return new RealtimeDetailSet(
                    [],
                    RealtimeDetailAvailability.Unavailable,
                    $"实时详情文件 {Path.GetFileName(file)} 无法读取：{ex.Message}",
                    sourceRunId);
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("rows", out var jsonRows) ||
                    jsonRows.ValueKind != JsonValueKind.Array)
                {
                    return new RealtimeDetailSet([], RealtimeDetailAvailability.MissingSnapshot,
                        $"{building} 的实时详情文件缺少 rows 数组，请重新采集实时详情。", sourceRunId);
                }

                var fileRunId = ReadRunId(document.RootElement);
                if (fileRunId is null && requireBatchMetadata)
                {
                    return new RealtimeDetailSet([], RealtimeDetailAvailability.MissingSnapshot,
                        $"{building} 的实时详情缺少批次号，无法安全绑定当前数据。", null);
                }

                if (expectedRunId is not null && fileRunId != expectedRunId)
                {
                    var actual = $"#{fileRunId}";
                    return new RealtimeDetailSet(
                        [],
                        RealtimeDetailAvailability.MissingSnapshot,
                        $"实时详情批次不匹配：当前批次 #{expectedRunId}，文件批次 {actual}。请重新采集实时详情。",
                        fileRunId);
                }

                if (sourceRunId is not null && sourceRunId != fileRunId)
                {
                    return new RealtimeDetailSet(
                        [],
                        RealtimeDetailAvailability.Unavailable,
                        "实时详情文件来自多个批次，无法安全合并。请重新采集实时详情。",
                        null);
                }

                if (requireBatchMetadata && !TryReadSourceTimestamp(document.RootElement, out _))
                {
                    return new RealtimeDetailSet([], RealtimeDetailAvailability.MissingSnapshot,
                        $"{building} 的实时详情缺少采集时间，无法验证数据新鲜度。", fileRunId);
                }

                var updatedAt = ReadSourceUpdatedAt(document.RootElement, file);
                IReadOnlyList<RealtimeDetailRecord> fileRows;
                try
                {
                    var sourceFile = Path.GetRelativePath(rootPath, file);
                    fileRows = ReadRows(document.RootElement, building, sourceFile, updatedAt);
                }
                catch (InvalidDataException ex)
                {
                    return new RealtimeDetailSet([], RealtimeDetailAvailability.Unavailable, ex.Message, fileRunId);
                }

                if (fileRows.Count == 0)
                {
                    return new RealtimeDetailSet([], RealtimeDetailAvailability.MissingSnapshot,
                        $"{building} 的实时详情 rows 为空，请重新采集实时详情。", fileRunId);
                }

                sourceRunId ??= fileRunId;
                foreach (var row in fileRows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rows.Add(row);
                }
            }
        }

        return requestedBuildings.Length == 0
            ? new RealtimeDetailSet([], RealtimeDetailAvailability.MissingSnapshot, "未指定实时详情楼栋。")
            : new RealtimeDetailSet(rows, RealtimeDetailAvailability.Available, null, sourceRunId);
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

        return StoredTimestamp.TryParse(value.GetString(), out timestamp);
    }

    private string LatestRealtimeFile(string building)
    {
        return FindLatestFile(OutDirectoryResolver(), building);
    }

    internal static string FindLatestFile(string outDirectory, string building)
    {
        if (!Directory.Exists(outDirectory))
        {
            return string.Empty;
        }

        var paths = Directory.EnumerateFiles(outDirectory, $"realtime_{building}_*.json")
            .Where(path => Path.GetFileName(path).Contains("_batch_", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetFileName(path).EndsWith("_latest.json", StringComparison.OrdinalIgnoreCase) ||
                           System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), $"^realtime_{System.Text.RegularExpressions.Regex.Escape(building)}_\\d{{8}}_\\d{{6}}\\.json$"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ReadCandidate)
            .OrderByDescending(item => item.HasUsableMetadata)
            .ThenByDescending(item => item.CapturedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(item => item.LastWriteUtc)
            .FirstOrDefault();

        return paths?.Path ?? string.Empty;
    }

    private static RealtimeFileCandidate ReadCandidate(string path)
    {
        var info = new FileInfo(path);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var hasRows = root.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array;
            var runId = ReadRunId(root);
            var hasTimestamp = TryReadSourceTimestamp(root, out var capturedAt);
            return new RealtimeFileCandidate(path, hasRows && runId is not null && hasTimestamp, hasTimestamp ? capturedAt : null, info.LastWriteTimeUtc);
        }
        catch
        {
            return new RealtimeFileCandidate(path, false, null, info.LastWriteTimeUtc);
        }
    }

    private sealed record RealtimeFileCandidate(
        string Path,
        bool HasUsableMetadata,
        DateTimeOffset? CapturedAt,
        DateTime LastWriteUtc);

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

    internal static bool HasSourceTimestamp(JsonElement root) => TryReadSourceTimestamp(root, out _);

    private static bool TryReadSourceTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        foreach (var propertyName in new[] { "capturedAt", "completedAt", "collectedAt", "updatedAt" })
        {
            if (TryReadTimestamp(root, propertyName, out timestamp))
            {
                return true;
            }
        }

        if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "capturedAt", "completedAt", "generatedAt" })
            {
                if (TryReadTimestamp(summary, propertyName, out timestamp))
                {
                    return true;
                }
            }
        }

        timestamp = default;
        return false;
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
        var rawFields = ReadStringDictionary(row, "rawFields");
        var validFields = ReadBoolDictionary(row, "validFields");
        var building = ReadString(row, "building");
        if (!string.IsNullOrWhiteSpace(building) &&
            !string.Equals(building, fallbackBuilding, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"实时详情文件 {sourceFile} 的第 {index + 1} 行楼栋为“{building}”，与目标楼栋“{fallbackBuilding}”不一致。文件已拒绝。 ");
        }

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
            ValidFields: validFields,
            RawFields: rawFields);
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
