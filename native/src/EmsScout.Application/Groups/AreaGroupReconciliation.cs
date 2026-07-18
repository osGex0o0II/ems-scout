namespace EmsScout.Application.Groups;

public interface IAreaGroupReconciliationRepository
{
    Task<AreaGroupManagementSnapshot> LoadAsync(
        long? groupId = null,
        CancellationToken cancellationToken = default);

    Task<AreaGroupRuleRecord> SaveRuleAsync(
        AreaGroupRuleEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteRuleAsync(long ruleId, CancellationToken cancellationToken = default);

    Task<AreaGroupMemberRecord> AddManualMemberAsync(
        AreaGroupManualMemberEdit edit,
        CancellationToken cancellationToken = default);

    Task DeleteManualMemberAsync(long memberId, CancellationToken cancellationToken = default);

    Task UpdateMemberNoteAsync(
        long memberId,
        string note,
        CancellationToken cancellationToken = default);

    Task BlockMemberAsync(
        long memberId,
        string note,
        CancellationToken cancellationToken = default);

    Task UpdateExceptionNoteAsync(
        long exceptionId,
        string note,
        CancellationToken cancellationToken = default);

    Task DeleteExceptionAsync(long exceptionId, CancellationToken cancellationToken = default);

    Task DecideChangeAsync(
        long requestId,
        AreaGroupChangeDecision decision,
        string note,
        CancellationToken cancellationToken = default);
}

public enum AreaGroupChangeDecision
{
    Accept,
    Reject,
}

public sealed record AreaGroupRuleEdit(
    long GroupId,
    string RuleType,
    string Building,
    string FloorLabel,
    string MatchValue,
    string Note,
    long? Id = null,
    bool Enabled = true);

public sealed record AreaGroupRuleRecord(
    long Id,
    long GroupId,
    string RuleType,
    string Building,
    string FloorLabel,
    double? FloorValue,
    string MatchValue,
    bool Enabled,
    string Note)
{
    public string RuleTypeLabel => RuleType switch
    {
        "floor" => "楼层持续规则",
        "name_exact" => "设备名精确匹配",
        "name_keyword" => "设备名关键字",
        "area_public" => "预设公区规则",
        "area_non_public" => "预设非公区规则",
        "legacy_sub_area" => "历史子区规则",
        _ => RuleType,
    };

    public string ScopeLabel => RuleType == "floor"
        ? $"{Building} / {FloorLabel}"
        : string.IsNullOrWhiteSpace(MatchValue) ? "全部现有设备" : MatchValue;
}

public sealed record AreaGroupManualMemberEdit(
    long GroupId,
    string DeviceUid,
    string Building,
    string FloorLabel,
    double? FloorValue,
    string SubAreaText,
    string PageName,
    string CardName,
    string SourceKey,
    int Occurrence,
    string Note);

public sealed record AreaGroupMemberRecord(
    long Id,
    long GroupId,
    long? RuleId,
    string MemberOrigin,
    string IdentityKey,
    string DeviceUid,
    string Building,
    string FloorLabel,
    double? FloorValue,
    string SubAreaText,
    string PageName,
    string CardName,
    string SourceKey,
    int Occurrence,
    string Note)
{
    public string MemberOriginLabel => MemberOrigin switch
    {
        "manual" => "手动加入",
        "rule" => "规则确认",
        "legacy" => "历史成员",
        _ => MemberOrigin,
    };
}

public sealed record AreaGroupExceptionRecord(
    long Id,
    long GroupId,
    string ExceptionType,
    string IdentityKey,
    string DeviceUid,
    string Building,
    string FloorLabel,
    string SubAreaText,
    string PageName,
    string CardName,
    string SourceKey,
    int Occurrence,
    string Note)
{
    public string ExceptionTypeLabel => ExceptionType switch
    {
        "blocked" => "长期屏蔽",
        "retained" => "手动保留",
        _ => ExceptionType,
    };
}

public sealed record AreaGroupChangeRequestRecord(
    long Id,
    long GroupId,
    string GroupName,
    long? RuleId,
    long? RunId,
    string Action,
    string Status,
    string IdentityKey,
    string DeviceUid,
    string Building,
    string FloorLabel,
    string SubAreaText,
    string PageName,
    string CardName,
    string SourceKey,
    int Occurrence,
    string MatchReason,
    string DecisionNote,
    string DetectedAt,
    string DecidedAt);

public sealed record AreaGroupManagementSnapshot(
    IReadOnlyList<AreaGroupRuleRecord> Rules,
    IReadOnlyList<AreaGroupMemberRecord> Members,
    IReadOnlyList<AreaGroupExceptionRecord> Exceptions,
    IReadOnlyList<AreaGroupChangeRequestRecord> PendingChanges);
