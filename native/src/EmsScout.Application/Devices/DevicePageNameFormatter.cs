namespace EmsScout.Application.Devices;

public static class DevicePageNameFormatter
{
    private static readonly IReadOnlyDictionary<string, int> PageNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["一页"] = 1,
        ["二页"] = 2,
        ["三页"] = 3,
        ["四页"] = 4,
        ["五页"] = 5,
        ["六页"] = 6,
    };

    public static string Format(string? value)
    {
        var pageName = NormalizeValue(value);
        if (pageName.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return "默认页";
        }

        if (PageNumbers.TryGetValue(pageName, out var pageNumber))
        {
            return $"第{pageNumber}页";
        }

        const string podiumPrefix = "裙楼/";
        if (pageName.StartsWith(podiumPrefix, StringComparison.OrdinalIgnoreCase) &&
            PageNumbers.TryGetValue(pageName[podiumPrefix.Length..], out pageNumber))
        {
            return $"裙楼 / 第{pageNumber}页";
        }

        return pageName;
    }

    public static double SortValue(string? value)
    {
        var pageName = NormalizeValue(value);
        if (pageName.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (PageNumbers.TryGetValue(pageName, out var pageNumber))
        {
            return 10 + pageNumber;
        }

        const string podiumPrefix = "裙楼/";
        if (pageName.StartsWith(podiumPrefix, StringComparison.OrdinalIgnoreCase) &&
            PageNumbers.TryGetValue(pageName[podiumPrefix.Length..], out pageNumber))
        {
            return 20 + pageNumber;
        }

        return pageName.Equals("BM", StringComparison.OrdinalIgnoreCase) ? 100 : 200;
    }

    public static string NormalizeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
    }
}
