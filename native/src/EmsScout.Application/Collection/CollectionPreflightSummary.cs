namespace EmsScout.Application.Collection;

public sealed record CollectionPreflightRequirement(
    string Name,
    string Resolution,
    bool IsSatisfied);

public sealed record CollectionPreflightSummary(
    bool IsReady,
    string Title,
    string Detail,
    int PassedCount,
    int TotalCount)
{
    public string DetailsHeader => $"{PassedCount}/{TotalCount} 已通过";
}

public static class CollectionPreflightSummaryBuilder
{
    public static CollectionPreflightSummary Build(
        IReadOnlyList<CollectionPreflightRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        var firstFailure = requirements.FirstOrDefault(item => !item.IsSatisfied);
        var passedCount = requirements.Count(item => item.IsSatisfied);
        return firstFailure is null
            ? new CollectionPreflightSummary(
                true,
                "运行前检查通过",
                "采集浏览器、EMS 页面和本地采集组件均已就绪",
                passedCount,
                requirements.Count)
            : new CollectionPreflightSummary(
                false,
                "运行前检查未通过",
                $"卡在{firstFailure.Name}：{firstFailure.Resolution}",
                passedCount,
                requirements.Count);
    }
}
