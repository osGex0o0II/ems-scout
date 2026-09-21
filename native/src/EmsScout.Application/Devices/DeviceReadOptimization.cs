using EmsScout.Application.Groups;

namespace EmsScout.Application.Devices;

public sealed record DevicePageAndFilterResult(
    DeviceListResult Page,
    DeviceFilterOptions FilterOptions);

public interface IDeviceReadRepositoryWithFilterOptions
{
    Task<DevicePageAndFilterResult> SearchWithFilterOptionsAsync(
        DeviceQuery query,
        CancellationToken cancellationToken = default);
}

public interface IAreaGroupRuleMatchRepository
{
    Task<IReadOnlyList<int>> CountAreaGroupRuleMatchesAsync(
        IReadOnlyList<AreaGroupRuleRecord> rules,
        CancellationToken cancellationToken = default);
}

public interface IDeviceReadRevisionSource
{
    Task<string> GetRevisionAsync(CancellationToken cancellationToken = default);
}
