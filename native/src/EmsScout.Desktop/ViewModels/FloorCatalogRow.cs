using EmsScout.Application.Groups;

namespace EmsScout.Desktop.ViewModels;

public sealed class FloorCatalogRow(FloorCatalogRecord record)
{
    public long Id { get; } = record.Id;

    public string Building { get; } = record.Building;

    public string FloorLabel { get; } = record.FloorLabel;

    public string Source { get; } = record.Source switch
    {
        "manual" => "手动",
        "discovered" => "采集",
        "manual+discovered" => "手动+采集",
        _ => record.Source,
    };

    public string StateLabel { get; } = record.Enabled ? "启用" : "停用";

    public string Note { get; } = string.IsNullOrWhiteSpace(record.Note) ? "--" : record.Note;

    public string DisplayLabel { get; } = $"{record.Building} / {record.FloorLabel}";
}
