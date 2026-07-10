namespace EmsScout.Desktop.ViewModels;

public sealed class CollectionStageRow(string key, string label, string state, string detail)
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    public string State { get; } = state;

    public string Detail { get; } = detail;
}
