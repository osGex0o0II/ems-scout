using CommunityToolkit.Mvvm.ComponentModel;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class CollectionBuildingOption(string value, string label, bool isSelected) : ObservableObject
{
    public string Value { get; } = value;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = isSelected;
}
