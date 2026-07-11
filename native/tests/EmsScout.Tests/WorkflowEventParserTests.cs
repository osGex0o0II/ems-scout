using EmsScout.Application.Workflows;

namespace EmsScout.Tests;

public sealed class WorkflowEventParserTests
{
    [Fact]
    public void ParsesTypedProgressEvent()
    {
        var workflowEvent = WorkflowEventParser.Parse("""
            {"contractVersion":"ems.workflow-event/v1","workflowId":"collect-1","seq":2,"timestamp":"2026-07-11T00:00:01.000Z","type":"progress","stage":"enumeration","progress":{"percent":40,"current":4,"total":10,"unit":"sub_area","data":{"bldg":"1号","cards":20}}}
            """);

        Assert.Equal(WorkflowEventType.Progress, workflowEvent.Type);
        Assert.Equal("collect-1", workflowEvent.WorkflowId);
        Assert.Equal(2, workflowEvent.Sequence);
        Assert.Equal("enumeration", workflowEvent.Stage);
        Assert.Equal(40d, workflowEvent.Progress!.Percent!.Value);
        Assert.Equal(4L, workflowEvent.Progress.Current!.Value);
        Assert.Equal("1号", workflowEvent.Progress.Data!.Value.GetProperty("bldg").GetString());
    }

    [Theory]
    [InlineData("{\"contractVersion\":\"v0\",\"workflowId\":\"w1\",\"seq\":1,\"timestamp\":\"2026-07-11T00:00:00Z\",\"type\":\"started\",\"stage\":\"sidecar\"}", "contractVersion")]
    [InlineData("{\"contractVersion\":\"ems.workflow-event/v1\",\"workflowId\":\"w1\",\"seq\":1,\"timestamp\":\"2026-07-11T00:00:00Z\",\"type\":\"started\",\"stage\":\"sidecar\",\"extra\":true}", "extra")]
    [InlineData("{\"contractVersion\":\"ems.workflow-event/v1\",\"workflowId\":\"w1\",\"seq\":1,\"seq\":2,\"timestamp\":\"2026-07-11T00:00:00Z\",\"type\":\"started\",\"stage\":\"sidecar\"}", "Duplicate")]
    [InlineData("{\"contractVersion\":\"ems.workflow-event/v1\",\"workflowId\":\"w1\",\"seq\":1,\"timestamp\":\"07/11/2026 00:00:00Z\",\"type\":\"started\",\"stage\":\"sidecar\"}", "timestamp")]
    public void RejectsVersionDriftUnknownFieldsAndDuplicateProperties(string json, string expected)
    {
        var error = Assert.Throws<WorkflowEventParseException>(() => WorkflowEventParser.Parse(json));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("succeeded", 2)]
    [InlineData("rejected", 0)]
    [InlineData("unknown", 1)]
    public void RejectsInvalidTerminalSemantics(string outcome, int exitCode)
    {
        var json = $$"""
            {"contractVersion":"ems.workflow-event/v1","workflowId":"w1","seq":2,"timestamp":"2026-07-11T00:00:01Z","type":"terminal","stage":"sidecar","outcome":"{{outcome}}","exitCode":{{exitCode}}}
            """;

        Assert.Throws<WorkflowEventParseException>(() => WorkflowEventParser.Parse(json));
    }

    [Fact]
    public void ValidatesACompleteContiguousStreamWithOneTerminalEvent()
    {
        var events = WorkflowEventStreamParser.Parse(
        [
            "{\"contractVersion\":\"ems.workflow-event/v1\",\"workflowId\":\"w1\",\"seq\":1,\"timestamp\":\"2026-07-11T00:00:00Z\",\"type\":\"started\",\"stage\":\"sidecar\"}",
            "{\"contractVersion\":\"ems.workflow-event/v1\",\"workflowId\":\"w1\",\"seq\":2,\"timestamp\":\"2026-07-11T00:00:01Z\",\"type\":\"terminal\",\"stage\":\"sidecar\",\"outcome\":\"succeeded\",\"exitCode\":0}",
        ]);

        Assert.Equal(2, events.Count);
        Assert.Equal(WorkflowTerminalOutcome.Succeeded, events[^1].Outcome);
    }

    [Fact]
    public void RejectsSequenceGapsEventsAfterTerminalAndMissingTerminal()
    {
        var started = WorkflowEventParser.Parse(
            "{\"contractVersion\":\"ems.workflow-event/v1\",\"workflowId\":\"w1\",\"seq\":1,\"timestamp\":\"2026-07-11T00:00:00Z\",\"type\":\"started\",\"stage\":\"sidecar\"}");
        var gap = WorkflowEventParser.Parse(
            "{\"contractVersion\":\"ems.workflow-event/v1\",\"workflowId\":\"w1\",\"seq\":3,\"timestamp\":\"2026-07-11T00:00:01Z\",\"type\":\"progress\",\"stage\":\"sidecar\",\"progress\":{\"percent\":50}}");
        var terminal = WorkflowEventParser.Parse(
            "{\"contractVersion\":\"ems.workflow-event/v1\",\"workflowId\":\"w1\",\"seq\":2,\"timestamp\":\"2026-07-11T00:00:01Z\",\"type\":\"terminal\",\"stage\":\"sidecar\",\"outcome\":\"succeeded\",\"exitCode\":0}");
        var validator = new WorkflowEventStreamValidator();
        validator.Accept(started);

        Assert.Throws<WorkflowEventParseException>(() => validator.Accept(gap));
        Assert.Throws<WorkflowEventParseException>(() => validator.EnsureComplete());

        validator.Accept(terminal);
        Assert.Throws<WorkflowEventParseException>(() => validator.Accept(terminal with { Sequence = 3 }));
    }
}
