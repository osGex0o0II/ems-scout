using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EmsScout.Application.Collection;
using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Application.Settings;
using EmsScout.Desktop.Services;
using Microsoft.UI.Xaml;
using System.Text.RegularExpressions;

namespace EmsScout.Desktop.ViewModels;

public sealed class DataViewModel(
    IDeviceReadRepository repository,
    IDeviceExportService exportService,
    IAreaGroupRepository areaGroupRepository,
    ICollectionRunRepository collectionRunRepository,
    AppDataPathService pathService,
    AppUiSettingsService uiSettingsService) : ObservableObject
{
    private const int PageSize = 500;
    private const int ExportLimit = 50000;
    private static readonly Regex NativeExportFileNamePattern =
        new(@"^数据管理筛选结果_\d{8}_\d{6}\.xlsx$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private string _statusText = "正在读取 SQLite 数据";
    private string _dataStatusText = string.Empty;
    private string _resultSummary = "--";
    private string _pageSummary = "--";
    private string _lastExportPath = string.Empty;
    private string _lastExportFilePath = string.Empty;
    private bool _isLoading;
    private long? _latestDataSourceRunId;
    private string _currentDataSourceTimestamp = "暂无采集时间";
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
    private bool _suppressFilterSelectionChanges;
    private long _filterLoadVersion;
    private CancellationTokenSource? _filterLoadCancellation;
    private DataSourceOption? _selectedDataSource;
    private readonly Dictionary<long, string> _areaGroupFilterValues = [];

    public ObservableCollection<DataDeviceRow> Devices { get; } = [];

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

    public ObservableCollection<DataSourceOption> DataSources { get; } = [];

    public ObservableCollection<DataSourceOption> HistoricalDataSources => DataSources;

    public string CurrentDataSourceTimestamp
    {
        get => _currentDataSourceTimestamp;
        private set => SetProperty(ref _currentDataSourceTimestamp, value);
    }

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

    public string DataStatusText
    {
        get => _dataStatusText;
        private set
        {
            if (SetProperty(ref _dataStatusText, value))
            {
                OnPropertyChanged(nameof(DataStatusVisibility));
            }
        }
    }

    public Visibility DataStatusVisibility => string.IsNullOrWhiteSpace(DataStatusText)
        ? Visibility.Collapsed
        : Visibility.Visible;

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

    public bool CanOpenLastExport => !IsLoading && !string.IsNullOrWhiteSpace(_lastExportFilePath) && File.Exists(_lastExportFilePath);

    public bool HasRecentExports => RecentExports.Count > 0;

    public Visibility RecentExportsEmptyVisibility => HasRecentExports
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool IsInitializing => _isInitializing;

    public bool CanRunDataAction => !IsLoading;

    public bool CanExport => IsLatestDataSource && !IsLoading && TotalRows > 0 && TotalRows <= ExportLimit;

    public bool CanChangeDataSource => !IsLoading && DataSources.Count > 0;

    public bool IsLatestDataSource => SelectedDataSource is null ||
                                      SelectedDataSource.IsCurrent ||
                                      (SelectedDataSource.RunId is not null &&
                                       SelectedDataSource.RunId == _latestDataSourceRunId);

    public double LatestBatchIndicatorOpacity => IsLatestDataSource ? 1 : 0.22;

    public double HistoricalBatchIndicatorOpacity => IsLatestDataSource ? 0.08 : 1;

    public string LatestBatchIndicatorToolTip => IsLatestDataSource
        ? "当前数据"
        : "历史数据，点击切换当前数据";

    public string LatestBatchIndicatorAutomationName => IsLatestDataSource
        ? "当前数据"
        : "历史数据，切换当前数据";

    public DataSourceOption? SelectedDataSource
    {
        get => _selectedDataSource;
        set
        {
            if (SetProperty(ref _selectedDataSource, value))
            {
                OnPropertyChanged(nameof(CanChangeDataSource));
                OnPropertyChanged(nameof(IsLatestDataSource));
                OnPropertyChanged(nameof(LatestBatchIndicatorOpacity));
                OnPropertyChanged(nameof(HistoricalBatchIndicatorOpacity));
                OnPropertyChanged(nameof(LatestBatchIndicatorToolTip));
                OnPropertyChanged(nameof(LatestBatchIndicatorAutomationName));
                OnPropertyChanged(nameof(CanExport));
            }
        }
    }

    public Visibility EmptyStateVisibility => !IsLoading && Devices.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility LoadingStateVisibility => IsLoading
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
                OnPropertyChanged(nameof(CanChangeDataSource));
                OnPropertyChanged(nameof(CanExport));
                OnPropertyChanged(nameof(CanOpenLastExport));
                OnPropertyChanged(nameof(EmptyStateVisibility));
                OnPropertyChanged(nameof(LoadingStateVisibility));
                OnPropertyChanged(nameof(CanExport));
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
            }
        }
    }

    public string DeviceNameText
    {
        get => _deviceNameText;
        set => SetProperty(ref _deviceNameText, value);
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
            }
        }
    }

    public DataFilterOption? SelectedCommunication
    {
        get => _selectedCommunication;
        set
        {
            if (!SetProperty(ref _selectedCommunication, value) ||
                !string.Equals(value?.Value, "离线", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SelectedMode = ModeOptions.FirstOrDefault();
            SelectedFan = FanOptions.FirstOrDefault();
            SelectedSetTemperature = SetTemperatureOptions.FirstOrDefault();
            SelectedRealtimeLock = RealtimeLockOptions.FirstOrDefault();
        }
    }

    public DataFilterOption? SelectedFloor
    {
        get => _selectedFloor;
        set => SetProperty(ref _selectedFloor, value);
    }

    public DataFilterOption? SelectedZuo
    {
        get => _selectedZuo;
        set => SetProperty(ref _selectedZuo, value);
    }

    public bool CanFilterByZuo => IsZuoBuilding(SelectedBuilding?.Value);

    public DataFilterOption? SelectedPageName
    {
        get => _selectedPageName;
        set => SetProperty(ref _selectedPageName, value);
    }

    public DataFilterOption? SelectedMode
    {
        get => _selectedMode;
        set => SetProperty(ref _selectedMode, value);
    }

    public DataFilterOption? SelectedFan
    {
        get => _selectedFan;
        set => SetProperty(ref _selectedFan, value);
    }

    public DataFilterOption? SelectedSetTemperature
    {
        get => _selectedSetTemperature;
        set => SetProperty(ref _selectedSetTemperature, value);
    }

    public DataFilterOption? SelectedRealtimeLock
    {
        get => _selectedRealtimeLock;
        set => SetProperty(ref _selectedRealtimeLock, value);
    }

    public DataFilterOption? SelectedArea
    {
        get => _selectedArea;
        set => SetProperty(ref _selectedArea, value);
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
            await RefreshDataSourcesAsync(cancellationToken).ConfigureAwait(true);
            if (navigationRequest is not null)
            {
                ApplyNavigationRequest(navigationRequest);
            }

            var navigationSnapshot = navigationRequest is null
                ? null
                : CaptureNavigationFilterLoadSnapshot(navigationRequest);
            var reloadFilterOptions = BuildingOptions.Count == 0 || navigationRequest is not null;

            CurrentPage = 1;
            if (reloadFilterOptions)
                await LoadFilterOptionsAndPageAsync(cancellationToken, navigationSnapshot).ConfigureAwait(true);
            else
                await LoadPageAsync(cancellationToken).ConfigureAwait(true);
            if (navigationRequest is not null)
            {
                StatusText = "已定位到数据管理筛选结果";
            }
            else
            {
                StatusText = ResultStatusText("已读取当前筛选结果");
            }
        }
        finally
        {
            _isInitializing = false;
        }
    }

    public void ReportInitializationError(Exception exception)
    {
        SetDataError(exception);
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
            CurrentPage = 1;
            if (await LoadFilterOptionsAndPageAsync(cancellationToken).ConfigureAwait(true))
                StatusText = ResultStatusText("已刷新当前 SQLite 数据");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Devices.Clear();
            SelectedDevice = null;
            TotalRows = 0;
            ResultSummary = "--";
            PageSummary = "--";
            StatusText = ex.Message;
            OnPropertyChanged(nameof(EmptyStateVisibility));
            OnPropertyChanged(nameof(LoadingStateVisibility));
            RefreshRecentExports();
        }
        finally
        {
            if (Volatile.Read(ref _filterLoadCancellation) is null)
                IsLoading = false;
        }
    }

    public async Task SelectDataSourceAsync(
        DataSourceOption? option,
        CancellationToken cancellationToken = default)
    {
        if (option is null || IsLoading || _isInitializing)
        {
            return;
        }

        SelectedDataSource = option;
        IsLoading = true;
        StatusText = $"正在读取 {option.Label}";
        try
        {
            CurrentPage = 1;
            if (await LoadFilterOptionsAndPageAsync(cancellationToken).ConfigureAwait(true))
                StatusText = ResultStatusText($"已读取 {option.Label}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetDataError(ex);
        }
        finally
        {
            if (Volatile.Read(ref _filterLoadCancellation) is null)
                IsLoading = false;
        }
    }

    public async Task UseLatestDataSourceAsync(CancellationToken cancellationToken = default)
    {
        var latestOption = DataSources.FirstOrDefault();
        if (IsLoading || _isInitializing || latestOption is null ||
            SelectedDataSource?.RunId == latestOption.RunId)
        {
            return;
        }

        SelectedDataSource = latestOption;
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task RefreshLatestAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading || _isInitializing)
        {
            return;
        }

        IsLoading = true;
        StatusText = "正在刷新当前数据";
        try
        {
            await RefreshDataSourcesAsync(cancellationToken).ConfigureAwait(true);
            SelectedDataSource = DataSources.FirstOrDefault();
            CurrentPage = 1;
            if (await LoadFilterOptionsAndPageAsync(cancellationToken).ConfigureAwait(true))
                StatusText = ResultStatusText("已刷新当前数据");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetDataError(ex);
        }
        finally
        {
            if (Volatile.Read(ref _filterLoadCancellation) is null)
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

        var query = BuildQuery(limit: 1, offset: 0);
        var groupSetTask = LoadCurrentAreaConfigurationAsync(query.RunId, cancellationToken);
        var optionsTask = Task.Run(() => repository.LoadFilterOptionsAsync(
            query, cancellationToken), cancellationToken);
        var groupSet = await groupSetTask.ConfigureAwait(true);
        var options = await optionsTask.ConfigureAwait(true);
        ApplyFilterOptions(options, groupSet, selectedBuilding, selectedCommunication, selectedFloor, selectedZuo, selectedPageName, selectedMode, selectedFan, selectedSetTemperature, selectedRealtimeLock, selectedArea);
    }

    private async Task<bool> LoadFilterOptionsAndPageAsync(
        CancellationToken cancellationToken,
        FilterLoadSnapshot? requestedSnapshot = null)
    {
        var requestVersion = Interlocked.Increment(ref _filterLoadVersion);
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousCancellation = Interlocked.Exchange(ref _filterLoadCancellation, loadCancellation);
        previousCancellation?.Cancel();
        var snapshot = requestedSnapshot ?? CaptureFilterLoadSnapshot();

        try
        {
            var groupSetTask = LoadCurrentAreaConfigurationAsync(snapshot.Query.RunId, loadCancellation.Token);

            DevicePageAndFilterResult combined;
            if (repository is IDeviceReadRepositoryWithFilterOptions optimizedRepository)
            {
                combined = await Task.Run(
                    () => optimizedRepository.SearchWithFilterOptionsAsync(snapshot.Query, loadCancellation.Token),
                    loadCancellation.Token).ConfigureAwait(true);
            }
            else
            {
                var optionsQuery = snapshot.Query with { Limit = 1, Offset = 0 };
                var options = await Task.Run(
                    () => repository.LoadFilterOptionsAsync(optionsQuery, loadCancellation.Token),
                    loadCancellation.Token).ConfigureAwait(true);
                var page = await Task.Run(
                    () => repository.SearchAsync(snapshot.Query, loadCancellation.Token),
                    loadCancellation.Token).ConfigureAwait(true);
                combined = new DevicePageAndFilterResult(page, options);
            }

            var groupSet = await groupSetTask.ConfigureAwait(true);
            loadCancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentFilterLoad(requestVersion, loadCancellation))
                return false;
            if (requestedSnapshot is null &&
                !snapshot.Query.Equals(BuildQuery(limit: PageSize, offset: (CurrentPage - 1) * PageSize)))
            {
                _ = ApplyFiltersAsync(cancellationToken);
                return false;
            }

            ApplyFilterOptions(
                combined.FilterOptions,
                groupSet,
                snapshot.SelectedBuilding,
                snapshot.SelectedCommunication,
                snapshot.SelectedFloor,
                snapshot.SelectedZuo,
                snapshot.SelectedPageName,
                snapshot.SelectedMode,
                snapshot.SelectedFan,
                snapshot.SelectedSetTemperature,
                snapshot.SelectedRealtimeLock,
                snapshot.SelectedArea);
            ApplyPageResult(combined.Page);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && loadCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception) when (!IsCurrentFilterLoad(requestVersion, loadCancellation))
        {
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _filterLoadCancellation, null, loadCancellation);
            loadCancellation.Dispose();
        }
    }

    private FilterLoadSnapshot CaptureFilterLoadSnapshot() => new(
        BuildQuery(limit: PageSize, offset: (CurrentPage - 1) * PageSize),
        SelectedBuilding?.Value ?? string.Empty,
        SelectedCommunication?.Value ?? string.Empty,
        SelectedFloor?.Value ?? string.Empty,
        SelectedZuo?.Value ?? string.Empty,
        SelectedPageName?.Value ?? string.Empty,
        SelectedMode?.Value ?? string.Empty,
        SelectedFan?.Value ?? string.Empty,
        SelectedSetTemperature?.Value ?? string.Empty,
        SelectedRealtimeLock?.Value ?? string.Empty,
        SelectedArea?.Value ?? string.Empty);

    private FilterLoadSnapshot CaptureNavigationFilterLoadSnapshot(DataNavigationRequest request)
    {
        var areaValue = request.AreaGroupId is long groupId
            ? $"group:{groupId.ToString(CultureInfo.InvariantCulture)}"
            : request.AreaType;
        var areaFilter = BuildAreaQuery(areaValue);
        var building = request.Building ?? string.Empty;
        var zuo = IsZuoBuilding(building) ? request.Zuo ?? string.Empty : string.Empty;
        return new FilterLoadSnapshot(
            new DeviceQuery(
                Building: EmptyToNull(building),
                CommunicationState: EmptyToNull(request.CommunicationState),
                Floor: EmptyToNull(request.Floor),
                DeviceName: EmptyToNull(request.SearchText),
                Zuo: EmptyToNull(zuo),
                PageName: EmptyToNull(request.PageName),
                AreaType: areaFilter.AreaType,
                MonitorGroupIds: areaFilter.MonitorGroupIds,
                Limit: PageSize,
                Offset: 0,
                RunId: SelectedDataSource?.RunId),
            building,
            request.CommunicationState ?? string.Empty,
            request.Floor ?? string.Empty,
            zuo,
            request.PageName ?? string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            areaValue ?? string.Empty);
    }

    private bool IsCurrentFilterLoad(long requestVersion, CancellationTokenSource loadCancellation) =>
        requestVersion == Volatile.Read(ref _filterLoadVersion) &&
        ReferenceEquals(Volatile.Read(ref _filterLoadCancellation), loadCancellation);

    private async Task<AreaGroupSet?> LoadCurrentAreaConfigurationAsync(long? runId, CancellationToken token) =>
        runId is not null ? null : await Task.Run(() => areaGroupRepository.LoadConfigurationAsync(token), token).ConfigureAwait(false);

    private void ApplyFilterOptions(
        DeviceFilterOptions options,
        AreaGroupSet? groupSet,
        string selectedBuilding,
        string selectedCommunication,
        string selectedFloor,
        string selectedZuo,
        string selectedPageName,
        string selectedMode,
        string selectedFan,
        string selectedSetTemperature,
        string selectedRealtimeLock,
        string selectedArea)
    {
        _suppressFilterSelectionChanges = true;
        try
        {
            ReplaceOptions(
                AreaOptions,
                DataFilterOption.All("全部"),
                BuildAreaOptions(groupSet?.Groups, options.UnmatchedCount, options.AreaGroups),
                selectedArea);
            ReplaceOptions(BuildingOptions, DataFilterOption.All("全部"), options.Buildings.Select(DataFilterOption.From), selectedBuilding);
            ReplaceOptions(CommunicationOptions, DataFilterOption.All("全部"), options.CommunicationStates.Select(DataFilterOption.From), selectedCommunication);
            ReplaceOptions(FloorOptions, DataFilterOption.All("全部"), options.Floors.Select(DataFilterOption.From), selectedFloor);
            ReplaceOptions(ZuoOptions, DataFilterOption.All("全部"), options.Zuos.Select(DataFilterOption.From), selectedZuo);
            ReplaceOptions(PageNameOptions, DataFilterOption.All("全部"), options.PageNames.Select(DataFilterOption.From), selectedPageName);
            ReplaceOptions(ModeOptions, DataFilterOption.All("全部"), options.Modes.Select(DataFilterOption.From), selectedMode);
            ReplaceOptions(FanOptions, DataFilterOption.All("全部"), options.Fans.Select(DataFilterOption.From), selectedFan);
            ReplaceOptions(SetTemperatureOptions, DataFilterOption.All("全部"), options.SetTemperatures.Select(DataFilterOption.From), selectedSetTemperature);
            ReplaceOptions(
                RealtimeLockOptions,
                DataFilterOption.All("全部"),
                (options.RealtimeLocks ?? []).Select(DataFilterOption.From),
                selectedRealtimeLock);
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
        finally
        {
            _suppressFilterSelectionChanges = false;
        }
    }

    private void ApplyVisualSettings()
    {
        var compact = uiSettingsService.CompactDataTable;
        TableRowPadding = compact ? new Thickness(14, 14, 14, 14) : new Thickness(14, 16, 14, 16);
        TableHeaderPadding = compact ? new Thickness(14, 8, 14, 8) : new Thickness(14, 12, 14, 12);
    }

    public async Task ApplyFiltersAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusText = "正在同步筛选项和查询设备";
        CurrentPage = 1;
        try
        {
            if (await LoadFilterOptionsAndPageAsync(cancellationToken).ConfigureAwait(true))
                StatusText = ResultStatusText("已读取当前 SQLite 设备数据");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetDataError(ex);
        }
        finally
        {
            if (Volatile.Read(ref _filterLoadCancellation) is null)
                IsLoading = false;
        }
    }

    private void SetDataError(Exception exception)
    {
        Devices.Clear();
        SelectedDevice = null;
        TotalRows = 0;
        ResultSummary = "--";
        PageSummary = "--";
        StatusText = exception.Message;
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(LoadingStateVisibility));
    }

    public async Task ApplyBuildingSelectionAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitializing || _suppressFilterSelectionChanges)
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
        if (_isInitializing || _suppressFilterSelectionChanges)
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
            if (await LoadPageCoreAsync(cancellationToken).ConfigureAwait(true))
            {
                StatusText = ResultStatusText("已读取当前 SQLite 设备数据");
                OnPropertyChanged(nameof(EmptyStateVisibility));
                OnPropertyChanged(nameof(LoadingStateVisibility));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Devices.Clear();
            SelectedDevice = null;
            TotalRows = 0;
            ResultSummary = "--";
            PageSummary = "--";
            StatusText = ex.Message;
            OnPropertyChanged(nameof(EmptyStateVisibility));
            OnPropertyChanged(nameof(LoadingStateVisibility));
        }
        finally
        {
            if (Volatile.Read(ref _filterLoadCancellation) is null)
                IsLoading = false;
        }
    }

    private async Task<bool> LoadPageCoreAsync(CancellationToken cancellationToken)
    {
        var requestVersion = Interlocked.Increment(ref _filterLoadVersion);
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousCancellation = Interlocked.Exchange(ref _filterLoadCancellation, loadCancellation);
        previousCancellation?.Cancel();
        var query = BuildQuery(limit: PageSize, offset: (CurrentPage - 1) * PageSize);
        try
        {
            var result = await Task.Run(
                () => repository.SearchAsync(query, loadCancellation.Token),
                loadCancellation.Token).ConfigureAwait(true);
            if (!IsCurrentFilterLoad(requestVersion, loadCancellation))
                return false;
            ApplyPageResult(result);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && loadCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception) when (!IsCurrentFilterLoad(requestVersion, loadCancellation))
        {
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _filterLoadCancellation, null, loadCancellation);
            loadCancellation.Dispose();
        }
    }

    private void ApplyPageResult(DeviceListResult result)
    {
        TotalRows = result.Total;
        DataStatusText = result.DataStatusText;
        Devices.Clear();
        foreach (var record in result.Rows)
        {
            Devices.Add(new DataDeviceRow(
                record,
                uiSettingsService.TemperatureWarningThreshold,
                uiSettingsService.OfflineStatusColor,
                uiSettingsService.TemperatureWarningColor));
        }

        SelectedDevice = Devices.FirstOrDefault();
        ResultSummary = result.Total > ExportLimit
            ? $"共 {result.Total:N0} 条，超过 Excel 导出上限 {ExportLimit:N0} 条"
            : $"共 {result.Total:N0} 条，当前页 {Devices.Count:N0} 条";
        PageSummary = result.Total == 0
            ? "第 1 / 1 页"
            : $"第 {CurrentPage:N0} / {TotalPages:N0} 页";
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(LoadingStateVisibility));
    }

    private string ResultStatusText(string prefix)
    {
        return TotalRows == 0
            ? prefix + "：没有符合条件的设备"
            : $"{prefix}：{TotalRows:N0} 台设备";
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
        await ApplyFiltersAsync(cancellationToken).ConfigureAwait(true);
    }

    public async Task ExportAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        StatusText = "正在同步筛选并导出当前筛选 Excel";
        LastExportPath = string.Empty;
        SetLastExportFilePath(string.Empty);
        var requestVersion = Interlocked.Increment(ref _filterLoadVersion);
        var exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousCancellation = Interlocked.Exchange(ref _filterLoadCancellation, exportCancellation);
        previousCancellation?.Cancel();
        try
        {
            CurrentPage = 1;
            var exportQuery = BuildQuery(limit: ExportLimit, offset: 0);
            var fullSnapshot = await Task.Run(
                () => repository.SearchAsync(exportQuery, exportCancellation.Token),
                exportCancellation.Token).ConfigureAwait(true);
            if (!IsCurrentFilterLoad(requestVersion, exportCancellation))
                return;
            ApplyPageResult(fullSnapshot with { Rows = fullSnapshot.Rows.Take(PageSize).ToArray() });
            if (fullSnapshot.Total == 0)
            {
                StatusText = "当前筛选没有符合条件的设备，未导出 Excel";
                return;
            }

            if (fullSnapshot.Total > ExportLimit)
            {
                StatusText = $"当前筛选 {fullSnapshot.Total:N0} 行，超过 Excel 导出上限 {ExportLimit:N0} 行；请缩小筛选条件后再导出";
                return;
            }

            var result = exportService is IDeviceSnapshotExportService snapshotExportService
                ? await Task.Run(
                    () => snapshotExportService.ExportSnapshotToFileAsync(exportQuery, fullSnapshot, outputPath, exportCancellation.Token),
                    exportCancellation.Token).ConfigureAwait(true)
                : await Task.Run(
                    () => exportService.ExportToFileAsync(exportQuery, outputPath, exportCancellation.Token),
                    exportCancellation.Token).ConfigureAwait(true);
            if (!IsCurrentFilterLoad(requestVersion, exportCancellation))
                return;
            LastExportPath = $"上次导出：{result.FileName}；位置：{Path.GetDirectoryName(result.Path)}";
            SetLastExportFilePath(result.Path);
            RefreshRecentExports();
            StatusText = $"已导出 {result.RowCount:N0} 行当前筛选 Excel：{result.FileName}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && exportCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (IsCurrentFilterLoad(requestVersion, exportCancellation) && ex is not OperationCanceledException)
        {
            StatusText = ex.Message;
        }
        catch (Exception) when (!IsCurrentFilterLoad(requestVersion, exportCancellation))
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _filterLoadCancellation, null, exportCancellation);
            exportCancellation.Dispose();
            if (Volatile.Read(ref _filterLoadCancellation) is null)
                IsLoading = false;
        }
    }

    public void ReportExportCanceled()
    {
        StatusText = "已取消 Excel 导出";
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
            StatusText = "无法打开导出位置：" + ex.Message;
        }
    }

    private void RefreshRecentExports()
    {
        RecentExports.Clear();
        try
        {
            if (!uiSettingsService.TrackRecentExports)
            {
                return;
            }

            var exportDirectory = pathService.ExportDirectory;
            if (!Directory.Exists(exportDirectory))
            {
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
        }
        catch (Exception)
        {
            // Recent exports are optional; an invalid legacy path must not block data loading.
        }
        finally
        {
            OnPropertyChanged(nameof(HasRecentExports));
            OnPropertyChanged(nameof(RecentExportsEmptyVisibility));
        }
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
        var areaFilter = BuildAreaQuery(SelectedArea?.Value);
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
            AreaType: areaFilter.AreaType,
            MonitorGroupIds: areaFilter.MonitorGroupIds,
            Limit: limit,
            Offset: offset,
            RunId: SelectedDataSource?.RunId);
    }

    private async Task RefreshDataSourcesAsync(CancellationToken cancellationToken)
    {
        var selectedRunId = SelectedDataSource?.RunId;
            var runs = await collectionRunRepository.ListAsync(null, cancellationToken).ConfigureAwait(true);
            var catalog = CollectionDataSourceCatalog.Build(runs);
            CollectionDataSourceCatalog.EnsureSnapshotAvailable(catalog, selectedRunId);
            var currentBinding = await collectionRunRepository
                .GetCurrentDataSourceAsync(cancellationToken)
                .ConfigureAwait(true);
            var currentRun = currentBinding.IsBound
                ? runs.FirstOrDefault(run => currentBinding.Matches(run))
                : null;
            DataSources.Clear();
        DataSources.Add(DataSourceOption.Current(currentRun));
        foreach (var run in catalog.HistoricalRuns)
        {
            DataSources.Add(new DataSourceOption(run));
        }

        var latestOption = DataSources.FirstOrDefault();
        _latestDataSourceRunId = latestOption?.RunId;
        CurrentDataSourceTimestamp = latestOption?.Label ?? "暂无采集时间";
        SelectedDataSource = selectedRunId is null
            ? latestOption
            : DataSources.First(option => option.RunId == selectedRunId);
        OnPropertyChanged(nameof(CanChangeDataSource));
    }

    private void ApplyNavigationRequest(DataNavigationRequest request)
    {
        DeviceNameText = request.SearchText;
        if (request.RunId is not null)
        {
            SelectedDataSource = DataSources.FirstOrDefault(option => option.RunId == request.RunId)
                ?? throw new InvalidOperationException($"历史批次 {request.RunId} 不可用，请重新选择数据来源。");
        }
        SelectedBuilding = SelectOption(BuildingOptions, request.Building) ?? SelectedBuilding;
        SelectedCommunication = SelectOption(CommunicationOptions, request.CommunicationState) ?? SelectedCommunication;
        SelectedArea = request.AreaGroupId is null
            ? SelectAreaOption(request.AreaType) ?? SelectedArea
            : SelectOption(AreaOptions, $"group:{request.AreaGroupId.Value.ToString(CultureInfo.InvariantCulture)}") ?? SelectedArea;
        SelectedFloor = SelectOption(FloorOptions, request.Floor) ?? SelectedFloor;
        SelectedPageName = SelectOption(PageNameOptions, request.PageName) ?? SelectedPageName;
        SelectedZuo = SelectOption(ZuoOptions, request.Zuo) ?? SelectedZuo;
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

    private DataFilterOption? SelectAreaOptionForGroupId(long groupId)
    {
        return _areaGroupFilterValues.TryGetValue(groupId, out var value)
            ? SelectOption(AreaOptions, value)
            : AreaOptions.FirstOrDefault();
    }

    private DataFilterOption? SelectAreaOption(string value)
    {
        return SelectOption(AreaOptions, value);
    }

    private static (string? AreaType, string? MonitorGroupIds) BuildAreaQuery(string? value)
    {
        return value?.Trim() switch
        {
            var group when group?.StartsWith("group:", StringComparison.OrdinalIgnoreCase) == true
                => (null, EmptyToNull(group["group:".Length..])),
            "unmatched" or "未匹配" => ("未匹配", null),
            _ => (null, null),
        };
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

    private IEnumerable<DataFilterOption> BuildAreaOptions(
        IEnumerable<AreaGroupRecord>? groups,
        int unmatchedCount,
        IReadOnlyList<DeviceAreaGroupOption>? areaGroups)
    {
        _areaGroupFilterValues.Clear();
        var rows = new List<DataFilterOption>();
        var countsById = (areaGroups ?? []).ToDictionary(group => group.GroupId, group => group.Count);
        var choices = groups is null ? areaGroups ?? [] : groups.Where(group => group.Enabled)
            .Select(group => new DeviceAreaGroupOption(group.Id, group.Name, countsById.GetValueOrDefault(group.Id)));
        foreach (var group in choices.OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var value = $"group:{group.GroupId.ToString(CultureInfo.InvariantCulture)}";
            if (rows.Any(option => option.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _areaGroupFilterValues[group.GroupId] = value;
            rows.Add(new DataFilterOption(value, group.Name, group.Count));
        }

        if (unmatchedCount > 0)
        {
            rows.Add(new DataFilterOption("unmatched", "未匹配", unmatchedCount));
        }

        return rows;
    }

    private sealed record FilterLoadSnapshot(
        DeviceQuery Query,
        string SelectedBuilding,
        string SelectedCommunication,
        string SelectedFloor,
        string SelectedZuo,
        string SelectedPageName,
        string SelectedMode,
        string SelectedFan,
        string SelectedSetTemperature,
        string SelectedRealtimeLock,
        string SelectedArea);

}
