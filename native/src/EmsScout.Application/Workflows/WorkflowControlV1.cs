using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmsScout.Application.Workflows;

public static class WorkflowControlContractV1
{
    public const string Version = "ems.workflow-control/v1";
}

public sealed record WorkflowControlV1(
    [property: JsonPropertyName("contractVersion")] string ContractVersion,
    [property: JsonPropertyName("workflowId")] string WorkflowId,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason);

public static class WorkflowControlWriter
{
    public static string CreateCancel(
        string workflowId,
        string? reason = "user_requested",
        DateTimeOffset? timestamp = null)
    {
        if (!WorkflowEventParser.IsWorkflowId(workflowId))
        {
            throw new ArgumentException("workflowId must be a 1-128 character ASCII identifier.", nameof(workflowId));
        }

        if (reason is not null && (string.IsNullOrWhiteSpace(reason) || reason.Length > 512))
        {
            throw new ArgumentException("reason must be null or a non-empty string of at most 512 characters.", nameof(reason));
        }

        var emittedAt = (timestamp ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var control = new WorkflowControlV1(
            WorkflowControlContractV1.Version,
            workflowId,
            emittedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            "cancel",
            reason?.Trim());
        return JsonSerializer.Serialize(control);
    }
}
