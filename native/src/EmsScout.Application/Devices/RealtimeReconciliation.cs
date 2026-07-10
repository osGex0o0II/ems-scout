namespace EmsScout.Application.Devices;

public sealed record RealtimeReconciliationQuery(
    string? Building = null,
    string? DiffType = null,
    string? SearchText = null,
    int Limit = 500,
    int Offset = 0);

public interface IRealtimeReconciliationService
{
    Task<RealtimeReconciliationResult> AnalyzeAsync(
        RealtimeReconciliationQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record RealtimeReconciliationResult(
    RealtimeReconciliationSummary Summary,
    IReadOnlyList<RealtimeReconciliationItem> Items);

public sealed record RealtimeReconciliationSummary(
    int DbCount,
    int RealtimeCount,
    int Difference,
    int DiffItemCount,
    int ExactMatches,
    int ManualMatches,
    int RelaxedMatches,
    int OverrideCount,
    IReadOnlyDictionary<string, int> ByType,
    DateTimeOffset GeneratedAt,
    DateTimeOffset? SourceUpdatedAt = null);

public sealed record RealtimeReconciliationItem(
    string Type,
    string Severity,
    string Building,
    string FloorLabel,
    string Name,
    string DbLocation,
    string RealtimeLocation,
    string DevId,
    string OverrideAction,
    string Reason,
    double Confidence,
    string RuleVersion,
    string RuleDescription,
    string EvidenceSummary,
    IReadOnlyList<string> DecisionPath);

public static class RealtimeReconciliationTypes
{
    public const string RuleVersion = "reconcile-v1.0.0";
    public const string NewDevice = "NEW_DEVICE";
    public const string MissingInRealtime = "MISSING_IN_REALTIME";
    public const string MatchFailed = "MATCH_FAILED";
    public const string DuplicateRender = "DUPLICATE_RENDER";
    public const string VirtualOverride = "VIRTUAL_OVERRIDE";
    public const string DataNoise = "DATA_NOISE";
}
