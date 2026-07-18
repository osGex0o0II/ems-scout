using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Groups;
using EmsScout.Application.Logging;
using EmsScout.Desktop.Services;
using EmsScout.Infrastructure.Logging;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class GroupsViewModel(
    IAreaGroupRepository areaGroupRepository,
    IAreaGroupReconciliationRepository reconciliationRepository,
    INavigationService navigationService,
    IApplicationLogger applicationLogger) : ObservableObject
{
    private bool _suppressSelectionLoad;
    private int _selectionLoadVersion;
    private GroupSummaryRow? _selectedGroup;
    private readonly List<AreaGroupTargetOptionRow> _loadedDeviceDirectory = [];

    public ObservableCollection<GroupSummaryRow> Groups { get; } = [];

    public ObservableCollection<AreaGroupRuleRecord> Rules { get; } = [];

    public ObservableCollection<AreaGroupMemberRecord> Members { get; } = [];

    public ObservableCollection<AreaGroupExceptionRecord> Exceptions { get; } = [];

    public ObservableCollection<AreaGroupTargetOptionRow> DeviceDirectory { get; } = [];

    public ObservableCollection<string> BuildingOptions { get; } =
        ["1号", "2号", "3号", "4号", "5号", "6号"];

    public ObservableCollection<AreaGroupRuleBuildingOption> RuleBuildingOptions { get; } =
    [
        new(string.Empty, "全部楼栋"),
        new("1号", "1号"),
        new("2号", "2号"),
        new("3号", "3号"),
        new("4号", "4号"),
        new("5号", "5号"),
        new("6号", "6号"),
    ];

    public ObservableCollection<AreaGroupRuleTypeOption> RuleTypes { get; } =
    [
        new("area_public", "预设公区分类"),
        new("area_non_public", "预设非公区分类"),
        new("floor", "楼层持续规则"),
        new("name_exact", "设备名精确匹配"),
        new("name_keyword", "设备名关键字"),
    ];

    public GroupSummaryRow? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedGroup));
            OnPropertyChanged(nameof(GroupEditorTitle));
            OnPropertyChanged(nameof(PendingSummaryText));
            SaveGroupCommand.NotifyCanExecuteChanged();
            DeleteGroupCommand.NotifyCanExecuteChanged();
            SaveRuleCommand.NotifyCanExecuteChanged();
            AddManualMemberCommand.NotifyCanExecuteChanged();
            AddFloorRuleCommand.NotifyCanExecuteChanged();
            OpenAuditCommand.NotifyCanExecuteChanged();
            if (!_suppressSelectionLoad)
            {
                _ = LoadSelectedGroupAsync();
            }
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveGroupCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteGroupCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveRuleCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddFloorRuleCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddManualMemberCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenAuditCommand))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "正在读取区域组";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveGroupCommand))]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditAreaLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditPriority { get; set; } = "重点";

    [ObservableProperty]
    public partial bool EditEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRuleCommand))]
    public partial AreaGroupRuleTypeOption? SelectedRuleType { get; set; }

    [ObservableProperty]
    public partial AreaGroupRuleRecord? SelectedRule { get; set; }

    [ObservableProperty]
    public partial bool EditRuleEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRuleCommand))]
    public partial string RuleBuilding { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRuleCommand))]
    public partial string RuleFloorLabel { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRuleCommand))]
    public partial string RuleKeyword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RuleNote { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddFloorRuleCommand))]
    public partial string DeviceBuilding { get; set; } = "1号";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddFloorRuleCommand))]
    public partial string DeviceFloor { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceDirectorySearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddManualMemberCommand))]
    public partial AreaGroupTargetOptionRow? SelectedDevice { get; set; }

    [ObservableProperty]
    public partial string ManualMemberNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial AreaGroupMemberRecord? SelectedMember { get; set; }

    [ObservableProperty]
    public partial string MemberNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial AreaGroupExceptionRecord? SelectedException { get; set; }

    [ObservableProperty]
    public partial string ExceptionNote { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingSummaryText))]
    public partial int PendingAddCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingSummaryText))]
    public partial int PendingRemoveCount { get; private set; }

    public bool HasSelectedGroup => SelectedGroup?.GroupId is not null;

    public string GroupEditorTitle => SelectedGroup is null ? "新建区域组" : "编辑区域组";

    public string PendingSummaryText => HasSelectedGroup
        ? $"待确认加入 {PendingAddCount:N0} 台 · 待确认移除 {PendingRemoveCount:N0} 台"
        : "选择区域组后查看待确认变更";

    partial void OnDeviceDirectorySearchTextChanged(string value) => ApplyDeviceDirectoryFilter();

    partial void OnSelectedExceptionChanged(AreaGroupExceptionRecord? value)
    {
        ExceptionNote = value?.Note ?? string.Empty;
    }

    partial void OnSelectedRuleChanged(AreaGroupRuleRecord? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedRuleType = RuleTypes.FirstOrDefault(option => option.Value == value.RuleType);
        RuleBuilding = value.Building;
        RuleFloorLabel = value.FloorLabel;
        RuleKeyword = value.MatchValue;
        RuleNote = value.Note;
        EditRuleEnabled = value.Enabled;
    }

    partial void OnSelectedMemberChanged(AreaGroupMemberRecord? value)
    {
        MemberNote = value?.Note ?? string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await ReloadAsync(SelectedGroup?.GroupId, cancellationToken).ConfigureAwait(true);
    }

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync(CancellationToken cancellationToken = default) =>
        ReloadAsync(SelectedGroup?.GroupId, cancellationToken);

    [RelayCommand]
    private void NewGroup()
    {
        _selectionLoadVersion++;
        _suppressSelectionLoad = true;
        SelectedGroup = null;
        _suppressSelectionLoad = false;
        EditName = string.Empty;
        EditAreaLabel = string.Empty;
        EditDescription = string.Empty;
        EditPriority = "重点";
        EditEnabled = true;
        ClearManagementState();
        StatusText = "填写名称后保存区域组";
    }

    private bool CanSaveGroup() => !IsLoading && !string.IsNullOrWhiteSpace(EditName);

    [RelayCommand(CanExecute = nameof(CanSaveGroup))]
    private async Task SaveGroupAsync(CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(async () =>
        {
            var saved = await areaGroupRepository.SaveGroupAsync(
                new AreaGroupEdit(
                    SelectedGroup?.GroupId,
                    EditName,
                    EditAreaLabel,
                    EditDescription,
                    EditPriority,
                    EditEnabled),
                cancellationToken).ConfigureAwait(true);
            await ReloadAsync(saved.Id, cancellationToken, manageBusy: false).ConfigureAwait(true);
            StatusText = "区域组已保存";
        }, "保存区域组失败").ConfigureAwait(true);
    }

    private bool CanDeleteGroup() => HasSelectedGroup && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanDeleteGroup))]
    public async Task DeleteGroupAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGroup?.GroupId is not { } groupId)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await areaGroupRepository.DeleteGroupAsync(groupId, cancellationToken).ConfigureAwait(true);
            await ReloadAsync(null, cancellationToken, manageBusy: false).ConfigureAwait(true);
            StatusText = "区域组已删除";
        }, "删除区域组失败").ConfigureAwait(true);
    }

    private bool CanSaveRule()
    {
        if (!HasSelectedGroup || IsLoading || SelectedRuleType is null)
        {
            return false;
        }

        if (SelectedRuleType.Value == "floor")
        {
            return !string.IsNullOrWhiteSpace(RuleBuilding) &&
                   !string.IsNullOrWhiteSpace(RuleFloorLabel);
        }

        if (SelectedRuleType.Value is "name_exact" or "name_keyword")
        {
            return !string.IsNullOrWhiteSpace(RuleKeyword) &&
                   (string.IsNullOrWhiteSpace(RuleFloorLabel) ||
                    !string.IsNullOrWhiteSpace(RuleBuilding));
        }

        return true;
    }

    [RelayCommand]
    private void NewRule()
    {
        SelectedRule = null;
        SelectedRuleType = RuleTypes[0];
        RuleBuilding = string.Empty;
        RuleFloorLabel = string.Empty;
        RuleKeyword = string.Empty;
        RuleNote = string.Empty;
        EditRuleEnabled = true;
        StatusText = "填写持续规则后保存";
    }

    [RelayCommand(CanExecute = nameof(CanSaveRule))]
    private async Task SaveRuleAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGroup?.GroupId is not { } groupId || SelectedRuleType is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var saved = await reconciliationRepository.SaveRuleAsync(
                new AreaGroupRuleEdit(
                    groupId,
                    SelectedRuleType.Value,
                    RuleBuilding,
                    RuleFloorLabel,
                    RuleKeyword,
                    RuleNote,
                    SelectedRule?.Id,
                    EditRuleEnabled),
                cancellationToken).ConfigureAwait(true);
            await LoadSelectedGroupAsync(cancellationToken, manageBusy: false).ConfigureAwait(true);
            SelectedRule = Rules.FirstOrDefault(rule => rule.Id == saved.Id);
            StatusText = "持续规则已保存；新匹配设备会进入审计待确认";
        }, "保存持续规则失败").ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task DeleteRuleAsync(AreaGroupRuleRecord? rule, CancellationToken cancellationToken = default)
    {
        if (rule is null || IsLoading || rule.GroupId != SelectedGroup?.GroupId)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await reconciliationRepository.DeleteRuleAsync(rule.Id, cancellationToken).ConfigureAwait(true);
            await LoadSelectedGroupAsync(cancellationToken, manageBusy: false).ConfigureAwait(true);
            StatusText = "持续规则已删除；受影响成员会在后续采集后进入待确认移除";
        }, "删除持续规则失败").ConfigureAwait(true);
    }

    private bool CanAddFloorRule() => HasSelectedGroup && !IsLoading &&
                                      !string.IsNullOrWhiteSpace(DeviceBuilding) &&
                                      !string.IsNullOrWhiteSpace(DeviceFloor);

    [RelayCommand(CanExecute = nameof(CanAddFloorRule))]
    private async Task AddFloorRuleAsync(CancellationToken cancellationToken = default)
    {
        SelectedRule = null;
        SelectedRuleType = RuleTypes.First(option => option.Value == "floor");
        RuleBuilding = DeviceBuilding;
        RuleFloorLabel = DeviceFloor;
        RuleKeyword = string.Empty;
        await SaveRuleAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LoadDeviceDirectoryAsync(CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(async () =>
        {
            ClearDeviceDirectoryState();
            var options = await areaGroupRepository
                .LoadTargetOptionsAsync(DeviceBuilding, DeviceFloor, cancellationToken)
                .ConfigureAwait(true);
            _loadedDeviceDirectory.AddRange(options
                .Where(option => option.Type == "device")
                .Select(option => new AreaGroupTargetOptionRow(option)));
            ApplyDeviceDirectoryFilter();
            StatusText = $"现有设备目录已完整加载 {_loadedDeviceDirectory.Count:N0} 台";
        }, "读取现有设备目录失败").ConfigureAwait(true);
    }

    private bool CanAddManualMember() => HasSelectedGroup && SelectedDevice is not null && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanAddManualMember))]
    private async Task AddManualMemberAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGroup?.GroupId is not { } groupId || SelectedDevice is null)
        {
            return;
        }

        var device = SelectedDevice;
        await RunBusyAsync(async () =>
        {
            await reconciliationRepository.AddManualMemberAsync(
                new AreaGroupManualMemberEdit(
                    groupId,
                    device.DeviceUid,
                    device.Building,
                    device.FloorLabel,
                    ParseFloor(device.FloorLabel),
                    device.SubAreaText,
                    device.PageName,
                    device.CardName,
                    device.SourceKey,
                    device.Occurrence,
                    ManualMemberNote),
                cancellationToken).ConfigureAwait(true);
            await LoadSelectedGroupAsync(cancellationToken, manageBusy: false).ConfigureAwait(true);
            StatusText = "设备已作为手动成员加入";
        }, "添加设备失败").ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task DeleteManualMemberAsync(AreaGroupMemberRecord? member, CancellationToken cancellationToken = default)
    {
        if (member is null || IsLoading || member.GroupId != SelectedGroup?.GroupId)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            if (member.MemberOrigin is "manual" or "legacy")
            {
                await reconciliationRepository.DeleteManualMemberAsync(member.Id, cancellationToken).ConfigureAwait(true);
                StatusText = "成员已移除";
            }
            else
            {
                await reconciliationRepository.BlockMemberAsync(member.Id, member.Note, cancellationToken).ConfigureAwait(true);
                StatusText = "规则成员已移除并加入长期屏蔽名单";
            }
            await LoadSelectedGroupAsync(cancellationToken, manageBusy: false).ConfigureAwait(true);
        }, "移除成员失败").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UpdateMemberNoteAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedMember is not { } member || IsLoading || member.GroupId != SelectedGroup?.GroupId)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await reconciliationRepository
                .UpdateMemberNoteAsync(member.Id, MemberNote, cancellationToken)
                .ConfigureAwait(true);
            await LoadSelectedGroupAsync(cancellationToken, manageBusy: false).ConfigureAwait(true);
            SelectedMember = Members.FirstOrDefault(row => row.Id == member.Id);
            StatusText = "成员备注已更新";
        }, "更新成员备注失败").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UpdateExceptionNoteAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedException is null || IsLoading || SelectedException.GroupId != SelectedGroup?.GroupId)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await reconciliationRepository.UpdateExceptionNoteAsync(
                SelectedException.Id,
                ExceptionNote,
                cancellationToken).ConfigureAwait(true);
            await LoadSelectedGroupAsync(cancellationToken, manageBusy: false).ConfigureAwait(true);
            StatusText = "例外备注已更新";
        }, "更新例外备注失败").ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task DeleteExceptionAsync(AreaGroupExceptionRecord? exception, CancellationToken cancellationToken = default)
    {
        if (exception is null || IsLoading || exception.GroupId != SelectedGroup?.GroupId)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await reconciliationRepository.DeleteExceptionAsync(exception.Id, cancellationToken).ConfigureAwait(true);
            await LoadSelectedGroupAsync(cancellationToken, manageBusy: false).ConfigureAwait(true);
            StatusText = "例外已撤销；后续采集将重新按规则判断";
        }, "撤销例外失败").ConfigureAwait(true);
    }

    private bool CanOpenAudit() => HasSelectedGroup && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanOpenAudit))]
    public void OpenAudit()
    {
        navigationService.NavigateToAudit(SelectedGroup?.GroupId);
    }

    private async Task ReloadAsync(long? selectedGroupId, CancellationToken cancellationToken, bool manageBusy = true)
    {
        if (manageBusy)
        {
            IsLoading = true;
        }

        try
        {
            var set = await areaGroupRepository.LoadAsync(cancellationToken).ConfigureAwait(true);
            Groups.Clear();
            foreach (var group in set.Groups)
            {
                Groups.Add(new GroupSummaryRow(group));
            }

            _suppressSelectionLoad = true;
            SelectedGroup = Groups.FirstOrDefault(group => group.GroupId == selectedGroupId) ?? Groups.FirstOrDefault();
            _suppressSelectionLoad = false;
            if (SelectedGroup is null)
            {
                NewGroup();
            }
            else
            {
                ApplyGroupDraft(SelectedGroup);
                await LoadSelectedGroupAsync(cancellationToken, manageBusy: false).ConfigureAwait(true);
            }
            StatusText = $"已加载 {Groups.Count:N0} 个区域组";
        }
        catch (Exception ex)
        {
            StatusText = "读取区域组失败：" + applicationLogger.WriteFailure(ex, "groups").DisplayText;
        }
        finally
        {
            if (manageBusy)
            {
                IsLoading = false;
            }
        }
    }

    private async Task LoadSelectedGroupAsync(CancellationToken cancellationToken = default, bool manageBusy = true)
    {
        var loadVersion = ++_selectionLoadVersion;
        var selectedGroup = SelectedGroup;
        if (selectedGroup?.GroupId is not { } groupId)
        {
            if (loadVersion == _selectionLoadVersion)
            {
                ClearManagementState();
            }
            return;
        }

        if (manageBusy)
        {
            IsLoading = true;
        }

        try
        {
            ApplyGroupDraft(selectedGroup);
            AreaGroupManagementSnapshot snapshot = await reconciliationRepository.LoadAsync(groupId, cancellationToken).ConfigureAwait(true);
            if (loadVersion != _selectionLoadVersion || SelectedGroup?.GroupId != groupId)
            {
                return;
            }

            Replace(Rules, snapshot.Rules);
            Replace(Members, snapshot.Members);
            Replace(Exceptions, snapshot.Exceptions);
            PendingAddCount = snapshot.PendingChanges.Count(change => change.Action == "add");
            PendingRemoveCount = snapshot.PendingChanges.Count(change => change.Action == "remove");
            StatusText = $"已读取“{selectedGroup.Name}”的规则和正式成员";
        }
        catch (Exception ex)
        {
            StatusText = "读取分组详情失败：" + applicationLogger.WriteFailure(ex, "groups").DisplayText;
        }
        finally
        {
            if (manageBusy && loadVersion == _selectionLoadVersion)
            {
                IsLoading = false;
            }
        }
    }

    private async Task RunBusyAsync(Func<Task> action, string failurePrefix)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = failurePrefix + "：" + applicationLogger.WriteFailure(ex, "groups").DisplayText;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyGroupDraft(GroupSummaryRow group)
    {
        EditName = group.Name;
        EditAreaLabel = group.AreaLabel;
        EditDescription = group.Description;
        EditPriority = string.IsNullOrWhiteSpace(group.Priority) ? "重点" : group.Priority;
        EditEnabled = group.IsEnabled;
        SelectedRuleType ??= RuleTypes[0];
    }

    private void ApplyDeviceDirectoryFilter()
    {
        var search = (DeviceDirectorySearchText ?? string.Empty).Trim();
        DeviceDirectory.Clear();
        foreach (var row in _loadedDeviceDirectory.Where(row =>
                     search.Length == 0 ||
                     row.CardName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                     row.DeviceUid.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                     row.SubAreaText.Contains(search, StringComparison.OrdinalIgnoreCase)))
        {
            DeviceDirectory.Add(row);
        }
    }

    private void ClearDeviceDirectoryState()
    {
        SelectedDevice = null;
        DeviceDirectory.Clear();
        _loadedDeviceDirectory.Clear();
    }

    private void ClearManagementState()
    {
        SelectedRule = null;
        SelectedMember = null;
        SelectedException = null;
        Rules.Clear();
        Members.Clear();
        Exceptions.Clear();
        ClearDeviceDirectoryState();
        PendingAddCount = 0;
        PendingRemoveCount = 0;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }

    private static double? ParseFloor(string value)
    {
        if (string.Equals((value ?? string.Empty).Trim(), "BM", StringComparison.OrdinalIgnoreCase))
        {
            return -2;
        }

        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant()
            .Replace("F", string.Empty, StringComparison.Ordinal)
            .Replace("B", "-", StringComparison.Ordinal);
        return double.TryParse(normalized, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}

public sealed record AreaGroupRuleTypeOption(string Value, string Label);

public sealed record AreaGroupRuleBuildingOption(string Value, string Label);
