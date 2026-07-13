using EmsScout.Application.Groups;

namespace EmsScout.Desktop.ViewModels;

public sealed class ScheduleGroupRow(ScheduleGroupRecord record)
{
    public long Id { get; } = record.Id;
    public string Name { get; } = record.Name;
    public string Description { get; } = string.IsNullOrWhiteSpace(record.Description) ? "--" : record.Description;
    public bool Enabled { get; } = record.Enabled;
    public string StateLabel { get; } = record.Enabled ? "启用" : "停用";
    public IReadOnlyList<ScheduleRuleRecord> Rules { get; } = record.Rules;
    public IReadOnlyList<ScheduleMemberRecord> Members { get; } = record.Members;
    public string RuleSummary => Rules.Count == 0 ? "未配置日期" : $"{Rules.Count} 个日期规则";
    public string MemberSummary => $"{Members.Count} 个成员";
}

public sealed class ScheduleRuleRow(ScheduleRuleRecord record)
{
    public ScheduleRuleRecord Record { get; } = record;
    public long Id { get; } = record.Id;
    public string Date { get; } = record.CalendarDate;
    public string StatusLabel { get; } = record.ExpectedStatus == "not_open" ? "全天不启用" : "按时间启用";
    public string IntervalsText { get; } = record.Intervals.Count == 0
        ? "无启用时段"
        : string.Join("、", record.Intervals.Select(item => $"{item.StartTime}-{item.EndTime}"));
}

public sealed class ScheduleMemberRow(ScheduleMemberRecord record)
{
    public ScheduleMemberRecord Record { get; } = record;
    public long Id { get; } = record.Id;
    public string TargetTypeLabel { get; } = record.TargetType switch
    {
        "device" => "设备",
        "sub_area" => "子区",
        _ => "楼层",
    };
    public string TargetLabel { get; } = string.Join(" / ", new[]
    {
        record.Building, record.FloorLabel, record.SubAreaText, record.CardName,
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string ExpectedStatusLabel { get; } = record.ExpectedStatus == "not_open" ? "未开放" : "正常启用";
    public string Note { get; } = string.IsNullOrWhiteSpace(record.Note) ? "--" : record.Note;
}
