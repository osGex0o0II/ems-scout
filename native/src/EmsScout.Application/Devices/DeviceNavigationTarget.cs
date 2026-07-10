namespace EmsScout.Application.Devices;

public sealed record DeviceNavigationTarget(
    string SearchText = "",
    string Building = "",
    string RealtimeMatch = "",
    string RealtimePoints = "");

public static class DeviceNavigationTargetFactory
{
    public static DeviceNavigationTarget FromReconciliationItem(RealtimeReconciliationItem item)
    {
        var realtimeMatch = item.Type switch
        {
            RealtimeReconciliationTypes.MissingInRealtime => "missing",
            RealtimeReconciliationTypes.VirtualOverride => "virtual",
            RealtimeReconciliationTypes.MatchFailed => "manual",
            _ => string.Empty,
        };

        return new DeviceNavigationTarget(
            SearchText: item.Name,
            Building: item.Building,
            RealtimeMatch: realtimeMatch);
    }
}
