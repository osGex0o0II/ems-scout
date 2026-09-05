using EmsScout.Application.Devices;

namespace EmsScout.Application.Watch;

public interface IDeviceWatchRepository
{
    Task<DeviceWatchEvaluation> EvaluateAsync(
        DeviceWatchQuery query,
        CancellationToken cancellationToken = default);

    Task<DeviceWatchRule?> LoadRuleForGroupAsync(
        long groupId,
        CancellationToken cancellationToken = default);

    Task<DeviceWatchRule> SaveRuleAsync(
        DeviceWatchEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteRuleAsync(long id, long groupId, CancellationToken cancellationToken = default);
}

public sealed record DeviceWatchQuery(
    long? GroupId = null,
    bool IncludeDisabled = false);

public sealed record DeviceWatchRule(
    long Id,
    long GroupId,
    string GroupName,
    string Name,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    bool Enabled,
    string Note,
    int WatchedDevices,
    int AbnormalDevices);

public sealed record DeviceWatchEdit(
    long? Id,
    long GroupId,
    string Name,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    bool Enabled,
    string Note);

public sealed record DeviceWatchEvaluation(
    IReadOnlyList<DeviceWatchRule> Rules,
    IReadOnlyList<DeviceWatchIncident> Incidents,
    IReadOnlyDictionary<string, DeviceWatchState> DeviceStates)
{
    public int WatchedDevices => DeviceStates.Values.Count(state => state.IsWatched);

    public int AbnormalDevices => DeviceStates.Values.Count(state => state.IsAbnormal);
}

public sealed record DeviceWatchIncident(
    long RuleId,
    long GroupId,
    string GroupName,
    DeviceWatchKey Device,
    string PreviousState,
    string CurrentState,
    DateTimeOffset PreviousAt,
    DateTimeOffset CurrentAt,
    long PreviousRunId,
    long CurrentRunId)
{
    public string Summary =>
        $"{PreviousState} -> {CurrentState}，{CurrentAt.LocalDateTime:yyyy-MM-dd HH:mm}";

    public string Evidence =>
        $"#{PreviousRunId} {PreviousAt.LocalDateTime:yyyy-MM-dd HH:mm} {PreviousState}; " +
        $"#{CurrentRunId} {CurrentAt.LocalDateTime:yyyy-MM-dd HH:mm} {CurrentState}";
}

public sealed record DeviceWatchState(
    bool IsWatched,
    bool IsAbnormal,
    long? RuleId,
    long? GroupId,
    string RuleName,
    DateTimeOffset? StartAt,
    DateTimeOffset? EndAt,
    string Summary,
    string Evidence)
{
    public static DeviceWatchState Unwatched { get; } = new(
        IsWatched: false,
        IsAbnormal: false,
        RuleId: null,
        GroupId: null,
        RuleName: string.Empty,
        StartAt: null,
        EndAt: null,
        Summary: "未关注",
        Evidence: string.Empty);

    public string WindowText => StartAt is null || EndAt is null
        ? string.Empty
        : $"{StartAt.Value.LocalDateTime:yyyy-MM-dd HH:mm} - {EndAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}";
}

public sealed record DeviceWatchKey(
    string Building,
    string FloorLabel,
    string SubArea,
    string PageName,
    string Name)
{
    public string Key => string.Join(
        "|",
        Normalize(Building),
        Normalize(DeviceFloorLabelFormatter.Normalize(FloorLabel)),
        Normalize(SubArea),
        Normalize(DevicePageNameFormatter.NormalizeValue(PageName)),
        Normalize(Name));

    public string Label => string.Join(
        " / ",
        new[] { Building, FloorLabel, SubArea, DevicePageNameFormatter.Format(PageName), Name }.Where(item => !string.IsNullOrWhiteSpace(item)));

    public static DeviceWatchKey From(DeviceRecord row) => new(
        row.Building,
        row.FloorLabel,
        row.SubArea,
        row.PageName,
        row.Name);

    public static string KeyFor(DeviceRecord row) => From(row).Key;

    private static string Normalize(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
