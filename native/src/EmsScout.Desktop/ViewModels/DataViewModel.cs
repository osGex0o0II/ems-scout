using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using EmsScout.Application.Devices;
using EmsScout.Application.Settings;
using EmsScout.Desktop.Services;
using Microsoft.UI.Xaml;
using System.Text.RegularExpressions;

namespace EmsScout.Desktop.ViewModels;

public sealed class DataViewModel(
    IDeviceReadRepository repository,
    IDeviceExportService exportService,
    AppDataPathService pathService,
    AppUiSettingsService uiSettingsService) : ObservableObject
{
    private const int PageSize = 500;
    private const int ExportLimit = 50000;
    private static readonly Regex NativeExportFileNamePattern =
        new(@"^数据管理筛选结果_\d{8}_\d{6}\.xlsx$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private string _statusText = "正在读取 SQLite 数据";
    private string _resultSummary = "--";
    private string _pageSummary = "--";
    private string _lastExportPath = string.Empty;
    private string _lastExportFilePath = string.Empty;
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

    public bool CanOpenLastExport => !IsLoading && !string.IsNullOrWhiteSpace(_lastExportFilePath) && File.Exists(_lastExportFilePath);

    public bool HasRecentExports => RecentExports.Count > 0;

    public Visibility RecentExportsEmptyVisibility => HasRecentExports
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool IsInitializing => _isInitializing;

    public bool CanRunDataAction => !IsLoading;

    public bool CanExport => !IsLoading && TotalRows > 0 && TotalRows <= ExportLimit;

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
        set => SetProperty(ref _selectedCommunication, value);
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
                StatusText = ResultStatusText("已读取当前筛选结果");
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
            StatusText = ResultStatusText("已刷新当前 SQLite 数据");
        }
        catch (Exception ex)
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
            StatusText = ResultStatusText("已读取当前 SQLite 设备数据");
        }
        catch (Exception ex)
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
            StatusText = ResultStatusText("已读取当前 SQLite 设备数据");
            OnPropertyChanged(nameof(EmptyStateVisibility));
            OnPropertyChanged(nameof(LoadingStateVisibility));
        }
        catch (Exception ex)
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
            IsLoading = false;
        }
    }

    private async Task LoadPageCoreAsync(CancellationToken cancellationToken)
    {
        var query = BuildQuery(limit: PageSize, offset: (CurrentPage - 1) * PageSize);
        var result = await repository.SearchAsync(query, cancellationToken).ConfigureAwait(true);
        TotalRows = result.Total;
        Devices.Clear();
        foreach (var record in result.Rows)
        {
            Devices.Add(new DataDeviceRow(record));
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

    public async Task ExportAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        StatusText = "正在同步筛选并导出当前筛选 Excel";
        LastExportPath = string.Empty;
        SetLastExportFilePath(string.Empty);
        try
        {
            CurrentPage = 1;
            await LoadPageCoreAsync(cancellationToken).ConfigureAwait(true);
            if (TotalRows == 0)
            {
                StatusText = "当前筛选没有符合条件的设备，未导出 Excel";
                return;
            }

            if (TotalRows > ExportLimit)
            {
                StatusText = $"当前筛选 {TotalRows:N0} 行，超过 Excel 导出上限 {ExportLimit:N0} 行；请缩小筛选条件后再导出";
                return;
            }

            var result = await exportService.ExportAsync(
                BuildQuery(limit: ExportLimit, offset: 0),
                pathService.ExportDirectory,
                cancellationToken).ConfigureAwait(true);
            LastExportPath = $"上次导出：{result.FileName}；位置：{Path.GetDirectoryName(result.Path)}";
            SetLastExportFilePath(result.Path);
            RefreshRecentExports();
            StatusText = $"已导出 {result.RowCount:N0} 行当前筛选 Excel：{result.FileName}；可打开导出位置查看";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
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
            StatusText = "无法打开导出位置：" + ex.Message;
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
            Limit: limit,
            Offset: offset,
            RunId: null);
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
