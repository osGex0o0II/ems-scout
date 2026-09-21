using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Desktop.Services;
using Microsoft.UI.Xaml;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class GroupsViewModel(
    IAreaGroupRepository areaGroupRepository,
    INavigationService navigationService,
    IDeviceReadRepository deviceReadRepository) : ObservableObject
{
    private GroupSummaryRow? _selectedGroup;
    private string _statusText = "正在读取区域组";
    private bool _isLoading;
    private bool _isCreatingGroup;
    private string _editName = string.Empty;
    private string _editAreaLabel = string.Empty;
    private string _editGroupKey = string.Empty;
    private string _editNote = string.Empty;
    private string _editPriority = "重点";
    private bool _editEnabled = true;
    private string _loadError = string.Empty;
    private string _ruleOptionsError = string.Empty;
    private long _ruleOptionsVersion;
    private readonly Dictionary<AreaGroupRuleRow, long> _ruleOptionsVersions = [];
    private long _ruleMatchCountVersion;
    private int _busyDepth;
    private bool _suppressDraftDirty;
    private bool _suppressRuleMatchRefresh;
    private AreaGroupDraftSnapshot? _savedDraft;
    private bool _hasUnsavedChanges;
    private GroupSummaryRow? _selectedGroupBeforeNew;

    public ObservableCollection<GroupSummaryRow> Groups { get; } = [];
    public ObservableCollection<AreaGroupRuleRow> Rules { get; } = [];

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string LoadError
    {
        get => _loadError;
        private set
        {
            if (SetProperty(ref _loadError, value))
            {
                OnPropertyChanged(nameof(GroupListEmptyVisibility));
                OnPropertyChanged(nameof(GroupListErrorVisibility));
            }
        }
    }

    public string RuleOptionsError
    {
        get => _ruleOptionsError;
        private set
        {
            if (SetProperty(ref _ruleOptionsError, value))
                OnPropertyChanged(nameof(RuleOptionsErrorVisibility));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                NotifyCommands();
                OnPropertyChanged(nameof(LoadingStateVisibility));
                OnPropertyChanged(nameof(GroupListEmptyVisibility));
                OnPropertyChanged(nameof(GroupListErrorVisibility));
                OnPropertyChanged(nameof(RuleOptionsErrorVisibility));
                OnPropertyChanged(nameof(OpenDataHint));
                OnPropertyChanged(nameof(CanRunFileOperation));
            }
        }
    }

    public Visibility LoadingStateVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GroupListEmptyVisibility => !IsLoading && Groups.Count == 0 && string.IsNullOrWhiteSpace(LoadError) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GroupListErrorVisibility => !IsLoading && Groups.Count == 0 && !string.IsNullOrWhiteSpace(LoadError) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RuleOptionsErrorVisibility => !IsLoading && !string.IsNullOrWhiteSpace(RuleOptionsError) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RulesEmptyVisibility => !IsLoading && (SelectedGroup is not null || IsCreatingGroup) && Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RuleEditorVisibility => SelectedGroup is not null || IsCreatingGroup ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RulesEditorVisibility => IsCreatingGroup || SelectedGroup is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NewGroupCancelVisibility => IsCreatingGroup ? Visibility.Visible : Visibility.Collapsed;
    public bool IsCreatingGroup
    {
        get => _isCreatingGroup;
        private set
        {
            if (SetProperty(ref _isCreatingGroup, value))
            {
                OnPropertyChanged(nameof(RuleEditorVisibility));
                OnPropertyChanged(nameof(RulesEditorVisibility));
                OnPropertyChanged(nameof(NewGroupCancelVisibility));
                OnPropertyChanged(nameof(RulesEmptyVisibility));
                OnPropertyChanged(nameof(GroupEditorTitle));
            }
        }
    }
    public bool CanSaveGroup => CanEditSelectedGroup && !string.IsNullOrWhiteSpace(EditName);
    public bool CanRefresh => !IsLoading;
    public bool CanRunFileOperation => !IsLoading;
    public bool CanDeleteSelectedGroup => !IsLoading && SelectedGroup is not null;
    public bool CanOpenSelectedInData => !IsLoading && SelectedGroup?.GroupId is not null;
    public bool CanEditSelectedGroup => !IsLoading && (IsCreatingGroup || SelectedGroup is not null);
    public bool CanBeginAddRule => CanEditSelectedGroup;
    public bool CanNewGroup => !IsLoading && !IsCreatingGroup;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }
    public string GroupEditorTitle => SelectedGroup is null ? "新建区域组" : "区域组设置";
    public string OpenDataHint => CanOpenSelectedInData ? "打开数据页查看该区域组设备" : "保存区域组后可查看设备";

    public GroupSummaryRow? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!ReferenceEquals(value, _selectedGroup) && HasUnsavedChanges && !IsLoading)
            {
                StatusText = "当前有未保存修改，请先保存或取消后再切换区域组。";
                OnPropertyChanged(nameof(SelectedGroup));
                return;
            }

            if (SetProperty(ref _selectedGroup, value))
            {
                if (value is not null) IsCreatingGroup = false;
                LoadGroupEdit(value);
                RefreshRules();
                CaptureSavedDraft();
                NotifyCommands();
                OnPropertyChanged(nameof(RuleEditorVisibility));
                OnPropertyChanged(nameof(RulesEditorVisibility));
                OnPropertyChanged(nameof(RulesEmptyVisibility));
                OnPropertyChanged(nameof(GroupEditorTitle));
                OnPropertyChanged(nameof(OpenDataHint));
            }
        }
    }

    public string EditName { get => _editName; set { if (SetProperty(ref _editName, value)) MarkDraftDirty(); SaveGroupCommand.NotifyCanExecuteChanged(); } }
    public string EditGroupKey { get => _editGroupKey; set { if (SetProperty(ref _editGroupKey, value)) MarkDraftDirty(); } }
    public string EditNote { get => _editNote; set { if (SetProperty(ref _editNote, value)) MarkDraftDirty(); } }
    public string EditPriority { get => _editPriority; set { if (SetProperty(ref _editPriority, value)) MarkDraftDirty(); } }
    public bool EditEnabled { get => _editEnabled; set { if (SetProperty(ref _editEnabled, value)) MarkDraftDirty(); } }
    private IReadOnlyList<AreaGroupRecord> GroupRecords { get; set; } = [];
    private IReadOnlyList<AreaGroupRuleRecord> RuleRecords { get; set; } = [];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (HasUnsavedChanges && !IsLoading)
        {
            StatusText = "当前有未保存修改，请先保存或取消后再刷新。";
            return;
        }

        LoadError = string.Empty;
        RuleOptionsError = string.Empty;
        BeginBusy();
        var previousSuppressRuleMatchRefresh = _suppressRuleMatchRefresh;
        _suppressRuleMatchRefresh = true;
        try
        {
            var set = await areaGroupRepository.LoadAsync(cancellationToken).ConfigureAwait(true);
            GroupRecords = set.Groups;
            RuleRecords = set.RuleRecords;
            Groups.Clear();
            foreach (var group in set.Groups) Groups.Add(new GroupSummaryRow(group));
            IsCreatingGroup = false;
            SelectedGroup = Groups.FirstOrDefault(group => group.IsEnabled) ?? Groups.FirstOrDefault();
            await RefreshRuleOptionsForRowsAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            CaptureSavedDraft();
            StatusText = $"已读取 {Groups.Count:N0} 个区域组";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "已取消读取区域组";
        }
        catch (Exception ex)
        {
            Groups.Clear(); Rules.Clear(); IsCreatingGroup = false; SelectedGroup = null;
            _savedDraft = null;
            HasUnsavedChanges = false;
            LoadError = ex.Message;
            StatusText = "读取区域组失败：" + ex.Message;
        }
        finally
        {
            _suppressRuleMatchRefresh = previousSuppressRuleMatchRefresh;
            EndBusy();
        }

        if (!cancellationToken.IsCancellationRequested && !_suppressRuleMatchRefresh && SelectedGroup is not null)
            _ = RefreshRuleMatchCountsAsync(cancellationToken);
    }

    public async Task SelectGroupAsync(long groupId, CancellationToken cancellationToken = default)
    {
        var group = Groups.FirstOrDefault(item => item.Id == groupId);
        if (group is null)
            return;

        BeginBusy();
        var previousSuppressRuleMatchRefresh = _suppressRuleMatchRefresh;
        _suppressRuleMatchRefresh = true;
        try
        {
            SelectedGroup = group;
            await RefreshRuleOptionsForRowsAsync(cancellationToken).ConfigureAwait(true);
            CaptureSavedDraft();
        }
        finally
        {
            _suppressRuleMatchRefresh = previousSuppressRuleMatchRefresh;
            EndBusy();
        }

        if (!cancellationToken.IsCancellationRequested && !_suppressRuleMatchRefresh)
            _ = RefreshRuleMatchCountsAsync(cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task Refresh() => await LoadAsync().ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanSaveGroup))]
    private async Task SaveGroup() => await SaveGroupAsync().ConfigureAwait(true);

    public async Task<bool> SaveGroupAsync()
    {
        if (!CanSaveGroup)
            return false;

        BeginBusy();
        try
        {
            RenumberRules();
            var saved = await areaGroupRepository.SaveConfigurationAsync(
                new AreaGroupEdit(SelectedGroup?.Id, EditName, _editAreaLabel, EditNote, EditPriority, EditEnabled, EditGroupKey),
                Rules.Select(row => new AreaGroupRuleEdit(
                    GroupId: 0,
                    row.Building,
                    row.Zuo,
                    row.Floor,
                    row.MatchMode,
                    row.Keywords,
                    row.Note,
                    row.Id == 0 ? null : row.Id,
                    row.RuleOrder)).ToArray()).ConfigureAwait(true);

            HasUnsavedChanges = false;
            await LoadAsync().ConfigureAwait(true);
            await SelectGroupAsync(saved.Id).ConfigureAwait(true);
            HasUnsavedChanges = false;
            StatusText = $"已保存区域组及 {Rules.Count:N0} 条规则：{saved.Name}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "保存区域组失败：" + ex.Message;
            return false;
        }
        finally { EndBusy(); }
    }

    public void DiscardChanges()
    {
        if (IsCreatingGroup)
        {
            CancelNewGroup();
            return;
        }

        if (SelectedGroup is null)
            return;

        LoadGroupEdit(SelectedGroup);
        RefreshRules();
        CaptureSavedDraft();
        StatusText = "已放弃未保存修改";
        NotifyCommands();
    }

    [RelayCommand]
    private void NewGroup()
    {
        if (HasUnsavedChanges)
        {
            StatusText = "当前有未保存修改，请先保存或取消后再新建区域组。";
            return;
        }

        _selectedGroupBeforeNew = SelectedGroup;
        IsCreatingGroup = true;
        SelectedGroup = null;
        Rules.Clear();
        EditName = "新区域组";
        _editAreaLabel = string.Empty;
        EditGroupKey = string.Empty;
        EditNote = string.Empty;
        EditPriority = "重点";
        EditEnabled = true;
        CaptureSavedDraft();
        StatusText = "正在新建区域组";
        NotifyCommands();
    }

    [RelayCommand]
    private void CancelNewGroup()
    {
        var groupToRestore = _selectedGroupBeforeNew;
        _selectedGroupBeforeNew = null;
        IsCreatingGroup = false;
        Rules.Clear();
        _editAreaLabel = string.Empty;
        SelectedGroup = groupToRestore ?? Groups.FirstOrDefault(group => group.IsEnabled) ?? Groups.FirstOrDefault();
        CaptureSavedDraft();
        StatusText = "已取消新建区域组";
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedGroup))]
    public async Task DeleteGroupAsync()
    {
        if (SelectedGroup is null) return;
        await RunMutationAsync(async () => { var id = SelectedGroup.Id; await areaGroupRepository.DeleteGroupAsync(id).ConfigureAwait(true); StatusText = $"已删除区域组：#{id}"; await LoadAsync().ConfigureAwait(true); }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanBeginAddRule))]
    private void BeginAddRule()
    {
        if (SelectedGroup is null && !IsCreatingGroup)
            return;

        var groupId = SelectedGroup?.GroupId ?? 0;
        var nextOrder = Rules.Count + 1;
        var row = new AreaGroupRuleRow(groupId, nextOrder);
        row.PropertyChanged += RuleRow_PropertyChanged;
        Rules.Add(row);
        RenumberRules();
        RecomputeDraftDirty();
        OnPropertyChanged(nameof(RulesEmptyVisibility));
        _ = RefreshRuleOptionsAsync(row);
        _ = RefreshRuleMatchCountsAsync();
        StatusText = "正在添加规则";
        NotifyCommands();
    }
    public async Task RefreshRuleOptionsAsync(AreaGroupRuleRow row, CancellationToken cancellationToken = default)
    {
        var requestVersion = BeginRuleOptionsRequest(row);
        var requestedBuilding = row.Building;
        if (string.IsNullOrWhiteSpace(requestedBuilding))
            return;

        IReadOnlyList<FloorCatalogRecord> floors;
        try
        {
            floors = await areaGroupRepository.LoadFloorsAsync(
                requestedBuilding,
                includeDisabled: false,
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!IsCurrentRuleOptionsRequest(row, requestVersion))
                return;

            RuleOptionsError = $"楼层加载失败：{ex.Message}";
            return;
        }

        ApplyRuleFloorOptions(row, requestedBuilding, requestVersion, floors);
    }

    private async Task RefreshRuleOptionsForRowsAsync(CancellationToken cancellationToken)
    {
        var rows = Rules.ToArray();
        var floorsByBuilding = new Dictionary<string, IReadOnlyList<FloorCatalogRecord>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var building in rows
                .Select(row => row.Building)
                .Where(building => !string.IsNullOrWhiteSpace(building))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                floorsByBuilding[building] = await areaGroupRepository.LoadFloorsAsync(
                    building,
                    includeDisabled: false,
                    cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            RuleOptionsError = $"楼层加载失败：{ex.Message}";
            return;
        }

        foreach (var row in rows)
        {
            var requestedBuilding = row.Building;
            if (string.IsNullOrWhiteSpace(requestedBuilding) ||
                !floorsByBuilding.TryGetValue(requestedBuilding, out var floors))
            {
                continue;
            }

            ApplyRuleFloorOptions(row, requestedBuilding, BeginRuleOptionsRequest(row), floors);
        }
    }

    private long BeginRuleOptionsRequest(AreaGroupRuleRow row)
    {
        var requestVersion = _ruleOptionsVersions.TryGetValue(row, out var previousVersion)
            ? previousVersion + 1
            : 1;
        _ruleOptionsVersions[row] = requestVersion;
        Interlocked.Increment(ref _ruleOptionsVersion);
        return requestVersion;
    }

    private void ApplyRuleFloorOptions(
        AreaGroupRuleRow row,
        string requestedBuilding,
        long requestVersion,
        IReadOnlyList<FloorCatalogRecord> floors)
    {
        if (!IsCurrentRuleOptionsRequest(row, requestVersion) ||
            !string.Equals(requestedBuilding, row.Building, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RuleOptionsError = string.Empty;

        var floorLabels = floors
            .Select(item => AreaGroupRuleNormalizer.NormalizeFloorLabel(item.FloorLabel))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.Equals(row.Building, "6号", StringComparison.OrdinalIgnoreCase) &&
            !floorLabels.Contains("BM", StringComparer.OrdinalIgnoreCase))
        {
            floorLabels.Add("BM");
        }

        var options = floorLabels
            .OrderBy(FloorSortValue)
            .ThenBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();
        options.Insert(0, "-");
        if (!options.Contains(row.Floor, StringComparer.OrdinalIgnoreCase))
            options.Add(row.Floor);
        _suppressDraftDirty = true;
        try
        {
            row.ConfigureFloorOptions(options);
        }
        finally
        {
            _suppressDraftDirty = false;
        }
    }

    public async Task DeleteRuleRowAsync(AreaGroupRuleRow row)
    {
        if (IsLoading)
            return;

        if (row.Id == 0)
        {
            Rules.Remove(row);
            _ruleOptionsVersions.Remove(row);
            RenumberRules();
            RecomputeDraftDirty();
            OnPropertyChanged(nameof(RulesEmptyVisibility));
            StatusText = "已移除未保存规则";
            return;
        }

        Rules.Remove(row);
        _ruleOptionsVersions.Remove(row);
        RenumberRules();
        RecomputeDraftDirty();
        OnPropertyChanged(nameof(RulesEmptyVisibility));
        StatusText = "规则已移除，保存区域组后生效";
    }

    public void OpenSelectedInData()
    {
        if (SelectedGroup?.GroupId is long groupId) navigationService.NavigateToData(new DataNavigationRequest(AreaGroupId: groupId));
    }

    public async Task ExportRulesAsync(string path, CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;
        BeginBusy();
        try
        {
            var document = await areaGroupRepository.ExportAsync(cancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(path, AreaGroupTransferCodec.Serialize(document), Encoding.UTF8, cancellationToken).ConfigureAwait(true);
            StatusText = $"已导出 {document.Groups.Count:N0} 个区域组";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "已取消导出规则";
        }
        catch (Exception ex)
        {
            StatusText = "导出规则失败：" + ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    public async Task ImportRulesAsync(string path, CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;
        if (HasUnsavedChanges)
        {
            StatusText = "当前有未保存修改，请先保存或取消后再导入规则。";
            return;
        }

        BeginBusy();
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true);
            await areaGroupRepository.ImportAsync(AreaGroupTransferCodec.Deserialize(json), cancellationToken).ConfigureAwait(true);
            await LoadAsync(cancellationToken).ConfigureAwait(true);
            StatusText = "区域组规则已导入";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "已取消导入规则";
        }
        catch (Exception ex)
        {
            StatusText = "导入规则失败：" + ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    private void RefreshRules()
    {
        Rules.Clear();
        if (SelectedGroup is not null)
        {
            foreach (var rule in RuleRecords.Where(rule => rule.GroupId == SelectedGroup.GroupId).OrderBy(rule => rule.RuleOrder))
            {
                var row = new AreaGroupRuleRow(rule);
                row.PropertyChanged += RuleRow_PropertyChanged;
                Rules.Add(row);
            }
        }
        RenumberRules();
        OnPropertyChanged(nameof(RulesEmptyVisibility));
        if (!_suppressRuleMatchRefresh)
            _ = RefreshRuleMatchCountsAsync();
    }

    private void LoadGroupEdit(GroupSummaryRow? group)
    {
        var record = group is null ? null : GroupRecords.FirstOrDefault(item => item.Id == group.Id);
        _suppressDraftDirty = true;
        EditName = record?.Name ?? string.Empty;
        _editAreaLabel = record?.AreaLabel ?? string.Empty;
        EditGroupKey = record?.GroupKey ?? string.Empty;
        EditNote = record?.Description ?? string.Empty;
        EditPriority = record?.Priority ?? "重点";
        EditEnabled = record?.Enabled ?? true;
        _suppressDraftDirty = false;
    }

    private async Task RefreshRuleMatchCountsAsync(CancellationToken cancellationToken = default)
    {
        var requestVersion = Interlocked.Increment(ref _ruleMatchCountVersion);
        var rows = Rules.ToArray();
        if (rows.Length == 0 || SelectedGroup is null)
            return;

        try
        {
            var result = await deviceReadRepository.SearchAsync(
                new DeviceQuery(Limit: 50000),
                cancellationToken).ConfigureAwait(true);
            var devicesByBuilding = result.Rows
                .GroupBy(device => device.Building, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<DeviceRecord>)group.ToArray(), StringComparer.OrdinalIgnoreCase);

            if (requestVersion != Volatile.Read(ref _ruleMatchCountVersion))
                return;

            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Building))
                {
                    row.SetMatchCount(0);
                    continue;
                }

                var devices = devicesByBuilding.TryGetValue(row.Building, out var buildingDevices)
                    ? buildingDevices
                    : [];
                row.SetMatchCount(AreaGroupRuleMatcher.CountMatches(devices, row.Record));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (requestVersion == Volatile.Read(ref _ruleMatchCountVersion))
                StatusText = "规则匹配数读取失败：" + ex.Message;
        }
    }

    private static double FloorSortValue(string floor)
    {
        return DeviceFloorLabelFormatter.SortValue(floor);
    }

    private async Task RunMutationAsync(Func<Task> mutation)
    {
        try { BeginBusy(); await mutation().ConfigureAwait(true); } catch (Exception ex) { StatusText = "操作失败：" + ex.Message; } finally { EndBusy(); }
    }

    private void BeginBusy()
    {
        if (Interlocked.Increment(ref _busyDepth) == 1)
            IsLoading = true;
    }

    private void EndBusy()
    {
        if (Interlocked.Decrement(ref _busyDepth) == 0)
            IsLoading = false;
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged(); SaveGroupCommand.NotifyCanExecuteChanged(); DeleteGroupCommand.NotifyCanExecuteChanged(); BeginAddRuleCommand.NotifyCanExecuteChanged(); NewGroupCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanRefresh)); OnPropertyChanged(nameof(CanRunFileOperation)); OnPropertyChanged(nameof(CanDeleteSelectedGroup)); OnPropertyChanged(nameof(CanOpenSelectedInData)); OnPropertyChanged(nameof(CanBeginAddRule)); OnPropertyChanged(nameof(CanEditSelectedGroup)); OnPropertyChanged(nameof(CanNewGroup));
    }

    private void MarkDraftDirty()
    {
        if (!_suppressDraftDirty && !IsLoading)
        {
            RecomputeDraftDirty();
            SaveGroupCommand.NotifyCanExecuteChanged();
        }
    }

    private void CaptureSavedDraft()
    {
        _savedDraft = CreateDraftSnapshot();
        HasUnsavedChanges = false;
    }

    private void RecomputeDraftDirty()
    {
        if (_suppressDraftDirty || IsLoading)
            return;

        HasUnsavedChanges = _savedDraft is null || !_savedDraft.Equals(CreateDraftSnapshot());
        SaveGroupCommand.NotifyCanExecuteChanged();
    }

    private AreaGroupDraftSnapshot CreateDraftSnapshot()
    {
        var group = new AreaGroupEdit(
            SelectedGroup?.Id,
            EditName,
            _editAreaLabel,
            EditNote,
            EditPriority,
            EditEnabled,
            EditGroupKey);
        var rules = Rules.Select(row => new AreaGroupRuleEdit(
            GroupId: 0,
            row.Building,
            row.Zuo,
            row.Floor,
            row.MatchMode,
            row.Keywords,
            row.Note,
            row.Id == 0 ? null : row.Id,
            row.RuleOrder));
        return new AreaGroupDraftSnapshot(group, rules);
    }

    private bool IsCurrentRuleOptionsRequest(AreaGroupRuleRow row, long requestVersion)
    {
        return _ruleOptionsVersions.TryGetValue(row, out var currentVersion) && currentVersion == requestVersion;
    }

    private void RuleRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AreaGroupRuleRow.MatchCount) or nameof(AreaGroupRuleRow.MatchCountText))
            return;

        MarkDraftDirty();
        if (!_suppressRuleMatchRefresh)
            _ = RefreshRuleMatchCountsAsync();
    }

    private void RenumberRules()
    {
        for (var index = 0; index < Rules.Count; index++)
            Rules[index].SetRuleOrder(index + 1);
    }
}
