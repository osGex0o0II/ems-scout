using System.Globalization;
using System.Text.Json;
using EmsScout.Application.Quality;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Quality;

public sealed class JsonRealtimeQualityAuditService(
    Func<string> qualityOutputDirectoryResolver,
    Func<string>? databasePathResolver = null) : IRealtimeQualityAuditService
{
    public async Task<RealtimeQualityAuditReport?> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        var directory = qualityOutputDirectoryResolver();
        var file = EnumerateAuditFiles(directory).FirstOrDefault();
        return file is null ? null : await LoadFileAsync(file, expectedRunId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RealtimeQualityAuditReport?> LoadForRunAsync(
        long runId,
        CancellationToken cancellationToken = default)
    {
        var directory = qualityOutputDirectoryResolver();
        foreach (var file in EnumerateAuditFiles(directory))
        {
            var report = await LoadFileAsync(file, runId, cancellationToken).ConfigureAwait(false);
            if (report is not null)
            {
                return report;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAuditFiles(string directory)
    {
        return !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory, "realtime_quality_classified_*.json")
                .Select(path => new FileInfo(path))
                .OrderByDescending(fileInfo => fileInfo.LastWriteTimeUtc)
                .Select(fileInfo => fileInfo.FullName);
    }

    private async Task<RealtimeQualityAuditReport?> LoadFileAsync(
        string file,
        long? expectedRunId,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var reportRunId = ReadNullableInt64(root, "runId");
        if (expectedRunId is not null && reportRunId != expectedRunId)
        {
            return null;
        }
        var reportBatchUid = ReadString(root, "batchUid", "batch_uid");
        var reportRunKey = ReadString(root, "runKey", "run_key");
        var staleReasons = new List<string>();
        if (databasePathResolver is not null)
        {
            if (!reportRunId.HasValue || string.IsNullOrWhiteSpace(reportBatchUid) || string.IsNullOrWhiteSpace(reportRunKey))
            {
                staleReasons.Add("实时审计报告缺少完整批次身份");
            }

            var identity = await LoadRunIdentityAsync(
                databasePathResolver(),
                expectedRunId ?? reportRunId,
                cancellationToken).ConfigureAwait(false);
            if (identity is null)
            {
                staleReasons.Add("SQLite 中不存在可核对的批次身份");
            }
            else
            {
                if (reportRunId != identity.Value.RunId)
                {
                    staleReasons.Add("实时审计报告 runId 与 SQLite 批次身份不一致");
                }

                if (!string.Equals(reportBatchUid, identity.Value.BatchUid, StringComparison.Ordinal))
                {
                    staleReasons.Add("实时审计报告 batchUid 与 SQLite 批次身份不一致");
                }

                if (!string.Equals(reportRunKey, identity.Value.RunKey, StringComparison.Ordinal))
                {
                    staleReasons.Add("实时审计报告 runKey 与 SQLite 批次身份不一致");
                }
            }
        }
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
            SourcePath: file,
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
            Note: ReadString(conclusion, "note"),
            RunId: reportRunId,
            IsStale: staleReasons.Count > 0,
            StaleReason: string.Join("；", staleReasons),
            BatchUid: reportBatchUid,
            RunKey: reportRunKey);
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

    private static string ReadString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            return property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : property.ToString();
        }

        return string.Empty;
    }

    private static long? ReadNullableInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number > 0 ? number : null;
        }

        return long.TryParse(property.ToString(), out var textValue) && textValue > 0
            ? textValue
            : null;
    }

    private static async Task<RunIdentity?> LoadRunIdentityAsync(
        string databasePath,
        long? runId,
        CancellationToken cancellationToken)
    {
        if (!runId.HasValue || !File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, NULLIF(TRIM(batch_uid), ''), NULLIF(TRIM(run_key), '') FROM collection_runs WHERE id = $id LIMIT 1";
            command.Parameters.AddWithValue("$id", runId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.IsDBNull(1) || reader.IsDBNull(2))
            {
                return null;
            }

            return new RunIdentity(reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private readonly record struct RunIdentity(long RunId, string BatchUid, string RunKey);
}
