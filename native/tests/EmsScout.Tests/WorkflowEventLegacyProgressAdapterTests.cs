using EmsScout.Application.Workflows;

namespace EmsScout.Tests;

public sealed class WorkflowEventLegacyProgressAdapterTests
{
    [Fact]
    public void AdaptsEnumerationProgressWithoutCouplingItToTheV1Parser()
    {
        var adapted = WorkflowEventLegacyProgressAdapter.TryAdapt(
            "[PROGRESS]{\"t\":\"c\",\"bldg\":\"1号\",\"cards\":20,\"acc\":40,\"totalSa\":10,\"curSa\":4}",
            "collect-1",
            2,
            DateTimeOffset.Parse("2026-07-11T08:00:00+08:00"),
            "enumeration",
            out var workflowEvent);

        Assert.True(adapted);
        Assert.NotNull(workflowEvent);
        Assert.Equal(WorkflowEventContractV1.Version, workflowEvent.ContractVersion);
        Assert.Equal(WorkflowEventType.Progress, workflowEvent.Type);
        Assert.Equal(DateTimeOffset.Parse("2026-07-11T00:00:00Z"), workflowEvent.Timestamp);
        Assert.Equal(40d, workflowEvent.Progress!.Percent!.Value);
        Assert.Equal(4L, workflowEvent.Progress.Current!.Value);
        Assert.Equal(10L, workflowEvent.Progress.Total!.Value);
        Assert.Equal("sub_area", workflowEvent.Progress.Unit);
        Assert.Equal("1号", workflowEvent.Progress.Data!.Value.GetProperty("bldg").GetString());
    }

    [Fact]
    public void AdaptsRealtimePhaseDeviceCountersAndMessage()
    {
        var adapted = WorkflowEventLegacyProgressAdapter.TryAdapt(
            "[PROGRESS] {\"phase\":\"realtime_batch\",\"percent\":25,\"deviceDone\":5,\"deviceTotal\":20,\"message\":\"采集中\"}",
            "realtime-1",
            8,
            DateTimeOffset.UtcNow,
            "realtime",
            out var workflowEvent);

        Assert.True(adapted);
        Assert.Equal("realtime_batch", workflowEvent!.Stage);
        Assert.Equal(25d, workflowEvent.Progress!.Percent!.Value);
        Assert.Equal(5L, workflowEvent.Progress.Current!.Value);
        Assert.Equal(20L, workflowEvent.Progress.Total!.Value);
        Assert.Equal("device", workflowEvent.Progress.Unit);
        Assert.Equal("采集中", workflowEvent.Progress.Message);
    }

    [Theory]
    [InlineData("human log")]
    [InlineData("[PROGRESS]")]
    [InlineData("[PROGRESS]{bad json")]
    [InlineData("[PROGRESS][]")]
    public void DoesNotAcceptNonProgressOrMalformedLegacyLines(string line)
    {
        Assert.False(WorkflowEventLegacyProgressAdapter.TryAdapt(
            line,
            "workflow-1",
            1,
            DateTimeOffset.UtcNow,
            "sidecar",
            out var workflowEvent));
        Assert.Null(workflowEvent);
    }
}
