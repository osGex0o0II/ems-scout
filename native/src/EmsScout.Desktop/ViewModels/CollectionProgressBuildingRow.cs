using CommunityToolkit.Mvvm.ComponentModel;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class CollectionProgressBuildingRow(string building) : ObservableObject
{
    public string Building { get; } = building;

    [ObservableProperty]
    public partial string State { get; private set; } = "待处理";

    [ObservableProperty]
    public partial string StateGlyph { get; private set; } = "\uE7C1";

    [ObservableProperty]
    public partial bool IsCurrent { get; private set; }

    public void MarkPending()
    {
        State = "待处理";
        StateGlyph = "\uE7C1";
        IsCurrent = false;
    }

    public void MarkCurrent()
    {
        State = "采集中";
        StateGlyph = "\uE7C1";
        IsCurrent = true;
    }

    public void MarkCompleted()
    {
        State = "已完成";
        StateGlyph = "\uE73E";
        IsCurrent = false;
    }
}
