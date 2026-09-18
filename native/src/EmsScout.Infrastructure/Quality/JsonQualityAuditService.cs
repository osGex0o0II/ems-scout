using System.Text.Json;
using EmsScout.Application.Quality;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Quality;

public sealed class JsonQualityAuditService(
    Func<string> qualityOutputDirectoryResolver,
    Func<string> databasePathResolver) : IQualityAuditService
{
    public async Task<QualityAuditReport?> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        return await LoadAsync(null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QualityAuditReport?> LoadForRunAsync(
        long runId,
        CancellationToken cancellationToken = default)
    {
        return await LoadAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<QualityAuditReport?> LoadAsync(
        long? runId,
        CancellationToken cancellationToken)
    {
        var outputDirectory = qualityOutputDirectoryResolver();
        var path = runId.HasValue
            ? Path.Combine(outputDirectory, $"quality_report_run{runId.Value}.json")
            : Path.Combine(outputDirectory, "quality_report.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var summary = ReadSummary(root);
        var issues = ReadIssues(root);
        var generatedAt = ReadString(root, "generated_at");
        var generatedAtLocal = ReadString(root, "generated_at_local");
        var reportRunId = ReadNullableInt64(root, "run_id");
        var reportBatchUid = ReadString(root, "batch_uid", "batchUid");
        var reportRunKey = ReadString(root, "run_key", "runKey");
        var reportTime = File.GetLastWriteTimeUtc(path);
        var databasePath = databasePathResolver();
        var databaseTime = File.Exists(databasePath)
            ? File.GetLastWriteTimeUtc(databasePath)
            : DateTime.MinValue;
        var reasons = new List<string>();
        if (databaseTime > reportTime.AddSeconds(2))
        {
            reasons.Add($"质量报告早于当前数据库：报告 {reportTime:yyyy-MM-dd HH:mm:ss} UTC，数据库 {databaseTime:yyyy-MM-dd HH:mm:ss} UTC");
        }

        if (runId.HasValue && reportRunId != runId)
        {
            reasons.Add($"报告批次 #{reportRunId?.ToString() ?? "缺失"} 与所选批次 #{runId} 不一致");
        }

        if (!reportRunId.HasValue)
        {
            reasons.Add("质量报告缺少 run_id，批次身份无法验证");
        }

        if (string.IsNullOrWhiteSpace(reportBatchUid))
        {
            reasons.Add("质量报告缺少 batch_uid，批次身份无法验证");
        }

        if (string.IsNullOrWhiteSpace(reportRunKey))
        {
            reasons.Add("质量报告缺少 run_key，批次身份无法验证");
        }

        var identity = await LoadRunIdentityAsync(
            databasePath,
            runId ?? reportRunId,
            cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            reasons.Add("SQLite 中不存在可核对的批次身份");
        }
        else
        {
            if (reportRunId != identity.Value.RunId)
            {
                reasons.Add($"报告 run_id #{reportRunId?.ToString() ?? "缺失"} 与 SQLite 批次 #{identity.Value.RunId} 不一致");
            }

            if (!string.Equals(reportBatchUid, identity.Value.BatchUid, StringComparison.Ordinal))
            {
                reasons.Add("报告 batch_uid 与 SQLite 批次身份不一致");
            }

            if (!string.Equals(reportRunKey, identity.Value.RunKey, StringComparison.Ordinal))
            {
                reasons.Add("报告 run_key 与 SQLite 批次身份不一致");
            }
        }

        return new QualityAuditReport(
            SourcePath: path,
            GeneratedAt: generatedAt,
            GeneratedAtLocal: generatedAtLocal,
            RunId: reportRunId,
            Summary: summary,
            Issues: issues,
            IsStale: reasons.Count > 0,
            StaleReason: string.Join("；", reasons),
            BatchUid: reportBatchUid,
            RunKey: reportRunKey);
    }

    private static QualityAuditSummary ReadSummary(JsonElement root)
    {
        if (!root.TryGetProperty("summary", out var summary))
        {
            return new QualityAuditSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        return new QualityAuditSummary(
            TotalCards: ReadInt(summary, "total_cards"),
            IssueCount: ReadInt(summary, "issue_count"),
            PlaceholderCards: ReadInt(summary, "placeholder_cards"),
            StateMismatch: ReadInt(summary, "state_mismatch"),
            UnknownCommunication: ReadInt(summary, "unknown_comm"),
            MissingIndicator: ReadInt(summary, "missing_indicator"),
            UnknownSwitch: ReadInt(summary, "unknown_switch"),
            DuplicateCardsSamePage: ReadInt(summary, "duplicate_cards_same_page"),
            DuplicateRenderedPages: ReadInt(summary, "duplicate_rendered_pages"),
            EmptySubAreas: ReadInt(summary, "empty_sub_areas"),
            InlineSubAreas: ReadInt(summary, "inline_sub_areas"),
            SuspiciousUniformPages: ReadInt(summary, "suspicious_uniform_pages"),
            UniformResolvedPages: ReadInt(summary, "uniform_resolved_pages"));
    }

    private static IReadOnlyList<QualityAuditIssue> ReadIssues(JsonElement root)
    {
        if (!root.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<QualityAuditIssue>();
        foreach (var issue in issues.EnumerateArray())
        {
            rows.Add(new QualityAuditIssue(
                Severity: ReadString(issue, "severity"),
                Code: ReadString(issue, "code"),
                Count: ReadInt(issue, "count"),
                Message: ReadString(issue, "message")));
        }

        return rows;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return value;
        }

        return int.TryParse(property.ToString(), out var parsed) ? parsed : 0;
    }

    private static long? ReadNullableInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static string ReadString(JsonElement element, params string[] propertyNames)
    {
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
