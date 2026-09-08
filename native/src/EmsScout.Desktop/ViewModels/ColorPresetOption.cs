using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EmsScout.Desktop.ViewModels;

public sealed class ColorPresetOption(string displayLabel, Color color)
{
    public string DisplayLabel { get; } = displayLabel;

    public Color Color { get; } = color;

    public SolidColorBrush SwatchBrush { get; } = new(color);
}
