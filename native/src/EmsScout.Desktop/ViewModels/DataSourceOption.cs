using EmsScout.Application.Collection;

namespace EmsScout.Desktop.ViewModels;

public sealed class DataSourceOption
{
    public DataSourceOption(CollectionRunRecord run)
    {
        Value = run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Label = FormatDateTime(run.CompletedAt);
        Detail = $"{run.ScopeLabel} · {run.CountLabel}";
        RunId = run.Id;
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
        var label = latestRun is null ? "当前 SQLite 数据" : FormatDateTime(latestRun.CompletedAt);
        var detail = latestRun is null ? "当前数据" : "当前 SQLite 数据";
        return new DataSourceOption(label, detail);
    }

    public string Value { get; }

    public string Label { get; }

    public string Detail { get; }

    public long? RunId { get; }

    public bool IsCurrent { get; }

    public string DisplayLabel => $"{Label} · {Detail}";

    private static string FormatDateTime(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : value;
    }
}
