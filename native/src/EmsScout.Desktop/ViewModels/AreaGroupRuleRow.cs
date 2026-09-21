using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmsScout.Application.Groups;

namespace EmsScout.Desktop.ViewModels;

public sealed class AreaGroupRuleRow : ObservableObject
{
    private string _building;
    private string _zuo;
    private string _floor;
    private string _matchMode;
    private string _keywords;
    private int _ruleOrder;
    private int _matchCount = -1;

    public AreaGroupRuleRow(AreaGroupRuleRecord record)
    {
        Id = record.Id;
        GroupId = record.GroupId;
        _ruleOrder = record.RuleOrder;
        _building = AreaGroupRuleNormalizer.NormalizeBuildingSelection(record.Building, "1号");
        _zuo = string.IsNullOrWhiteSpace(record.Zuo) ? "-" : record.Zuo;
        _floor = string.IsNullOrWhiteSpace(record.FloorLabel) ? "-" : record.FloorLabel;
        _matchMode = record.IsExclude ? "不含" : "包含";
        _keywords = record.KeywordText;
        Note = record.Note;
        ConfigureBuildingOptions();
        ConfigureZuoOptions();
        ConfigureFloorOptions(["-", _floor]);
    }

    public AreaGroupRuleRow(long groupId, int ruleOrder)
    {
        GroupId = groupId;
        _ruleOrder = ruleOrder;
        _building = "1号";
        _zuo = "-";
        _floor = "-";
        _matchMode = "-";
        _keywords = string.Empty;
        Note = string.Empty;
        ConfigureBuildingOptions();
        ConfigureZuoOptions();
        ConfigureFloorOptions(["-"]);
    }

    public long Id { get; }
    public long GroupId { get; }
    public int RuleOrder
    {
        get => _ruleOrder;
        private set => SetProperty(ref _ruleOrder, value);
    }
    public string Note { get; }

    public ObservableCollection<string> BuildingOptions { get; } = [];
    public ObservableCollection<string> ZuoOptions { get; } = [];
    public ObservableCollection<string> FloorOptions { get; } = [];
    public ObservableCollection<string> MatchModeOptions { get; } = ["-", "包含", "不含"];

    public string Building
    {
        get => _building;
        set
        {
            var normalized = AreaGroupRuleNormalizer.NormalizeBuildingSelection(value, _building);
            if (SetProperty(ref _building, normalized))
            {
                ConfigureZuoOptions();
                OnPropertyChanged(nameof(Record));
                OnPropertyChanged(nameof(Scope));
            }
        }
    }

    public string Zuo
    {
        get => _zuo;
        set
        {
            if (value is null)
                return;

            if (SetProperty(ref _zuo, string.IsNullOrWhiteSpace(value) ? "-" : value))
            {
                OnPropertyChanged(nameof(Record));
                OnPropertyChanged(nameof(Scope));
            }
        }
    }

    public string Floor
    {
        get => _floor;
        set
        {
            if (value is null)
                return;

            if (SetProperty(ref _floor, string.IsNullOrWhiteSpace(value) ? "-" : value))
            {
                OnPropertyChanged(nameof(Record));
                OnPropertyChanged(nameof(Scope));
            }
        }
    }

    public string MatchMode
    {
        get => _matchMode;
        set
        {
            if (value is null)
                return;

            if (SetProperty(ref _matchMode, string.IsNullOrWhiteSpace(value) ? "-" : value))
                OnPropertyChanged(nameof(Record));
        }
    }

    public string Keywords
    {
        get => _keywords;
        set
        {
            if (SetProperty(ref _keywords, value ?? string.Empty))
                OnPropertyChanged(nameof(Record));
        }
    }

    public AreaGroupRuleRecord Record => new(
        Id,
        GroupId,
        RuleOrder,
        Building,
        Zuo,
        Floor,
        AreaGroupRuleNormalizer.TryParseFloorValue(Floor),
        AreaGroupRuleNormalizer.NormalizeMatchMode(MatchMode),
        AreaGroupRuleNormalizer.NormalizeKeywords(Keywords),
        Note);

    public string Scope => $"{Building} / {Zuo} / {Floor}";

    public int MatchCount
    {
        get => _matchCount;
        private set
        {
            if (SetProperty(ref _matchCount, value))
                OnPropertyChanged(nameof(MatchCountText));
        }
    }

    public string MatchCountText => MatchCount < 0
        ? (Record.IsExclude ? "排除 -- 台" : "命中 -- 台")
        : Record.IsExclude ? $"排除 {MatchCount:N0} 台" : $"命中 {MatchCount:N0} 台";

    public void ConfigureBuildingOptions()
    {
        Replace(BuildingOptions, ["1号", "2号", "3号", "4号", "5号", "6号"]);
    }

    public void ConfigureZuoOptions()
    {
        var options = Building switch
        {
            "5号" => new[] { "-", "A座", "B座", "C座", "D座", "E座", "F座" },
            "6号" => new[] { "-", "A座", "B座", "C座" },
            _ => new[] { "-" },
        };
        Replace(ZuoOptions, options);
        if (!ZuoOptions.Contains(Zuo, StringComparer.OrdinalIgnoreCase))
            Zuo = "-";
    }

    public void ConfigureFloorOptions(IEnumerable<string> options)
    {
        var normalized = options
            .Select(value => string.IsNullOrWhiteSpace(value) ? "-" : value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!normalized.Contains("-", StringComparer.OrdinalIgnoreCase))
            normalized.Insert(0, "-");
        Replace(FloorOptions, normalized);
        if (!FloorOptions.Contains(Floor, StringComparer.OrdinalIgnoreCase))
            Floor = "-";
    }

    public void SetMatchCount(int count) => MatchCount = Math.Max(0, count);

    public void SetRuleOrder(int value)
    {
        if (RuleOrder == value)
            return;

        RuleOrder = value;
        OnPropertyChanged(nameof(Record));
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
}
