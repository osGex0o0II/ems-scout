using EmsScout.Application.Groups;

namespace EmsScout.Desktop.ViewModels;

public sealed class AreaGroupTargetOptionRow(AreaGroupTargetOption option)
{
    public string Type { get; } = option.Type;

    public string Building { get; } = option.Building;

    public string FloorLabel { get; } = option.FloorLabel;

    public string SubAreaText { get; } = option.SubAreaText;

    public string CardName { get; } = option.CardName;

    public string DeviceUid { get; } = option.DeviceUid;

    public string PageName { get; } = option.PageName;

    public string SourceKey { get; } = option.SourceKey;

    public int Occurrence { get; } = option.Occurrence;

    public string Label => option.Type == "device"
        ? $"{option.SubAreaText} / {option.CardName}{DeviceDisambiguator(option)} ({option.Count:N0})"
        : $"{option.SubAreaText} ({option.Count:N0})";

    private static string DeviceDisambiguator(AreaGroupTargetOption candidate)
    {
        var page = string.IsNullOrWhiteSpace(candidate.PageName) ? string.Empty : $" / {candidate.PageName}";
        var occurrence = candidate.Occurrence <= 1 ? string.Empty : $" / #{candidate.Occurrence}";
        return page + occurrence;
    }
}
