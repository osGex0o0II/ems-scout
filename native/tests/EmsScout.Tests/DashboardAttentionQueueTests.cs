using EmsScout.Application;
using EmsScout.Application.Attention;
using EmsScout.Application.Collection;
using EmsScout.Application.Devices;
using EmsScout.Application.Quality;
using EmsScout.Infrastructure.Migrations;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class DashboardAttentionQueueTests
{
    [Fact]
    public async Task CurrentContextSynchronizesStableQueueAndHistoricalContextDoesNotWrite()
    {
        var attention = new RecordingAttentionRepository();
        var service = new DashboardOverviewService(
            new DeviceRepository(),
            new QualityService(),
            new RealtimeQualityService(),
            new ReconciliationService(),
            new RunRepository(),
            attention);

        var current = await service.LoadAsync();

        Assert.Equal(1, attention.SynchronizeCalls);
        Assert.NotNull(attention.LastSnapshot);
        Assert.Contains("inventory", attention.LastSnapshot.ObservedSources);
        Assert.Contains("quality", attention.LastSnapshot.ObservedSources);
        Assert.Contains(attention.LastSnapshot.Candidates, item =>
            item.IssueId == "inventory:communication:offline" &&
            item.RunId == 17 &&
            item.Navigation.CommunicationState == "离线");
        Assert.Contains(current.Risks, item => item.Status == AttentionIssueStatuses.Unprocessed);

        attention.Reset();
        var historical = await service.LoadAsync(12);

        Assert.Equal(0, attention.SynchronizeCalls);
        Assert.NotEmpty(historical.Risks);
        Assert.All(historical.Risks.Where(item => item.IsActionable), item =>
            Assert.Equal(AttentionIssueStatuses.Unprocessed, item.Status));
    }

    [Fact]
    public async Task FailedSourceIsNotMarkedObservedAndUsesSafeRecoveryText()
    {
        var attention = new RecordingAttentionRepository();
        var service = new DashboardOverviewService(
            new DeviceRepository(),
            new FailingQualityService(),
            new RealtimeQualityService(),
            new ReconciliationService(),
            new RunRepository(),
            attention);

        var overview = await service.LoadAsync();

        Assert.DoesNotContain("quality", attention.LastSnapshot!.ObservedSources);
        var failure = Assert.Single(overview.Risks, item => item.IssueId == "quality:read-error");
        Assert.DoesNotContain("secret-provider-message", failure.Detail, StringComparison.Ordinal);
        Assert.Contains("审计", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentLoadResolvesAndHidesLegacyWatchAttentionIssues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ems-scout-dashboard-attention-" + Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "ac.db");
        Directory.CreateDirectory(directory);
        try
        {
            await new SqliteSchemaMigrator().CreateNewAsync(databasePath);
            var attention = new SqliteAttentionIssueRepository(() => databasePath);
            await attention.SynchronizeAsync(new AttentionQueueSnapshot(
                [new AttentionIssueCandidate(
                    "watch:state:abnormal",
                    "watch",
                    "state",
                    OverviewMetricKind.Danger,
                    16,
                    "关注设备发生开关变化",
                    "旧版本遗留关注设备问题",
                    "关注设备",
                    2,
                    new AttentionNavigationTarget("devices", WatchState: "abnormal"))],
                new HashSet<string>(["watch"], StringComparer.Ordinal),
                new DateTimeOffset(2026, 7, 12, 8, 0, 0, TimeSpan.Zero)));
            var service = new DashboardOverviewService(
                new DeviceRepository(),
                new QualityService(),
                new RealtimeQualityService(),
                new ReconciliationService(),
                new RunRepository(),
                attention);

            var overview = await service.LoadAsync();

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM attention_issues WHERE issue_id = 'watch:state:abnormal'";
            Assert.Equal(AttentionIssueStatuses.Resolved, Convert.ToString(await command.ExecuteScalarAsync()));
            Assert.DoesNotContain(overview.Risks, risk => risk.SourceKey == "watch");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingAttentionRepository : IAttentionIssueRepository
    {
        public int SynchronizeCalls { get; private set; }
        public AttentionQueueSnapshot? LastSnapshot { get; private set; }

        public Task<IReadOnlyList<AttentionIssueRecord>> SynchronizeAsync(
            AttentionQueueSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            SynchronizeCalls++;
            LastSnapshot = snapshot;
            return Task.FromResult<IReadOnlyList<AttentionIssueRecord>>(snapshot.Candidates.Select(candidate => new AttentionIssueRecord(
                candidate.IssueId,
                candidate.SourceKey,
                candidate.IssueType,
                candidate.Severity,
                candidate.RunId,
                candidate.Title,
                candidate.Detail,
                candidate.Scope,
                candidate.Count,
                candidate.Navigation,
                AttentionIssueStatuses.Unprocessed,
                string.Empty,
                snapshot.ObservedAt,
                snapshot.ObservedAt,
                null)).ToList());
        }

        public Task<AttentionIssueRecord> SetStatusAsync(
            string issueId,
            string targetStatus,
            string? reason = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Reset()
        {
            SynchronizeCalls = 0;
            LastSnapshot = null;
        }
    }

    private sealed class DeviceRepository : IDeviceReadRepository
    {
        private static readonly DeviceFacets Facets = new(
            Total: 10, Running: 3, Stopped: 5, Offline: 2, Unknown: 0,
            PublicArea: 2, PrivateArea: 8, TemperatureIssues: 0, NeedsReview: 0);

        public Task<DeviceListResult> SearchAsync(DeviceQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceListResult(10, [], Facets));

        public Task<DeviceFilterOptions> LoadFilterOptionsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeviceFilterOptions> LoadFilterOptionsAsync(DeviceQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class QualityService : IQualityAuditService
    {
        public Task<QualityAuditReport?> LoadLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<QualityAuditReport?>(new QualityAuditReport(
                "quality.json", "2026-07-13T08:00:00Z", "2026-07-13 16:00", 17,
                new QualityAuditSummary(10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                [], false, string.Empty));
    }

    private sealed class FailingQualityService : IQualityAuditService
    {
        public Task<QualityAuditReport?> LoadLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<QualityAuditReport?>(new InvalidOperationException("secret-provider-message"));
    }

    private sealed class RealtimeQualityService : IRealtimeQualityAuditService
    {
        public Task<RealtimeQualityAuditReport?> LoadLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<RealtimeQualityAuditReport?>(new RealtimeQualityAuditReport(
                "realtime.json", "2026-07-13T08:00:00Z", "summary.json", 10, 10,
                true, 0, 0, 0, [], [], [], string.Empty));
    }

    private sealed class ReconciliationService : IRealtimeReconciliationService
    {
        public Task<RealtimeReconciliationResult> AnalyzeAsync(
            RealtimeReconciliationQuery query,
            CancellationToken cancellationToken = default) => Task.FromResult(new RealtimeReconciliationResult(
                new RealtimeReconciliationSummary(10, 10, 0, 0, 10, 0, 0, 0,
                    new Dictionary<string, int>(), DateTimeOffset.UtcNow),
                []));
    }

    private sealed class RunRepository : ICollectionRunRepository
    {
        public Task<IReadOnlyList<CollectionRunRecord>> ListAsync(int limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CollectionRunRecord>>([
                new CollectionRunRecord(17, "run17", "", "", "", "completed", "full", [], "", "",
                    10, 3, 5, 2, 0, "{}", false, "")]);

        public Task<CollectionRunRecord> SetAnomalyAsync(long runId, bool isAnomaly, string note, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CollectionRunRestoreResult> RestoreCurrentAsync(long runId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CollectionRunDeleteResult> DeleteAsync(long runId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
