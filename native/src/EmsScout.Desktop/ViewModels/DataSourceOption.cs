using EmsScout.Application;
using EmsScout.Application.Collection;

namespace EmsScout.Desktop.ViewModels;

public sealed class DataSourceOption
{
    public DataSourceOption(CollectionRunRecord run)
        : this(run, isCurrent: false)
    {
    }

    private DataSourceOption(CollectionRunRecord run, bool isCurrent)
    {
        Value = run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Label = FormatDateTime(run.CompletedAt, run.ImportedAt);
        Detail = $"{run.ScopeLabel} · {run.CountLabel}";
        RunId = run.Id;
        IsCurrent = isCurrent;
    }

    private DataSourceOption(string label, string detail)
    {
        Label = label;
        Detail = detail;
        Value = string.Empty;
        RunId = null;
        IsCurrent = true;
    }

    public static DataSourceOption Current(CollectionRunRecord? latestRun)
    {
        return latestRun is null
            ? new DataSourceOption("暂无采集时间", "暂无批次")
            : new DataSourceOption(
                FormatDateTime(latestRun.CompletedAt, latestRun.ImportedAt),
                $"{latestRun.ScopeLabel} · {latestRun.CountLabel}");
    }

    public string Value { get; }

    public string Label { get; }

    public string Detail { get; }

    public long? RunId { get; }

    public bool IsCurrent { get; }

    public string DisplayLabel => $"{Label} · {Detail}";

    private static string FormatDateTime(string primary, string fallback)
    {
        return StoredTimestamp.TryParse(primary, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : StoredTimestamp.TryParse(fallback, out parsed)
                ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : primary;
    }
}
