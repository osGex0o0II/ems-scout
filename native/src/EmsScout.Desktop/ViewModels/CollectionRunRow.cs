using EmsScout.Application;
using EmsScout.Application.Collection;

namespace EmsScout.Desktop.ViewModels;

public sealed class CollectionRunRow(CollectionRunRecord record, bool isCurrent = false)
{
    public CollectionRunRecord Record { get; } = record;

    public long Id { get; } = record.Id;

    public long RunNumber { get; } = record.RunNumber > 0 ? record.RunNumber : record.Id;

    public string RunKey { get; } = record.RunKey;

    public string BatchLabel => $"批次 {RunNumber} · {RunKey}";

    public string CompletedAt { get; } = CollectionRunDisplay.CompletedAtLabel(record);

    public string Scope { get; } = record.Scope;

    public IReadOnlyList<string> Buildings { get; } = record.Buildings;

    public int CardCount { get; } = record.CardCount;

    public int BuildingCount { get; } = record.Buildings.Count;

    public string ScopeLabel { get; } = record.ScopeLabel;

    public string CountLabel { get; } = record.CountLabel;

    public string StateLabel { get; } = record.StatusLabel;

    public string SourceLabel { get; } = string.IsNullOrWhiteSpace(record.Source) ? "采集导入" : record.Source;

    public string VersionLabel { get; } = string.IsNullOrWhiteSpace(record.DataVersion) ? "v1.0.0" : record.DataVersion;

    public string DurationLabel { get; } = CollectionRunDisplay.DurationLabel(record);

    public string CollectionModeLabel { get; } = CollectionRunDisplay.CollectionModeLabel(record);

    public string OperatorLabel { get; } = string.IsNullOrWhiteSpace(record.Operator) ? "本机" : record.Operator;

    public bool IsCurrent { get; } = isCurrent;

    public string CurrentLabel => IsCurrent ? "当前版本" : "历史版本";

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

}
