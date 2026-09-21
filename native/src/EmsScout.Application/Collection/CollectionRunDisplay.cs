namespace EmsScout.Application.Collection;

public static class CollectionRunDisplay
{
    public static string CompletedAtLabel(CollectionRunRecord run) =>
        FormatTimestamp(run.CompletedAt, run.ImportedAt);

    public static string DurationLabel(CollectionRunRecord run)
    {
        var elapsed = run.DurationMs is >= 0
            ? TimeSpan.FromMilliseconds(run.DurationMs.Value)
            : ParseTimestampDuration(run);

        if (elapsed is null)
        {
            return "-";
        }

        return elapsed.Value.TotalHours >= 1
            ? $"{(int)elapsed.Value.TotalHours} 小时 {elapsed.Value.Minutes} 分"
            : $"{(int)elapsed.Value.TotalMinutes} 分 {elapsed.Value.Seconds} 秒";
    }

    public static string CollectionModeLabel(CollectionRunRecord run) => run.CollectionMode.Trim().ToLowerInvariant() switch
    {
        "stable-full" => "稳定模式",
        "fast-batch" => "快速模式",
        _ => "-",
    };

    private static string FormatTimestamp(string primary, string fallback)
    {
        return StoredTimestamp.TryParse(primary, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : StoredTimestamp.TryParse(fallback, out parsed)
                ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : primary;
    }

    private static TimeSpan? ParseTimestampDuration(CollectionRunRecord run)
    {
        if (!StoredTimestamp.TryParse(run.StartedAt, out var started) ||
            !StoredTimestamp.TryParse(run.CompletedAt, out var completed) ||
            completed < started)
        {
            return null;
        }

        return completed - started;
    }
}
