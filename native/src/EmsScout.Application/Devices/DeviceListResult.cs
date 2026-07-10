namespace EmsScout.Application.Devices;

public sealed record DeviceListResult(
    int Total,
    IReadOnlyList<DeviceRecord> Rows,
    DeviceFacets Facets);
