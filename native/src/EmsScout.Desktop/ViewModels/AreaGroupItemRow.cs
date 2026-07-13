using EmsScout.Application.Groups;

namespace EmsScout.Desktop.ViewModels;

public sealed class AreaGroupItemRow(AreaGroupItemRecord record)
{
    public long Id { get; } = record.Id;

    public long GroupId { get; } = record.GroupId;

    public string TargetType { get; } = record.TargetType;

    public string TargetTypeLabel { get; } = record.TargetType switch
    {
        "device" => "设备",
        "sub_area" => "子区",
        _ => "楼层",
    };

    public string Building { get; } = record.Building;

    public string FloorLabel { get; } = record.FloorLabel;

    public string SubAreaText { get; } = record.SubAreaText;

    public string CardName { get; } = record.CardName;

    public string DeviceUid { get; } = record.DeviceUid;

    public string RawNote { get; } = record.Note;

    public string Note { get; } = string.IsNullOrWhiteSpace(record.Note) ? "--" : record.Note;

    public string TargetLabel => TargetType switch
    {
        "device" => string.Join(" / ", new[] { Building, FloorLabel, SubAreaText, CardName }.Where(item => !string.IsNullOrWhiteSpace(item))),
        "sub_area" => string.Join(" / ", new[] { Building, FloorLabel, SubAreaText }.Where(item => !string.IsNullOrWhiteSpace(item))),
        _ => string.Join(" / ", new[] { Building, FloorLabel }.Where(item => !string.IsNullOrWhiteSpace(item))),
    };
}
