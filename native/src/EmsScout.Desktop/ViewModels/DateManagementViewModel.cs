using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Groups;
using EmsScout.Application.Logging;
using EmsScout.Infrastructure.Logging;
using Microsoft.UI.Xaml;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class DateManagementViewModel(
    IAreaGroupRepository repository,
    IApplicationLogger applicationLogger) : ObservableObject
{
    private readonly List<AreaGroupItemRecord> _allItems = [];
    private readonly SortedSet<DateOnly> _selectedDates = [];
    private GroupSummaryRow? _selectedAreaGroup;
    private ScheduleGroupRow? _selectedScheduleGroup;
    private string _statusText = "正在读取日期计划";
    private string _scheduleGroupName = string.Empty;
    private string _scheduleGroupDescription = string.Empty;
    private bool _scheduleGroupEnabled = true;
    private string _ruleStatus = "enabled";
    private string _ruleNote = string.Empty;
    private bool _isLoading;
    private long _loadVersion;

    public ObservableCollection<GroupSummaryRow> AreaGroups { get; } = [];

    public ObservableCollection<ScheduleGroupRow> ScheduleGroups { get; } = [];

    public ObservableCollection<ScheduleRuleRow> Rules { get; } = [];

    public ObservableCollection<AreaGroupItemRow> AreaMembers { get; } = [];

    public ObservableCollection<ScheduleMemberRow> ScheduleMembers { get; } = [];

    public ObservableCollection<ScheduleIntervalEditorRow> Intervals { get; } = [];

    public event EventHandler? CalendarSelectionClearRequested;

    public event EventHandler? CalendarRulesChanged;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                NotifyState();
            }
        }
    }

    public GroupSummaryRow? SelectedAreaGroup
    {
        get => _selectedAreaGroup;
        set
        {
            if (SetProperty(ref _selectedAreaGroup, value))
            {
                _ = LoadAreaAsync(value?.GroupId);
                NotifyState();
            }
        }
    }

    public ScheduleGroupRow? SelectedScheduleGroup
    {
        get => _selectedScheduleGroup;
        set
        {
            if (SetProperty(ref _selectedScheduleGroup, value))
            {
                LoadScheduleEditor(value);
                NotifyState();
            }
        }
    }

    public string ScheduleGroupName
    {
        get => _scheduleGroupName;
        set
        {
            if (SetProperty(ref _scheduleGroupName, value)) NotifyState();
        }
    }

    public string ScheduleGroupDescription
    {
        get => _scheduleGroupDescription;
        set => SetProperty(ref _scheduleGroupDescription, value);
    }

    public bool ScheduleGroupEnabled
    {
        get => _scheduleGroupEnabled;
        set => SetProperty(ref _scheduleGroupEnabled, value);
    }

    public string RuleStatus
    {
        get => _ruleStatus;
        set
        {
            if (SetProperty(ref _ruleStatus, value))
            {
                OnPropertyChanged(nameof(IntervalEditorVisibility));
                OnPropertyChanged(nameof(RuleTypeHelp));
                NotifyState();
            }
        }
    }

    public string RuleNote
    {
        get => _ruleNote;
        set => SetProperty(ref _ruleNote, value);
    }

    public bool CanMaintain => SelectedAreaGroup?.GroupId is not null && !IsLoading;

    public bool CanSaveScheduleGroup => CanMaintain && !string.IsNullOrWhiteSpace(ScheduleGroupName);

    public bool CanDeleteScheduleGroup => CanMaintain && SelectedScheduleGroup is not null;

    public bool CanApplyDates => CanMaintain && SelectedScheduleGroup is not null && _selectedDates.Count > 0 &&
                                 (RuleStatus == "not_open" || Intervals.Count > 0);

    public bool CanRemoveSelectedDates => CanMaintain && SelectedScheduleGroup is not null &&
                                          _selectedDates.Any(date => RuleForDate(date) is not null);

    public Visibility IntervalEditorVisibility => RuleStatus == "enabled" ? Visibility.Visible : Visibility.Collapsed;

    public string RuleTypeHelp => RuleStatus == "not_open"
        ? "选中日期将标记为全天不启用，不控制设备开关。"
        : "选中日期应在下列一个或多个时段内启用。";

    public string SelectionSummary => _selectedDates.Count == 0
        ? "请在月历中选择一个或多个日期"
        : _selectedDates.Count == 1
            ? $"已选择 {_selectedDates.Min:yyyy-MM-dd}"
            : $"已选择 {_selectedDates.Count:N0} 个日期（{_selectedDates.Min:yyyy-MM-dd} 至 {_selectedDates.Max:yyyy-MM-dd}）";

    public string ConfiguredSummary => SelectedScheduleGroup is null
        ? "选择计划组后查看日期"
        : $"已配置 {Rules.Count:N0} 个日期，适用对象 {ScheduleMembers.Count:N0} 个";

    public string AreaMemberSummary => SelectedAreaGroup is null
        ? "请选择区域组"
        : AreaMembers.Count == 0
            ? "该区域组还没有人工成员，请先到分组设置添加成员"
            : $"区域组成员 {AreaMembers.Count:N0} 个，可加入当前计划组";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var set = await repository.LoadAsync(cancellationToken).ConfigureAwait(true);
            _allItems.Clear();
            _allItems.AddRange(set.Items);
            var selectedId = SelectedAreaGroup?.GroupId;
            AreaGroups.Clear();
            foreach (var group in set.Groups
                         .OrderBy(group => group.SystemKey == "public" ? 0 : group.SystemKey == "non_public" ? 1 : 2)
                         .ThenBy(group => group.Name))
            {
                AreaGroups.Add(new GroupSummaryRow(group));
            }

            SelectedAreaGroup = AreaGroups.FirstOrDefault(group => group.GroupId == selectedId) ?? AreaGroups.FirstOrDefault();
            StatusText = $"已读取 {AreaGroups.Count:N0} 个区域组的日期计划";
        }
        catch (Exception ex)
        {
            StatusText = "读取日期计划失败：" + applicationLogger.WriteFailure(ex, "date-management").DisplayText;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadAreaAsync(long? areaGroupId, long? preferredScheduleId = null)
    {
        var version = ++_loadVersion;
        ScheduleGroups.Clear();
        Rules.Clear();
        ScheduleMembers.Clear();
        AreaMembers.Clear();
        _selectedScheduleGroup = null;
        OnPropertyChanged(nameof(SelectedScheduleGroup));
        LoadScheduleEditor(null);
        if (areaGroupId is null) return;

        foreach (var item in _allItems.Where(item => item.GroupId == areaGroupId.Value))
        {
            AreaMembers.Add(new AreaGroupItemRow(item));
        }

        try
        {
            var groups = await repository.LoadScheduleGroupsAsync(areaGroupId.Value).ConfigureAwait(true);
            if (version != _loadVersion) return;
            foreach (var group in groups)
            {
                ScheduleGroups.Add(new ScheduleGroupRow(group));
            }

            SelectedScheduleGroup = ScheduleGroups.FirstOrDefault(group => group.Id == preferredScheduleId) ?? ScheduleGroups.FirstOrDefault();
            if (SelectedScheduleGroup is null) LoadScheduleEditor(null);
            OnPropertyChanged(nameof(AreaMemberSummary));
        }
        catch (Exception ex)
        {
            StatusText = "读取计划组失败：" + applicationLogger.WriteFailure(ex, "date-management-load").DisplayText;
        }
    }

    private void LoadScheduleEditor(ScheduleGroupRow? row)
    {
        Rules.Clear();
        ScheduleMembers.Clear();
        if (row is null)
        {
            ScheduleGroupName = string.Empty;
            ScheduleGroupDescription = string.Empty;
            ScheduleGroupEnabled = true;
            ResetRuleEditor();
        }
        else
        {
            ScheduleGroupName = row.Name;
            ScheduleGroupDescription = row.Description == "--" ? string.Empty : row.Description;
            ScheduleGroupEnabled = row.Enabled;
            foreach (var rule in row.Rules.OrderBy(rule => rule.CalendarDate)) Rules.Add(new ScheduleRuleRow(rule));
            foreach (var member in row.Members) ScheduleMembers.Add(new ScheduleMemberRow(member));
            ResetRuleEditor();
        }

        OnPropertyChanged(nameof(ConfiguredSummary));
        CalendarRulesChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void NewScheduleGroup()
    {
        SelectedScheduleGroup = null;
        ScheduleGroupName = string.Empty;
        ScheduleGroupDescription = string.Empty;
        ScheduleGroupEnabled = true;
        StatusText = "填写名称后保存新的计划组";
    }

    private bool CanSaveScheduleGroupCommand() => CanSaveScheduleGroup;

    [RelayCommand(CanExecute = nameof(CanSaveScheduleGroupCommand))]
    private async Task SaveScheduleGroup()
    {
        if (SelectedAreaGroup?.GroupId is null) return;
        IsLoading = true;
        try
        {
            var saved = await repository.SaveScheduleGroupAsync(new ScheduleGroupEdit(
                SelectedAreaGroup.GroupId.Value,
                ScheduleGroupName,
                ScheduleGroupDescription,
                ScheduleGroupEnabled,
                SelectedScheduleGroup?.Id)).ConfigureAwait(true);
            await LoadAreaAsync(SelectedAreaGroup.GroupId, saved.Id).ConfigureAwait(true);
            StatusText = $"已保存计划组：{saved.Name}";
        }
        catch (Exception ex)
        {
            StatusText = "保存计划组失败：" + applicationLogger.WriteFailure(ex, "date-plan-save").DisplayText;
        }
        finally { IsLoading = false; }
    }

    public async Task DeleteScheduleGroupAsync()
    {
        if (SelectedScheduleGroup is null || SelectedAreaGroup?.GroupId is null) return;
        IsLoading = true;
        try
        {
            await repository.DeleteScheduleGroupAsync(SelectedScheduleGroup.Id).ConfigureAwait(true);
            await LoadAreaAsync(SelectedAreaGroup.GroupId).ConfigureAwait(true);
            StatusText = "已删除计划组";
        }
        catch (Exception ex)
        {
            StatusText = "删除计划组失败：" + applicationLogger.WriteFailure(ex, "date-plan-delete").DisplayText;
        }
        finally { IsLoading = false; }
    }

    public void SetSelectedDates(IEnumerable<DateTimeOffset> dates)
    {
        _selectedDates.Clear();
        foreach (var date in dates)
        {
            _selectedDates.Add(DateOnly.FromDateTime(date.LocalDateTime.Date));
        }

        var rules = _selectedDates.Select(RuleForDate).Where(rule => rule is not null).Cast<ScheduleRuleRecord>().ToArray();
        if (_selectedDates.Count == 1 && rules.Length == 1)
        {
            LoadRuleEditor(rules[0]);
        }
        else if (_selectedDates.Count > 1 && rules.Length == _selectedDates.Count && HaveSameDefinition(rules))
        {
            LoadRuleEditor(rules[0]);
        }

        OnPropertyChanged(nameof(SelectionSummary));
        NotifyState();
    }

    public void SelectRule(ScheduleRuleRow row)
    {
        LoadRuleEditor(row.Record);
        _selectedDates.Clear();
        if (DateOnly.TryParseExact(row.Record.CalendarDate, "yyyy-MM-dd", out var date)) _selectedDates.Add(date);
        OnPropertyChanged(nameof(SelectionSummary));
        NotifyState();
    }

    private void LoadRuleEditor(ScheduleRuleRecord rule)
    {
        RuleStatus = rule.ExpectedStatus;
        RuleNote = rule.Note;
        Intervals.Clear();
        foreach (var interval in rule.Intervals)
        {
            Intervals.Add(new ScheduleIntervalEditorRow(ParseTime(interval.StartTime), ParseTime(interval.EndTime)));
        }
        if (RuleStatus == "enabled" && Intervals.Count == 0) AddDefaultInterval();
    }

    private bool CanApplyDatesCommand() => CanApplyDates;

    [RelayCommand(CanExecute = nameof(CanApplyDatesCommand))]
    private async Task ApplyDates()
    {
        if (SelectedScheduleGroup is null) return;
        IsLoading = true;
        try
        {
            var intervals = RuleStatus == "not_open"
                ? []
                : Intervals.Select(row => new ScheduleIntervalEdit(row.StartTime.ToString("hh\\:mm"), row.EndTime.ToString("hh\\:mm"))).ToArray();
            await repository.SaveScheduleRulesAsync(new ScheduleRuleBatchEdit(
                SelectedScheduleGroup.Id,
                _selectedDates.Select(date => date.ToString("yyyy-MM-dd")).ToArray(),
                RuleStatus,
                intervals,
                RuleNote)).ConfigureAwait(true);
            var count = _selectedDates.Count;
            await ReloadCurrentScheduleAsync().ConfigureAwait(true);
            StatusText = $"已将日期规则应用到 {count:N0} 个日期";
        }
        catch (Exception ex)
        {
            StatusText = "保存日期规则失败：" + applicationLogger.WriteFailure(ex, "date-rule-batch-save").DisplayText;
        }
        finally { IsLoading = false; }
    }

    public async Task RemoveSelectedDatesAsync()
    {
        if (SelectedScheduleGroup is null) return;
        IsLoading = true;
        try
        {
            var dates = _selectedDates.Select(date => date.ToString("yyyy-MM-dd")).ToArray();
            await repository.DeleteScheduleRulesAsync(SelectedScheduleGroup.Id, dates).ConfigureAwait(true);
            await ReloadCurrentScheduleAsync().ConfigureAwait(true);
            StatusText = $"已删除 {dates.Length:N0} 个日期规则";
        }
        catch (Exception ex)
        {
            StatusText = "删除日期规则失败：" + applicationLogger.WriteFailure(ex, "date-rule-batch-delete").DisplayText;
        }
        finally { IsLoading = false; }
    }

    public void AddInterval()
    {
        var start = Intervals.Count == 0 ? new TimeSpan(8, 0, 0) : Intervals[^1].EndTime;
        var end = start.Add(TimeSpan.FromHours(2));
        if (end > new TimeSpan(23, 59, 0)) end = new TimeSpan(23, 59, 0);
        Intervals.Add(new ScheduleIntervalEditorRow(start, end));
        NotifyState();
    }

    public void RemoveInterval(ScheduleIntervalEditorRow? row)
    {
        if (row is null) return;
        Intervals.Remove(row);
        NotifyState();
    }

    public async Task AddMemberAsync(AreaGroupItemRow? item)
    {
        if (item is null || SelectedScheduleGroup is null) return;
        IsLoading = true;
        try
        {
            var otherPlans = ScheduleGroups
                .Where(group => group.Id != SelectedScheduleGroup.Id && group.Members.Any(member => member.AreaGroupItemId == item.Id))
                .Select(group => group.Name)
                .ToArray();
            await repository.SaveScheduleMemberAsync(new ScheduleMemberEdit(
                SelectedScheduleGroup.Id, item.Id, item.TargetType, item.Building, item.FloorLabel,
                item.SubAreaText, item.CardName, item.DeviceUid, "normal", item.RawNote)).ConfigureAwait(true);
            await ReloadCurrentScheduleAsync().ConfigureAwait(true);
            StatusText = otherPlans.Length == 0
                ? $"已将 {item.TargetLabel} 加入当前计划组"
                : $"已加入；该对象还属于：{string.Join("、", otherPlans)}，审计会分别标注来源";
        }
        catch (Exception ex)
        {
            StatusText = "加入适用对象失败：" + applicationLogger.WriteFailure(ex, "date-member-add").DisplayText;
        }
        finally { IsLoading = false; }
    }

    public async Task RemoveMemberAsync(ScheduleMemberRow? row)
    {
        if (row is null) return;
        await repository.DeleteScheduleMemberAsync(row.Id).ConfigureAwait(true);
        await ReloadCurrentScheduleAsync().ConfigureAwait(true);
        StatusText = "已从当前计划组移除对象";
    }

    public async Task SetMemberStatusAsync(ScheduleMemberRow? row, string status)
    {
        if (row is null) return;
        var record = row.Record;
        await repository.SaveScheduleMemberAsync(new ScheduleMemberEdit(
            record.ScheduleGroupId, record.AreaGroupItemId, record.TargetType, record.Building, record.FloorLabel,
            record.SubAreaText, record.CardName, record.DeviceUid, status, record.Note, record.Id)).ConfigureAwait(true);
        await ReloadCurrentScheduleAsync().ConfigureAwait(true);
        StatusText = status == "not_open" ? "已标记为未开放" : "已标记为按日期规则启用";
    }

    public bool IsConfiguredDate(DateTimeOffset value) => RuleForDate(DateOnly.FromDateTime(value.LocalDateTime.Date)) is not null;

    public bool IsNotOpenDate(DateTimeOffset value) => RuleForDate(DateOnly.FromDateTime(value.LocalDateTime.Date))?.ExpectedStatus == "not_open";

    private async Task ReloadCurrentScheduleAsync()
    {
        if (SelectedAreaGroup?.GroupId is null || SelectedScheduleGroup is null) return;
        var scheduleId = SelectedScheduleGroup.Id;
        await LoadAreaAsync(SelectedAreaGroup.GroupId, scheduleId).ConfigureAwait(true);
        CalendarRulesChanged?.Invoke(this, EventArgs.Empty);
    }

    private ScheduleRuleRecord? RuleForDate(DateOnly date) =>
        SelectedScheduleGroup?.Rules.FirstOrDefault(rule => rule.CalendarDate == date.ToString("yyyy-MM-dd"));

    private static bool HaveSameDefinition(IReadOnlyList<ScheduleRuleRecord> rules)
    {
        var first = rules[0];
        return rules.Skip(1).All(rule =>
            rule.ExpectedStatus == first.ExpectedStatus &&
            rule.Note == first.Note &&
            rule.Intervals.Select(interval => (interval.StartTime, interval.EndTime))
                .SequenceEqual(first.Intervals.Select(interval => (interval.StartTime, interval.EndTime))));
    }

    private void ResetRuleEditor()
    {
        RuleStatus = "enabled";
        RuleNote = string.Empty;
        Intervals.Clear();
        AddDefaultInterval();
        ClearCalendarSelection();
    }

    private void AddDefaultInterval() => Intervals.Add(new ScheduleIntervalEditorRow(new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0)));

    private void ClearCalendarSelection()
    {
        _selectedDates.Clear();
        OnPropertyChanged(nameof(SelectionSummary));
        CalendarSelectionClearRequested?.Invoke(this, EventArgs.Empty);
        NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanMaintain));
        OnPropertyChanged(nameof(CanSaveScheduleGroup));
        OnPropertyChanged(nameof(CanDeleteScheduleGroup));
        OnPropertyChanged(nameof(CanApplyDates));
        OnPropertyChanged(nameof(CanRemoveSelectedDates));
        SaveScheduleGroupCommand.NotifyCanExecuteChanged();
        ApplyDatesCommand.NotifyCanExecuteChanged();
    }

    private static TimeSpan ParseTime(string value) => TimeSpan.TryParse(value, out var result) ? result : TimeSpan.Zero;
}

public sealed class ScheduleIntervalEditorRow : ObservableObject
{
    private TimeSpan _startTime;
    private TimeSpan _endTime;

    public ScheduleIntervalEditorRow(TimeSpan startTime, TimeSpan endTime)
    {
        _startTime = startTime;
        _endTime = endTime;
    }

    public TimeSpan StartTime
    {
        get => _startTime;
        set => SetProperty(ref _startTime, value);
    }

    public TimeSpan EndTime
    {
        get => _endTime;
        set => SetProperty(ref _endTime, value);
    }
}
