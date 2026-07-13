using EmsScout.Application.Groups;

namespace EmsScout.Desktop.ViewModels;

public sealed class ScheduleAuditRow(ScheduleAuditRecord record)
{
    public string Source { get; } = $"{record.AreaGroupName} / {record.ScheduleGroupName}";
    public string Date { get; } = record.CalendarDate;
    public string Interval { get; } = record.IntervalText;
    public string Target { get; } = record.TargetLabel;
    public string ObservedAt { get; } = record.ObservedAt;
    public string Expected { get; } = record.ExpectedStatus;
    public string Actual { get; } = record.ActualStatus;
    public string Result { get; } = record.ResultCode switch
    {
        "ok" => "正常",
        "not_enabled" => "未按计划启用",
        "not_open" => "未开放",
        "outside_window" => "计划外",
        "unexpected_running" => "计划外运行",
        _ => record.ResultCode,
    };
    public string Detail { get; } = record.Detail;
}
