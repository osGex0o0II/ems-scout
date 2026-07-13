using System.Globalization;
using System.Text.Json;
using EmsScout.Application;
using EmsScout.Application.Attention;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteAttentionIssueRepository(Func<string> databasePathResolver) : IAttentionIssueRepository
{
    public async Task<IReadOnlyList<AttentionIssueRecord>> SynchronizeAsync(
        AttentionQueueSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var candidates = snapshot.Candidates.ToDictionary(item => item.IssueId, StringComparer.Ordinal);
        if (candidates.Count != snapshot.Candidates.Count)
        {
            throw new ArgumentException("Attention issue candidates must have unique issue IDs.", nameof(snapshot));
        }

        await using var connection = await SqliteDatabase.OpenExistingAsync(
            databasePathResolver,
            SqliteOpenMode.ReadWrite,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await RequireSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await LoadAllAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var byId = existing.ToDictionary(item => item.IssueId, StringComparer.Ordinal);
        var observedAt = snapshot.ObservedAt.ToUniversalTime();

        foreach (var candidate in candidates.Values)
        {
            ValidateCandidate(candidate);
            if (byId.TryGetValue(candidate.IssueId, out var previous) &&
                previous.Status == AttentionIssueStatuses.Resolved)
            {
                await InsertHistoryAsync(
                    connection,
                    transaction,
                    candidate.IssueId,
                    observedAt,
                    AttentionIssueStatuses.Resolved,
                    AttentionIssueStatuses.Unprocessed,
                    "问题再次出现",
                    cancellationToken).ConfigureAwait(false);
            }

            await UpsertAsync(connection, transaction, candidate, observedAt, cancellationToken).ConfigureAwait(false);
        }

        foreach (var issue in existing)
        {
            if (candidates.ContainsKey(issue.IssueId) ||
                !snapshot.ObservedSources.Contains(issue.SourceKey) ||
                issue.Status == AttentionIssueStatuses.Resolved)
            {
                continue;
            }

            await UpdateStateAsync(
                connection,
                transaction,
                issue.IssueId,
                AttentionIssueStatuses.Resolved,
                string.Empty,
                observedAt,
                cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(
                connection,
                transaction,
                issue.IssueId,
                observedAt,
                issue.Status,
                AttentionIssueStatuses.Resolved,
                "后续成功读取未再发现该问题",
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAllAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AttentionIssueRecord> SetStatusAsync(
        string issueId,
        string targetStatus,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(issueId))
        {
            throw new ArgumentException("Issue ID is required.", nameof(issueId));
        }

        await using var connection = await SqliteDatabase.OpenExistingAsync(
            databasePathResolver,
            SqliteOpenMode.ReadWrite,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await RequireSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadOneAsync(connection, transaction, issueId, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Attention issue was not found: " + issueId);
        var transition = AttentionIssuePolicy.ValidateTransition(current.Status, targetStatus, reason);
        var changedAt = DateTimeOffset.UtcNow;
        var nextIgnoreReason = transition.Status == AttentionIssueStatuses.Ignored ? transition.Reason : string.Empty;

        if (current.Status != transition.Status || !string.Equals(current.IgnoreReason, nextIgnoreReason, StringComparison.Ordinal))
        {
            await UpdateStateAsync(
                connection,
                transaction,
                issueId,
                transition.Status,
                nextIgnoreReason,
                changedAt,
                cancellationToken).ConfigureAwait(false);
            await InsertHistoryAsync(
                connection,
                transaction,
                issueId,
                changedAt,
                current.Status,
                transition.Status,
                transition.Reason,
                cancellationToken).ConfigureAwait(false);
        }

        var updated = await LoadOneAsync(connection, transaction, issueId, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Attention issue disappeared after update: " + issueId);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AttentionIssueCandidate candidate,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO attention_issues
                (issue_id, source_key, issue_type, severity, run_id, title, detail, scope,
                 issue_count, navigation_json, status, ignore_reason, first_seen_at, last_seen_at, resolved_at)
            VALUES
                ($issue_id, $source_key, $issue_type, $severity, $run_id, $title, $detail, $scope,
                 $issue_count, $navigation_json, 'unprocessed', '', $observed_at, $observed_at, NULL)
            ON CONFLICT(issue_id) DO UPDATE SET
                source_key = excluded.source_key,
                issue_type = excluded.issue_type,
                severity = excluded.severity,
                run_id = excluded.run_id,
                title = excluded.title,
                detail = excluded.detail,
                scope = excluded.scope,
                issue_count = excluded.issue_count,
                navigation_json = excluded.navigation_json,
                status = CASE WHEN attention_issues.status = 'resolved' THEN 'unprocessed' ELSE attention_issues.status END,
                ignore_reason = CASE WHEN attention_issues.status = 'resolved' THEN '' ELSE attention_issues.ignore_reason END,
                last_seen_at = excluded.last_seen_at,
                resolved_at = CASE WHEN attention_issues.status = 'resolved' THEN NULL ELSE attention_issues.resolved_at END
            """;
        command.Parameters.AddWithValue("$issue_id", candidate.IssueId);
        command.Parameters.AddWithValue("$source_key", candidate.SourceKey);
        command.Parameters.AddWithValue("$issue_type", candidate.IssueType);
        command.Parameters.AddWithValue("$severity", candidate.Severity.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$run_id", candidate.RunId.HasValue ? candidate.RunId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$title", candidate.Title);
        command.Parameters.AddWithValue("$detail", candidate.Detail);
        command.Parameters.AddWithValue("$scope", candidate.Scope);
        command.Parameters.AddWithValue("$issue_count", Math.Max(0, candidate.Count));
        command.Parameters.AddWithValue("$navigation_json", JsonSerializer.Serialize(candidate.Navigation));
        command.Parameters.AddWithValue("$observed_at", Format(observedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string issueId,
        string status,
        string ignoreReason,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE attention_issues
            SET status = $status,
                ignore_reason = $ignore_reason,
                resolved_at = CASE WHEN $status = 'resolved' THEN $changed_at ELSE NULL END
            WHERE issue_id = $issue_id
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$ignore_reason", ignoreReason);
        command.Parameters.AddWithValue("$changed_at", Format(changedAt));
        command.Parameters.AddWithValue("$issue_id", issueId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string issueId,
        DateTimeOffset changedAt,
        string previousStatus,
        string currentStatus,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO attention_issue_history
                (issue_id, changed_at, previous_status, current_status, reason)
            VALUES ($issue_id, $changed_at, $previous_status, $current_status, $reason)
            """;
        command.Parameters.AddWithValue("$issue_id", issueId);
        command.Parameters.AddWithValue("$changed_at", Format(changedAt));
        command.Parameters.AddWithValue("$previous_status", previousStatus);
        command.Parameters.AddWithValue("$current_status", currentStatus);
        command.Parameters.AddWithValue("$reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<AttentionIssueRecord>> LoadAllAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT issue_id, source_key, issue_type, severity, run_id, title, detail, scope,
                   issue_count, navigation_json, status, ignore_reason, first_seen_at, last_seen_at, resolved_at
            FROM attention_issues
            ORDER BY status = 'resolved',
                     CASE severity WHEN 'danger' THEN 0 WHEN 'warning' THEN 1 WHEN 'info' THEN 2 ELSE 3 END,
                     datetime(last_seen_at) DESC,
                     issue_id
            """;
        var rows = new List<AttentionIssueRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    private static async Task<AttentionIssueRecord?> LoadOneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string issueId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT issue_id, source_key, issue_type, severity, run_id, title, detail, scope,
                   issue_count, navigation_json, status, ignore_reason, first_seen_at, last_seen_at, resolved_at
            FROM attention_issues
            WHERE issue_id = $issue_id
            """;
        command.Parameters.AddWithValue("$issue_id", issueId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static AttentionIssueRecord Read(SqliteDataReader reader)
    {
        var severityText = SqliteValueReader.ReadString(reader, "severity");
        var severity = Enum.TryParse<OverviewMetricKind>(severityText, ignoreCase: true, out var parsedSeverity)
            ? parsedSeverity
            : OverviewMetricKind.Neutral;
        var navigationJson = SqliteValueReader.ReadString(reader, "navigation_json");
        AttentionNavigationTarget navigation;
        try
        {
            navigation = JsonSerializer.Deserialize<AttentionNavigationTarget>(navigationJson)
                         ?? new AttentionNavigationTarget("audit");
        }
        catch (JsonException)
        {
            navigation = new AttentionNavigationTarget("audit");
        }

        return new AttentionIssueRecord(
            IssueId: SqliteValueReader.ReadString(reader, "issue_id"),
            SourceKey: SqliteValueReader.ReadString(reader, "source_key"),
            IssueType: SqliteValueReader.ReadString(reader, "issue_type"),
            Severity: severity,
            RunId: SqliteValueReader.ReadNullableInt64(reader, "run_id"),
            Title: SqliteValueReader.ReadString(reader, "title"),
            Detail: SqliteValueReader.ReadString(reader, "detail"),
            Scope: SqliteValueReader.ReadString(reader, "scope"),
            Count: SqliteValueReader.ReadInt32(reader, "issue_count"),
            Navigation: navigation,
            Status: SqliteValueReader.ReadString(reader, "status"),
            IgnoreReason: SqliteValueReader.ReadString(reader, "ignore_reason"),
            FirstSeenAt: ParseDate(SqliteValueReader.ReadString(reader, "first_seen_at")),
            LastSeenAt: ParseDate(SqliteValueReader.ReadString(reader, "last_seen_at")),
            ResolvedAt: ParseNullableDate(SqliteValueReader.ReadString(reader, "resolved_at")));
    }

    private static async Task RequireSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await SqliteSchemaGuard.RequireCurrentAsync(
            connection,
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["attention_issues"] = ["issue_id", "source_key", "status", "navigation_json", "last_seen_at"],
                ["attention_issue_history"] = ["id", "issue_id", "previous_status", "current_status", "reason"],
            },
            ["idx_attention_issues_source", "idx_attention_issues_status", "idx_attention_history_issue"],
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateCandidate(AttentionIssueCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.IssueId) || string.IsNullOrWhiteSpace(candidate.SourceKey))
        {
            throw new ArgumentException("Attention issue candidates require an issue ID and source key.");
        }
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;

    private static DateTimeOffset? ParseNullableDate(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : ParseDate(value);
}
