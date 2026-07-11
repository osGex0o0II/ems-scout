using System.Text.Json;
using EmsScout.Application.Workflows;

namespace EmsScout.Tests;

public sealed class WorkflowControlWriterTests
{
    [Fact]
    public void CreateCancel_WritesCanonicalControlEnvelope()
    {
        var line = WorkflowControlWriter.CreateCancel(
            "collect-20260711-001",
            "user_requested",
            new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero));

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal(5, root.EnumerateObject().Count());
        Assert.Equal("ems.workflow-control/v1", root.GetProperty("contractVersion").GetString());
        Assert.Equal("collect-20260711-001", root.GetProperty("workflowId").GetString());
        Assert.Equal("2026-07-11T08:00:00.000Z", root.GetProperty("timestamp").GetString());
        Assert.Equal("cancel", root.GetProperty("type").GetString());
        Assert.Equal("user_requested", root.GetProperty("reason").GetString());
    }

    [Fact]
    public void CreateCancel_OmitsNullReason()
    {
        var line = WorkflowControlWriter.CreateCancel("workflow-1", null);

        using var document = JsonDocument.Parse(line);
        Assert.False(document.RootElement.TryGetProperty("reason", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad workflow id")]
    [InlineData("/absolute/path")]
    public void CreateCancel_RejectsInvalidWorkflowId(string workflowId)
    {
        Assert.Throws<ArgumentException>(() => WorkflowControlWriter.CreateCancel(workflowId));
    }
}
