using EmsScout.Application.Collection;

namespace EmsScout.Tests;

public sealed class CollectionRunGovernanceTests
{
    [Fact]
    public void OperationUsesUniqueIdAndDoesNotContainDevicePayload()
    {
        var operation = RunOperationRecord.Delete("field-41", "batch-41", 41, 3, "completed");

        Assert.NotEqual(Guid.Empty, operation.OperationId);
        Assert.DoesNotContain("payload", operation.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("device", operation.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActivityGuardRemainsActiveUntilEveryLeaseIsDisposed()
    {
        var guard = new CollectionRunActivityRegistry();
        using var first = guard.Begin();
        using var second = guard.Begin();

        Assert.True(guard.IsActive);
        first.Dispose();
        first.Dispose();
        Assert.True(guard.IsActive);
        second.Dispose();
        Assert.False(guard.IsActive);
    }

    [Fact]
    public void DeleteImpactIsBlockedWhenItHasAnyBlockingReason()
    {
        var impact = new RunDeleteImpact(
            41,
            "field-41",
            false,
            10,
            10,
            1,
            1,
            1,
            [],
            ["当前数据正在使用该批次"]);

        Assert.False(impact.CanDelete);
    }
}
