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
    bool isLocked = true,
    bool isEnabled = true,
    int itemCount = 0)
{
    public GroupSummaryRow(AreaGroupRecord record)
        : this(
            record.Name,
            record.GroupKind.Equals("system", StringComparison.OrdinalIgnoreCase) ? "系统区域" : "自定义区域",
            record.Total,
            string.IsNullOrWhiteSpace(record.Description) ? record.AreaLabel : record.Description,
            record.SystemKey == "public" ? DeviceAreaClassifier.PublicArea : record.SystemKey == "non_public" ? DeviceAreaClassifier.PrivateArea : string.Empty,
            groupId: record.Id,
            isCustom: record.GroupKind.Equals("custom", StringComparison.OrdinalIgnoreCase),
            isLocked: record.Locked,
            isEnabled: record.Enabled,
            itemCount: record.ItemCount)
    {
        Id = record.Id;
        AreaLabel = record.AreaLabel;
        Priority = record.Priority;
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

    public bool IsLocked { get; } = isLocked;

    public bool IsEnabled { get; } = isEnabled;

    public int ItemCount { get; } = itemCount;

    public string ItemCountText => ItemCount.ToString("N0");

    public int OnCount { get; } = 0;

    public int OffCount { get; } = 0;

    public int OfflineCount { get; } = 0;

    public int UnknownCount { get; } = 0;

    public int CoveredAreas { get; } = 0;

    public string StateLabel => IsCustom ? IsEnabled ? "启用" : "停用" : "规则分类";

    public bool CanOpenInData =>
        !string.IsNullOrWhiteSpace(AreaFilter) ||
        !string.IsNullOrWhiteSpace(CommunicationFilter);
}
