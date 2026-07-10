using EmsScout.Application.Watch;

namespace EmsScout.Desktop.ViewModels;

public sealed class WatchIncidentRow(DeviceWatchIncident incident)
{
    public DeviceWatchIncident Source { get; } = incident;

    public string DeviceName { get; } = string.IsNullOrWhiteSpace(incident.Device.Name)
        ? "--"
        : incident.Device.Name;

    public string Location { get; } = incident.Device.Label;

    public string ChangeText { get; } = $"{incident.PreviousState} -> {incident.CurrentState}";

    public string TimeText { get; } =
        $"{incident.PreviousAt.LocalDateTime:MM-dd HH:mm} -> {incident.CurrentAt.LocalDateTime:MM-dd HH:mm}";

    public string RunText { get; } = $"#{incident.PreviousRunId} -> #{incident.CurrentRunId}";

    public string Evidence { get; } = incident.Evidence;
}
