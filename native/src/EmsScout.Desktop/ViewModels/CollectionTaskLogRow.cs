namespace EmsScout.Desktop.ViewModels;

public sealed class CollectionTaskLogRow(string time, string message, string severity = "INFO")
{
    public string Time { get; } = time;

    public string Message { get; } = message;

    public string Severity { get; } = severity;

    public bool IsWarning => Severity is "WARN" or "ERROR";

    public bool IsError => Severity == "ERROR";
}
