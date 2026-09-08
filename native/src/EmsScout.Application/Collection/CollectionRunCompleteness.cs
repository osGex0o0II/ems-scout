using System.Text.Json;

namespace EmsScout.Application.Collection;

public static class CollectionRunCompleteness
{
    private static readonly IReadOnlySet<string> BlockingQualityCodes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "empty_sub_areas",
            "placeholder_cards",
            "duplicate_cards_same_page",
            "duplicate_rendered_pages",
            "baseline_delta",
            "invalid_card_fields",
            "active_field_incomplete_pages",
            "offline_template_without_stability",
        };

    public static readonly IReadOnlySet<string> RequiredBuildings =
        new HashSet<string>(["1号", "2号", "3号", "4号", "5号", "6号"], StringComparer.OrdinalIgnoreCase);

    public static bool IsCompleteFleetSnapshot(CollectionRunRecord run)
    {
        var buildings = run.Buildings
            .Where(building => !string.IsNullOrWhiteSpace(building))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) &&
            run.Scope.Equals("full", StringComparison.OrdinalIgnoreCase) &&
            !run.IsAnomaly &&
            buildings.SetEquals(RequiredBuildings) &&
            run.CardCount > 0 &&
            run.SnapshotCardCount == run.CardCount &&
            !HasBlockingQualityFailure(run.QualitySummary);
    }

    public static IReadOnlyList<CollectionRunRecord> GetCompleteFleetSnapshots(
        IEnumerable<CollectionRunRecord> runs)
    {
        return runs
            .Where(IsCompleteFleetSnapshot)
            .OrderByDescending(run => ParseTimestamp(run.CompletedAt))
            .ThenByDescending(run => run.Id)
            .ToArray();
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    private static bool HasBlockingQualityFailure(string qualitySummary)
    {
        if (string.IsNullOrWhiteSpace(qualitySummary) || qualitySummary.Equals("{}", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(qualitySummary);
            if (document.RootElement.TryGetProperty("summary", out var summary) &&
                summary.ValueKind == JsonValueKind.Object)
            {
                foreach (var code in BlockingQualityCodes)
                {
                    if (summary.TryGetProperty(code, out var value) &&
                        value.TryGetInt32(out var count) && count > 0)
                    {
                        return true;
                    }
                }
            }

            if (document.RootElement.TryGetProperty("issues", out var issues) &&
                issues.ValueKind == JsonValueKind.Array)
            {
                foreach (var issue in issues.EnumerateArray())
                {
                    if (issue.TryGetProperty("code", out var code) &&
                        code.ValueKind == JsonValueKind.String &&
                        BlockingQualityCodes.Contains(code.GetString() ?? string.Empty))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
