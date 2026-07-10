using System.Globalization;
using System.Text.Json;
using EmsScout.Application.Quality;

namespace EmsScout.Infrastructure.Quality;

public sealed class JsonRealtimeQualityAuditService(Func<string> qualityOutputDirectoryResolver) : IRealtimeQualityAuditService
{
    public async Task<RealtimeQualityAuditReport?> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        var directory = qualityOutputDirectoryResolver();
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var file = Directory.EnumerateFiles(directory, "realtime_quality_classified_*.json")
            .Select(path => new FileInfo(path))
            .OrderByDescending(fileInfo => fileInfo.LastWriteTimeUtc)
            .FirstOrDefault();
        if (file is null)
        {
            return null;
        }

        await using var stream = file.OpenRead();
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var collectionErrors = root.TryGetProperty("collectionErrors", out var collectionElement)
            ? collectionElement
            : default;
        var deviceAnomalies = root.TryGetProperty("deviceAnomalies", out var anomalyElement)
            ? anomalyElement
            : default;
        var conclusion = root.TryGetProperty("conclusion", out var conclusionElement)
            ? conclusionElement
            : default;

        return new RealtimeQualityAuditReport(
            SourcePath: file.FullName,
            CreatedAt: ReadString(root, "createdAt"),
            SummarySource: ReadSummarySource(root),
            TotalRows: ReadInt(root, "totalRows"),
            UniqueDevices: ReadInt(root, "uniqueDevices"),
            CollectionOk: ReadBool(conclusion, "collectionOk"),
            CollectionErrorCount: ReadInt(collectionErrors, "count"),
            DeviceAnomalyRows: ReadInt(deviceAnomalies, "rowCount"),
            DeviceAnomalyEvents: ReadInt(deviceAnomalies, "eventCount"),
            CollectionErrorCategories: ReadCategories(collectionErrors, "byCategory"),
            DeviceAnomalyCategories: ReadCategories(deviceAnomalies, "byCategory"),
            Buildings: ReadBuildings(root),
            Note: ReadString(conclusion, "note"));
    }

    private static string ReadSummarySource(JsonElement root)
    {
        if (!root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return ReadString(input, "summaryFile");
    }

    private static IReadOnlyList<RealtimeQualityCategory> ReadCategories(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var categories) ||
            categories.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return categories
            .EnumerateObject()
            .Select(item => new RealtimeQualityCategory(item.Name, CategoryLabel(item.Name), ReadInt(item.Value)))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<RealtimeQualityBuilding> ReadBuildings(JsonElement root)
    {
        if (!root.TryGetProperty("byBuilding", out var byBuilding) || byBuilding.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var rows = new List<RealtimeQualityBuilding>();
        foreach (var building in byBuilding.EnumerateObject())
        {
            var item = building.Value;
            var categories = item.TryGetProperty("deviceAnomalyCategories", out var categoryElement)
                ? categoryElement
                : default;
            rows.Add(new RealtimeQualityBuilding(
                Building: building.Name,
                Rows: ReadInt(item, "rows"),
                CollectionErrors: ReadInt(item, "collectionErrors"),
                DeviceAnomalyRows: ReadInt(item, "deviceAnomalyRows"),
                DeviceAnomalyEvents: ReadInt(item, "deviceAnomalyEvents"),
                InvalidRealtimeTags: ReadInt(categories, "invalidRealtimeTags"),
                InvalidEnum: ReadInt(categories, "invalidEnum"),
                OutOfRange: ReadInt(categories, "outOfRange"),
                InvalidLock: ReadInt(categories, "invalidLock")));
        }

        return rows
            .OrderBy(item => item.Building, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CategoryLabel(string code)
    {
        return code switch
        {
            "summaryFailed" => "采集失败",
            "summaryDefaultLike" => "默认模板",
            "rowCountMismatch" => "行数不一致",
            "missingMetadata" => "缺设备标识",
            "duplicateDevId" => "DevId 重复",
            "rowError" => "行错误",
            "defaultLike" => "默认值疑似",
            "fieldCount" => "字段数异常",
            "realtimeTagCount" => "点位数异常",
            "missingRequiredField" => "缺必需字段",
            "invalidRealtimeTags" => "有效点位为 0",
            "partialRealtimeTags" => "有效点位不全",
            "invalidEnum" => "枚举异常",
            "outOfRange" => "范围异常",
            "invalidLock" => "集控异常",
            _ => code,
        };
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return ReadInt(property);
    }

    private static int ReadInt(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
        {
            return value;
        }

        return int.TryParse(element.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
               property.GetBoolean();
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.ToString();
    }
}
