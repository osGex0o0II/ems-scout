using EmsScout.Application.Collection;

namespace EmsScout.Desktop.ViewModels;

public sealed class CollectionRunRow(CollectionRunRecord record)
{
    public long Id { get; } = record.Id;

    public string RunKey { get; } = record.RunKey;

    public string CompletedAt { get; } = FormatDateTime(record.CompletedAt);

    public string Scope { get; } = record.Scope;

    public IReadOnlyList<string> Buildings { get; } = record.Buildings;

    public int CardCount { get; } = record.CardCount;

    public string ScopeLabel { get; } = record.ScopeLabel;

    public string CountLabel { get; } = record.CountLabel;

    public string StateLabel { get; } = record.StatusLabel;

    public string QualityLabel { get; } = BuildQualityLabel(record);

    public string Note { get; } = string.IsNullOrWhiteSpace(record.Note) ? "--" : record.Note;

    public bool IsAnomaly { get; } = record.IsAnomaly;

    public string Summary => $"{CompletedAt} · {ScopeLabel} · {CountLabel}";

    private static string BuildQualityLabel(CollectionRunRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.QualitySummary) || record.QualitySummary == "{}")
        {
            return "未记录";
        }

        return "已记录";
    }

    private static string FormatDateTime(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : value;
    }
}
