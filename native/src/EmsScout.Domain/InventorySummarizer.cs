namespace EmsScout.Domain;

public sealed class InventorySummarizer
{
    public FleetSummary Summarize(IEnumerable<AirConditionerCard> cards)
    {
        var list = cards.ToList();
        var buildings = list
            .GroupBy(card => card.Building)
            .OrderBy(group => BuildingSortKey(group.Key))
            .Select(group => SummarizeBuilding(group.Key, group))
            .ToList();

        return new FleetSummary(
            Total: list.Count,
            Running: CountState(list, DeviceCommunicationState.Running),
            Stopped: CountState(list, DeviceCommunicationState.Stopped),
            Offline: CountState(list, DeviceCommunicationState.Offline),
            Unknown: CountState(list, DeviceCommunicationState.Unknown),
            Buildings: buildings);
    }

    private static BuildingSummary SummarizeBuilding(string building, IEnumerable<AirConditionerCard> cards)
    {
        var list = cards.ToList();
        return new BuildingSummary(
            Building: building,
            Total: list.Count,
            Running: CountState(list, DeviceCommunicationState.Running),
            Stopped: CountState(list, DeviceCommunicationState.Stopped),
            Offline: CountState(list, DeviceCommunicationState.Offline),
            Unknown: CountState(list, DeviceCommunicationState.Unknown));
    }

    private static int CountState(IEnumerable<AirConditionerCard> cards, DeviceCommunicationState state)
    {
        return cards.Count(card => card.CommunicationState == state);
    }

    private static int BuildingSortKey(string building)
    {
        var digits = new string(building.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : int.MaxValue;
    }
}
