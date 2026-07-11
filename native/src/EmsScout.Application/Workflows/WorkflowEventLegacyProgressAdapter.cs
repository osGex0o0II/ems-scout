using System.Text.Json;

namespace EmsScout.Application.Workflows;

public static class WorkflowEventLegacyProgressAdapter
{
    public const string Prefix = "[PROGRESS]";

    public static bool TryAdapt(
        string line,
        string workflowId,
        long sequence,
        DateTimeOffset timestamp,
        string fallbackStage,
        out WorkflowEventV1? workflowEvent)
    {
        workflowEvent = null;
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        if (!WorkflowEventParser.IsWorkflowId(workflowId))
        {
            throw new ArgumentException("Invalid workflowId.", nameof(workflowId));
        }
        if (!WorkflowEventParser.IsStage(fallbackStage))
        {
            throw new ArgumentException("Invalid fallback stage.", nameof(fallbackStage));
        }
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence must be at least 1.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line[Prefix.Length..].Trim(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var stage = TryString(root, "phase") is { } phase && WorkflowEventParser.IsStage(phase)
                ? phase
                : fallbackStage;
            var percent = TryDouble(root, "percent") ?? RatioPercent(root, "curSa", "totalSa") ??
                RatioPercent(root, "deviceDone", "deviceTotal");
            if (percent.HasValue) percent = Math.Clamp(percent.Value, 0, 100);

            var deviceCurrent = TryNonNegativeInt64(root, "deviceDone");
            var deviceTotal = TryNonNegativeInt64(root, "deviceTotal");
            var subAreaCurrent = TryNonNegativeInt64(root, "curSa");
            var subAreaTotal = TryNonNegativeInt64(root, "totalSa");
            var buildingCurrent = TryNonNegativeInt64(root, "buildingIndex");
            var buildingTotal = TryNonNegativeInt64(root, "buildingTotal");

            long? current;
            long? total;
            string? unit;
            if (deviceCurrent.HasValue || deviceTotal.HasValue)
            {
                current = deviceCurrent;
                total = deviceTotal;
                unit = "device";
            }
            else if (subAreaCurrent.HasValue || subAreaTotal.HasValue)
            {
                current = subAreaCurrent;
                total = subAreaTotal;
                unit = "sub_area";
            }
            else if (buildingCurrent.HasValue || buildingTotal.HasValue)
            {
                current = buildingCurrent;
                total = buildingTotal;
                unit = "building";
            }
            else
            {
                current = null;
                total = null;
                unit = null;
            }

            var progress = new WorkflowProgressV1(
                percent,
                TryString(root, "message"),
                current,
                total,
                unit,
                root.Clone());
            workflowEvent = new(
                WorkflowEventContractV1.Version,
                workflowId,
                sequence,
                timestamp.ToUniversalTime(),
                WorkflowEventType.Progress,
                stage,
                null,
                progress,
                null,
                null,
                null);
            return true;
        }
    }

    private static string? TryString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static double? TryDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDouble(out var value) ||
            !double.IsFinite(value))
        {
            return null;
        }
        return value;
    }

    private static long? TryNonNegativeInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out var value) ||
            value < 0)
        {
            return null;
        }
        return value;
    }

    private static double? RatioPercent(JsonElement root, string currentName, string totalName)
    {
        var current = TryDouble(root, currentName);
        var total = TryDouble(root, totalName);
        return current.HasValue && total is > 0
            ? current.Value / total.Value * 100
            : null;
    }
}
