using EmsScout.Application;
using EmsScout.Application.Collection;

namespace EmsScout.Desktop.ViewModels;

public sealed class CollectionRunDetail(CollectionRunRecord record, bool isCurrent)
{
    public long Id { get; } = record.Id;

    public long RunNumber { get; } = record.RunNumber > 0 ? record.RunNumber : record.Id;

    public string RunKey { get; } = record.RunKey;

    public string BatchLabel => $"批次 {RunNumber} · {RunKey}";

    public string CompletedAt { get; } = FormatDateTime(record.CompletedAt);

    public string ImportedAt { get; } = FormatDateTime(record.ImportedAt);

    public string ScopeLabel { get; } = record.ScopeLabel;

    public string BuildingsLabel { get; } = record.Buildings.Count == 0
        ? "--"
        : string.Join("、", record.Buildings);

    public string BuildingCountText { get; } = $"{record.Buildings.Count:N0} 栋";

    public string CardCountText { get; } = $"{record.CardCount:N0} 张";

    public string StatusLabel { get; } = record.StatusLabel;

    public string SourceLabel { get; } = string.IsNullOrWhiteSpace(record.Source) ? "采集导入" : record.Source;

    public string VersionLabel { get; } = string.IsNullOrWhiteSpace(record.DataVersion) ? "v1.0.0" : record.DataVersion;

    public string OperatorLabel { get; } = string.IsNullOrWhiteSpace(record.Operator) ? "本机" : record.Operator;

    public string CurrentLabel { get; } = isCurrent ? "当前版本" : "历史版本";

    public string StatusSummary =>
        $"开机 {record.OnCount:N0} · 关机 {record.OffCount:N0} · 离线 {record.OfflineCount:N0} · 未知 {record.UnknownCount:N0}";

    public string Note { get; } = string.IsNullOrWhiteSpace(record.Note) ? "暂无操作备注" : record.Note;

    public string QualitySummary { get; } = string.IsNullOrWhiteSpace(record.QualitySummary) || record.QualitySummary == "{}"
        ? "暂无质量审计摘要"
        : "已保存质量审计摘要";

    private static string FormatDateTime(string value) =>
        StoredTimestamp.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : string.IsNullOrWhiteSpace(value) ? "--" : value;
}
