namespace EmsScout.Application.Logging;

public enum ApplicationLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

public sealed record ApplicationLogContext(
    string? WorkflowId = null,
    string? Stage = null,
    string? ErrorCode = null,
    bool? Retryable = null);

public sealed record ApplicationLogEvent(
    ApplicationLogLevel Level,
    string Category,
    string EventName,
    string Message,
    ApplicationLogContext? Context = null,
    Exception? Exception = null,
    IReadOnlyDictionary<string, object?>? Data = null,
    bool AlwaysWrite = false);

public interface IApplicationLogger
{
    string CurrentLogPath { get; }

    void Write(ApplicationLogEvent logEvent);
}
