using System.Text.Json;

namespace EmsScout.Application.Collection;

public static class CollectionRunCompleteness
{
    public static readonly IReadOnlySet<string> BlockingQualityCodes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "empty_sub_areas",
            "placeholder_cards",
            "duplicate_cards_same_page",
            "duplicate_rendered_pages",
            "state_mismatch",
            "unknown_comm",
            "unknown_switch",
            "missing_indicator",
            "suspicious_uniform_pages",
            "invalid_card_fields",
            "active_field_incomplete_pages",
            "offline_template_without_stability",
            "offline_template_stable",
        };

    public static IReadOnlySet<string> RequiredBuildings { get; } =
        new HashSet<string>(["1号", "2号", "3号", "4号", "5号", "6号"], StringComparer.OrdinalIgnoreCase);

    public static bool IsCompleteFleetSnapshot(CollectionRunRecord run)
    {
        var declaredBuildings = run.Buildings
            .Where(building => !string.IsNullOrWhiteSpace(building))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) &&
            run.Scope.Equals("full", StringComparison.OrdinalIgnoreCase) &&
            !run.IsAnomaly &&
            declaredBuildings.SetEquals(RequiredBuildings) &&
            run.SnapshotCardCount == run.CardCount &&
            run.CardCount > 0 &&
            HasSelfConsistentBuildingCardCounts(run.BuildingCardCounts, declaredBuildings, run.CardCount) &&
            !HasBlockingQualityFailure(run.QualitySummary);
    }

    public static bool HasSelfConsistentBuildingCardCounts(
        IReadOnlyDictionary<string, int> counts,
        IReadOnlySet<string> declaredBuildings,
        int declaredCardCount)
    {
        return declaredCardCount > 0 &&
            counts.Count == declaredBuildings.Count &&
            counts.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(declaredBuildings) &&
            counts.Values.All(count => count > 0) &&
            counts.Values.Sum() == declaredCardCount;
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
        return StoredTimestamp.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    public static bool HasBlockingQualityFailure(string qualitySummary)
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
                        BlockingQualityCodes.Contains(code.GetString() ?? string.Empty) &&
                        (!issue.TryGetProperty("severity", out var severity) ||
                         !string.Equals(severity.GetString(), "INFO", StringComparison.OrdinalIgnoreCase)) &&
                        (!issue.TryGetProperty("count", out var count) ||
                         !count.TryGetInt32(out var issueCount) || issueCount > 0))
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
