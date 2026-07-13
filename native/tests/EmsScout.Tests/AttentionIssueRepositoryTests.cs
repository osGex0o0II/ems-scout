using EmsScout.Application;
using EmsScout.Application.Attention;
using EmsScout.Infrastructure.Migrations;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class AttentionIssueRepositoryTests
{
    [Fact]
    public async Task SynchronizesManualStateAutoResolutionAndReopenWithHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ems-scout-attention-" + Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "ac.db");
        Directory.CreateDirectory(directory);
        try
        {
            await new SqliteSchemaMigrator().CreateNewAsync(databasePath);
            var repository = new SqliteAttentionIssueRepository(() => databasePath);
            var firstSeen = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);

            var initial = await repository.SynchronizeAsync(new AttentionQueueSnapshot(
                [Candidate("quality:summary:issues", "quality", 2), Candidate("realtime:devices:invalid", "realtime", 3)],
                new HashSet<string>(["quality", "realtime"], StringComparer.Ordinal),
                firstSeen));

            Assert.All(initial, issue => Assert.Equal(AttentionIssueStatuses.Unprocessed, issue.Status));
            await repository.SetStatusAsync("quality:summary:issues", AttentionIssueStatuses.Acknowledged);
            var ignored = await repository.SetStatusAsync(
                "realtime:devices:invalid",
                AttentionIssueStatuses.Ignored,
                "  已安排现场复核  ");
            Assert.Equal("已安排现场复核", ignored.IgnoreReason);

            var retained = await repository.SynchronizeAsync(new AttentionQueueSnapshot(
                [Candidate("quality:summary:issues", "quality", 5), Candidate("realtime:devices:invalid", "realtime", 4)],
                new HashSet<string>(["quality", "realtime"], StringComparer.Ordinal),
                firstSeen.AddMinutes(5)));
            Assert.Equal(AttentionIssueStatuses.Acknowledged, retained.Single(item => item.IssueId == "quality:summary:issues").Status);
            Assert.Equal(AttentionIssueStatuses.Ignored, retained.Single(item => item.IssueId == "realtime:devices:invalid").Status);
            Assert.Equal(5, retained.Single(item => item.IssueId == "quality:summary:issues").Count);

            var partiallyObserved = await repository.SynchronizeAsync(new AttentionQueueSnapshot(
                [],
                new HashSet<string>(["quality"], StringComparer.Ordinal),
                firstSeen.AddMinutes(10)));
            Assert.Equal(AttentionIssueStatuses.Resolved, partiallyObserved.Single(item => item.IssueId == "quality:summary:issues").Status);
            Assert.Equal(AttentionIssueStatuses.Ignored, partiallyObserved.Single(item => item.IssueId == "realtime:devices:invalid").Status);

            var reopened = await repository.SynchronizeAsync(new AttentionQueueSnapshot(
                [Candidate("quality:summary:issues", "quality", 1)],
                new HashSet<string>(["quality"], StringComparer.Ordinal),
                firstSeen.AddMinutes(15)));
            var quality = reopened.Single(item => item.IssueId == "quality:summary:issues");
            Assert.Equal(AttentionIssueStatuses.Unprocessed, quality.Status);
            Assert.Null(quality.ResolvedAt);

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM attention_issue_history";
            Assert.Equal(4L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsIgnoredStatusWithoutReasonAndUnknownIssue()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ems-scout-attention-" + Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "ac.db");
        Directory.CreateDirectory(directory);
        try
        {
            await new SqliteSchemaMigrator().CreateNewAsync(databasePath);
            var repository = new SqliteAttentionIssueRepository(() => databasePath);
            await repository.SynchronizeAsync(new AttentionQueueSnapshot(
                [Candidate("quality:summary:issues", "quality", 2)],
                new HashSet<string>(["quality"], StringComparer.Ordinal),
                DateTimeOffset.UtcNow));

            await Assert.ThrowsAsync<ArgumentException>(() => repository.SetStatusAsync(
                "quality:summary:issues",
                AttentionIssueStatuses.Ignored,
                " "));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SetStatusAsync(
                "missing",
                AttentionIssueStatuses.Acknowledged));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AttentionIssueCandidate Candidate(string issueId, string sourceKey, int count) => new(
        IssueId: issueId,
        SourceKey: sourceKey,
        IssueType: "summary",
        Severity: OverviewMetricKind.Warning,
        RunId: 17,
        Title: "待处理事项",
        Detail: "测试证据",
        Scope: "全部楼栋",
        Count: count,
        Navigation: new AttentionNavigationTarget("audit", QuickFilter: "needs_review"));
}
