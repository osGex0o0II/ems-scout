namespace EmsScout.Application.Devices;

public static class DeviceNavigationFilterPolicy
{
    public static DeviceQuery ResolveNavigation(
        DeviceQuery requested,
        IReadOnlyList<DeviceFilterOption> buildingOptions,
        IReadOnlyList<DeviceFilterOption> floorOptions,
        IReadOnlyList<DeviceFilterOption> subAreaOptions,
        IReadOnlyList<DeviceFilterOption> pageOptions)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(buildingOptions);
        ArgumentNullException.ThrowIfNull(floorOptions);
        ArgumentNullException.ThrowIfNull(subAreaOptions);
        ArgumentNullException.ThrowIfNull(pageOptions);

        return requested with
        {
            Building = ResolveOptionValue(buildingOptions, requested.Building),
            Floor = ResolveOptionValue(floorOptions, requested.Floor),
            SubArea = ResolveOptionValue(subAreaOptions, requested.SubArea),
            PageName = ResolveOptionValue(pageOptions, requested.PageName),
        };
    }

    public static DeviceQuery ApplySubAreaSelection(DeviceQuery current, string? subArea)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            SubArea = string.IsNullOrWhiteSpace(subArea) ? null : subArea,
            PageName = null,
            Offset = 0,
        };
    }

    public static string? ResolveOptionValue(
        IReadOnlyList<DeviceFilterOption> options,
        string? requestedValue)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(requestedValue))
        {
            return requestedValue;
        }

        return options.FirstOrDefault(option =>
                   string.Equals(option.Value, requestedValue, StringComparison.OrdinalIgnoreCase))?.Value
               ?? requestedValue;
    }
}
