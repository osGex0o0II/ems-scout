using EmsScout.Application;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

internal static class AreaGroupRuleOrderMigration
{
    private const string MigrationName = "area-group-rule-order-v2";

    public static async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "area_group_rules", cancellationToken).ConfigureAwait(false) ||
            !await TableExistsAsync(connection, transaction, "ems_schema_migrations", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM ems_schema_migrations WHERE name = $name";
            exists.Parameters.AddWithValue("$name", MigrationName);
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                return;
        }

        SqliteTransaction? ownedTransaction = null;
        try
        {
            ownedTransaction = transaction is null
                ? (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                : null;
            var activeTransaction = transaction ?? ownedTransaction!;

            var groups = await LoadGroupIdsAsync(connection, activeTransaction, cancellationToken).ConfigureAwait(false);
            foreach (var groupId in groups)
                await RenumberGroupAsync(connection, activeTransaction, groupId, cancellationToken).ConfigureAwait(false);

            await using var mark = connection.CreateCommand();
            mark.Transaction = activeTransaction;
            mark.CommandText = "INSERT INTO ems_schema_migrations(name, applied_at) VALUES ($name, $applied_at)";
            mark.Parameters.AddWithValue("$name", MigrationName);
            mark.Parameters.AddWithValue("$applied_at", StoredTimestamp.FormatLocal(DateTimeOffset.Now));
            await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (ownedTransaction is not null)
                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ownedTransaction is not null)
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static async Task RenumberGroupAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long groupId,
        CancellationToken cancellationToken)
    {
        var rules = new List<long>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT id
                FROM area_group_rules
                WHERE group_id = $group_id
                ORDER BY rule_order, id
                """;
            read.Parameters.AddWithValue("$group_id", groupId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rules.Add(reader.GetInt64(0));
        }

        for (var index = 0; index < rules.Count; index++)
        {
            await using var temporary = connection.CreateCommand();
            temporary.Transaction = transaction;
            temporary.CommandText = "UPDATE area_group_rules SET rule_order = $rule_order WHERE id = $id AND group_id = $group_id";
            temporary.Parameters.AddWithValue("$rule_order", -(index + 1));
            temporary.Parameters.AddWithValue("$id", rules[index]);
            temporary.Parameters.AddWithValue("$group_id", groupId);
            await temporary.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < rules.Count; index++)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE area_group_rules SET rule_order = $rule_order WHERE id = $id AND group_id = $groupId";
            update.Parameters.AddWithValue("$rule_order", index + 1);
            update.Parameters.AddWithValue("$id", rules[index]);
            update.Parameters.AddWithValue("$groupId", groupId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<long>> LoadGroupIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var groups = new List<long>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT group_id FROM area_group_rules ORDER BY group_id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            groups.Add(reader.GetInt64(0));
        return groups;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }
}
