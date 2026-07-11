using EmsScout.Application.Workflows;

namespace EmsScout.Tests;

public sealed class CollectionProgressPresenterTests
{
    [Fact]
    public void PresentsCanonicalPercentProgress()
    {
        var result = CollectionProgressPresenter.Parse("""
            {"percent":42.5,"message":"正在导入"}
            """);

        Assert.Equal("正在导入", result.LogText);
        Assert.Equal(42.5, result.Percent);
    }

    [Fact]
    public void PresentsLegacyEnumerationProgress()
    {
        var result = CollectionProgressPresenter.Parse("""
            {"bldg":"6号","curSa":3,"totalSa":31,"cards":20,"acc":60}
            """);

        Assert.Equal("采集进度：6号 子区 3/31，本页 20 张，累计 60 张", result.LogText);
        Assert.Equal("6号", result.Building);
        Assert.Equal(3, result.Current);
        Assert.Equal(31, result.Total);
    }

    [Fact]
    public void PresentsRealtimeDeviceProgress()
    {
        var result = CollectionProgressPresenter.Parse("""
            {"building":"1号","deviceDone":12,"deviceTotal":50}
            """);

        Assert.Equal("实时详情：1号 设备 12/50", result.LogText);
        Assert.Equal("1号", result.Building);
        Assert.Equal(12, result.Current);
        Assert.Equal(50, result.Total);
    }

    [Fact]
    public void PreservesMalformedPayloadForDiagnosis()
    {
        var result = CollectionProgressPresenter.Parse("not-json");

        Assert.Equal("采集进度 not-json", result.LogText);
        Assert.False(result.IsValid);
        Assert.Null(result.Percent);
        Assert.Equal(0, result.Total);
    }
}
