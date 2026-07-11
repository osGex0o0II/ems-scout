using EmsScout.Application.Devices;

namespace EmsScout.Desktop.ViewModels;

public sealed record ReconciliationFilterOption(string Value, string Label);

public sealed class ReconciliationTypeCountRow(string type, int count)
{
    public string Type { get; } = type;

    public string Label { get; } = ReconciliationLabels.TypeLabel(type);

    public string CountText { get; } = count.ToString("N0");
}

public sealed class ReconciliationItemRow
{
    public ReconciliationItemRow(RealtimeReconciliationItem item)
    {
        Source = item;
        Type = item.Type;
        TypeLabel = ReconciliationLabels.TypeLabel(item.Type);
        Severity = item.Severity;
        Building = item.Building;
        FloorLabel = string.IsNullOrWhiteSpace(item.FloorLabel) ? "--" : item.FloorLabel;
        Name = string.IsNullOrWhiteSpace(item.Name) ? "--" : item.Name;
        Location = $"DB {ValueOrDash(item.DbLocation)} / RT {ValueOrDash(item.RealtimeLocation)}";
        DevId = string.IsNullOrWhiteSpace(item.DevId) ? "--" : item.DevId;
        ConfidenceText = item.Confidence.ToString("P0");
        Reason = item.Reason;
        RuleDescription = item.RuleDescription;
        EvidenceSummary = item.EvidenceSummary;
        DecisionPathText = string.Join(Environment.NewLine, item.DecisionPath.Select(step => "- " + step));
        NavigationTarget = DeviceNavigationTargetFactory.FromReconciliationItem(item);
    }

    public RealtimeReconciliationItem Source { get; }

    public string Type { get; }

    public string TypeLabel { get; }

    public string Severity { get; }

    public string Building { get; }

    public string FloorLabel { get; }

    public string Name { get; }

    public string Location { get; }

    public string DevId { get; }

    public string ConfidenceText { get; }

    public string Reason { get; }

    public string RuleDescription { get; }

    public string EvidenceSummary { get; }

    public string DecisionPathText { get; }

    public DeviceNavigationTarget NavigationTarget { get; }

    private static string ValueOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "--" : value;
}

public static class ReconciliationLabels
{
    public static string TypeLabel(string type)
    {
        return type switch
        {
            RealtimeReconciliationTypes.NewDevice => "新增实时",
            RealtimeReconciliationTypes.MissingInRealtime => "缺实时",
            RealtimeReconciliationTypes.MatchFailed => "匹配失败",
            RealtimeReconciliationTypes.DuplicateRender => "重复渲染",
            RealtimeReconciliationTypes.VirtualOverride => "虚拟纳管",
            RealtimeReconciliationTypes.DataNoise => "数据噪声",
            _ => type,
        };
    }
}
