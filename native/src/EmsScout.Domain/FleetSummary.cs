namespace EmsScout.Domain;

public sealed record FleetSummary(
    int Total,
    int Running,
    int Stopped,
    int Offline,
    int Unknown,
    IReadOnlyList<BuildingSummary> Buildings)
{
    public int Online => Running + Stopped;

    public double RunningRate => Total == 0 ? 0 : Running / (double)Total;

    public double OfflineRate => Total == 0 ? 0 : Offline / (double)Total;
}
