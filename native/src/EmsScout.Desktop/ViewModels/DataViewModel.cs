using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using EmsScout.Application.Devices;
using EmsScout.Application.Logging;
using EmsScout.Application.Settings;
using EmsScout.Desktop.Services;
using EmsScout.Infrastructure.Logging;
using Microsoft.UI.Xaml;
using System.Text.RegularExpressions;

namespace EmsScout.Desktop.ViewModels;

public sealed class DataViewModel(
    IDeviceReadRepository repository,
    IDeviceExportService exportService,
    AppDataPathService pathService,
    AppUiSettingsService uiSettingsService,
    IApplicationLogger applicationLogger,
    DataContextService dataContext) : ObservableObject
{
    private const int PageSize = 500;
    private const int ExportLimit = 50000;
    private static readonly Regex NativeExportFileNamePattern =
        new(@"^数据管理筛选结果_\d{8}_\d{6}_\d{3}(?:_\d+)?\.xlsx$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private string _statusText = "正在读取 SQLite 数据";
    private string _resultSummary = "--";
    private string _pageSummary = "--";
    private string _lastExportPath = string.Empty;
    private string _lastExportFilePath = string.Empty;
    private string _loadErrorText = string.Empty;
    private string _activeFilterSummary = "全部设备";
    private string? _activeQuickFilter;
    private bool _hasStaleResults;
    private bool _isLoading;
    private Thickness _tableRowPadding = new(14, 8, 14, 8);
    private Thickness _tableHeaderPadding = new(14, 8, 14, 8);
    private int _currentPage = 1;
    private int _totalRows;
    private DataDeviceRow? _selectedDevice;
    private DataFilterOption? _selectedBuilding;
    private DataFilterOption? _selectedCommunication;
    private DataFilterOption? _selectedFloor;
    private DataFilterOption? _selectedZuo;
    private DataFilterOption? _selectedPageName;
    private DataFilterOption? _selectedMode;
    private DataFilterOption? _selectedFan;
    private DataFilterOption? _selectedSetTemperature;
    private DataFilterOption? _selectedRealtimeLock;
    private DataFilterOption? _selectedArea;
    private string _deviceNameText = string.Empty;
    private bool _isInitializing;
    private bool _contextAttached;
    private readonly DeviceDataQuerySession _querySession = new();

    public DataContextService DataContext { get; } = dataContext;

    public ObservableCollection<DataDeviceRow> Devices { get; } = [];

    public ObservableCollection<DeviceQuickFilterOption> QuickFilters { get; } = [];

    public ObservableCollection<RecentExportRow> RecentExports { get; } = [];

    public ObservableCollection<DataFilterOption> BuildingOptions { get; } = [];

    public ObservableCollection<DataFilterOption> CommunicationOptions { get; } = [];

    public ObservableCollection<DataFilterOption> FloorOptions { get; } = [];

    public ObservableCollection<DataFilterOption> ZuoOptions { get; } = [];

    public ObservableCollection<DataFilterOption> PageNameOptions { get; } = [];

    public ObservableCollection<DataFilterOption> ModeOptions { get; } = [];

    public ObservableCollection<DataFilterOption> FanOptions { get; } = [];

    public ObservableCollection<DataFilterOption> SetTemperatureOptions { get; } = [];

    public ObservableCollection<DataFilterOption> RealtimeLockOptions { get; } = [];

    public ObservableCollection<DataFilterOption> AreaOptions { get; } = [];

    public Thickness TableRowPadding
    {
        get => _tableRowPadding;
        private set => SetProperty(ref _tableRowPadding, value);
    }

    public Thickness TableHeaderPadding
    {
        get => _tableHeaderPadding;
        private set => SetProperty(ref _tableHeaderPadding, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    public string PageSummary
    {
        get => _pageSummary;
        private set => SetProperty(ref _pageSummary, value);
    }

    public string LastExportPath
    {
        get => _lastExportPath;
        private set => SetProperty(ref _lastExportPath, value);
    }

    public string LoadErrorText
    {
        get => _loadErrorText;
        private set
        {
            if (SetProperty(ref _loadErrorText, value))
            {
                OnPropertyChanged(nameof(HasLoadError));
                OnPropertyChanged(nameof(LoadErrorVisibility));
            }
        }
    }

    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadErrorText);

    public Visibility LoadErrorVisibility => HasLoadError ? Visibility.Visible : Visibility.Collapsed;

    public bool HasStaleResults
    {
        get => _hasStaleResults;
        private set
        {
            if (SetProperty(ref _hasStaleResults, value))
            {
                OnPropertyChanged(nameof(StaleResultsVisibility));
            }
        }
    }

    public Visibility StaleResultsVisibility => HasStaleResults ? Visibility.Visible : Visibility.Collapsed;

    public string ActiveFilterSummary
    {
        get => _activeFilterSummary;
        private set => SetProperty(ref _activeFilterSummary, value);
    }

    public string ExportPreviewText => DataContext.IsHistory
        ? "历史批次只读，不可导出"
        : !HasCurrentSuccessfulQuery
            ? "筛选条件已改变，请先查询"
            : $"将导出 {_querySession.SuccessfulTotal:N0} 台设备";

    public string DataStateText => DataContext.IsHistory
        ? $"{DataContext.DisplayText} · 只读"
        : $"{DataContext.DisplayText} · 当前可操作";

    public bool CanOpenLastExport => !IsLoading && !string.IsNullOrWhiteSpace(_lastExportFilePath) && File.Exists(_lastExportFilePath);

    public bool HasRecentExports => RecentExports.Count > 0;

    public Visibility RecentExportsEmptyVisibility => HasRecentExports
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool IsInitializing => _isInitializing;

    public bool CanRunDataAction => !IsLoading;

    public bool CanExport => !IsLoading && !DataContext.IsHistory &&
                             HasCurrentSuccessfulQuery &&
                             TotalRows > 0 && TotalRows <= ExportLimit;

    private bool HasCurrentSuccessfulQuery => _querySession.SuccessfulQuery is not null &&
                                              QueryScopesEqual(
                                                  _querySession.SuccessfulQuery,
                                                  BuildQuery(limit: PageSize, offset: 0));

    public string ExportHint => DataContext.IsHistory
        ? "所选历史批次为只读预览；切换到最近更新时间可导出。"
        : TotalRows > ExportLimit
            ? $"结果超过 {ExportLimit:N0} 行，请缩小筛选范围后导出。"
            : "导出内容与当前筛选结果一致。";

    public Visibility EmptyStateVisibility => !IsLoading && Devices.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ResultListVisibility => Devices.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility LoadingStateVisibility => IsLoading && Devices.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(CanMovePrevious));
                OnPropertyChanged(nameof(CanMoveNext));
                OnPropertyChanged(nameof(CanRunDataAction));
                OnPropertyChanged(nameof(CanExport));
                OnPropertyChanged(nameof(ExportHint));
                OnPropertyChanged(nameof(CanOpenLastExport));
                OnPropertyChanged(nameof(EmptyStateVisibility));
                OnPropertyChanged(nameof(ResultListVisibility));
                OnPropertyChanged(nameof(LoadingStateVisibility));
                OnPropertyChanged(nameof(ExportPreviewText));
            }
        }
    }

    public bool CanMovePrevious => !IsLoading && CurrentPage > 1;

    public bool CanMoveNext => !IsLoading && CurrentPage < TotalPages;

    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(CanMovePrevious));
                OnPropertyChanged(nameof(CanMoveNext));
            }
        }
    }

    public int TotalPages => _totalRows <= 0 ? 1 : (int)Math.Ceiling(_totalRows / (double)PageSize);

    private int TotalRows
    {
        get => _totalRows;
        set
        {
            if (SetProperty(ref _totalRows, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(TotalPages));
                OnPropertyChanged(nameof(CanMoveNext));
                OnPropertyChanged(nameof(CanMovePrevious));
                OnPropertyChanged(nameof(CanExport));
            }
        }
    }

    public DataDeviceRow? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(DetailVisibility));
                OnPropertyChanged(nameof(NoSelectionDetailVisibility));
            }
        }
    }

    public Visibility DetailVisibility => SelectedDevice is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NoSelectionDetailVisibility => SelectedDevice is null ? Visibility.Visible : Visibility.Collapsed;

    public string DeviceNameText
    {
        get => _deviceNameText;
        set
        {
            if (SetProperty(ref _deviceNameText, value))
            {
                NotifyFilterInputChanged();
            }
        }
    }

    public DataFilterOption? SelectedBuilding
    {
        get => _selectedBuilding;
        set
        {
            if (SetProperty(ref _selectedBuilding, value))
            {
                CoerceZuoSelectionForBuilding();
                OnPropertyChanged(nameof(CanFilterByZuo));
                NotifyFilterInputChanged();
            }
        }
    }

    public DataFilterOption? SelectedCommunication
    {
        get => _selectedCommunication;
        set
        {
            if (SetProperty(ref _selectedCommunication, value))
            {
                NotifyFilterInputChanged();
            }
        }
    }

    public DataFilterOption? SelectedFloor
    {
        get => _selectedFloor;
        set
        {
            if (SetProperty(ref _selectedFloor, value))
            {
                NotifyFilterInputChanged();
            }
        }
    }

    public DataFilterOption? SelectedZuo
    {
        get => _selectedZuo;
        set
        {
            if (SetProperty(ref _selectedZuo, value))
            {
                NotifyFilterInputChanged();
            }
        }
    }

    public bool CanFilterByZuo => IsZuoBuilding(SelectedBuilding?.Value);

    public DataFilterOption? SelectedPageName
    {
        get => _selectedPageName;
        set
        {
            if (SetProperty(ref _selectedPageName, value))
            {
                NotifyFilterInputChanged();
            }
        }
    }

    public DataFilterOption? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (SetProperty(ref _selectedMode, value))
            {
                NotifyFilterInputChanged();
            }
        }
    }

    public DataFilterOption? SelectedFan
    {
        get => _selectedFan;
        set
        {
            if (SetProperty(ref _selectedFan, value))
            {
                NotifyFilterInputChanged();
            }
        }
    }

    public DataFilterOption? SelectedSetTemperature
    {
        get => _selectedSetTemperature;
        set
        {
            if (SetProperty(ref _selectedSetTemperature, value))
            {
                NotifyFilterInputChanged();
            }
        }
    }

    public DataFilterOption? SelectedRealtimeLock
    {
        get => _selectedRealtimeLock;
        set
        {
            if (SetProperty(ref _selectedRealtimeLock, value))
            {
                NotifyFilterInputChanged();
            }
        }
    }

    public DataFilterOption? SelectedArea
    {
        get => _selectedArea;
        set
        {
            if (SetProperty(ref _selectedArea, value))
            {
                NotifyFilterInputChanged();
            }
        }
    }


    public async Task InitializeAsync(DataNavigationRequest? navigationRequest = null, CancellationToken cancellationToken = default)
    {
        if (_isInitializing)
        {
            return;
        }

        _isInitializing = true;
        ApplyVisualSettings();
        try
        {
            RefreshRecentExports();
            AttachDataContext();
            if (DataContext.Options.Count == 0)
            {
                await DataContext.RefreshAsync(cancellationToken).ConfigureAwait(true);
            }
            if (BuildingOptions.Count == 0)
            {
                await ReloadFilterOptionsAsync(cancellationToken).ConfigureAwait(true);
            }

            if (navigationRequest is not null)
            {
                ApplyNavigationRequest(navigationRequest);
            }

            CurrentPage = 1;
            await LoadPageAsync(cancellationToken).ConfigureAwait(true);
            if (navigationRequest is not null)
            {
                StatusText = "已定位到数据管理筛选结果";
            }
            else
            {
                StatusText = ResultStatusText("已读取筛选结果");
            }
        }
        finally
        {
            _isInitializing = false;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading || _isInitializing)
        {
            return;
        }

        IsLoading = true;
        StatusText = "正在刷新筛选项和设备数据";
        try
        {
            await ReloadFilterOptionsAsync(cancellationToken).ConfigureAwait(true);
            CurrentPage = 1;
            await LoadPageCoreAsync(cancellationToken).ConfigureAwait(true);
            StatusText = ResultStatusText("已刷新设备数据");
        }
        catch (Exception ex)
        {
            HandleLoadFailure(ex);
            RefreshRecentExports();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ReloadFilterOptionsAsync(CancellationToken cancellationToken)
    {
        var selectedBuilding = SelectedBuilding?.Value ?? string.Empty;
        var selectedCommunication = SelectedCommunication?.Value ?? string.Empty;
        var selectedFloor = SelectedFloor?.Value ?? string.Empty;
        var selectedZuo = SelectedZuo?.Value ?? string.Empty;
        var selectedPageName = SelectedPageName?.Value ?? string.Empty;
        var selectedMode = SelectedMode?.Value ?? string.Empty;
        var selectedFan = SelectedFan?.Value ?? string.Empty;
        var selectedSetTemperature = SelectedSetTemperature?.Value ?? string.Empty;
        var selectedRealtimeLock = SelectedRealtimeLock?.Value ?? string.Empty;
        var selectedArea = SelectedArea?.Value ?? string.Empty;

        var options = await repository.LoadFilterOptionsAsync(BuildQuery(limit: 1, offset: 0), cancellationToken).ConfigureAwait(true);
        ReplaceOptions(BuildingOptions, DataFilterOption.All("全部楼栋"), options.Buildings.Select(DataFilterOption.From), selectedBuilding);
        ReplaceOptions(CommunicationOptions, DataFilterOption.All("全部开关机状态"), options.CommunicationStates.Select(DataFilterOption.From), selectedCommunication);
        ReplaceOptions(FloorOptions, DataFilterOption.All("全部楼层"), options.Floors.Select(DataFilterOption.From), selectedFloor);
        ReplaceOptions(ZuoOptions, DataFilterOption.All("全部座号"), options.Zuos.Select(DataFilterOption.From), selectedZuo);
        ReplaceOptions(PageNameOptions, DataFilterOption.All("全部页面"), options.PageNames.Select(DataFilterOption.From), selectedPageName);
        ReplaceOptions(ModeOptions, DataFilterOption.All("全部模式"), options.Modes.Select(DataFilterOption.From), selectedMode);
        ReplaceOptions(FanOptions, DataFilterOption.All("全部风速"), options.Fans.Select(DataFilterOption.From), selectedFan);
        ReplaceOptions(SetTemperatureOptions, DataFilterOption.All("全部设置温度"), options.SetTemperatures.Select(DataFilterOption.From), selectedSetTemperature);
        ReplaceOptions(
            RealtimeLockOptions,
            DataFilterOption.All("全部集控锁定状态"),
            (options.RealtimeLocks ?? []).Select(DataFilterOption.From),
            selectedRealtimeLock);
        ReplaceOptions(
            AreaOptions,
            DataFilterOption.All("全部区域"),
            [
                new DataFilterOption("公区", "公区", -1),
                new DataFilterOption("非公区", "非公区", -1),
                new DataFilterOption("未匹配", "未匹配", -1),
            ],
            selectedArea);

        SelectedBuilding = SelectOption(BuildingOptions, selectedBuilding) ?? BuildingOptions.FirstOrDefault();
        SelectedCommunication = SelectOption(CommunicationOptions, selectedCommunication) ?? CommunicationOptions.FirstOrDefault();
        SelectedFloor = SelectOption(FloorOptions, selectedFloor) ?? FloorOptions.FirstOrDefault();
        SelectedZuo = SelectOption(ZuoOptions, selectedZuo) ?? ZuoOptions.FirstOrDefault();
        SelectedPageName = SelectOption(PageNameOptions, selectedPageName) ?? PageNameOptions.FirstOrDefault();
        SelectedMode = SelectOption(ModeOptions, selectedMode) ?? ModeOptions.FirstOrDefault();
        SelectedFan = SelectOption(FanOptions, selectedFan) ?? FanOptions.FirstOrDefault();
        SelectedSetTemperature = SelectOption(SetTemperatureOptions, selectedSetTemperature) ?? SetTemperatureOptions.FirstOrDefault();
        SelectedRealtimeLock = SelectOption(RealtimeLockOptions, selectedRealtimeLock) ?? RealtimeLockOptions.FirstOrDefault();
        SelectedArea = SelectOption(AreaOptions, selectedArea) ?? AreaOptions.FirstOrDefault();
        CoerceZuoSelectionForBuilding();
    }

    private void ApplyVisualSettings()
    {
        var compact = uiSettingsService.CompactDataTable;
        TableRowPadding = compact ? new Thickness(14, 6, 14, 6) : new Thickness(14, 10, 14, 10);
        TableHeaderPadding = compact ? new Thickness(14, 8, 14, 8) : new Thickness(14, 12, 14, 12);
    }

    public async Task ApplyFiltersAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        StatusText = "正在同步筛选项和查询设备";
        CurrentPage = 1;
        try
        {
            await ReloadFilterOptionsAsync(cancellationToken).ConfigureAwait(true);
            await LoadPageCoreAsync(cancellationToken).ConfigureAwait(true);
            StatusText = ResultStatusText("已读取设备数据");
        }
        catch (Exception ex)
        {
            HandleLoadFailure(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task ApplyBuildingSelectionAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading || _isInitializing)
        {
            return;
        }

        SelectedZuo = ZuoOptions.FirstOrDefault();
        SelectedFloor = FloorOptions.FirstOrDefault();
        SelectedPageName = PageNameOptions.FirstOrDefault();
        await ApplyFiltersAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task ApplyFloorSelectionAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading || _isInitializing)
        {
            return;
        }

        SelectedPageName = PageNameOptions.FirstOrDefault();
        await ApplyFiltersAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task MovePreviousAsync(CancellationToken cancellationToken = default)
    {
        if (!CanMovePrevious)
        {
            return;
        }

        CurrentPage--;
        await LoadPageAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task MoveNextAsync(CancellationToken cancellationToken = default)
    {
        if (!CanMoveNext)
        {
            return;
        }

        CurrentPage++;
        await LoadPageAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task LoadPageAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusText = "正在查询设备";
        try
        {
            await LoadPageCoreAsync(cancellationToken).ConfigureAwait(true);
            StatusText = ResultStatusText("已读取设备数据");
            OnPropertyChanged(nameof(EmptyStateVisibility));
            OnPropertyChanged(nameof(LoadingStateVisibility));
        }
        catch (Exception ex)
        {
            HandleLoadFailure(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadPageCoreAsync(CancellationToken cancellationToken)
    {
        var query = BuildQuery(limit: PageSize, offset: (CurrentPage - 1) * PageSize);
        var request = _querySession.Begin(query);
        var result = await repository.SearchAsync(query, cancellationToken).ConfigureAwait(true);
        if (!_querySession.TryAccept(request, result))
        {
            return;
        }

        var selectedId = SelectedDevice?.Id;
        var rows = result.Rows.Select(record => new DataDeviceRow(record)).ToArray();
        TotalRows = result.Total;
        Devices.Clear();
        foreach (var row in rows)
        {
            Devices.Add(row);
        }

        SelectedDevice = Devices.FirstOrDefault(row => row.Id == selectedId) ?? Devices.FirstOrDefault();
        ReplaceQuickFilters(result.Facets);
        ActiveFilterSummary = FormatActiveFilters(query);
        LoadErrorText = string.Empty;
        HasStaleResults = false;
        ResultSummary = result.Total > ExportLimit
            ? $"共 {result.Total:N0} 条，超过 Excel 导出上限 {ExportLimit:N0} 条"
            : $"共 {result.Total:N0} 条，当前页 {Devices.Count:N0} 条";
        PageSummary = result.Total == 0
            ? "第 1 / 1 页"
            : $"第 {CurrentPage:N0} / {TotalPages:N0} 页";
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(ResultListVisibility));
        OnPropertyChanged(nameof(LoadingStateVisibility));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ExportPreviewText));
    }

    private string ResultStatusText(string prefix)
    {
        return TotalRows == 0
            ? $"{prefix}（{DataContext.DisplayText}）：没有符合条件的设备"
            : $"{prefix}（{DataContext.DisplayText}）：{TotalRows:N0} 台设备";
    }

    public async Task ApplyQuickFilterAsync(string quickFilter, CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        _activeQuickFilter = string.Equals(_activeQuickFilter, quickFilter, StringComparison.OrdinalIgnoreCase)
            ? null
            : quickFilter;
        NotifyFilterInputChanged();
        await ApplyFiltersAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task ResetFiltersAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        DeviceNameText = string.Empty;
        SelectedBuilding = BuildingOptions.FirstOrDefault();
        SelectedCommunication = CommunicationOptions.FirstOrDefault();
        SelectedFloor = FloorOptions.FirstOrDefault();
        SelectedZuo = ZuoOptions.FirstOrDefault();
        SelectedPageName = PageNameOptions.FirstOrDefault();
        SelectedMode = ModeOptions.FirstOrDefault();
        SelectedFan = FanOptions.FirstOrDefault();
        SelectedSetTemperature = SetTemperatureOptions.FirstOrDefault();
        SelectedRealtimeLock = RealtimeLockOptions.FirstOrDefault();
        SelectedArea = AreaOptions.FirstOrDefault();
        _activeQuickFilter = null;
        NotifyFilterInputChanged();
        await ApplyFiltersAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task ExportAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        if (!HasCurrentSuccessfulQuery)
        {
            StatusText = "筛选条件已改变，请先查询再导出";
            return;
        }

        var exportQuery = _querySession.SuccessfulQuery! with
        {
            Limit = ExportLimit,
            Offset = 0,
        };

        IsLoading = true;
        StatusText = $"正在导出 {_querySession.SuccessfulTotal:N0} 台设备";
        LastExportPath = string.Empty;
        SetLastExportFilePath(string.Empty);
        try
        {
            if (_querySession.SuccessfulTotal == 0)
            {
                StatusText = "筛选结果没有设备，未导出 Excel";
                return;
            }

            if (_querySession.SuccessfulTotal > ExportLimit)
            {
                StatusText = $"筛选结果 {_querySession.SuccessfulTotal:N0} 行，超过 Excel 导出上限 {ExportLimit:N0} 行；请缩小筛选条件后再导出";
                return;
            }

            var result = await exportService.ExportAsync(
                exportQuery,
                pathService.ExportDirectory,
                cancellationToken).ConfigureAwait(true);
            if (result.RowCount != _querySession.SuccessfulTotal)
            {
                throw new InvalidDataException(
                    $"Export result count mismatch: expected {_querySession.SuccessfulTotal}, actual {result.RowCount}.");
            }
            LastExportPath = $"上次导出：{result.FileName}；位置：{Path.GetDirectoryName(result.Path)}";
            SetLastExportFilePath(result.Path);
            RefreshRecentExports();
            StatusText = $"已导出 {result.RowCount:N0} 行筛选结果：{result.FileName}；可打开导出位置查看";
        }
        catch (Exception ex)
        {
            StatusText = applicationLogger.WriteFailure(ex, "data").DisplayText;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void OpenExportLocation(RecentExportRow? row)
    {
        if (row is null)
        {
            StatusText = "请选择一个导出文件";
            return;
        }

        OpenFileInExplorer(row.FullPath);
    }

    public void OpenLastExportLocation()
    {
        if (!CanOpenLastExport)
        {
            StatusText = "导出文件不存在或尚未导出";
            return;
        }

        OpenFileInExplorer(_lastExportFilePath);
    }

    private void OpenFileInExplorer(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = "无法打开导出位置：" + applicationLogger.WriteFailure(ex, "data").DisplayText;
        }
    }

    private void RefreshRecentExports()
    {
        RecentExports.Clear();
        if (!uiSettingsService.TrackRecentExports)
        {
            OnPropertyChanged(nameof(HasRecentExports));
            OnPropertyChanged(nameof(RecentExportsEmptyVisibility));
            return;
        }

        var exportDirectory = pathService.ExportDirectory;
        if (!Directory.Exists(exportDirectory))
        {
            OnPropertyChanged(nameof(HasRecentExports));
            OnPropertyChanged(nameof(RecentExportsEmptyVisibility));
            return;
        }

        foreach (var file in new DirectoryInfo(exportDirectory)
                     .EnumerateFiles("数据管理筛选结果_*.xlsx", SearchOption.TopDirectoryOnly)
                     .Where(file => NativeExportFileNamePattern.IsMatch(file.Name))
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Take(8))
        {
            RecentExports.Add(new RecentExportRow(file, exportDirectory));
        }

        OnPropertyChanged(nameof(HasRecentExports));
        OnPropertyChanged(nameof(RecentExportsEmptyVisibility));
    }

    private void SetLastExportFilePath(string path)
    {
        _lastExportFilePath = path;
        OnPropertyChanged(nameof(CanOpenLastExport));
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private DeviceQuery BuildQuery(int limit, int offset)
    {
        return new DeviceQuery(
            Building: EmptyToNull(SelectedBuilding?.Value),
            CommunicationState: EmptyToNull(SelectedCommunication?.Value),
            Floor: EmptyToNull(SelectedFloor?.Value),
            DeviceName: EmptyToNull(DeviceNameText),
            Zuo: CanFilterByZuo ? EmptyToNull(SelectedZuo?.Value) : null,
            PageName: EmptyToNull(SelectedPageName?.Value),
            Mode: EmptyToNull(SelectedMode?.Value),
            Fan: EmptyToNull(SelectedFan?.Value),
            SetTemperature: EmptyToNull(SelectedSetTemperature?.Value),
            RealtimeLock: EmptyToNull(SelectedRealtimeLock?.Value),
            AreaType: EmptyToNull(SelectedArea?.Value),
            QuickFilter: EmptyToNull(_activeQuickFilter),
            Limit: limit,
            Offset: offset,
            RunId: DataContext.RunId);
    }

    private void HandleLoadFailure(Exception exception)
    {
        var failure = applicationLogger.WriteFailure(exception, "data");
        LoadErrorText = failure.DisplayText;
        HasStaleResults = Devices.Count > 0;
        StatusText = failure.DisplayText;
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(ResultListVisibility));
        OnPropertyChanged(nameof(LoadingStateVisibility));
    }

    private void ReplaceQuickFilters(DeviceFacets facets)
    {
        QuickFilters.Clear();
        foreach (var item in DeviceQuickFilterCatalog.Create(facets, _activeQuickFilter))
        {
            QuickFilters.Add(item);
        }
    }

    private static string FormatActiveFilters(DeviceQuery query)
    {
        var filters = new List<string>();
        AddFilter(filters, "楼栋", query.Building);
        AddFilter(filters, "座号", query.Zuo);
        AddFilter(filters, "楼层", query.Floor);
        AddFilter(filters, "页面", query.PageName);
        AddFilter(filters, "设备", query.DeviceName);
        AddFilter(filters, "状态", query.CommunicationState);
        AddFilter(filters, "区域", query.AreaType);
        AddFilter(filters, "模式", query.Mode);
        AddFilter(filters, "风速", query.Fan);
        AddFilter(filters, "设置温度", query.SetTemperature);
        AddFilter(filters, "集控锁定", query.RealtimeLock);
        if (!string.IsNullOrWhiteSpace(query.QuickFilter))
        {
            var label = query.QuickFilter switch
            {
                "offline" => "离线",
                "unknown" => "未知",
                "temp_abnormal" => "温度异常",
                "realtime_missing" => "无实时数据",
                "needs_review" => "需关注",
                _ => query.QuickFilter,
            };
            filters.Add($"快捷：{label}");
        }

        return filters.Count == 0 ? "已生效条件：全部设备" : "已生效条件：" + string.Join(" · ", filters);
    }

    private static void AddFilter(ICollection<string> filters, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            filters.Add($"{label}：{value.Trim()}");
        }
    }

    private static bool QueryScopesEqual(DeviceQuery left, DeviceQuery right)
    {
        return left with { Limit = 0, Offset = 0 } == right with { Limit = 0, Offset = 0 };
    }

    private void NotifyFilterInputChanged()
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ExportPreviewText));
    }

    public async Task SelectDataContextAsync(DataContextOption? option, CancellationToken cancellationToken = default)
    {
        DataContext.Select(option);
        await LoadPageAsync(cancellationToken).ConfigureAwait(true);
    }

    private void AttachDataContext()
    {
        if (_contextAttached)
        {
            return;
        }

        DataContext.ContextChanged += async (_, _) =>
        {
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(ExportHint));
            OnPropertyChanged(nameof(ExportPreviewText));
            OnPropertyChanged(nameof(DataStateText));
            if (!_isInitializing && !IsLoading)
            {
                CurrentPage = 1;
                await LoadPageAsync().ConfigureAwait(true);
            }
        };
        _contextAttached = true;
    }

    private void ApplyNavigationRequest(DataNavigationRequest request)
    {
        DeviceNameText = request.SearchText;
        SelectedBuilding = SelectOption(BuildingOptions, request.Building) ?? SelectedBuilding;
        SelectedCommunication = SelectOption(CommunicationOptions, request.CommunicationState) ?? SelectedCommunication;
        SelectedArea = SelectOption(AreaOptions, request.AreaType) ?? SelectedArea;
        SelectedFloor = SelectOption(FloorOptions, request.Floor) ?? FloorOptions.FirstOrDefault();
        SelectedPageName = SelectOption(PageNameOptions, request.PageName) ?? PageNameOptions.FirstOrDefault();
        SelectedZuo = SelectOption(ZuoOptions, request.Zuo) ?? ZuoOptions.FirstOrDefault();
        CoerceZuoSelectionForBuilding();
    }

    private static DataFilterOption? SelectOption(IEnumerable<DataFilterOption> options, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return options.FirstOrDefault();
        }

        return options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase));
    }

    private void CoerceZuoSelectionForBuilding()
    {
        if (CanFilterByZuo)
        {
            return;
        }

        var all = ZuoOptions.FirstOrDefault();
        if (!ReferenceEquals(SelectedZuo, all))
        {
            SelectedZuo = all;
        }
    }

    private static bool IsZuoBuilding(string? building)
    {
        return string.Equals(building, "5号", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(building, "6号", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReplaceOptions(
        ObservableCollection<DataFilterOption> target,
        DataFilterOption allOption,
        IEnumerable<DataFilterOption> options,
        string selectedValue = "")
    {
        var rows = options.ToList();
        if (!string.IsNullOrWhiteSpace(selectedValue) &&
            rows.All(option => !string.Equals(option.Value, selectedValue, StringComparison.OrdinalIgnoreCase)))
        {
            rows.Add(new DataFilterOption(selectedValue, selectedValue, 0));
        }

        target.Clear();
        target.Add(allOption);
        foreach (var option in rows)
        {
            target.Add(option);
        }
    }

}
