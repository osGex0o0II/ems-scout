using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmsScout.Application;
using EmsScout.Application.Collection;
using EmsScout.Domain;
using EmsScout.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class HomeViewModel(
    DashboardOverviewService overviewService,
    INavigationService navigationService,
    ICollectionRunRepository collectionRunRepository) : ObservableObject
{
    private string _pageStatus = "正在读取当前采集数据";
    private string _onlineDevices = "--";
    private string _attentionDevices = "--";
    private string _runningRate = "--";
    private string _offlineRate = "--";
    private string _currentBatchTimestamp = "--";
    private string _realtimeStatusText = "正在读取实时详情状态";
    private string _areaGroupsStatus = "正在计算区域组状态";
    private string _areaGroupsError = string.Empty;
    private bool _isLoading;
    private long? _latestDataSourceRunId;
    private DataSourceOption? _selectedDataSource;

    public string PageStatus
    {
        get => _pageStatus;
        private set => SetProperty(ref _pageStatus, value);
    }

    public string OnlineDevices
    {
        get => _onlineDevices;
        private set => SetProperty(ref _onlineDevices, value);
    }

    public string AttentionDevices
    {
        get => _attentionDevices;
        private set => SetProperty(ref _attentionDevices, value);
    }

    public string RunningRate
    {
        get => _runningRate;
        private set => SetProperty(ref _runningRate, value);
    }

    public string OfflineRate
    {
        get => _offlineRate;
        private set => SetProperty(ref _offlineRate, value);
    }

    public string CurrentBatchTimestamp
    {
        get => _currentBatchTimestamp;
        private set => SetProperty(ref _currentBatchTimestamp, value);
    }

    public string RealtimeStatusText
    {
        get => _realtimeStatusText;
        private set => SetProperty(ref _realtimeStatusText, value);
    }

    public string AreaGroupsStatus
    {
        get => _areaGroupsStatus;
        private set => SetProperty(ref _areaGroupsStatus, value);
    }

    public string AreaGroupsError
    {
        get => _areaGroupsError;
        private set
        {
            if (SetProperty(ref _areaGroupsError, value))
            {
                NotifyAreaGroupState();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(CanRefresh));
                OnPropertyChanged(nameof(CanChangeDataSource));
                OnPropertyChanged(nameof(IsLatestDataSource));
                OnPropertyChanged(nameof(LoadingVisibility));
                NotifyAreaGroupState();
            }
        }
    }

    public bool CanRefresh => !IsLoading;

    public bool CanChangeDataSource => !IsLoading && DataSources.Count > 0;

    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;

    public double LatestBatchIndicatorOpacity => IsLatestDataSource ? 1 : 0.22;

    public double HistoricalBatchIndicatorOpacity => IsLatestDataSource ? 0.08 : 1;

    public string LatestBatchIndicatorToolTip => IsLatestDataSource
        ? "当前数据"
        : "历史数据，点击切换当前数据";

    public string LatestBatchIndicatorAutomationName => IsLatestDataSource
        ? "当前数据"
        : "历史数据，切换当前数据";

    public Visibility AreaGroupsListVisibility => AreaGroups.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AreaGroupsEmptyVisibility => !IsLoading &&
                                                   string.IsNullOrWhiteSpace(AreaGroupsError) &&
                                                   AreaGroups.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility AreaGroupsErrorVisibility => !IsLoading && !string.IsNullOrWhiteSpace(AreaGroupsError)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public ObservableCollection<MetricItem> Metrics { get; } = [];

    public ObservableCollection<StatusDistributionRow> StatusDistribution { get; } = [];

    public ObservableCollection<BuildingSummaryRow> Buildings { get; } = [];

    public ObservableCollection<DashboardAreaGroupRow> AreaGroups { get; } = [];

    public ObservableCollection<DataSourceOption> DataSources { get; } = [];

    public ObservableCollection<DataSourceOption> HistoricalDataSources => DataSources;

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
            }
        }
    }

    public bool IsLatestDataSource => SelectedDataSource is null ||
                                      SelectedDataSource.IsCurrent ||
                                      (SelectedDataSource.RunId is not null &&
                                       SelectedDataSource.RunId == _latestDataSourceRunId);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        PageStatus = "正在读取当前采集数据";
        try
        {
            await RefreshDataSourcesAsync(cancellationToken).ConfigureAwait(true);
            await LoadOverviewAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetLoadError(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SelectDataSourceAsync(
        DataSourceOption? option,
        CancellationToken cancellationToken = default)
    {
        if (option is null || IsLoading)
        {
            return;
        }

        SelectedDataSource = option;
        IsLoading = true;
        try
        {
            await LoadOverviewAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetLoadError(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RefreshLatestAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        PageStatus = "正在刷新当前数据";
        try
        {
            await RefreshDataSourcesAsync(cancellationToken).ConfigureAwait(true);
            SelectedDataSource = DataSources.FirstOrDefault();
            await LoadOverviewAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetLoadError(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task UseLatestDataSourceAsync(CancellationToken cancellationToken = default)
    {
        var latestOption = DataSources.FirstOrDefault();
        if (IsLoading || latestOption is null || SelectedDataSource?.RunId == latestOption.RunId)
        {
            return;
        }

        IsLoading = true;
        PageStatus = "正在切换当前数据";
        try
        {
            SelectedDataSource = latestOption;
            await LoadOverviewAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetLoadError(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RefreshDataSourcesAsync(CancellationToken cancellationToken)
    {
        var selectedRunId = SelectedDataSource?.RunId;
        var runs = await Task.Run(() => collectionRunRepository.ListAsync(null, cancellationToken), cancellationToken).ConfigureAwait(true);
        var catalog = CollectionDataSourceCatalog.Build(runs);
        CollectionDataSourceCatalog.EnsureSnapshotAvailable(catalog, selectedRunId);
        var currentBinding = await Task.Run(
            () => collectionRunRepository.GetCurrentDataSourceAsync(cancellationToken),
            cancellationToken).ConfigureAwait(true);
        var currentRun = currentBinding.IsBound
            ? runs.FirstOrDefault(run => currentBinding.Matches(run))
            : null;
        DataSources.Clear();
        DataSources.Add(DataSourceOption.Current(currentRun));
        foreach (var run in catalog.HistoricalRuns)
        {
            DataSources.Add(new DataSourceOption(run));
        }

        _latestDataSourceRunId = DataSources.FirstOrDefault()?.RunId;
        SelectedDataSource = selectedRunId is null
            ? DataSources.FirstOrDefault()
            : DataSources.First(option => option.RunId == selectedRunId);
        OnPropertyChanged(nameof(CanChangeDataSource));
    }

    private async Task LoadOverviewAsync(CancellationToken cancellationToken)
    {
        var runId = SelectedDataSource?.RunId;
        PageStatus = runId is null
            ? "正在读取当前数据"
            : "正在读取所选数据";
        var overview = await Task.Run(
            () => overviewService.LoadAsync(runId, cancellationToken),
            cancellationToken).ConfigureAwait(true);
        CurrentBatchTimestamp = SelectedDataSource?.Label ?? "当前来源未确定";
        RealtimeStatusText = overview.RealtimeStatusText;
        Metrics.Clear();
        StatusDistribution.Clear();
        Buildings.Clear();
        AreaGroups.Clear();

        foreach (var metric in overview.Metrics)
        {
            Metrics.Add(new MetricItem(metric, runId));
        }

        var summary = overview.Summary;
        StatusDistribution.Add(new StatusDistributionRow("开机", summary.Running, summary.Total, "运行中"));
        StatusDistribution.Add(new StatusDistributionRow("关机", summary.Stopped, summary.Total, "在线待机"));
        StatusDistribution.Add(new StatusDistributionRow("离线", summary.Offline, summary.Total, "通讯异常"));
        StatusDistribution.Add(new StatusDistributionRow("未知", summary.Unknown, summary.Total, "状态待判定"));

        foreach (var building in overview.Summary.Buildings)
        {
            Buildings.Add(new BuildingSummaryRow(building, runId));
        }

        foreach (var group in overview.AreaGroups)
        {
            AreaGroups.Add(new DashboardAreaGroupRow(group, runId));
        }

        OnlineDevices = summary.Online.ToString("N0");
        AttentionDevices = (summary.Offline + summary.Unknown).ToString("N0");
        RunningRate = summary.RunningRate.ToString("P1");
        OfflineRate = summary.OfflineRate.ToString("P1");
        ApplyAreaGroupsStatus(overview.AreaGroupsError);
        PageStatus = runId is null
            ? "已刷新当前数据"
            : "已读取所选数据";
    }

    private void SetLoadError(Exception ex)
    {
        PageStatus = ex.Message;
    }

    public void OpenMetric(MetricItem? item)
    {
        if (item?.NavigationRequest is null)
        {
            return;
        }

        navigationService.NavigateToData(item.NavigationRequest);
    }

    public void OpenBuilding(BuildingSummaryRow? row)
    {
        if (row is null)
        {
            return;
        }

        navigationService.NavigateToData(row.NavigationRequest);
    }

    public void OpenAreaGroup(DashboardAreaGroupRow? row)
    {
        if (row is null)
        {
            return;
        }

        navigationService.NavigateToData(row.NavigationRequest);
    }

    public void OpenAreaGroupMetric(DataNavigationRequest? request)
    {
        if (request is not null)
        {
            navigationService.NavigateToData(request);
        }
    }

    public void OpenAreaGroups()
    {
        navigationService.NavigateToGroups();
    }

    private void ApplyAreaGroupsStatus(string error)
    {
        AreaGroupsError = error;
        AreaGroupsStatus = !string.IsNullOrWhiteSpace(error)
            ? "区域组统计暂不可用"
            : AreaGroups.Count == 0
                ? SelectedDataSource?.RunId is not null ? "该历史批次没有可用的区域规则快照" : "尚未配置启用的区域组"
                : SelectedDataSource?.RunId is not null
                    ? $"{AreaGroups.Count:N0} 个历史区域组；点击任一组查看该批次设备"
                    : $"{AreaGroups.Count:N0} 个启用区域组；点击任一组查看设备、继续筛选并导出";
        NotifyAreaGroupState();
    }

    private void NotifyAreaGroupState()
    {
        OnPropertyChanged(nameof(AreaGroupsListVisibility));
        OnPropertyChanged(nameof(AreaGroupsEmptyVisibility));
        OnPropertyChanged(nameof(AreaGroupsErrorVisibility));
    }

}

public sealed class StatusDistributionRow(string label, int count, int total, string detail)
{
    public string Label { get; } = label;

    public string Count { get; } = count.ToString("N0");

    public string Detail { get; } = detail;

    public string PercentText { get; } = total == 0 ? "0.0%" : (count / (double)total).ToString("P1");

    public double PercentValue { get; } = total == 0 ? 0 : count * 100.0 / total;
}

public sealed class MetricItem(OverviewMetric metric, long? runId)
{
    public string Label { get; } = metric.Label;

    public string Value { get; } = metric.Value;

    public string Detail { get; } = metric.Detail;

    public string Kind { get; } = metric.Kind.ToString().ToLowerInvariant();

    public DataNavigationRequest NavigationRequest { get; } = new(
        CommunicationState: metric.CommunicationState,
        AreaType: metric.AreaType,
        RunId: runId);

}

public sealed class DashboardAreaGroupRow(DashboardAreaGroupSummary summary, long? runId = null)
{
    public long Id { get; } = summary.Id;

    public string Name { get; } = summary.Name;

    public string Description { get; } = string.IsNullOrWhiteSpace(summary.Description)
        ? "-"
        : summary.Description.Trim();

    public string Priority { get; } = string.IsNullOrWhiteSpace(summary.Priority) ? "普通" : summary.Priority;

    public string ScopeText { get; } = !string.IsNullOrWhiteSpace(summary.AreaType)
        ? $"{summary.Name}规则命中统计"
        : summary.CoveredAreas == 0
        ? $"{summary.MemberCount:N0} 条规则，暂无设备"
            : $"{summary.CoveredAreas:N0} 个位置 / {summary.Total:N0} 台设备";

    public string AreaType { get; } = summary.AreaType;

    public DataNavigationRequest NavigationRequest { get; } = CreateRequest(summary, runId);

    public DataNavigationRequest TotalNavigationRequest { get; } = CreateRequest(summary, runId);

    public DataNavigationRequest OnlineNavigationRequest { get; } = CreateRequest(summary, runId, QuickFilter: "online");

    public DataNavigationRequest OfflineNavigationRequest { get; } = CreateRequest(summary, runId, QuickFilter: "offline");

    public DataNavigationRequest RunningNavigationRequest { get; } = CreateRequest(summary, runId, CommunicationState: "开机");

    public DataNavigationRequest StoppedNavigationRequest { get; } = CreateRequest(summary, runId, CommunicationState: "关机");

    public DataNavigationRequest ModeAbnormalNavigationRequest { get; } = CreateRequest(summary, runId, QuickFilter: "mode_abnormal");

    public DataNavigationRequest TemperatureAbnormalNavigationRequest { get; } = CreateRequest(summary, runId, QuickFilter: "temperature_abnormal");

    public DataNavigationRequest LockOnNavigationRequest { get; } = CreateRequest(summary, runId, RealtimeLock: "开启");

    public DataNavigationRequest LockOffNavigationRequest { get; } = CreateRequest(summary, runId, RealtimeLock: "关闭");

    public bool CanNavigateLock { get; } = IsRealtimeMetricsAvailable(summary);

    public string Total { get; } = summary.Total.ToString("N0");

    public string Online { get; } = summary.Online.ToString("N0");

    public string Offline { get; } = summary.Offline.ToString("N0");

    public string Running { get; } = summary.Running.ToString("N0");

    public string Stopped { get; } = summary.Stopped.ToString("N0");

    public string Unknown { get; } = summary.Unknown.ToString("N0");

    public string RealtimeStatusText { get; } = string.IsNullOrWhiteSpace(summary.RealtimeStatusText)
        ? "实时详情不可用"
        : summary.RealtimeStatusText;

    public string ModeAbnormal { get; } = summary.ModeAbnormal.ToString("N0");

    public string TemperatureAbnormal { get; } = summary.TemperatureAbnormal.ToString("N0");

    public string LockOn { get; } = IsRealtimeMetricsAvailable(summary)
        ? summary.LockOn.ToString("N0")
        : "--";

    public string LockOff { get; } = IsRealtimeMetricsAvailable(summary)
        ? summary.LockOff.ToString("N0")
        : "--";

    public string StateText { get; } = summary.Total == 0
        ? "暂无设备"
        : summary.Unknown > 0
            ? $"{summary.Unknown:N0} 台待确认"
            : summary.Offline > 0
                ? $"{summary.Offline:N0} 台离线"
                : "全部在线";

    public string Glyph { get; } = summary.Total == 0
        ? "\uE946"
        : summary.Attention > 0
            ? "\uE7BA"
            : "\uE930";

    public string AutomationName { get; } = $"区域组 {summary.Name}，设备 {summary.Total:N0} 台，在线 {summary.Online:N0} 台，离线 {summary.Offline:N0} 台，开机 {summary.Running:N0} 台，关机 {summary.Stopped:N0} 台";

    private static bool IsRealtimeMetricsAvailable(DashboardAreaGroupSummary summary)
    {
        return summary.RealtimeAvailability is
            DashboardRealtimeAvailability.Available or
            DashboardRealtimeAvailability.Partial or
            DashboardRealtimeAvailability.NotApplicable;
    }

    private static DataNavigationRequest CreateRequest(
        DashboardAreaGroupSummary summary,
        long? runId,
        string QuickFilter = "",
        string CommunicationState = "",
        string RealtimeLock = "")
    {
        return new DataNavigationRequest(
            CommunicationState: CommunicationState,
            AreaType: string.IsNullOrWhiteSpace(summary.AreaType) ? "" : summary.AreaType,
            AreaGroupId: string.IsNullOrWhiteSpace(summary.AreaType) ? summary.Id : null,
            RunId: runId,
            QuickFilter: QuickFilter,
            NormalMode: summary.NormalMode,
            TemperatureMin: summary.TemperatureMin,
            TemperatureMax: summary.TemperatureMax,
            RealtimeLock: RealtimeLock);
    }
}
