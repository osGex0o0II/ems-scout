using EmsScout.Application.Attention;

namespace EmsScout.Tests;

public sealed class AttentionIssuePolicyTests
{
    [Theory]
    [InlineData(AttentionIssueStatuses.Unprocessed, AttentionIssueStatuses.Acknowledged)]
    [InlineData(AttentionIssueStatuses.Unprocessed, AttentionIssueStatuses.Resolved)]
    [InlineData(AttentionIssueStatuses.Acknowledged, AttentionIssueStatuses.Unprocessed)]
    [InlineData(AttentionIssueStatuses.Acknowledged, AttentionIssueStatuses.Resolved)]
    [InlineData(AttentionIssueStatuses.Ignored, AttentionIssueStatuses.Unprocessed)]
    [InlineData(AttentionIssueStatuses.Ignored, AttentionIssueStatuses.Resolved)]
    [InlineData(AttentionIssueStatuses.Resolved, AttentionIssueStatuses.Unprocessed)]
    public void AllowsSupportedTransitions(string currentStatus, string targetStatus)
    {
        var result = AttentionIssuePolicy.ValidateTransition(currentStatus, targetStatus, null);

        Assert.Equal(targetStatus, result.Status);
        Assert.Equal(string.Empty, result.Reason);
    }

    [Fact]
    public void IgnoreRequiresAndTrimsReason()
    {
        Assert.Throws<ArgumentException>(() => AttentionIssuePolicy.ValidateTransition(
            AttentionIssueStatuses.Unprocessed,
            AttentionIssueStatuses.Ignored,
            "   "));

        var result = AttentionIssuePolicy.ValidateTransition(
            AttentionIssueStatuses.Unprocessed,
            AttentionIssueStatuses.Ignored,
            "  现场已确认是计划停机  ");

        Assert.Equal(AttentionIssueStatuses.Ignored, result.Status);
        Assert.Equal("现场已确认是计划停机", result.Reason);
    }

    [Theory]
    [InlineData("unknown", AttentionIssueStatuses.Unprocessed)]
    [InlineData(AttentionIssueStatuses.Unprocessed, "unknown")]
    public void RejectsUnknownStatuses(string currentStatus, string targetStatus)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AttentionIssuePolicy.ValidateTransition(currentStatus, targetStatus, null));
    }

    [Fact]
    public void ResolvedIssueMustReopenBeforeAnotherManualState()
    {
        Assert.Throws<InvalidOperationException>(() => AttentionIssuePolicy.ValidateTransition(
            AttentionIssueStatuses.Resolved,
            AttentionIssueStatuses.Acknowledged,
            null));
        Assert.Throws<InvalidOperationException>(() => AttentionIssuePolicy.ValidateTransition(
            AttentionIssueStatuses.Resolved,
            AttentionIssueStatuses.Ignored,
            "重新忽略"));
    }
}
