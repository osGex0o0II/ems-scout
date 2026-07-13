namespace EmsScout.Application.Groups;

public interface IAreaGroupRepository
{
    Task<AreaGroupSet> LoadAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AreaGroupTargetOption>> LoadTargetOptionsAsync(
        string building,
        string floorLabel,
        CancellationToken cancellationToken = default);

    Task<AreaGroupRecord> SaveGroupAsync(
        AreaGroupEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteGroupAsync(long id, CancellationToken cancellationToken = default);

    Task<AreaGroupItemRecord> SaveItemAsync(
        AreaGroupItemEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteItemAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FloorCatalogRecord>> LoadFloorsAsync(
        string building,
        bool includeDisabled = false,
        CancellationToken cancellationToken = default);

    Task<FloorCatalogRecord> SaveFloorAsync(
        FloorCatalogEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteFloorAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduleGroupRecord>> LoadScheduleGroupsAsync(
        long areaGroupId,
        CancellationToken cancellationToken = default);

    Task<ScheduleGroupRecord> SaveScheduleGroupAsync(
        ScheduleGroupEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteScheduleGroupAsync(long id, CancellationToken cancellationToken = default);

    Task<ScheduleRuleRecord> SaveScheduleRuleAsync(
        ScheduleRuleEdit edit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduleRuleRecord>> SaveScheduleRulesAsync(
        ScheduleRuleBatchEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteScheduleRuleAsync(long id, CancellationToken cancellationToken = default);

    Task DeleteScheduleRulesAsync(
        long scheduleGroupId,
        IReadOnlyList<string> calendarDates,
        CancellationToken cancellationToken = default);

    Task<ScheduleMemberRecord> SaveScheduleMemberAsync(
        ScheduleMemberEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteScheduleMemberAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduleAuditRecord>> EvaluateSchedulesAsync(
        long? runId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);
}

public sealed record AreaGroupSet(
    IReadOnlyList<AreaGroupRecord> Groups,
    IReadOnlyList<AreaGroupItemRecord> Items);

public sealed record AreaGroupRecord(
    long Id,
    string Name,
    string AreaLabel,
    string Description,
    string Priority,
    string GroupKind,
    string SystemKey,
    bool Locked,
    bool Enabled,
    int ItemCount,
    int Total,
    int OnCount,
    int OffCount,
    int OfflineCount,
    int UnknownCount,
    int CoveredAreas);

public sealed record AreaGroupItemRecord(
    long Id,
    long GroupId,
    string GroupName,
    string TargetType,
    string Building,
    string FloorLabel,
    double? FloorValue,
    string SubAreaText,
    string CardName,
    string Note,
    string DeviceUid = "");

public sealed record AreaGroupEdit(
    long? Id,
    string Name,
    string AreaLabel,
    string Description,
    string Priority,
    bool Enabled);

public sealed record AreaGroupItemEdit(
    long GroupId,
    string TargetType,
    string Building,
    string FloorLabel,
    string SubAreaText,
    string CardName,
    string Note,
    long? Id = null);

public sealed record AreaGroupTargetOption(
    string Type,
    string Building,
    string FloorLabel,
    double? FloorValue,
    string SubAreaText,
    string CardName,
    int Count);

public sealed record FloorCatalogRecord(
    long Id,
    string Building,
    string FloorLabel,
    double FloorValue,
    string Source,
    bool Enabled,
    string Note);

public sealed record FloorCatalogEdit(
    long? Id,
    string Building,
    string FloorLabel,
    bool Enabled,
    string Note);

public sealed record ScheduleGroupRecord(
    long Id,
    long AreaGroupId,
    string Name,
    string Description,
    bool Enabled,
    IReadOnlyList<ScheduleRuleRecord> Rules,
    IReadOnlyList<ScheduleMemberRecord> Members);

public sealed record ScheduleRuleRecord(
    long Id,
    long ScheduleGroupId,
    string CalendarDate,
    string ExpectedStatus,
    string Note,
    IReadOnlyList<ScheduleIntervalRecord> Intervals);

public sealed record ScheduleIntervalRecord(
    long Id,
    long RuleId,
    string StartTime,
    string EndTime);

public sealed record ScheduleMemberRecord(
    long Id,
    long ScheduleGroupId,
    long? AreaGroupItemId,
    string TargetType,
    string Building,
    string FloorLabel,
    double? FloorValue,
    string SubAreaText,
    string CardName,
    string DeviceUid,
    string ExpectedStatus,
    string Note);

public sealed record ScheduleGroupEdit(
    long AreaGroupId,
    string Name,
    string Description,
    bool Enabled,
    long? Id = null);

public sealed record ScheduleRuleEdit(
    long ScheduleGroupId,
    string CalendarDate,
    string ExpectedStatus,
    IReadOnlyList<ScheduleIntervalEdit> Intervals,
    string Note,
    long? Id = null);

public sealed record ScheduleIntervalEdit(string StartTime, string EndTime);

public sealed record ScheduleRuleBatchEdit(
    long ScheduleGroupId,
    IReadOnlyList<string> CalendarDates,
    string ExpectedStatus,
    IReadOnlyList<ScheduleIntervalEdit> Intervals,
    string Note);

public sealed record ScheduleMemberEdit(
    long ScheduleGroupId,
    long? AreaGroupItemId,
    string TargetType,
    string Building,
    string FloorLabel,
    string SubAreaText,
    string CardName,
    string DeviceUid,
    string ExpectedStatus,
    string Note,
    long? Id = null);

public sealed record ScheduleAuditRecord(
    string AreaGroupName,
    string ScheduleGroupName,
    string CalendarDate,
    string IntervalText,
    string TargetType,
    string TargetLabel,
    string ObservedAt,
    string ExpectedStatus,
    string ActualStatus,
    string ResultCode,
    string Detail);
