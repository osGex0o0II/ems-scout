using EmsScout.Application.Workflows;

namespace EmsScout.Infrastructure.Sidecar;

public sealed class WorkflowExecutionException : Exception
{
    public WorkflowExecutionException(
        string workflowLabel,
        WorkflowTerminalOutcome outcome,
        int exitCode,
        string? terminalMessage)
        : base(BuildMessage(workflowLabel, outcome, exitCode, terminalMessage))
    {
        WorkflowLabel = workflowLabel;
        Outcome = outcome;
        ExitCode = exitCode;
        TerminalMessage = terminalMessage;
    }

    public string WorkflowLabel { get; }

    public WorkflowTerminalOutcome Outcome { get; }

    public int ExitCode { get; }

    public string? TerminalMessage { get; }

    private static string BuildMessage(
        string label,
        WorkflowTerminalOutcome outcome,
        int exitCode,
        string? terminalMessage)
    {
        return $"{label} ended with outcome {outcome} and exit code {exitCode}" +
               (string.IsNullOrWhiteSpace(terminalMessage) ? "." : ": " + terminalMessage);
    }
}
