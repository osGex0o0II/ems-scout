namespace EmsScout.Application.Devices;

public interface IDeviceReadRepository
{
    Task<DeviceListResult> SearchAsync(DeviceQuery query, CancellationToken cancellationToken = default);

    Task<DeviceFilterOptions> LoadFilterOptionsAsync(CancellationToken cancellationToken = default);

    Task<DeviceFilterOptions> LoadFilterOptionsAsync(
        DeviceQuery query,
        CancellationToken cancellationToken = default);
}
