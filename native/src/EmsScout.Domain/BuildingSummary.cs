namespace EmsScout.Domain;

public sealed record BuildingSummary(
    string Building,
    int Total,
    int Running,
    int Stopped,
    int Offline,
    int Unknown)
{
    public int Online => Running + Stopped;

    public double RunningRate => Total == 0 ? 0 : Running / (double)Total;

    public double OfflineRate => Total == 0 ? 0 : Offline / (double)Total;
}
