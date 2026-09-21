namespace EmsScout.Application.Collection;

public static class CollectionRunDisplay
{
    public static string CompletedAtLabel(CollectionRunRecord run) =>
        FormatTimestamp(run.CompletedAt, run.ImportedAt);

    public static string DurationLabel(CollectionRunRecord run)
    {
        if (!StoredTimestamp.TryParse(run.StartedAt, out var started) ||
            !StoredTimestamp.TryParse(run.CompletedAt, out var completed) ||
            completed < started)
        {
            return "-";
        }

        var elapsed = completed - started;
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分"
            : $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒";
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
}
