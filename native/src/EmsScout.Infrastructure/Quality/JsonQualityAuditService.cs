using System.Text.Json;
using EmsScout.Application.Quality;

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

        return new QualityAuditReport(
            SourcePath: path,
            GeneratedAt: generatedAt,
            GeneratedAtLocal: generatedAtLocal,
            RunId: reportRunId,
            Summary: summary,
            Issues: issues,
            IsStale: reasons.Count > 0,
            StaleReason: string.Join("；", reasons));
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

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.ToString();
    }
}
