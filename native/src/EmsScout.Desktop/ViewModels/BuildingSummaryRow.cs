using EmsScout.Domain;
using EmsScout.Desktop.Services;

namespace EmsScout.Desktop.ViewModels;

public sealed class BuildingSummaryRow(BuildingSummary summary)
{
    public string Building { get; } = summary.Building;

    public string Total { get; } = summary.Total.ToString("N0");

    public string Running { get; } = summary.Running.ToString("N0");

    public string Stopped { get; } = summary.Stopped.ToString("N0");

    public string Offline { get; } = summary.Offline.ToString("N0");

    public string Unknown { get; } = summary.Unknown.ToString("N0");

    public string Attention { get; } = (summary.Offline + summary.Unknown).ToString("N0");

    public string RunningRate { get; } = summary.RunningRate.ToString("P1");

    public string OfflineRate { get; } = summary.OfflineRate.ToString("P1");

    public DataNavigationRequest NavigationRequest { get; } = new(Building: summary.Building);
}
