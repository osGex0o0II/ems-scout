using EmsScout.Application.Collection;

namespace EmsScout.Desktop.ViewModels;

public sealed class DataSourceOption
{
    public DataSourceOption()
    {
        Value = string.Empty;
        Label = "当前数据";
        Detail = "SQLite 当前表";
        RunId = null;
    }

    public DataSourceOption(CollectionRunRecord run)
    {
        Value = run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Label = $"历史 #{run.Id}";
        Detail = $"{FormatDateTime(run.CompletedAt)} · {run.ScopeLabel} · {run.CountLabel}";
        RunId = run.Id;
    }

    public string Value { get; }

    public string Label { get; }

    public string Detail { get; }

    public long? RunId { get; }

    public bool IsCurrent => RunId is null;

    public string DisplayLabel => IsCurrent ? Label : $"{Label}  {Detail}";

    private static string FormatDateTime(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : value;
    }
}
