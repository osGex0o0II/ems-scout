namespace EmsScout.Desktop.ViewModels;

public sealed class DataFacetItem(
    string label,
    int value,
    string detail,
    string filterKind = "",
    string filterValue = "")
{
    public string Label { get; } = label;

    public string Value { get; } = value.ToString("N0");

    public string Detail { get; } = detail;

    public string FilterKind { get; } = filterKind;

    public string FilterValue { get; } = filterValue;

    public bool CanApply => !string.IsNullOrWhiteSpace(FilterKind);
}
