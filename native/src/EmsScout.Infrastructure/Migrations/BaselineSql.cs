using System.Reflection;
using System.Text.RegularExpressions;

namespace EmsScout.Infrastructure.Migrations;

internal static partial class BaselineSql
{
    private static readonly Lazy<string> CachedSql = new(() => LoadAndValidate(".Migrations.Sql.V001__baseline.sql"));

    public static string Text => CachedSql.Value;

    internal static string LoadAndValidate(string resourceSuffix)
    {
        var assembly = typeof(BaselineSql).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded migration resource was not found: " + resourceSuffix);
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException("Embedded migration resource could not be opened: " + resourceName);
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();

        if (DestructiveSqlPattern().IsMatch(sql))
        {
            throw new InvalidOperationException(
                $"Migration resource '{resourceSuffix}' must remain additive; destructive SQL was detected.");
        }

        return sql;
    }

    [GeneratedRegex(@"\b(DROP|DELETE|UPDATE|REPLACE|TRUNCATE|VACUUM|REINDEX)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DestructiveSqlPattern();
}

internal static class FreshCoreSql
{
    private static readonly Lazy<string> CachedSql = new(
        () => BaselineSql.LoadAndValidate(".Migrations.Sql.V000__fresh_core.sql"));

    public static string Text => CachedSql.Value;
}

internal static class IdentitySql
{
    private static readonly Lazy<string> CachedSql = new(
        () => BaselineSql.LoadAndValidate(".Migrations.Sql.V002__identity.sql"));

    public static string Text => CachedSql.Value;
}

internal static class ScheduleSql
{
    private static readonly Lazy<string> CachedSql = new(
        () => BaselineSql.LoadAndValidate(".Migrations.Sql.V003__schedule_groups.sql"));

    public static string Text => CachedSql.Value;
}

internal static class ScheduleIntegritySql
{
    private static readonly Lazy<string> CachedSql = new(
        () => BaselineSql.LoadAndValidate(".Migrations.Sql.V004__schedule_integrity.sql"));

    public static string Text => CachedSql.Value;
}

internal static class AttentionQueueSql
{
    private static readonly Lazy<string> CachedSql = new(
        () => BaselineSql.LoadAndValidate(".Migrations.Sql.V005__attention_queue.sql"));

    public static string Text => CachedSql.Value;
}
