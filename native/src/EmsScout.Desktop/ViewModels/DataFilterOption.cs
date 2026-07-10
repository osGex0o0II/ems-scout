using EmsScout.Application.Devices;

namespace EmsScout.Desktop.ViewModels;

public sealed class DataFilterOption(string value, string label, int count)
{
    public string Value { get; } = value;

    public string Label { get; } = label;

    public int Count { get; } = count;

    public string DisplayLabel => Count >= 0 ? $"{Label} ({Count:N0})" : Label;

    public static DataFilterOption All(string label) => new(string.Empty, label, -1);

    public static DataFilterOption From(DeviceFilterOption option) => new(option.Value, option.Label, option.Count);
}
