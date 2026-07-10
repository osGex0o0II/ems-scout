using EmsScout.Application.Quality;

namespace EmsScout.Desktop.ViewModels;

public sealed class RealtimeQualityCategoryRow(RealtimeQualityCategory category)
{
    public string Label { get; } = category.Label;

    public string Code { get; } = category.Code;

    public int Count { get; } = category.Count;

    public string CountText { get; } = category.Count.ToString("N0");
}

public sealed class RealtimeQualityBuildingRow(RealtimeQualityBuilding building)
{
    public string Building { get; } = building.Building;

    public string RowsText { get; } = building.Rows.ToString("N0");

    public string CollectionErrorsText { get; } = building.CollectionErrors.ToString("N0");

    public string DeviceAnomalyRowsText { get; } = building.DeviceAnomalyRows.ToString("N0");

    public string DeviceAnomalyEventsText { get; } = building.DeviceAnomalyEvents.ToString("N0");

    public string MainCategoryText => $"点位0 {building.InvalidRealtimeTags:N0}；枚举 {building.InvalidEnum:N0}；范围 {building.OutOfRange:N0}；集控 {building.InvalidLock:N0}";
}
