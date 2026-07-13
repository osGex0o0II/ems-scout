using EmsScout.Application.Collection;

namespace EmsScout.Tests;

public sealed class CollectionPreflightSummaryTests
{
    [Fact]
    public void FirstFailedRequirementBecomesTheVisibleBlocker()
    {
        var summary = CollectionPreflightSummaryBuilder.Build(
        [
            new("本地采集组件", "重新安装应用", true),
            new("采集浏览器", "请先打开采集浏览器", false),
            new("EMS 登录", "请完成登录", false),
        ]);

        Assert.False(summary.IsReady);
        Assert.Equal("运行前检查未通过", summary.Title);
        Assert.Equal("卡在采集浏览器：请先打开采集浏览器", summary.Detail);
        Assert.Equal("1/3 已通过", summary.DetailsHeader);
    }

    [Fact]
    public void AllRequirementsProduceAnExplicitPassResult()
    {
        var summary = CollectionPreflightSummaryBuilder.Build(
        [
            new("本地采集组件", string.Empty, true),
            new("采集浏览器", string.Empty, true),
            new("EMS 页面", string.Empty, true),
        ]);

        Assert.True(summary.IsReady);
        Assert.Equal("运行前检查通过", summary.Title);
        Assert.Equal("采集浏览器、EMS 页面和本地采集组件均已就绪", summary.Detail);
        Assert.Equal("3/3 已通过", summary.DetailsHeader);
    }
}
