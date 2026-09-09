using EmsScout.Application.Collection;

namespace EmsScout.Desktop.ViewModels;

public sealed class CollectionRunBuildingDifferenceRow(CollectionRunBuildingDifference difference)
{
    public string Building { get; } = difference.Building;

    public string SnapshotCountText { get; } = difference.SnapshotCount.ToString("N0");

    public string CurrentCountText { get; } = difference.CurrentCount.ToString("N0");

    public string AddedCountText { get; } = difference.AddedCount.ToString("N0");

    public string MissingCountText { get; } = difference.MissingCount.ToString("N0");

    public string DeltaText => difference.Delta switch
    {
        > 0 => $"+{difference.Delta:N0}",
        < 0 => difference.Delta.ToString("N0"),
        _ => "0",
    };
}
