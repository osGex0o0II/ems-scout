using System.Globalization;
using System.Text.Json;

namespace EmsScout.Application.Workflows;

public sealed class WorkflowEventParseException : FormatException
{
    public WorkflowEventParseException(string message)
        : base(message)
    {
    }

    public WorkflowEventParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class WorkflowEventParser
{
    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "contractVersion",
        "workflowId",
        "seq",
        "timestamp",
        "type",
        "stage",
        "message",
        "progress",
        "action",
        "outcome",
        "exitCode",
    };

    private static readonly HashSet<string> ProgressProperties = new(StringComparer.Ordinal)
    {
        "percent",
        "message",
        "current",
        "total",
        "unit",
        "data",
    };

    public static WorkflowEventV1 Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new WorkflowEventParseException("Workflow event line is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        }
        catch (JsonException error)
        {
            throw new WorkflowEventParseException("Workflow event is not valid JSON.", error);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new WorkflowEventParseException("Workflow event root must be an object.");
            }

            EnsureNoDuplicateProperties(root, "$", recursive: true);
            EnsureOnlyProperties(root, RootProperties, "$.");

            var contractVersion = RequiredString(root, "contractVersion");
            if (!contractVersion.Equals(WorkflowEventContractV1.Version, StringComparison.Ordinal))
            {
                throw new WorkflowEventParseException($"Unsupported workflow contractVersion '{contractVersion}'.");
            }

            var workflowId = RequiredString(root, "workflowId");
            EnsureWorkflowId(workflowId);
            var sequence = RequiredInt64(root, "seq");
            if (sequence < 1)
            {
                throw new WorkflowEventParseException("Workflow event seq must be at least 1.");
            }

            var timestampText = RequiredString(root, "timestamp");
            if (!HasUtcIsoShape(timestampText) ||
                !DateTimeOffset.TryParse(
                    timestampText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var timestamp) ||
                timestamp.Offset != TimeSpan.Zero)
            {
                throw new WorkflowEventParseException("Workflow event timestamp must be an ISO-8601 UTC timestamp.");
            }

            var type = ParseType(RequiredString(root, "type"));
            var stage = RequiredString(root, "stage");
            EnsureStage(stage);
            var message = OptionalString(root, "message");

            return type switch
            {
                WorkflowEventType.Started => ParseStarted(
                    contractVersion, workflowId, sequence, timestamp, stage, message, root),
                WorkflowEventType.Progress => ParseProgress(
                    contractVersion, workflowId, sequence, timestamp, stage, message, root),
                WorkflowEventType.Action => ParseAction(
                    contractVersion, workflowId, sequence, timestamp, stage, message, root),
                WorkflowEventType.Terminal => ParseTerminal(
                    contractVersion, workflowId, sequence, timestamp, stage, message, root),
                _ => throw new WorkflowEventParseException("Unknown workflow event type."),
            };
        }
    }

    internal static bool IsWorkflowId(string value) => IsIdentifier(value, 128, allowColon: true);

    internal static bool IsStage(string value) => IsIdentifier(value, 64, allowColon: false);

    private static WorkflowEventV1 ParseStarted(
        string contractVersion,
        string workflowId,
        long sequence,
        DateTimeOffset timestamp,
        string stage,
        string? message,
        JsonElement root)
    {
        RejectPresent(root, "started", "progress", "action", "outcome", "exitCode");
        return new(
            contractVersion,
            workflowId,
            sequence,
            timestamp,
            WorkflowEventType.Started,
            stage,
            message,
            null,
            null,
            null,
            null);
    }

    private static WorkflowEventV1 ParseProgress(
        string contractVersion,
        string workflowId,
        long sequence,
        DateTimeOffset timestamp,
        string stage,
        string? message,
        JsonElement root)
    {
        RejectPresent(root, "progress", "action", "outcome", "exitCode");
        if (!root.TryGetProperty("progress", out var progressElement) ||
            progressElement.ValueKind != JsonValueKind.Object)
        {
            throw new WorkflowEventParseException("A progress event requires an object progress property.");
        }

        EnsureOnlyProperties(progressElement, ProgressProperties, "$.progress.");
        if (!progressElement.EnumerateObject().Any())
        {
            throw new WorkflowEventParseException("Workflow progress must contain at least one property.");
        }

        var percent = OptionalDouble(progressElement, "percent");
        if (percent is < 0 or > 100)
        {
            throw new WorkflowEventParseException("Workflow progress percent must be between 0 and 100.");
        }

        var current = OptionalInt64(progressElement, "current");
        var total = OptionalInt64(progressElement, "total");
        if (current < 0 || total < 0)
        {
            throw new WorkflowEventParseException("Workflow progress counters cannot be negative.");
        }

        JsonElement? data = null;
        if (progressElement.TryGetProperty("data", out var dataElement))
        {
            if (dataElement.ValueKind != JsonValueKind.Object)
            {
                throw new WorkflowEventParseException("Workflow progress data must be an object.");
            }
            data = dataElement.Clone();
        }

        var unit = OptionalString(progressElement, "unit");
        if (unit?.Length > 64)
        {
            throw new WorkflowEventParseException("Workflow progress unit cannot exceed 64 characters.");
        }

        var progress = new WorkflowProgressV1(
            percent,
            OptionalString(progressElement, "message"),
            current,
            total,
            unit,
            data);
        return new(
            contractVersion,
            workflowId,
            sequence,
            timestamp,
            WorkflowEventType.Progress,
            stage,
            message,
            progress,
            null,
            null,
            null);
    }

    private static WorkflowEventV1 ParseAction(
        string contractVersion,
        string workflowId,
        long sequence,
        DateTimeOffset timestamp,
        string stage,
        string? message,
        JsonElement root)
    {
        RejectPresent(root, "action", "progress", "outcome", "exitCode");
        var action = RequiredString(root, "action");
        if (!IsIdentifier(action, 64, allowColon: false))
        {
            throw new WorkflowEventParseException("Workflow action must be a 1-64 character ASCII identifier.");
        }
        return new(
            contractVersion,
            workflowId,
            sequence,
            timestamp,
            WorkflowEventType.Action,
            stage,
            message,
            null,
            action,
            null,
            null);
    }

    private static WorkflowEventV1 ParseTerminal(
        string contractVersion,
        string workflowId,
        long sequence,
        DateTimeOffset timestamp,
        string stage,
        string? message,
        JsonElement root)
    {
        RejectPresent(root, "terminal", "progress", "action");
        var outcome = ParseOutcome(RequiredString(root, "outcome"));
        var exitCodeValue = RequiredInt64(root, "exitCode");
        if (exitCodeValue is < 0 or > 255)
        {
            throw new WorkflowEventParseException("Workflow terminal exitCode must be between 0 and 255.");
        }

        var exitCode = checked((int)exitCodeValue);
        if ((outcome == WorkflowTerminalOutcome.Succeeded) != (exitCode == 0))
        {
            throw new WorkflowEventParseException("Only the succeeded outcome may use exitCode 0.");
        }

        return new(
            contractVersion,
            workflowId,
            sequence,
            timestamp,
            WorkflowEventType.Terminal,
            stage,
            message,
            null,
            null,
            outcome,
            exitCode);
    }

    private static WorkflowEventType ParseType(string value) => value switch
    {
        "started" => WorkflowEventType.Started,
        "progress" => WorkflowEventType.Progress,
        "action" => WorkflowEventType.Action,
        "terminal" => WorkflowEventType.Terminal,
        _ => throw new WorkflowEventParseException($"Unknown workflow event type '{value}'."),
    };

    private static WorkflowTerminalOutcome ParseOutcome(string value) => value switch
    {
        "succeeded" => WorkflowTerminalOutcome.Succeeded,
        "succeeded_with_findings" => WorkflowTerminalOutcome.SucceededWithFindings,
        "rejected" => WorkflowTerminalOutcome.Rejected,
        "auth_required" => WorkflowTerminalOutcome.AuthRequired,
        "cancelled" => WorkflowTerminalOutcome.Cancelled,
        "internal_error" => WorkflowTerminalOutcome.InternalError,
        _ => throw new WorkflowEventParseException($"Unknown workflow terminal outcome '{value}'."),
    };

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new WorkflowEventParseException($"Workflow event requires string property '{name}'.");
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new WorkflowEventParseException($"Workflow event property '{name}' cannot be empty.");
        }
        return value;
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new WorkflowEventParseException($"Workflow event property '{name}' must be a non-empty string.");
        }
        return property.GetString();
    }

    private static long RequiredInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out var value))
        {
            throw new WorkflowEventParseException($"Workflow event requires integer property '{name}'.");
        }
        return value;
    }

    private static long? OptionalInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value))
        {
            throw new WorkflowEventParseException($"Workflow event property '{name}' must be an integer.");
        }
        return value;
    }

    private static double? OptionalDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value) || !double.IsFinite(value))
        {
            throw new WorkflowEventParseException($"Workflow event property '{name}' must be a finite number.");
        }
        return value;
    }

    private static void EnsureOnlyProperties(JsonElement element, HashSet<string> allowed, string path)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new WorkflowEventParseException($"Unknown workflow event property '{path}{property.Name}'.");
            }
        }
    }

    private static void EnsureNoDuplicateProperties(JsonElement element, string path, bool recursive)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new WorkflowEventParseException($"Duplicate workflow event property '{path}.{property.Name}'.");
                }
                if (recursive) EnsureNoDuplicateProperties(property.Value, $"{path}.{property.Name}", recursive: true);
            }
        }
        else if (recursive && element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicateProperties(item, $"{path}[{index++}]", recursive: true);
            }
        }
    }

    private static void RejectPresent(JsonElement root, string type, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out _))
            {
                throw new WorkflowEventParseException($"A {type} event cannot contain '{name}'.");
            }
        }
    }

    private static void EnsureWorkflowId(string value)
    {
        if (!IsWorkflowId(value))
        {
            throw new WorkflowEventParseException("workflowId must be a 1-128 character ASCII identifier.");
        }
    }

    private static void EnsureStage(string value)
    {
        if (!IsStage(value))
        {
            throw new WorkflowEventParseException("stage must be a 1-64 character ASCII identifier.");
        }
    }

    private static bool IsIdentifier(string value, int maxLength, bool allowColon)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength || !IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') continue;
            if (allowColon && character == ':') continue;
            return false;
        }
        return true;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool HasUtcIsoShape(string value)
    {
        if (value.Length < 20 || value[^1] != 'Z') return false;
        if (value[4] != '-' || value[7] != '-' || value[10] != 'T' ||
            value[13] != ':' || value[16] != ':')
        {
            return false;
        }

        for (var index = 0; index < 19; index++)
        {
            if (index is 4 or 7 or 10 or 13 or 16) continue;
            if (value[index] is < '0' or > '9') return false;
        }

        if (value.Length == 20) return true;
        if (value.Length < 22 || value[19] != '.') return false;
        for (var index = 20; index < value.Length - 1; index++)
        {
            if (value[index] is < '0' or > '9') return false;
        }
        return true;
    }
}
