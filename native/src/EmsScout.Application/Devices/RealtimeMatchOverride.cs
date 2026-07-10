namespace EmsScout.Application.Devices;

public sealed record RealtimeMatchOverride(
    long Id,
    string Building,
    string DevId,
    string FloorLabel,
    string SubArea,
    string PageName,
    string RealtimeName,
    string Action,
    long? TargetCardId,
    string ZuoOverride,
    string AreaTypeOverride,
    string Note)
{
    public bool IsMapToDatabase => string.Equals(Action, "map_to_db", StringComparison.OrdinalIgnoreCase);

    public bool IsVirtual => string.Equals(Action, "create_virtual", StringComparison.OrdinalIgnoreCase);

    public bool IsIgnoredDuplicate => string.Equals(Action, "ignore_duplicate", StringComparison.OrdinalIgnoreCase);
}

public sealed class RealtimeMatchOverrideSet(IReadOnlyList<RealtimeMatchOverride> rows)
{
    public static RealtimeMatchOverrideSet Empty { get; } = new([]);

    private readonly Dictionary<string, RealtimeMatchOverride> _byDev = rows
        .Where(row => !string.IsNullOrWhiteSpace(row.DevId))
        .GroupBy(row => DevKey(row.Building, row.DevId), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Id).Last(), StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, RealtimeMatchOverride> _byIdentity = rows
        .Where(row => string.IsNullOrWhiteSpace(row.DevId))
        .GroupBy(row => IdentityKey(row.Building, row.FloorLabel, row.SubArea, row.PageName, row.RealtimeName), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Id).Last(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RealtimeMatchOverride> Rows { get; } = rows;

    public RealtimeMatchOverride? Find(RealtimeDetailRecord detail)
    {
        var devKey = DevKey(detail.Building, detail.DevId);
        if (!string.IsNullOrWhiteSpace(devKey) && _byDev.TryGetValue(devKey, out var byDev))
        {
            return byDev;
        }

        var floorLabel = DeviceFloorLabelFormatter.Format(detail.Floor, detail.SubArea);
        return _byIdentity.TryGetValue(
            IdentityKey(detail.Building, floorLabel, detail.SubArea, detail.PageName, detail.Name),
            out var byIdentity)
            ? byIdentity
            : null;
    }

    private static string DevKey(string building, string devId)
    {
        var dev = KeyPart(devId);
        return string.IsNullOrWhiteSpace(dev) ? string.Empty : $"{KeyPart(building)}::{dev}";
    }

    private static string IdentityKey(string building, string floorLabel, string subArea, string pageName, string name)
    {
        return string.Join(
            "::",
            KeyPart(building),
            KeyPart(floorLabel),
            KeyPart(subArea),
            KeyPart(string.IsNullOrWhiteSpace(pageName) ? "default" : pageName),
            KeyPart(name));
    }

    private static string KeyPart(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }
}
