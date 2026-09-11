namespace EmsScout.Application.Devices;

public sealed record DeviceQuery(
    string? SearchText = null,
    string? Building = null,
    string? CommunicationState = null,
    string? Floor = null,
    string? SubArea = null,
    string? DeviceName = null,
    string? Zuo = null,
    string? Mode = null,
    string? Fan = null,
    string? SetTemperature = null,
    string? IndoorTemperature = null,
    string? PageName = null,
    string? Tag = null,
    string? RealtimeMatch = null,
    string? RealtimePoints = null,
    string? RealtimePower = null,
    string? RealtimeMode = null,
    string? RealtimeFan = null,
    string? RealtimeLock = null,
    string? RealtimeSystemType = null,
    string? RealtimeModbus = null,
    string? AreaType = null,
    string? MonitorGroupIds = null,
    string? WatchState = null,
    string? QuickFilter = null,
    string? SortBy = null,
    bool SortDescending = false,
    int Limit = 500,
    int Offset = 0,
    long? RunId = null);

public enum DeviceFilterFacet
{
    Building,
    CommunicationState,
    Floor,
    SubArea,
    PageName,
    DeviceName,
    Zuo,
    Mode,
    Fan,
    SetTemperature,
    IndoorTemperature,
    Tag,
    RealtimePower,
    RealtimeMode,
    RealtimeFan,
    RealtimeLock,
    RealtimeSystemType,
}

public static class DeviceQueryFacetExtensions
{
    public static DeviceQuery WithoutFacets(this DeviceQuery query)
    {
        return query with
        {
            Building = null,
            CommunicationState = null,
            Floor = null,
            SubArea = null,
            PageName = null,
            DeviceName = null,
            Zuo = null,
            Mode = null,
            Fan = null,
            SetTemperature = null,
            IndoorTemperature = null,
            Tag = null,
            RealtimePower = null,
            RealtimeMode = null,
            RealtimeFan = null,
            RealtimeLock = null,
            RealtimeSystemType = null,
        };
    }
}
