using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Application.Watch;
using EmsScout.Desktop.Services;
using Microsoft.UI.Xaml;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class GroupsViewModel(
    IAreaGroupRepository areaGroupRepository,
    IDeviceWatchRepository watchRepository,
    INavigationService navigationService) : ObservableObject
{
    private string _statusText = "正在读取区域组";
    private bool _isLoading;
    private GroupSummaryRow? _selectedGroup;
    private AreaGroupItemRow? _selectedItem;
    private AreaGroupTargetOptionRow? _selectedTargetOption;
    private AreaGroupTargetTypeOption? _selectedTargetType;
    private MemberEditorMode _memberEditorMode = MemberEditorMode.None;
    private long? _editingItemId;
    private string _draftSubAreaText = string.Empty;
    private string _draftCardName = string.Empty;
    private bool _loadingMemberDraft;
    private string _editName = string.Empty;
    private string _editAreaLabel = string.Empty;
    private string _editDescription = string.Empty;
    private string _editPriority = "重点";
    private bool _editEnabled = true;
    private string _targetBuilding = "1号";
    private string _targetFloor = string.Empty;
    private string _targetNamePattern = string.Empty;
    private string _targetNote = string.Empty;
    private string _targetOptionSearchText = string.Empty;
    private long _targetOptionsLoadVersion;
    private FloorCatalogRow? _selectedFloorCatalog;
    private string _floorCatalogBuilding = "1号";
    private string _floorCatalogLabel = string.Empty;
    private string _floorCatalogNote = string.Empty;
    private long? _watchRuleId;
    private bool _watchEnabled;
    private string _watchName = "关注设备";
    private DateTimeOffset _watchStartDate = DateTimeOffset.Now.Date;
    private TimeSpan _watchStartTime = new(18, 0, 0);
    private DateTimeOffset _watchEndDate = DateTimeOffset.Now.Date.AddDays(1);
    private TimeSpan _watchEndTime = new(8, 0, 0);
    private string _watchNote = string.Empty;
    private string _watchSummaryText = "选择自定义区域组后可设置关注窗口";
    private WatchIncidentRow? _selectedWatchIncident;
    private long _watchLoadVersion;

    public ObservableCollection<GroupSummaryRow> Groups { get; } = [];

    public ObservableCollection<AreaGroupItemRow> Items { get; } = [];

    public ObservableCollection<AreaGroupTargetOptionRow> TargetOptions { get; } = [];

    private List<AreaGroupTargetOptionRow> LoadedTargetOptions { get; } = [];

    public ObservableCollection<DataFilterOption> FloorOptions { get; } = [];

    public ObservableCollection<FloorCatalogRow> FloorCatalog { get; } = [];

    public ObservableCollection<WatchIncidentRow> WatchIncidents { get; } = [];

    public ObservableCollection<AreaGroupTargetTypeOption> TargetTypes { get; } =
    [
        new("floor", "整个楼层"),
        new("sub_area", "页面区域"),
        new("device", "单台设备"),
        new("name_contains", "名称包含"),
        new("name_excludes", "名称不包含"),
    ];

    public ObservableCollection<string> BuildingOptions { get; } = ["1号", "2号", "3号", "4号", "5号", "6号"];

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsEditingCustomGroup => SelectedGroup is { IsCustom: true } || SelectedGroup is null;

    public bool IsSystemGroupSelected => SelectedGroup is { IsCustom: false };

    public bool CanMaintainMembers => SelectedGroup is { IsCustom: true } && !IsLoading;

    public bool CanLoadMemberOptions => CanMaintainMembers && CanEditMemberTarget && SelectedTargetType?.Value is "sub_area" or "device";

    public Visibility NamePatternVisibility => SelectedTargetType?.Value is "name_contains" or "name_excludes"
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsMemberDraftActive => _memberEditorMode is MemberEditorMode.Adding or MemberEditorMode.Editing;

    public bool IsEditingMember => _memberEditorMode == MemberEditorMode.Editing;

    public bool CanEditMemberTarget => CanMaintainMembers && IsMemberDraftActive;

    public bool CanOperateMemberRows => CanMaintainMembers && !IsMemberDraftActive;

    public bool CanSelectMemberOption => CanLoadMemberOptions;

    public bool CanSearchMemberOptions => CanLoadMemberOptions && LoadedTargetOptions.Count > 0;

    public Visibility CandidatePickerVisibility => CanLoadMemberOptions
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility MemberDraftVisibility => IsMemberDraftActive
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool CanBeginAddMember => CanMaintainMembers && !IsMemberDraftActive;

    public bool CanSaveMemberDraft => CanAddItem();

    public bool CanCancelMemberDraft => CanMaintainMembers && IsMemberDraftActive;

    public string MemberDraftTitle => _memberEditorMode switch
    {
        MemberEditorMode.Adding => "添加楼层或设备",
        MemberEditorMode.Editing => "编辑已添加内容",
        _ => "已添加内容",
    };

    public string MemberSaveButtonText => _memberEditorMode == MemberEditorMode.Editing ? "保存" : "添加";

    public string MemberConflictMessage => BuildMemberConflictMessage();

    public Visibility MemberConflictVisibility => string.IsNullOrWhiteSpace(MemberConflictMessage)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool CanOpenSelectedInData => SelectedGroup?.CanOpenInData == true && !IsLoading;

    public bool CanMaintainWatch => SelectedGroup is { IsCustom: true } && !IsLoading;

    public bool CanDeleteSelectedGroup => CanDeleteGroup();

    public bool CanDeleteSelectedItem => CanDeleteItem();

    public bool CanDeleteSelectedWatch => CanDeleteWatch();

    public bool CanDeleteSelectedFloor => CanDeleteFloor();

    public bool CanOpenSelectedWatchIncident => SelectedWatchIncident is not null && !IsLoading;

    public Visibility LoadingStateVisibility => IsLoading
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility GroupListEmptyVisibility => !IsLoading && Groups.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ItemsEmptyVisibility => !IsLoading && CanMaintainMembers && Items.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string WatchEditorTitle => CanMaintainWatch ? "关注设备" : "关注设备（选择自定义区域组后可用）";

    public string WatchEditorMessage => SelectedGroup is null
        ? "保存区域组后，可设置关注时间。"
        : IsSystemGroupSelected
            ? "系统区域不直接维护关注规则，请新建自定义区域组。"
            : "关注时间内组内设备发生开机/关机变化时，会在数据管理标记为异常。";

    public string WatchIncidentSummary => WatchIncidents.Count == 0
        ? "当前关注窗口内暂无开关变化事件"
        : $"关注事件 {WatchIncidents.Count:N0} 条";

    public string WatchTimeValidationMessage => !CanMaintainWatch
        ? string.Empty
        : IsWatchWindowValid
            ? "关注窗口有效"
            : "结束时间必须晚于开始时间";

    public Visibility WatchTimeValidationVisibility => CanMaintainWatch && !IsWatchWindowValid
        ? Visibility.Visible
        : Visibility.Collapsed;

    private bool IsWatchWindowValid => CombineDateAndTime(WatchEndDate, WatchEndTime) >
                                       CombineDateAndTime(WatchStartDate, WatchStartTime);

    public Visibility WatchIncidentEmptyVisibility => WatchIncidents.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string GroupEditorTitle => SelectedGroup is null ? "新建区域组" : IsEditingCustomGroup ? "区域组设置" : "系统区域详情";

    public string GroupEditorMessage => SelectedGroup is null
        ? "填写名称并保存，然后添加要关注的楼层或设备。"
        : IsEditingCustomGroup
            ? "该区域组会显示在首页，也可在数据管理中筛选并导出。"
            : "公区和非公区由设备类型自动归类，不能手动添加或删除。";

    public string MemberEditorTitle => CanMaintainMembers ? "已添加的楼层和设备" : "区域组成员";

    public string MemberEditorMessage => SelectedGroup is null
        ? "先保存区域组。"
        : IsSystemGroupSelected
            ? "公区和非公区由设备类型自动归类，不能手动添加或删除。"
            : $"已添加 {Items.Count:N0} 个范围，覆盖 {SelectedGroup.Count:N0} 台设备。";

    public string MemberTargetPreview
    {
        get
        {
            if (!CanMaintainMembers)
            {
                return "保存并选择区域组后即可添加楼层或设备";
            }

            if (!IsMemberDraftActive)
            {
                return "添加要关注的楼层或设备。";
            }

            if (IsEditingMember)
            {
                return $"正在编辑：{SelectedItem?.TargetLabel ?? "--"}。保存或取消后才能切换其他内容。";
            }

            if (SelectedTargetType is null)
            {
                return "请选择添加方式";
            }

            if (SelectedTargetType.Value == "floor")
            {
                return string.IsNullOrWhiteSpace(TargetFloor)
                    ? "请选择楼层"
                    : $"将添加：{TargetBuilding} / {TargetFloor} / 整层";
            }

            if (SelectedTargetType.Value is "name_contains" or "name_excludes")
            {
                return string.IsNullOrWhiteSpace(TargetNamePattern)
                    ? "请输入名称条件"
                    : $"将添加：{TargetBuilding} / {(SelectedTargetType.Value == "name_contains" ? "名称包含" : "名称不包含")} / {TargetNamePattern}";
            }

            if (SelectedTargetOption is null)
            {
                return SelectedTargetType.Value == "device"
                    ? "请选择设备"
                    : "请选择页面区域";
            }

            return SelectedTargetType.Value == "device"
                ? $"将添加：{SelectedTargetOption.Building} / {SelectedTargetOption.FloorLabel} / {SelectedTargetOption.SubAreaText} / {SelectedTargetOption.CardName}"
                : $"将添加：{SelectedTargetOption.Building} / {SelectedTargetOption.FloorLabel} / {SelectedTargetOption.SubAreaText}";
        }
    }

    public Visibility CustomEditorVisibility => IsEditingCustomGroup ? Visibility.Visible : Visibility.Collapsed;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                NotifyEditorState();
                NotifyCommands();
                OnPropertyChanged(nameof(CanDeleteSelectedGroup));
                OnPropertyChanged(nameof(CanOpenSelectedWatchIncident));
                OnPropertyChanged(nameof(LoadingStateVisibility));
                OnPropertyChanged(nameof(GroupListEmptyVisibility));
                OnPropertyChanged(nameof(ItemsEmptyVisibility));
            }
        }
    }

    public GroupSummaryRow? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                LoadSelectedGroupEdit(value);
                RefreshSelectedItems();
                ResetMemberDraft();
                NotifyEditorState();
                NotifyCommands();
            }
        }
    }

    public AreaGroupItemRow? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(MemberTargetPreview));
                OnPropertyChanged(nameof(MemberConflictMessage));
                OnPropertyChanged(nameof(MemberConflictVisibility));
                DeleteItemCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanDeleteSelectedItem));
            }
        }
    }

    public AreaGroupTargetOptionRow? SelectedTargetOption
    {
        get => _selectedTargetOption;
        set
        {
            if (SetProperty(ref _selectedTargetOption, value))
            {
                if (value is not null)
                {
                    _draftSubAreaText = value.SubAreaText;
                    _draftCardName = value.CardName;
                }
                else if (!_loadingMemberDraft)
                {
                    _draftSubAreaText = string.Empty;
                    _draftCardName = string.Empty;
                }

                OnPropertyChanged(nameof(MemberTargetPreview));
                OnPropertyChanged(nameof(MemberConflictMessage));
                OnPropertyChanged(nameof(MemberConflictVisibility));
                SaveMemberDraftCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public AreaGroupTargetTypeOption? SelectedTargetType
    {
        get => _selectedTargetType;
        set
        {
            if (SetProperty(ref _selectedTargetType, value))
            {
                ClearTargetOptions();
                ClearDraftTargetIfUserChanged();
                OnPropertyChanged(nameof(CanLoadMemberOptions));
                OnPropertyChanged(nameof(CandidatePickerVisibility));
                OnPropertyChanged(nameof(NamePatternVisibility));
                OnPropertyChanged(nameof(MemberTargetPreview));
                OnPropertyChanged(nameof(MemberConflictMessage));
                OnPropertyChanged(nameof(MemberConflictVisibility));
                LoadTargetOptionsCommand.NotifyCanExecuteChanged();
                SaveMemberDraftCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string EditName
    {
        get => _editName;
        set
        {
            if (SetProperty(ref _editName, value))
            {
                SaveGroupCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string EditAreaLabel
    {
        get => _editAreaLabel;
        set => SetProperty(ref _editAreaLabel, value);
    }

    public string EditDescription
    {
        get => _editDescription;
        set => SetProperty(ref _editDescription, value);
    }

    public string EditPriority
    {
        get => _editPriority;
        set => SetProperty(ref _editPriority, value);
    }

    public bool EditEnabled
    {
        get => _editEnabled;
        set => SetProperty(ref _editEnabled, value);
    }

    public string TargetBuilding
    {
        get => _targetBuilding;
        set
        {
            if (SetProperty(ref _targetBuilding, value))
            {
                ClearTargetOptions();
                ClearDraftTargetIfUserChanged();
                if (!string.Equals(FloorCatalogBuilding, value, StringComparison.OrdinalIgnoreCase))
                {
                    FloorCatalogBuilding = value;
                }

                OnPropertyChanged(nameof(MemberTargetPreview));
                OnPropertyChanged(nameof(MemberConflictMessage));
                OnPropertyChanged(nameof(MemberConflictVisibility));
                SaveMemberDraftCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string TargetFloor
    {
        get => _targetFloor;
        set
        {
            if (SetProperty(ref _targetFloor, value))
            {
                ClearTargetOptions();
                ClearDraftTargetIfUserChanged();
                OnPropertyChanged(nameof(MemberTargetPreview));
                OnPropertyChanged(nameof(MemberConflictMessage));
                OnPropertyChanged(nameof(MemberConflictVisibility));
                SaveMemberDraftCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string TargetNamePattern
    {
        get => _targetNamePattern;
        set
        {
            if (SetProperty(ref _targetNamePattern, value))
            {
                OnPropertyChanged(nameof(MemberTargetPreview));
                OnPropertyChanged(nameof(MemberConflictMessage));
                OnPropertyChanged(nameof(MemberConflictVisibility));
                SaveMemberDraftCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string TargetNote
    {
        get => _targetNote;
        set
        {
            if (SetProperty(ref _targetNote, value))
            {
                SaveMemberDraftCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string TargetOptionSearchText
    {
        get => _targetOptionSearchText;
        set
        {
            if (SetProperty(ref _targetOptionSearchText, value))
            {
                ApplyTargetOptionFilter();
            }
        }
    }

    public bool WatchEnabled
    {
        get => _watchEnabled;
        set => SetProperty(ref _watchEnabled, value);
    }

    public string WatchName
    {
        get => _watchName;
        set => SetProperty(ref _watchName, value);
    }

    public DateTimeOffset WatchStartDate
    {
        get => _watchStartDate;
        set
        {
            if (SetProperty(ref _watchStartDate, value))
            {
                NotifyWatchTimeState();
            }
        }
    }

    public TimeSpan WatchStartTime
    {
        get => _watchStartTime;
        set
        {
            if (SetProperty(ref _watchStartTime, value))
            {
                NotifyWatchTimeState();
            }
        }
    }

    public DateTimeOffset WatchEndDate
    {
        get => _watchEndDate;
        set
        {
            if (SetProperty(ref _watchEndDate, value))
            {
                NotifyWatchTimeState();
            }
        }
    }

    public TimeSpan WatchEndTime
    {
        get => _watchEndTime;
        set
        {
            if (SetProperty(ref _watchEndTime, value))
            {
                NotifyWatchTimeState();
            }
        }
    }

    public string WatchNote
    {
        get => _watchNote;
        set => SetProperty(ref _watchNote, value);
    }

    public string WatchSummaryText
    {
        get => _watchSummaryText;
        private set => SetProperty(ref _watchSummaryText, value);
    }

    public WatchIncidentRow? SelectedWatchIncident
    {
        get => _selectedWatchIncident;
        set
        {
            if (SetProperty(ref _selectedWatchIncident, value))
            {
                OnPropertyChanged(nameof(CanOpenSelectedWatchIncident));
            }
        }
    }

    public FloorCatalogRow? SelectedFloorCatalog
    {
        get => _selectedFloorCatalog;
        set
        {
            if (SetProperty(ref _selectedFloorCatalog, value))
            {
                DeleteFloorCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanDeleteSelectedFloor));
            }
        }
    }

    public string FloorCatalogBuilding
    {
        get => _floorCatalogBuilding;
        set
        {
            if (SetProperty(ref _floorCatalogBuilding, value))
            {
                TargetBuilding = value;
                if (!_loadingMemberDraft)
                {
                    _ = LoadFloorCatalogAsync();
                }
            }
        }
    }

    public string FloorCatalogLabel
    {
        get => _floorCatalogLabel;
        set
        {
            if (SetProperty(ref _floorCatalogLabel, value))
            {
                SaveFloorCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string FloorCatalogNote
    {
        get => _floorCatalogNote;
        set => SetProperty(ref _floorCatalogNote, value);
    }

    private IReadOnlyList<AreaGroupRecord> GroupRecords { get; set; } = [];

    private IReadOnlyList<AreaGroupItemRecord> ItemRecords { get; set; } = [];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusText = "正在计算区域组设备";
        try
        {
            var groupTask = areaGroupRepository.LoadAsync(cancellationToken);
            await groupTask.ConfigureAwait(true);
            var groupSet = groupTask.Result;
            GroupRecords = groupSet.Groups;
            ItemRecords = groupSet.Items;
            await LoadFloorCatalogAsync(cancellationToken).ConfigureAwait(true);
            SelectedTargetType ??= TargetTypes.FirstOrDefault();

            Groups.Clear();
            foreach (var group in groupSet.Groups.Where(group =>
                         group.Enabled &&
                         (group.GroupKind.Equals("custom", StringComparison.OrdinalIgnoreCase) ||
                          group.GroupKind.Equals("system", StringComparison.OrdinalIgnoreCase))))
            {
                Groups.Add(new GroupSummaryRow(group));
            }

            SelectedGroup = Groups.FirstOrDefault();
            StatusText = $"已读取 {Groups.Count:N0} 个区域组";
        }
        catch (Exception ex)
        {
            Groups.Clear();
            Items.Clear();
            TargetOptions.Clear();
            SelectedGroup = null;
            StatusText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void SelectGroup(long groupId)
    {
        SelectedGroup = Groups.FirstOrDefault(group => group.Id == groupId || group.GroupId == groupId)
            ?? SelectedGroup;
    }

    [RelayCommand]
    private async Task Refresh()
    {
        await LoadAsync().ConfigureAwait(true);
    }

    private bool CanSaveGroup() => !IsLoading && IsEditingCustomGroup && !string.IsNullOrWhiteSpace(EditName);

    [RelayCommand(CanExecute = nameof(CanSaveGroup))]
    private async Task SaveGroup()
    {
        IsLoading = true;
        try
        {
            var id = SelectedGroup is { IsCustom: true } ? SelectedGroup.Id : null as long?;
            var saved = await areaGroupRepository.SaveGroupAsync(new AreaGroupEdit(
                id,
                EditName,
                EditAreaLabel,
                EditDescription,
                EditPriority,
                EditEnabled)).ConfigureAwait(true);
            StatusText = $"已保存区域组：{saved.Name}";
            await LoadAsync().ConfigureAwait(true);
            SelectedGroup = Groups.FirstOrDefault(group => group.GroupId == saved.Id || group.Id == saved.Id) ?? SelectedGroup;
        }
        catch (Exception ex)
        {
            StatusText = "保存区域组失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void NewGroup()
    {
        SelectedGroup = null;
        EditName = "新区域组";
        EditAreaLabel = string.Empty;
        EditDescription = string.Empty;
        EditPriority = "重点";
        EditEnabled = true;
        Items.Clear();
        SelectedItem = null;
        ClearTargetOptions();
        StatusText = "正在新建自定义区域组";
        NotifyEditorState();
        NotifyCommands();
    }

    private bool CanDeleteGroup() => !IsLoading && SelectedGroup is { IsCustom: true, IsLocked: false };

    [RelayCommand(CanExecute = nameof(CanDeleteGroup))]
    public async Task DeleteGroupAsync()
    {
        if (SelectedGroup is null)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var id = SelectedGroup.Id;
            await areaGroupRepository.DeleteGroupAsync(id).ConfigureAwait(true);
            StatusText = $"已删除区域组：#{id}";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = "删除区域组失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanLoadTargetOptions() => CanLoadMemberOptions;

    private bool CanSaveFloor() => !IsLoading && !string.IsNullOrWhiteSpace(FloorCatalogBuilding) && !string.IsNullOrWhiteSpace(FloorCatalogLabel);

    [RelayCommand(CanExecute = nameof(CanSaveFloor))]
    private async Task SaveFloor()
    {
        IsLoading = true;
        try
        {
            var saved = await areaGroupRepository.SaveFloorAsync(new FloorCatalogEdit(
                Id: null,
                Building: FloorCatalogBuilding,
                FloorLabel: FloorCatalogLabel,
                Enabled: true,
                Note: FloorCatalogNote)).ConfigureAwait(true);
            StatusText = $"已保存楼层目录：{saved.Building} / {saved.FloorLabel}";
            FloorCatalogLabel = string.Empty;
            FloorCatalogNote = string.Empty;
            await LoadFloorCatalogAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = "保存楼层目录失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanDeleteFloor() => !IsLoading && SelectedFloorCatalog is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteFloor))]
    public async Task DeleteFloorAsync()
    {
        if (SelectedFloorCatalog is null)
        {
            return;
        }

        IsLoading = true;
        try
        {
            await areaGroupRepository.DeleteFloorAsync(SelectedFloorCatalog.Id).ConfigureAwait(true);
            StatusText = $"已停用楼层目录：{SelectedFloorCatalog.DisplayLabel}";
            await LoadFloorCatalogAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = "删除楼层目录失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadTargetOptions))]
    private async Task LoadTargetOptions()
    {
        await RefreshTargetOptionsAsync().ConfigureAwait(true);
    }

    public async Task RefreshTargetOptionsAsync(CancellationToken cancellationToken = default)
    {
        if (!CanLoadMemberOptions || SelectedTargetType is null)
        {
            ClearTargetOptions();
            return;
        }

        var loadVersion = ++_targetOptionsLoadVersion;
        var targetType = SelectedTargetType.Value;
        var building = TargetBuilding;
        var floor = TargetFloor;
        try
        {
            var options = await areaGroupRepository.LoadTargetOptionsAsync(
                building,
                floor,
                cancellationToken).ConfigureAwait(true);
            if (loadVersion != _targetOptionsLoadVersion ||
                !string.Equals(SelectedTargetType?.Value, targetType, StringComparison.Ordinal) ||
                !string.Equals(TargetBuilding, building, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(TargetFloor, floor, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            LoadedTargetOptions.Clear();
            foreach (var option in options.Where(option => option.Type == targetType))
            {
                LoadedTargetOptions.Add(new AreaGroupTargetOptionRow(option));
            }

            ApplyTargetOptionFilter();
            SelectedTargetOption = null;
            StatusText = $"已找到 {LoadedTargetOptions.Count:N0} 个可选项";
        }
        catch (Exception ex)
        {
            if (loadVersion != _targetOptionsLoadVersion)
            {
                return;
            }

            ClearTargetOptions(clearSearch: false);
            StatusText = "读取可选设备失败：" + ex.Message;
        }
    }

    private bool CanSaveWatch() => CanMaintainWatch && IsWatchWindowValid;

    [RelayCommand(CanExecute = nameof(CanSaveWatch))]
    private async Task SaveWatch()
    {
        var group = SelectedGroup;
        if (group?.GroupId is null)
        {
            StatusText = "请先选择自定义区域组";
            return;
        }

        var groupId = group.GroupId.Value;
        var watchRuleId = _watchRuleId;
        IsLoading = true;
        try
        {
            var startAt = CombineDateAndTime(WatchStartDate, WatchStartTime);
            var endAt = CombineDateAndTime(WatchEndDate, WatchEndTime);
            var saved = await watchRepository.SaveRuleAsync(new DeviceWatchEdit(
                Id: watchRuleId,
                GroupId: groupId,
                Name: WatchName,
                StartAt: startAt,
                EndAt: endAt,
                Enabled: WatchEnabled,
                Note: WatchNote)).ConfigureAwait(true);
            if (!IsCurrentWatchGroup(groupId))
            {
                return;
            }

            _watchRuleId = saved.Id;
            StatusText = "关注规则已保存";
            await LoadWatchAsync(group, ++_watchLoadVersion).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = "保存关注规则失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanDeleteWatch() => CanMaintainWatch && _watchRuleId is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteWatch))]
    public async Task DeleteWatchAsync()
    {
        if (_watchRuleId is null || SelectedGroup?.GroupId is null)
        {
            return;
        }

        var groupId = SelectedGroup.GroupId.Value;
        var watchRuleId = _watchRuleId.Value;
        IsLoading = true;
        try
        {
            await watchRepository.DeleteRuleAsync(watchRuleId, groupId).ConfigureAwait(true);
            if (!IsCurrentWatchGroup(groupId))
            {
                return;
            }

            ResetWatchEdit();
            StatusText = "关注规则已删除";
            OnPropertyChanged(nameof(CanDeleteSelectedWatch));
        }
        catch (Exception ex)
        {
            StatusText = "删除关注规则失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanAddItem()
    {
        if (IsLoading || SelectedGroup is not { IsCustom: true } || SelectedTargetType is null || !IsMemberDraftActive)
        {
            return false;
        }

        return SelectedTargetType.Value switch
        {
            "floor" => !string.IsNullOrWhiteSpace(TargetBuilding) && !string.IsNullOrWhiteSpace(TargetFloor),
            "sub_area" => SelectedTargetOption is not null,
            "device" => SelectedTargetOption is not null,
            "name_contains" or "name_excludes" => !string.IsNullOrWhiteSpace(TargetBuilding) && !string.IsNullOrWhiteSpace(TargetNamePattern),
            _ => false,
        };
    }

    private bool CanBeginAddMemberCore() => CanBeginAddMember;

    [RelayCommand(CanExecute = nameof(CanBeginAddMemberCore))]
    private void BeginAddMember()
    {
        _memberEditorMode = MemberEditorMode.Adding;
        _editingItemId = null;
        _draftSubAreaText = string.Empty;
        _draftCardName = string.Empty;
        TargetNamePattern = string.Empty;
        ClearTargetOptions();
        TargetNote = string.Empty;
        StatusText = "正在添加楼层或设备";
        NotifyEditorState();
        NotifyCommands();
    }

    public async Task BeginEditItemAsync(AreaGroupItemRow? item)
    {
        if (item is null || !CanMaintainMembers)
        {
            return;
        }

        _loadingMemberDraft = true;
        try
        {
            SelectedItem = item;
            _memberEditorMode = MemberEditorMode.Editing;
            _editingItemId = item.Id;
            SelectedTargetType = TargetTypes.FirstOrDefault(type => type.Value == item.TargetType) ?? TargetTypes.FirstOrDefault();
            TargetBuilding = item.Building;
            await LoadFloorCatalogAsync(item.FloorLabel).ConfigureAwait(true);
            TargetFloor = item.FloorLabel;
            _draftSubAreaText = item.SubAreaText;
            _draftCardName = item.CardName;
            TargetNamePattern = item.TargetType is "name_contains" or "name_excludes" ? item.CardName : string.Empty;
            TargetNote = item.RawNote;
            ClearTargetOptions();
            StatusText = $"正在编辑：{item.TargetLabel}";
        }
        finally
        {
            _loadingMemberDraft = false;
            NotifyEditorState();
            NotifyCommands();
        }

        if (SelectedTargetType?.Value is "sub_area" or "device")
        {
            await LoadTargetOptions().ConfigureAwait(true);
            SelectedTargetOption = TargetOptions.FirstOrDefault(option =>
                string.Equals(option.SubAreaText, item.SubAreaText, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(option.CardName, item.CardName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private bool CanSaveMemberDraftCore() => CanSaveMemberDraft;

    [RelayCommand(CanExecute = nameof(CanSaveMemberDraftCore))]
    private async Task SaveMemberDraft()
    {
        if (SelectedGroup is null || SelectedTargetType is null || !CanAddItem())
        {
            return;
        }

        var option = SelectedTargetOption;
        IsLoading = true;
        try
        {
            if (SelectedTargetType.Value is "sub_area" or "device" && option is null)
            {
                StatusText = "请选择要添加的设备或区域";
                return;
            }

            var groupId = SelectedGroup.Id;
            var saved = await areaGroupRepository.SaveItemAsync(new AreaGroupItemEdit(
                groupId,
                SelectedTargetType.Value,
                option?.Building ?? TargetBuilding,
                option?.FloorLabel ?? TargetFloor,
                SelectedTargetType.Value == "floor" ? string.Empty : option?.SubAreaText ?? string.Empty,
                SelectedTargetType.Value == "device" ? option?.CardName ?? string.Empty :
                    SelectedTargetType.Value is "name_contains" or "name_excludes" ? TargetNamePattern : string.Empty,
                TargetNote,
                _editingItemId)).ConfigureAwait(true);
            StatusText = _editingItemId is null ? "已加入区域组" : "已保存";
            var savedItemId = saved.Id;
            ResetMemberDraft();
            await LoadAsync().ConfigureAwait(true);
            SelectedGroup = Groups.FirstOrDefault(group => group.GroupId == groupId || group.Id == groupId) ?? SelectedGroup;
            SelectedItem = Items.FirstOrDefault(item => item.Id == savedItemId) ?? SelectedItem;
        }
        catch (Exception ex)
        {
            StatusText = "保存失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanCancelMemberDraftCore() => CanCancelMemberDraft;

    [RelayCommand(CanExecute = nameof(CanCancelMemberDraftCore))]
    private void CancelMemberDraft()
    {
        ResetMemberDraft();
        StatusText = "已取消编辑";
        NotifyEditorState();
        NotifyCommands();
    }

    private bool CanDeleteItem() => !IsLoading && SelectedItem is not null && SelectedGroup is { IsCustom: true };

    [RelayCommand(CanExecute = nameof(CanDeleteItem))]
    public async Task DeleteItemAsync()
    {
        if (!CanDeleteItem() || SelectedItem is null)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var deletingId = SelectedItem.Id;
            await areaGroupRepository.DeleteItemAsync(SelectedItem.Id).ConfigureAwait(true);
            StatusText = "已从区域组移除";
            if (_editingItemId == deletingId)
            {
                ResetMemberDraft();
            }

            await LoadAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(CanDeleteSelectedItem));
        }
        catch (Exception ex)
        {
            StatusText = "移除失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task DeleteItemAsync(AreaGroupItemRow? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedItem = item;
        await DeleteItemAsync().ConfigureAwait(true);
    }

    public void OpenSelectedInData()
    {
        if (SelectedGroup is null || !SelectedGroup.CanOpenInData)
        {
            StatusText = "当前区域组还不能打开设备数据";
            return;
        }

        navigationService.NavigateToData(new DataNavigationRequest(
            SearchText: string.Empty,
            Building: string.Empty,
            CommunicationState: SelectedGroup.CommunicationFilter,
            AreaType: SelectedGroup.AreaFilter,
            AreaGroupId: SelectedGroup.GroupId));
    }

    private void LoadSelectedGroupEdit(GroupSummaryRow? group)
    {
        EditName = group?.Name ?? string.Empty;
        EditAreaLabel = group?.AreaLabel ?? string.Empty;
        EditDescription = group?.Description ?? string.Empty;
        EditPriority = string.IsNullOrWhiteSpace(group?.Priority) ? "重点" : group.Priority;
        EditEnabled = group?.IsEnabled ?? true;
        ClearTargetOptions();
    }

    private async Task LoadWatchAsync(GroupSummaryRow? group, long watchLoadVersion)
    {
        if (group?.GroupId is null || !group.IsCustom)
        {
            return;
        }

        var groupId = group.GroupId.Value;
        try
        {
            var rule = await watchRepository.LoadRuleForGroupAsync(groupId).ConfigureAwait(true);
            if (!IsCurrentWatchGroup(groupId, watchLoadVersion))
            {
                return;
            }

            if (rule is null)
            {
                WatchSummaryText = "尚未设置关注窗口";
                ReplaceWatchIncidents([]);
                return;
            }

            _watchRuleId = rule.Id;
            WatchName = rule.Name;
            WatchEnabled = rule.Enabled;
            WatchStartDate = rule.StartAt.ToLocalTime();
            WatchStartTime = rule.StartAt.ToLocalTime().TimeOfDay;
            WatchEndDate = rule.EndAt.ToLocalTime();
            WatchEndTime = rule.EndAt.ToLocalTime().TimeOfDay;
            WatchNote = rule.Note;
            var evaluation = await watchRepository.EvaluateAsync(new DeviceWatchQuery(groupId, IncludeDisabled: true)).ConfigureAwait(true);
            if (!IsCurrentWatchGroup(groupId, watchLoadVersion))
            {
                return;
            }

            var current = evaluation.Rules.FirstOrDefault(item => item.Id == rule.Id) ?? rule;
            WatchSummaryText = $"关注 {current.WatchedDevices:N0} 台，异常 {current.AbnormalDevices:N0} 台";
            ReplaceWatchIncidents(evaluation.Incidents
                .Where(incident => incident.RuleId == rule.Id)
                .OrderByDescending(incident => incident.CurrentAt)
                .ThenBy(incident => incident.Device.Name, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            if (IsCurrentWatchGroup(groupId, watchLoadVersion))
            {
                ReplaceWatchIncidents([]);
                WatchSummaryText = "关注规则读取失败：" + ex.Message;
            }
        }
        finally
        {
            if (IsCurrentWatchGroup(groupId, watchLoadVersion))
            {
                NotifyCommands();
                NotifyEditorState();
            }
        }
    }

    private void ResetWatchEdit()
    {
        _watchRuleId = null;
        WatchEnabled = false;
        WatchName = "关注设备";
        WatchStartDate = DateTimeOffset.Now.Date;
        WatchStartTime = new TimeSpan(18, 0, 0);
        WatchEndDate = DateTimeOffset.Now.Date.AddDays(1);
        WatchEndTime = new TimeSpan(8, 0, 0);
        WatchNote = string.Empty;
        WatchSummaryText = "尚未设置关注窗口";
        ReplaceWatchIncidents([]);
        NotifyCommands();
    }

    public void OpenSelectedWatchIncident()
    {
        if (SelectedWatchIncident is null)
        {
            StatusText = "请选择一个关注事件";
            return;
        }

        var incident = SelectedWatchIncident.Source;
        navigationService.NavigateToData(new DataNavigationRequest(
            SearchText: incident.Device.Name,
            Building: incident.Device.Building,
            Floor: incident.Device.FloorLabel,
            SubArea: incident.Device.SubArea,
            PageName: incident.Device.PageName,
            CommunicationState: string.Empty,
            AreaType: string.Empty));
    }

    private bool IsCurrentWatchGroup(long groupId)
    {
        return SelectedGroup?.GroupId == groupId;
    }

    private bool IsCurrentWatchGroup(long groupId, long watchLoadVersion)
    {
        return _watchLoadVersion == watchLoadVersion && IsCurrentWatchGroup(groupId);
    }

    private void ReplaceWatchIncidents(IEnumerable<DeviceWatchIncident> incidents)
    {
        WatchIncidents.Clear();
        foreach (var incident in incidents)
        {
            WatchIncidents.Add(new WatchIncidentRow(incident));
        }

        SelectedWatchIncident = WatchIncidents.FirstOrDefault();
        OnPropertyChanged(nameof(WatchIncidentSummary));
        OnPropertyChanged(nameof(WatchIncidentEmptyVisibility));
        OnPropertyChanged(nameof(CanOpenSelectedWatchIncident));
    }

    private void RefreshSelectedItems()
    {
        Items.Clear();
        if (SelectedGroup?.GroupId is null)
        {
            OnPropertyChanged(nameof(MemberEditorMessage));
            OnPropertyChanged(nameof(ItemsEmptyVisibility));
            return;
        }

        foreach (var item in ItemRecords.Where(item => item.GroupId == SelectedGroup.GroupId.Value))
        {
            Items.Add(new AreaGroupItemRow(item));
        }

        SelectedItem = Items.FirstOrDefault();
        OnPropertyChanged(nameof(MemberEditorMessage));
        OnPropertyChanged(nameof(ItemsEmptyVisibility));
    }

    private async Task LoadFloorCatalogAsync(CancellationToken cancellationToken = default)
    {
        await LoadFloorCatalogAsync(preferredFloorLabel: null, cancellationToken).ConfigureAwait(true);
    }

    private async Task LoadFloorCatalogAsync(string? preferredFloorLabel, CancellationToken cancellationToken = default)
    {
        var selectedFloor = string.IsNullOrWhiteSpace(preferredFloorLabel) ? TargetFloor : preferredFloorLabel;
        var rows = await areaGroupRepository.LoadFloorsAsync(FloorCatalogBuilding, includeDisabled: false, cancellationToken).ConfigureAwait(true);
        FloorCatalog.Clear();
        FloorOptions.Clear();
        foreach (var row in rows)
        {
            FloorCatalog.Add(new FloorCatalogRow(row));
            FloorOptions.Add(new DataFilterOption(row.FloorLabel, row.FloorLabel, -1));
        }

        SelectedFloorCatalog = FloorCatalog.FirstOrDefault();
        TargetFloor = FloorOptions.Any(option => option.Value == selectedFloor)
            ? selectedFloor
            : FloorOptions.FirstOrDefault()?.Value ?? string.Empty;
        SelectedTargetType ??= TargetTypes.FirstOrDefault();
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        SaveGroupCommand.NotifyCanExecuteChanged();
        DeleteGroupCommand.NotifyCanExecuteChanged();
        SaveFloorCommand.NotifyCanExecuteChanged();
        DeleteFloorCommand.NotifyCanExecuteChanged();
        LoadTargetOptionsCommand.NotifyCanExecuteChanged();
        BeginAddMemberCommand.NotifyCanExecuteChanged();
        SaveMemberDraftCommand.NotifyCanExecuteChanged();
        CancelMemberDraftCommand.NotifyCanExecuteChanged();
        DeleteItemCommand.NotifyCanExecuteChanged();
        SaveWatchCommand.NotifyCanExecuteChanged();
        DeleteWatchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanDeleteSelectedGroup));
        OnPropertyChanged(nameof(CanDeleteSelectedItem));
        OnPropertyChanged(nameof(CanDeleteSelectedWatch));
        OnPropertyChanged(nameof(CanDeleteSelectedFloor));
        OnPropertyChanged(nameof(CanOpenSelectedWatchIncident));
        OnPropertyChanged(nameof(CanBeginAddMember));
        OnPropertyChanged(nameof(CanSaveMemberDraft));
        OnPropertyChanged(nameof(CanCancelMemberDraft));
        OnPropertyChanged(nameof(CanOperateMemberRows));
        OnPropertyChanged(nameof(CanSelectMemberOption));
        OnPropertyChanged(nameof(CanSearchMemberOptions));
        OnPropertyChanged(nameof(MemberConflictMessage));
        OnPropertyChanged(nameof(MemberConflictVisibility));
    }

    private void NotifyEditorState()
    {
        OnPropertyChanged(nameof(IsEditingCustomGroup));
        OnPropertyChanged(nameof(IsSystemGroupSelected));
        OnPropertyChanged(nameof(CanMaintainMembers));
        OnPropertyChanged(nameof(CanLoadMemberOptions));
        OnPropertyChanged(nameof(CandidatePickerVisibility));
        OnPropertyChanged(nameof(NamePatternVisibility));
        OnPropertyChanged(nameof(MemberDraftVisibility));
        OnPropertyChanged(nameof(IsMemberDraftActive));
        OnPropertyChanged(nameof(IsEditingMember));
        OnPropertyChanged(nameof(CanEditMemberTarget));
        OnPropertyChanged(nameof(CanBeginAddMember));
        OnPropertyChanged(nameof(CanSaveMemberDraft));
        OnPropertyChanged(nameof(CanCancelMemberDraft));
        OnPropertyChanged(nameof(CanOperateMemberRows));
        OnPropertyChanged(nameof(CanSelectMemberOption));
        OnPropertyChanged(nameof(CanSearchMemberOptions));
        OnPropertyChanged(nameof(MemberDraftTitle));
        OnPropertyChanged(nameof(MemberSaveButtonText));
        OnPropertyChanged(nameof(CanOpenSelectedInData));
        OnPropertyChanged(nameof(CanMaintainWatch));
        OnPropertyChanged(nameof(WatchEditorTitle));
        OnPropertyChanged(nameof(WatchEditorMessage));
        OnPropertyChanged(nameof(WatchTimeValidationMessage));
        OnPropertyChanged(nameof(WatchTimeValidationVisibility));
        OnPropertyChanged(nameof(GroupEditorTitle));
        OnPropertyChanged(nameof(GroupEditorMessage));
        OnPropertyChanged(nameof(MemberEditorTitle));
        OnPropertyChanged(nameof(MemberEditorMessage));
        OnPropertyChanged(nameof(ItemsEmptyVisibility));
        OnPropertyChanged(nameof(MemberTargetPreview));
        OnPropertyChanged(nameof(MemberConflictMessage));
        OnPropertyChanged(nameof(MemberConflictVisibility));
        OnPropertyChanged(nameof(CustomEditorVisibility));
    }

    private void ResetMemberDraft()
    {
        _memberEditorMode = MemberEditorMode.None;
        _editingItemId = null;
        _draftSubAreaText = string.Empty;
        _draftCardName = string.Empty;
        TargetNamePattern = string.Empty;
        ClearTargetOptions();
        TargetNote = string.Empty;
        NotifyEditorState();
        NotifyCommands();
    }

    private void NotifyWatchTimeState()
    {
        OnPropertyChanged(nameof(WatchTimeValidationMessage));
        OnPropertyChanged(nameof(WatchTimeValidationVisibility));
        SaveWatchCommand.NotifyCanExecuteChanged();
    }

    private void ClearDraftTargetIfUserChanged()
    {
        if (_loadingMemberDraft)
        {
            return;
        }

        _draftSubAreaText = string.Empty;
        _draftCardName = string.Empty;
        TargetNamePattern = string.Empty;
    }

    private static DateTimeOffset CombineDateAndTime(DateTimeOffset date, TimeSpan time)
    {
        var local = date.Date.Add(time);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private void ClearTargetOptions(bool clearSearch = true)
    {
        LoadedTargetOptions.Clear();
        if (TargetOptions.Count > 0)
        {
            TargetOptions.Clear();
        }

        SelectedTargetOption = null;
        if (clearSearch && !string.IsNullOrEmpty(TargetOptionSearchText))
        {
            TargetOptionSearchText = string.Empty;
        }

        OnPropertyChanged(nameof(CanSearchMemberOptions));
    }

    private void ApplyTargetOptionFilter()
    {
        var selected = SelectedTargetOption;
        var keyword = (TargetOptionSearchText ?? string.Empty).Trim();
        var rows = string.IsNullOrWhiteSpace(keyword)
            ? LoadedTargetOptions
            : LoadedTargetOptions.Where(option => MatchesTargetOption(option, keyword)).ToList();
        TargetOptions.Clear();
        foreach (var row in rows)
        {
            TargetOptions.Add(row);
        }

        SelectedTargetOption = selected is not null && TargetOptions.Any(option => ReferenceEquals(option, selected))
            ? selected
            : null;
        OnPropertyChanged(nameof(CanSearchMemberOptions));
        OnPropertyChanged(nameof(MemberTargetPreview));
    }

    private static bool MatchesTargetOption(AreaGroupTargetOptionRow option, string keyword)
    {
        return Contains(option.Label, keyword) ||
               Contains(option.Building, keyword) ||
               Contains(option.FloorLabel, keyword) ||
               Contains(option.SubAreaText, keyword) ||
               Contains(option.CardName, keyword);
    }

    private static bool Contains(string value, string keyword)
    {
        return (value ?? string.Empty).Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private string BuildMemberConflictMessage()
    {
        var draft = CurrentMemberDraft();
        if (draft is null || !IsMemberDraftActive)
        {
            return string.Empty;
        }

        var existing = Items.Where(item => item.Id != _editingItemId).ToArray();
        var exact = existing.FirstOrDefault(item => SameTarget(item, draft.Value));
        if (exact is not null)
        {
            return _editingItemId is null
                ? $"已经添加过：{exact.TargetLabel}。保存会更新备注，不会新增重复项。"
                : $"与已添加内容重复：{exact.TargetLabel}。保存会合并，并移除当前编辑项。";
        }

        if (draft.Value.TargetType is "sub_area" or "device")
        {
            var floor = existing.FirstOrDefault(item =>
                item.TargetType == "floor" &&
                SameBuilding(item, draft.Value) &&
                SameFloor(item.FloorLabel, draft.Value.FloorLabel));
            if (floor is not null)
            {
                return $"已添加整个楼层：{floor.TargetLabel}。仍可单独保存，但设备数量不会增加。";
            }
        }

        if (draft.Value.TargetType == "device")
        {
            var subArea = existing.FirstOrDefault(item =>
                item.TargetType == "sub_area" &&
                SameBuilding(item, draft.Value) &&
                SameFloor(item.FloorLabel, draft.Value.FloorLabel) &&
                SameText(item.SubAreaText, draft.Value.SubAreaText));
            if (subArea is not null)
            {
                return $"该设备已包含在页面区域中：{subArea.TargetLabel}。仍可单独保存，但设备数量不会增加。";
            }
        }

        if (draft.Value.TargetType == "floor")
        {
            var covered = existing.Count(item =>
                item.TargetType is "sub_area" or "device" &&
                SameBuilding(item, draft.Value) &&
                SameFloor(item.FloorLabel, draft.Value.FloorLabel));
            if (covered > 0)
            {
                return $"添加整个楼层后，已有 {covered:N0} 项仍会保留，但不会重复计算设备。";
            }
        }

        if (draft.Value.TargetType == "sub_area")
        {
            var covered = existing.Count(item =>
                item.TargetType == "device" &&
                SameBuilding(item, draft.Value) &&
                SameFloor(item.FloorLabel, draft.Value.FloorLabel) &&
                SameText(item.SubAreaText, draft.Value.SubAreaText));
            if (covered > 0)
            {
                return $"添加该页面区域后，已有 {covered:N0} 台设备仍会保留，但不会重复计算。";
            }
        }

        return string.Empty;
    }

    private MemberDraftTarget? CurrentMemberDraft()
    {
        if (SelectedTargetType is null)
        {
            return null;
        }

        var option = SelectedTargetOption;
        var targetType = SelectedTargetType.Value;
        if (targetType is "sub_area" or "device" && option is null)
        {
            return null;
        }

        var building = option?.Building ?? TargetBuilding;
        var floorLabel = option?.FloorLabel ?? TargetFloor;
        var subAreaText = targetType == "floor" ? string.Empty : option?.SubAreaText ?? _draftSubAreaText;
        var cardName = targetType == "device" ? option?.CardName ?? _draftCardName : string.Empty;
        if (string.IsNullOrWhiteSpace(building) || string.IsNullOrWhiteSpace(floorLabel))
        {
            return null;
        }

        return targetType switch
        {
            "floor" => new MemberDraftTarget(targetType, building, floorLabel, string.Empty, string.Empty),
            "sub_area" when !string.IsNullOrWhiteSpace(subAreaText) => new MemberDraftTarget(targetType, building, floorLabel, subAreaText, string.Empty),
            "device" when !string.IsNullOrWhiteSpace(cardName) => new MemberDraftTarget(targetType, building, floorLabel, subAreaText, cardName),
            _ => null,
        };
    }

    private static bool SameTarget(AreaGroupItemRow item, MemberDraftTarget draft)
    {
        return item.TargetType == draft.TargetType &&
               SameBuilding(item, draft) &&
               SameFloor(item.FloorLabel, draft.FloorLabel) &&
               SameText(item.SubAreaText, draft.SubAreaText) &&
               SameText(item.CardName, draft.CardName);
    }

    private static bool SameBuilding(AreaGroupItemRow item, MemberDraftTarget draft)
    {
        return SameText(item.Building, draft.Building);
    }

    private static bool SameFloor(string left, string right)
    {
        return SameText(left, right);
    }

    private static bool SameText(string left, string right)
    {
        return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private enum MemberEditorMode
    {
        None,
        Adding,
        Editing,
    }

    private readonly record struct MemberDraftTarget(
        string TargetType,
        string Building,
        string FloorLabel,
        string SubAreaText,
        string CardName);
}
