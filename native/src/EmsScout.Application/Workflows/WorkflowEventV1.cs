using System.Text.Json;

namespace EmsScout.Application.Workflows;

public static class WorkflowEventContractV1
{
    public const string Version = "ems.workflow-event/v1";
}

public enum WorkflowEventType
{
    Started,
    Progress,
    Action,
    Terminal,
}

public enum WorkflowTerminalOutcome
{
    Succeeded,
    SucceededWithFindings,
    Rejected,
    AuthRequired,
    Cancelled,
    InternalError,
}

public sealed record WorkflowProgressV1(
    double? Percent,
    string? Message,
    long? Current,
    long? Total,
    string? Unit,
    JsonElement? Data);

public sealed record WorkflowEventV1(
    string ContractVersion,
    string WorkflowId,
    long Sequence,
    DateTimeOffset Timestamp,
    WorkflowEventType Type,
    string Stage,
    string? Message,
    WorkflowProgressV1? Progress,
    string? Action,
    WorkflowTerminalOutcome? Outcome,
    int? ExitCode)
{
    public bool IsTerminal => Type == WorkflowEventType.Terminal;
}
