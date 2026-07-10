namespace EmsScout.Desktop.ViewModels;

public sealed class AreaGroupTargetTypeOption(string value, string label)
{
    public string Value { get; } = value;

    public string Label { get; } = label;
}
