using EmsScout.Application.Devices;
using EmsScout.Application.Groups;

namespace EmsScout.Desktop.ViewModels;

public sealed class GroupSummaryRow(
    string name,
    string kind,
    int count,
    string description,
    string areaFilter,
    string communicationFilter = "",
    string quickFilter = "",
    long? groupId = null,
    bool isCustom = false,
    bool isEnabled = true,
    int itemCount = 0)
{
    public GroupSummaryRow(AreaGroupRecord record)
        : this(
            record.Name,
            "区域组",
            record.Total,
            string.IsNullOrWhiteSpace(record.Description) ? record.AreaLabel : record.Description,
            string.Empty,
            groupId: record.Id,
            isCustom: !string.IsNullOrWhiteSpace(record.GroupKey),
            isEnabled: record.Enabled,
            itemCount: record.ItemCount)
    {
        Id = record.Id;
        AreaLabel = record.AreaLabel;
        Priority = record.Priority;
        GroupKey = record.GroupKey;
        OnCount = record.OnCount;
        OffCount = record.OffCount;
        OfflineCount = record.OfflineCount;
        UnknownCount = record.UnknownCount;
        CoveredAreas = record.CoveredAreas;
    }

    public long Id { get; } = groupId ?? 0;

    public string Name { get; } = name;

    public string Kind { get; } = kind;

    public int Count { get; } = count;

    public string CountText { get; } = count >= 0 ? count.ToString("N0") : "--";

    public string Description { get; } = description;

    public string AreaLabel { get; } = string.Empty;

    public string Priority { get; } = string.Empty;

    public string AreaFilter { get; } = areaFilter;

    public string CommunicationFilter { get; } = communicationFilter;

    public string QuickFilter { get; } = quickFilter;

    public long? GroupId { get; } = groupId;

    public bool IsCustom { get; } = isCustom;

    public bool IsEnabled { get; } = isEnabled;

    public int ItemCount { get; } = itemCount;

    public string GroupKey { get; } = string.Empty;

    public string ItemCountText => $"{ItemCount:N0} 条规则";

    public int OnCount { get; } = 0;

    public int OffCount { get; } = 0;

    public int OnlineCount => OnCount + OffCount;

    public int RunningCount => OnCount;

    public int StoppedCount => OffCount;

    public int OfflineCount { get; } = 0;

    public int UnknownCount { get; } = 0;

    public string AreaBreakdownText => string.Empty;

    public int CoveredAreas { get; } = 0;

    public string StateLabel => IsEnabled ? "启用" : "停用";

    public bool CanOpenInData =>
        GroupId is not null ||
        !string.IsNullOrWhiteSpace(AreaFilter) ||
        !string.IsNullOrWhiteSpace(CommunicationFilter);
}
