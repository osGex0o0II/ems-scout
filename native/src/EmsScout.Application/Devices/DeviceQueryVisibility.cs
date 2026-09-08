namespace EmsScout.Application.Devices;

public static class DeviceQueryVisibility
{
    public static bool ShouldInclude(DeviceRecord row, DeviceQuery query)
    {
        return !row.IsVirtual ||
               string.Equals(query.RealtimeMatch?.Trim(), "virtual", StringComparison.OrdinalIgnoreCase);
    }
}
