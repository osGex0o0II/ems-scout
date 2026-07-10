namespace EmsScout.Desktop.ViewModels;

public sealed class CollectionTaskLogRow(string time, string message)
{
    public string Time { get; } = time;

    public string Message { get; } = message;
}
