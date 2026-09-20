using EmsScout.Application.Collection;

namespace EmsScout.Tests;

public sealed class AuditIssueViewModelTests
{
    [Fact]
    public void CollectionRunRowShowsDurationAndStableCollectionMode()
    {
        var row = CreateRun("stable-full");

        Assert.Equal("1 分 30 秒", CollectionRunDisplay.DurationLabel(row));
        Assert.Equal("稳定模式", CollectionRunDisplay.CollectionModeLabel(row));
    }

    [Fact]
    public void CollectionRunRowShowsFastModeAndDashForUnknownMode()
    {
        Assert.Equal("快速模式", CollectionRunDisplay.CollectionModeLabel(CreateRun("fast-batch")));
        Assert.Equal("-", CollectionRunDisplay.CollectionModeLabel(CreateRun("legacy-mode")));
    }

    private static CollectionRunRecord CreateRun(string collectionMode) => new(
        Id: 24,
        RunKey: "run-24",
        StartedAt: "2026-09-21T10:00:00+08:00",
        CompletedAt: "2026-09-21T10:01:30+08:00",
        ImportedAt: "2026-09-21T10:01:30+08:00",
        Status: "completed",
        Scope: "partial",
        Buildings: ["1号"],
        JsonPath: "enum.json",
        DbSnapshotPath: "",
        CardCount: 1,
        OnCount: 0,
        OffCount: 1,
        OfflineCount: 0,
        UnknownCount: 0,
        QualitySummary: "{}",
        IsAnomaly: false,
        Note: "",
        CollectionMode: collectionMode);
}
