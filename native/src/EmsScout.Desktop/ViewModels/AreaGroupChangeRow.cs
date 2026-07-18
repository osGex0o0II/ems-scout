using CommunityToolkit.Mvvm.ComponentModel;
using EmsScout.Application.Groups;
using Microsoft.UI.Xaml;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class AreaGroupChangeRow(AreaGroupChangeRequestRecord record) : ObservableObject
{
    public long Id { get; } = record.Id;

    public long GroupId { get; } = record.GroupId;

    public string GroupName { get; } = record.GroupName;

    public string Action { get; } = record.Action;

    public string ActionLabel => Action == "add" ? "待确认加入" : "待确认移除";

    public string DeviceLabel => $"{record.Building} / {record.FloorLabel} / {record.CardName}";

    public string LocationLabel => string.Join(" / ", new[] { record.SubAreaText, record.PageName }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public string MatchReason { get; } = record.MatchReason;

    public string DetectedAt { get; } = FormatDateTime(record.DetectedAt);

    public Visibility AddActionVisibility => Action == "add" ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RemoveActionVisibility => Action == "remove" ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    public partial string DecisionNote { get; set; } = record.DecisionNote;

    private static string FormatDateTime(string value) => DateTimeOffset.TryParse(value, out var parsed)
        ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : value;
}

public sealed record AreaGroupFilterOption(long? GroupId, string Value, string Label);
