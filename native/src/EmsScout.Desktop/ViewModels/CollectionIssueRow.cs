using EmsScout.Application.Quality;

namespace EmsScout.Desktop.ViewModels;

public sealed class CollectionIssueRow(CollectionIssueRecord record)
{
    public string IssueType { get; } = record.IssueType;
    public string Severity { get; } = record.Severity;
    public string Building { get; } = Dash(record.Building);
    public string Floor { get; } = Dash(record.Floor);
    public string Zone { get; } = Dash(record.Zone);
    public string PageName { get; } = Dash(record.PageName);
    public string DeviceName { get; } = Dash(record.DeviceName);
    public string DeviceId { get; } = Dash(record.DeviceId);
    public string CollectedAt { get; } = Dash(record.CollectedAt);
    public string ObservedValue { get; } = Dash(record.ObservedValue);
    public string Evidence { get; } = Dash(record.Evidence);
    public string CollectorDecision { get; } = Dash(record.CollectorDecision);
    public string Reason { get; } = Dash(record.Reason);
    public string Attribution { get; } = Dash(record.Attribution);
    public string ResolutionState { get; } = Dash(record.ResolutionState);

    public string LocationLabel => Join(Floor, Zone, PageName);
    public string DeviceLabel => Join(DeviceName, DeviceId);
    public string ObservationLabel => Join(CollectedAt, ObservedValue, Evidence);
    public string DecisionLabel => Join(CollectorDecision, Reason, Attribution, ResolutionState);

    private static string Dash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string Join(params string[] values)
    {
        var result = string.Join(" · ", values.Where(value => value != "-"));
        return string.IsNullOrWhiteSpace(result) ? "-" : result;
    }
}
