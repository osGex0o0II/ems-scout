using System.Text.Json;
using EmsScout.Application.Quality;

namespace EmsScout.Infrastructure.Quality;

public sealed class JsonCollectionIssueService(
    Func<string> qualityOutputDirectoryResolver,
    Func<string> databasePathResolver) : ICollectionIssueService
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["placeholder_cards"] = "占位卡片",
        ["invalid_card_fields"] = "字段不完整",
        ["missing_indicator"] = "缺少 indicator",
        ["unknown_comm"] = "通讯状态未知",
        ["unknown_switch"] = "开关状态未知",
        ["offline_template_without_stability"] = "默认值疑似",
        ["offline_template_stable"] = "全离线待复核",
        ["realtime_device_anomaly"] = "实时设备异常",
    };

    public async Task<CollectionIssueReport> LoadForRunAsync(
        long runId,
        CancellationToken cancellationToken = default)
    {
        var records = new List<CollectionIssueRecord>();
        var warnings = new List<string>();
        var directory = qualityOutputDirectoryResolver();
        var batchIdentity = await LoadBatchIdentityAsync(runId, cancellationToken).ConfigureAwait(false);

        var qualityPath = Path.Combine(directory, $"quality_report_run{runId}.json");
        if (File.Exists(qualityPath))
        {
            await ReadQualityReportAsync(qualityPath, runId, batchIdentity, records, warnings, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            warnings.Add("未找到该批次质量报告");
        }

        var realtimeFiles = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "realtime_quality_classified_*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray()
            : [];
        var realtimeMatched = false;
        foreach (var path in realtimeFiles)
        {
            if (await ReadRealtimeReportAsync(path, runId, batchIdentity, records, warnings, cancellationToken).ConfigureAwait(false))
            {
                realtimeMatched = true;
                break;
            }
        }

        if (realtimeFiles.Length > 0 && !realtimeMatched)
        {
            warnings.Add("未找到与所选批次匹配的实时报告");
        }

        var deduplicated = records
            .GroupBy(BuildDeduplicationKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(record => SeverityRank(record.Severity))
            .ThenBy(record => record.Building, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Floor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.PageName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var categories = deduplicated
            .GroupBy(record => record.IssueType, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CollectionIssueCategory(
                group.Key,
                Labels.TryGetValue(group.Key, out var label) ? label : group.Key,
                group.MinBy(record => SeverityRank(record.Severity))?.Severity ?? "P2",
                group.Count()))
            .OrderBy(category => SeverityRank(category.Severity))
            .ThenByDescending(category => category.Count)
            .ThenBy(category => category.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CollectionIssueReport(runId, deduplicated, categories, warnings);
    }

    private async Task ReadQualityReportAsync(
        string path,
        long runId,
        BatchIdentity? batchIdentity,
        ICollection<CollectionIssueRecord> records,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (ReadNullableLong(root, "run_id") is { } reportRunId && reportRunId != runId)
            {
                warnings.Add($"质量报告批次 #{reportRunId} 与所选批次 #{runId} 不一致");
                return;
            }

            ReadDetailsObject(root, "details", "quality", runId, batchIdentity, path, records);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warnings.Add($"质量报告无法解析：{Path.GetFileName(path)} ({ex.Message})");
        }
    }

    private async Task<bool> ReadRealtimeReportAsync(
        string path,
        long runId,
        BatchIdentity? batchIdentity,
        ICollection<CollectionIssueRecord> records,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (ReadNullableLong(root, "runId", "run_id") != runId)
            {
                return false;
            }

            var before = records.Count;
            ReadDetailsObject(root, "details", "realtime", runId, batchIdentity, path, records);
            if (root.TryGetProperty("details", out _) == false)
            {
                ReadLegacyRealtimeRows(root, runId, batchIdentity, path, records);
            }

            return before != records.Count || root.TryGetProperty("details", out _);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warnings.Add($"实时报告无法解析：{Path.GetFileName(path)} ({ex.Message})");
            return false;
        }
    }

    private static void ReadDetailsObject(
        JsonElement root,
        string propertyName,
        string sourceKind,
        long runId,
        BatchIdentity? batchIdentity,
        string sourcePath,
        ICollection<CollectionIssueRecord> records)
    {
        if (!root.TryGetProperty(propertyName, out var details) || details.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in details.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var row in property.Value.EnumerateArray())
            {
                var issueType = property.Name switch
                {
                    "realtime_collection_errors" => $"realtime_{ReadString(row, "category")}".TrimEnd('_'),
                    "realtime_device_anomalies" => "realtime_device_anomaly",
                    _ => property.Name,
                };
                AddRecord(records, row, issueType, sourceKind, runId, batchIdentity, sourcePath);
            }
        }
    }

    private static void ReadLegacyRealtimeRows(
        JsonElement root,
        long runId,
        BatchIdentity? batchIdentity,
        string sourcePath,
        ICollection<CollectionIssueRecord> records)
    {
        if (root.TryGetProperty("collectionErrors", out var errors) &&
            errors.TryGetProperty("rows", out var errorRows) &&
            errorRows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in errorRows.EnumerateArray())
            {
                AddRecord(records, row, $"realtime_{ReadString(row, "category")}", "realtime", runId, batchIdentity, sourcePath);
            }
        }

        if (!root.TryGetProperty("deviceAnomalies", out var anomalies) ||
            !anomalies.TryGetProperty("rows", out var anomalyRows) ||
            anomalyRows.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var row in anomalyRows.EnumerateArray())
        {
            AddRecord(records, row, "realtime_device_anomaly", "realtime", runId, batchIdentity, sourcePath);
        }
    }

    private static void AddRecord(
        ICollection<CollectionIssueRecord> records,
        JsonElement row,
        string issueType,
        string sourceKind,
        long runId,
        BatchIdentity? batchIdentity,
        string sourcePath)
    {
        var issues = row.TryGetProperty("issues", out var issueValues) && issueValues.ValueKind == JsonValueKind.Array
            ? issueValues.EnumerateArray().Select(value => value.ToString()).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
            : [];
        var observed = row.TryGetProperty("fields", out var fields) ? fields.ToString() : ReadString(row, "observed_value", "observedValue");
        records.Add(new CollectionIssueRecord(
            BatchId: runId,
            BatchUid: batchIdentity?.BatchUid ?? ReadString(row, "batch_uid", "batchUid"),
            IssueType: ReadString(row, "issue_code", "issueCode") is { Length: > 0 } code ? code : issueType,
            Severity: ReadString(row, "severity") is { Length: > 0 } severity ? severity : sourceKind == "quality" ? "P2" : "P2",
            Building: ReadString(row, "building"),
            Floor: ReadString(row, "floor", "floorLabel", "floor_name"),
            Zone: ReadString(row, "zone", "sub_area", "subAreaText"),
            PageName: ReadString(row, "page_name", "pageName", "page"),
            DeviceName: ReadString(row, "device_name", "deviceName", "name"),
            DeviceId: ReadString(row, "device_id", "deviceId", "devId"),
            CollectedAt: ReadString(row, "collected_at", "collectedAt", "timestamp", "createdAt"),
            ObservedValue: observed,
            Evidence: issues.Length > 0 ? string.Join("；", issues) : ReadString(row, "evidence", "detail", "reason", "message"),
            CollectorDecision: ReadString(row, "collector_decision", "collectorDecision") is { Length: > 0 } decision ? decision : "需复核",
            Reason: ReadString(row, "reason", "message", "detail"),
            Attribution: ReadString(row, "attribution") is { Length: > 0 } attribution ? attribution : sourceKind == "realtime" ? "实时采集判断" : "采集判断",
            ResolutionState: ReadString(row, "resolution_state", "resolutionState") is { Length: > 0 } state ? state : "需复核",
            SourceArtifact: sourceKind,
            SourcePath: sourcePath));
    }

    private async Task<BatchIdentity?> LoadBatchIdentityAsync(long runId, CancellationToken cancellationToken)
    {
        var path = databasePathResolver();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT batch_uid, run_key FROM collection_runs WHERE id = $id LIMIT 1";
            command.Parameters.AddWithValue("$id", runId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new BatchIdentity(ReadNullableString(reader, 0), ReadNullableString(reader, 1))
                : null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    private static string BuildDeduplicationKey(CollectionIssueRecord record) =>
        string.Join("\u001f", record.BatchId, record.IssueType, record.Building, record.Floor, record.Zone, record.PageName, record.DeviceName, record.DeviceId, record.Evidence);

    private static int SeverityRank(string severity) => severity.ToUpperInvariant() switch
    {
        "P1" => 0,
        "P2" => 1,
        "P3" => 2,
        "INFO" => 3,
        _ => 4,
    };

    private static long? ReadNullableLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (long.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
        }

        return string.Empty;
    }

    private static string ReadNullableString(Microsoft.Data.Sqlite.SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? string.Empty : reader.GetString(index);

    private sealed record BatchIdentity(string BatchUid, string RunKey);
}
