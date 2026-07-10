using EmsScout.Application.Groups;

namespace EmsScout.Desktop.ViewModels;

public sealed class AreaGroupTargetOptionRow(AreaGroupTargetOption option)
{
    public string Type { get; } = option.Type;

    public string Building { get; } = option.Building;

    public string FloorLabel { get; } = option.FloorLabel;

    public string SubAreaText { get; } = option.SubAreaText;

    public string CardName { get; } = option.CardName;

    public string Label => option.Type == "device"
        ? $"{option.SubAreaText} / {option.CardName} ({option.Count:N0})"
        : $"{option.SubAreaText} ({option.Count:N0})";
}
