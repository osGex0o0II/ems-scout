using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Application.Logging;
using EmsScout.Application.Watch;
using EmsScout.Desktop.Services;
using EmsScout.Infrastructure.Logging;
using Microsoft.UI.Xaml;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class GroupsViewModel(
    IDeviceReadRepository repository,
    IAreaGroupRepository areaGroupRepository,
    IDeviceWatchRepository watchRepository,
    INavigationService navigationService,
    IApplicationLogger applicationLogger) : ObservableObject
{
    private string _statusText = "正在读取当前分组规则";
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
    private string _targetNote = string.Empty;
    private string _targetOptionSearchText = string.Empty;
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

    public ObservableCollection<GroupRuleRow> Rules { get; } =
    [
        new("layout = group", "采集页布局标记为 group 的设备归入公区。", "命中公区"),
        new("公区命名关键词", "名称包含 GQ、WSJ、DTT、FDT、XFDT、CSJ、FWJ、ZBS、ZSG、MD、RDJHJF 时归入公区。", "命中公区"),
        new("QL-NNN 房间号保护", "名称符合 QL-数字 的裙楼具体房间不按公区处理。", "命中非公区"),
        new("自定义区域组", "按楼栋、楼层、子区或设备维护固定成员；用于分组统计和关注规则，不伪装成数据管理基础筛选。", "命中自定义组"),
    ];

    public ObservableCollection<AreaGroupItemRow> Items { get; } = [];

    public ObservableCollection<AreaGroupTargetOptionRow> TargetOptions { get; } = [];

    private List<AreaGroupTargetOptionRow> LoadedTargetOptions { get; } = [];

    public ObservableCollection<DataFilterOption> FloorOptions { get; } = [];

    public ObservableCollection<FloorCatalogRow> FloorCatalog { get; } = [];

    public ObservableCollection<WatchIncidentRow> WatchIncidents { get; } = [];

    public ObservableCollection<AreaGroupTargetTypeOption> TargetTypes { get; } =
    [
        new("floor", "楼层"),
        new("sub_area", "子区"),
        new("device", "设备"),
    ];

    public ObservableCollection<string> BuildingOptions { get; } = ["1号", "2号", "3号", "4号", "5号", "6号"];

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsEditingCustomGroup => SelectedGroup is { IsCustom: true } || SelectedGroup is null;

    public bool IsSystemGroupSelected => SelectedGroup is { IsCustom: false };

    public bool CanMaintainMembers => SelectedGroup?.GroupId is not null && !IsLoading;

    public bool CanLoadMemberOptions => CanMaintainMembers && CanEditMemberTarget && SelectedTargetType?.Value is "sub_area" or "device";

    public bool IsMemberDraftActive => _memberEditorMode is MemberEditorMode.Adding or MemberEditorMode.Editing;

    public bool IsEditingMember => _memberEditorMode == MemberEditorMode.Editing;

    public bool CanEditMemberTarget => CanMaintainMembers && IsMemberDraftActive;

    public bool CanOperateMemberRows => CanMaintainMembers && !IsMemberDraftActive;

    public bool CanSelectMemberOption => CanLoadMemberOptions;

    public bool CanSearchMemberOptions => CanLoadMemberOptions && LoadedTargetOptions.Count > 0;

    public bool CanBeginAddMember => CanMaintainMembers && !IsMemberDraftActive;

    public bool CanSaveMemberDraft => CanAddItem();

    public bool CanCancelMemberDraft => CanMaintainMembers && IsMemberDraftActive;

    public string MemberDraftTitle => _memberEditorMode switch
    {
        MemberEditorMode.Adding => "新增成员",
        MemberEditorMode.Editing => "编辑成员",
        _ => "成员维护",
    };

    public string MemberSaveButtonText => _memberEditorMode == MemberEditorMode.Editing ? "保存成员" : "添加成员";

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

    public string WatchEditorTitle => CanMaintainWatch ? "关注设备" : "关注设备（选择自定义区域组后可用）";

    public string WatchEditorMessage => SelectedGroup is null
        ? "保存自定义分组后，可设置关注时间窗。"
        : IsSystemGroupSelected
            ? "系统区域不直接维护关注规则，请新建自定义区域组。"
            : "关注窗口内成员设备发生开机/关机变化时，会在数据管理标记为异常。";

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

    public string GroupEditorTitle => SelectedGroup is null ? "新建自定义区域组" : IsEditingCustomGroup ? "编辑自定义区域组" : "系统区域详情";

    public string GroupEditorMessage => SelectedGroup is null
        ? "填写名称并保存后，可继续添加楼层、子区或设备成员。"
        : IsEditingCustomGroup
            ? "修改名称、用途、级别和启用状态后保存；自定义成员用于分组统计和关注规则。数据管理仅保留基础筛选，不按自定义成员跳转。"
            : "公区/非公区的基础分类由规则计算；这里维护人工成员，日期和时段请到独立的日期管理中设置。";

    public string MemberEditorTitle => CanMaintainMembers ? "成员维护" : "成员维护（选择自定义区域组后可用）";

    public string MemberEditorMessage => SelectedGroup is null
        ? "新建分组保存后即可添加楼层、子区或设备成员。"
        : "楼层可直接添加；子区和设备先按楼栋、楼层加载候选。系统分类成员是人工维护范围，不会改写基础公区判定。";

    public string MemberTargetPreview
    {
        get
        {
            if (!CanMaintainMembers)
            {
                return "保存并选择自定义区域组后可维护成员";
            }

            if (!IsMemberDraftActive)
            {
                return "点击“新增成员”，或在成员列表中编辑已有成员。";
            }

            if (IsEditingMember)
            {
                return $"正在编辑：{SelectedItem?.TargetLabel ?? "--"}。保存或取消后才能切换其他成员。";
            }

            if (SelectedTargetType is null)
            {
                return "请选择要添加的成员类型";
            }

            if (SelectedTargetType.Value == "floor")
            {
                return string.IsNullOrWhiteSpace(TargetFloor)
                    ? "请选择楼层"
                    : $"将添加：{TargetBuilding} / {TargetFloor} / 整层";
            }

            if (SelectedTargetOption is null)
            {
                return SelectedTargetType.Value == "device"
                    ? "请先加载并选择设备候选"
                    : "请先加载并选择子区候选";
            }

            return SelectedTargetType.Value == "device"
                ? $"将添加：{SelectedTargetOption.Building} / {SelectedTargetOption.FloorLabel} / {SelectedTargetOption.SubAreaText} / {SelectedTargetOption.CardName}"
                : $"将添加：{SelectedTargetOption.Building} / {SelectedTargetOption.FloorLabel} / {SelectedTargetOption.SubAreaText}";
        }
    }

    public Visibility CustomEditorVisibility => IsEditingCustomGroup ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SystemEditorVisibility => IsSystemGroupSelected ? Visibility.Visible : Visibility.Collapsed;

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
        StatusText = "正在计算分组命中数量";
        try
        {
            var allTask = repository.SearchAsync(new DeviceQuery(Limit: 1, Offset: 0), cancellationToken);
            var publicRunningTask = repository.SearchAsync(
                new DeviceQuery(
                    CommunicationState: "开机",
                    AreaType: DeviceAreaClassifier.PublicArea,
                    Limit: 1,
                    Offset: 0),
                cancellationToken);
            var groupTask = areaGroupRepository.LoadAsync(cancellationToken);
            var filterTask = repository.LoadFilterOptionsAsync(cancellationToken);

            await Task.WhenAll(allTask, publicRunningTask, groupTask, filterTask).ConfigureAwait(true);
            var result = allTask.Result;
            var publicRunning = publicRunningTask.Result;
            var groupSet = groupTask.Result;
            GroupRecords = groupSet.Groups;
            ItemRecords = groupSet.Items;
            await LoadFloorCatalogAsync(cancellationToken).ConfigureAwait(true);
            SelectedTargetType ??= TargetTypes.FirstOrDefault();

            Groups.Clear();
            foreach (var systemGroup in groupSet.Groups.Where(group =>
                         group.SystemKey is "public" or "non_public"))
            {
                Groups.Add(new GroupSummaryRow(systemGroup));
            }
            Groups.Add(new GroupSummaryRow(
                "公区开机",
                "派生筛选",
                publicRunning.Total,
                "区域为公区且通讯状态为开机的设备；用于巡检公共区域当前开启范围。",
                DeviceAreaClassifier.PublicArea,
                communicationFilter: "开机"));
            Groups.Add(new GroupSummaryRow(
                "离线设备",
                "派生筛选",
                result.Facets.Offline,
                "通讯状态为离线的设备；用于优先排查采集和现场通讯问题。",
                string.Empty,
                communicationFilter: "离线"));
            Groups.Add(new GroupSummaryRow(
                "需排查",
                "健康规则",
                result.Facets.NeedsReview,
                "命中离线、未知、温度缺失或温度越界等健康规则的设备。",
                string.Empty,
                quickFilter: "needs_review"));
            Groups.Add(new GroupSummaryRow(
                "温度异常",
                "健康规则",
                result.Facets.TemperatureIssues,
                "室温或设定温度缺失、越界的设备；用于采集质量和现场状态复核。",
                string.Empty,
                quickFilter: "temp_abnormal"));

            foreach (var group in groupSet.Groups.Where(group =>
                         group.SystemKey is not "public" and not "non_public"))
            {
                Groups.Add(new GroupSummaryRow(group));
            }

            SelectedGroup = Groups.FirstOrDefault(group => group.GroupId is not null) ?? Groups.FirstOrDefault();
            StatusText = $"已读取 {result.Facets.Total:N0} 台设备，{groupSet.Groups.Count:N0} 个数据库区域组";
        }
        catch (Exception ex)
        {
            Groups.Clear();
            Items.Clear();
            TargetOptions.Clear();
            SelectedGroup = null;
            StatusText = applicationLogger.WriteFailure(ex, "groups").DisplayText;
        }
        finally
        {
            IsLoading = false;
        }
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
            StatusText = "保存区域组失败：" + applicationLogger.WriteFailure(ex, "groups").DisplayText;
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
            StatusText = "删除区域组失败：" + applicationLogger.WriteFailure(ex, "groups").DisplayText;
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
            StatusText = "保存楼层目录失败：" + applicationLogger.WriteFailure(ex, "groups").DisplayText;
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
            StatusText = "删除楼层目录失败：" + applicationLogger.WriteFailure(ex, "groups").DisplayText;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadTargetOptions))]
    private async Task LoadTargetOptions()
    {
        IsLoading = true;
        try
        {
            var options = await areaGroupRepository.LoadTargetOptionsAsync(TargetBuilding, TargetFloor).ConfigureAwait(true);
            LoadedTargetOptions.Clear();
            foreach (var option in options.Where(option => SelectedTargetType is null || option.Type == SelectedTargetType.Value))
            {
                LoadedTargetOptions.Add(new AreaGroupTargetOptionRow(option));
            }

            ApplyTargetOptionFilter();
            SelectedTargetOption = null;
            StatusText = $"已读取 {LoadedTargetOptions.Count:N0} 个可选成员";
        }
        catch (Exception ex)
        {
            ClearTargetOptions(clearSearch: false);
            StatusText = "读取成员候选失败：" + applicationLogger.WriteFailure(ex, "groups").DisplayText;
        }
        finally
        {
            IsLoading = false;
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
            StatusText = "保存关注规则失败：" + applicationLogger.WriteFailure(ex, "groups").DisplayText;
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
            StatusText = "删除关注规则失败：" + applicationLogger.WriteFailure(ex, "groups").DisplayText;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanAddItem()
    {
        if (IsLoading || SelectedGroup?.GroupId is null || SelectedTargetType is null || !IsMemberDraftActive)
        {
            return false;
        }

        return SelectedTargetType.Value switch
        {
            "floor" => !string.IsNullOrWhiteSpace(TargetBuilding) && !string.IsNullOrWhiteSpace(TargetFloor),
            "sub_area" => SelectedTargetOption is not null,
            "device" => SelectedTargetOption is not null,
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
        ClearTargetOptions();
        TargetNote = string.Empty;
        StatusText = "正在新增分组成员";
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
            TargetNote = item.RawNote;
            ClearTargetOptions();
            StatusText = $"正在编辑成员：{item.TargetLabel}";
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
                StatusText = "请先明确选择候选成员";
                return;
            }

            var groupId = SelectedGroup.Id;
            var saved = await areaGroupRepository.SaveItemAsync(new AreaGroupItemEdit(
                groupId,
                SelectedTargetType.Value,
                option?.Building ?? TargetBuilding,
                option?.FloorLabel ?? TargetFloor,
                SelectedTargetType.Value == "floor" ? string.Empty : option?.SubAreaText ?? string.Empty,
                SelectedTargetType.Value == "device" ? option?.CardName ?? string.Empty : string.Empty,
                TargetNote,
                _editingItemId)).ConfigureAwait(true);
            StatusText = _editingItemId is null ? "成员已加入区域组" : "成员已保存";
            var savedItemId = saved.Id;
            ResetMemberDraft();
            await LoadAsync().ConfigureAwait(true);
            SelectedGroup = Groups.FirstOrDefault(group => group.GroupId == groupId || group.Id == groupId) ?? SelectedGroup;
            SelectedItem = Items.FirstOrDefault(item => item.Id == savedItemId) ?? SelectedItem;
        }
        catch (Exception ex)
        {
            StatusText = "保存成员失败：" + applicationLogger.WriteFailure(ex, "groups").DisplayText;
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
        StatusText = "已取消成员编辑";
        NotifyEditorState();
        NotifyCommands();
    }

    private bool CanDeleteItem() => !IsLoading && SelectedItem is not null && SelectedGroup?.GroupId is not null;

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
            StatusText = "成员已删除";
            if (_editingItemId == deletingId)
            {
                ResetMemberDraft();
            }

            await LoadAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(CanDeleteSelectedItem));
        }
        catch (Exception ex)
        {
            StatusText = "删除成员失败：" + applicationLogger.WriteFailure(ex, "groups").DisplayText;
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
            StatusText = "当前分组还不能跳转到数据管理";
            return;
        }

        navigationService.NavigateToData(new DataNavigationRequest(
            SearchText: string.Empty,
            Building: string.Empty,
            CommunicationState: SelectedGroup.CommunicationFilter,
            AreaType: SelectedGroup.AreaFilter));
    }

    public void OpenDateManagement() => navigationService.NavigateToDates();

    private void LoadSelectedGroupEdit(GroupSummaryRow? group)
    {
        EditName = group?.Name ?? string.Empty;
        EditAreaLabel = group?.AreaLabel ?? string.Empty;
        EditDescription = group?.Description ?? string.Empty;
        EditPriority = string.IsNullOrWhiteSpace(group?.Priority) ? "重点" : group.Priority;
        EditEnabled = group?.IsEnabled ?? true;
        ClearTargetOptions();
        ResetWatchEdit();
        var watchLoadVersion = ++_watchLoadVersion;
        if (group?.GroupId is not null && group.IsCustom)
        {
            _ = LoadWatchAsync(group, watchLoadVersion);
        }
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
                WatchSummaryText = "关注规则读取失败：" + applicationLogger.WriteFailure(ex, "groups").DisplayText;
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
            return;
        }

        foreach (var item in ItemRecords.Where(item => item.GroupId == SelectedGroup.GroupId.Value))
        {
            Items.Add(new AreaGroupItemRow(item));
        }

        SelectedItem = Items.FirstOrDefault();
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
        OnPropertyChanged(nameof(MemberTargetPreview));
        OnPropertyChanged(nameof(MemberConflictMessage));
        OnPropertyChanged(nameof(MemberConflictVisibility));
        OnPropertyChanged(nameof(CustomEditorVisibility));
        OnPropertyChanged(nameof(SystemEditorVisibility));
    }

    private void ResetMemberDraft()
    {
        _memberEditorMode = MemberEditorMode.None;
        _editingItemId = null;
        _draftSubAreaText = string.Empty;
        _draftCardName = string.Empty;
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
                ? $"已存在相同成员：{exact.TargetLabel}。保存会更新该成员备注，不会新增重复行。"
                : $"目标与已有成员重复：{exact.TargetLabel}。保存会合并到已有成员，并移除当前编辑项。";
        }

        if (draft.Value.TargetType is "sub_area" or "device")
        {
            var floor = existing.FirstOrDefault(item =>
                item.TargetType == "floor" &&
                SameBuilding(item, draft.Value) &&
                SameFloor(item.FloorLabel, draft.Value.FloorLabel));
            if (floor is not null)
            {
                return $"该范围已被整层成员覆盖：{floor.TargetLabel}。保存仍会保留单独成员，但分组统计和关注规则命中范围不会增加。";
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
                return $"该设备已被子区成员覆盖：{subArea.TargetLabel}。保存仍会保留单独成员，但分组统计和关注规则命中范围不会增加。";
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
                return $"当前整层将覆盖已有 {covered:N0} 个子区/设备成员的命中范围；这些成员不会被自动删除。";
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
                return $"当前子区将覆盖已有 {covered:N0} 个设备成员的命中范围；这些成员不会被自动删除。";
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
