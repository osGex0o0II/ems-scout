namespace EmsScout.Application.Devices;

public sealed record DeviceDataQueryRequest(long Version, DeviceQuery Query);

public sealed class DeviceDataQuerySession
{
    private long _version;

    public DeviceQuery? SuccessfulQuery { get; private set; }

    public int SuccessfulTotal { get; private set; }

    public DeviceDataQueryRequest Begin(DeviceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new DeviceDataQueryRequest(Interlocked.Increment(ref _version), query);
    }

    public bool IsCurrent(DeviceDataQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Version == Interlocked.Read(ref _version);
    }

    public bool TryAccept(DeviceDataQueryRequest request, DeviceListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!IsCurrent(request))
        {
            return false;
        }

        SuccessfulQuery = request.Query;
        SuccessfulTotal = result.Total;
        return true;
    }
}

public sealed record DeviceQuickFilterOption(string Key, string Label, int Count, bool IsActive)
{
    public string DisplayText => $"{Label} {Count:N0}";
}

public static class DeviceQuickFilterCatalog
{
    public static IReadOnlyList<DeviceQuickFilterOption> Create(DeviceFacets facets, string? activeKey = null)
    {
        ArgumentNullException.ThrowIfNull(facets);
        return
        [
            Create("offline", "离线", facets.Offline, activeKey),
            Create("unknown", "未知", facets.Unknown, activeKey),
            Create("temp_abnormal", "温度异常", facets.TemperatureIssues, activeKey),
            Create("realtime_missing", "无实时数据", facets.RealtimeMissing, activeKey),
            Create("needs_review", "需关注", facets.NeedsReview, activeKey),
        ];
    }

    private static DeviceQuickFilterOption Create(string key, string label, int count, string? activeKey)
    {
        return new DeviceQuickFilterOption(
            key,
            label,
            count,
            string.Equals(key, activeKey, StringComparison.OrdinalIgnoreCase));
    }
}
