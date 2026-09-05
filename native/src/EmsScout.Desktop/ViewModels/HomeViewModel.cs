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
    private string _overviewStatusTitle = "当前数据";
    private string _overviewStatusMessage = "正在读取当前采集数据";
    private string _onlineDevices = "--";
    private string _attentionDevices = "--";
    private string _runningRate = "--";
    private string _offlineRate = "--";
    private string _areaGroupsStatus = "正在计算区域组公区状态";
    private string _areaGroupsError = string.Empty;
    private InfoBarSeverity _overviewSeverity = InfoBarSeverity.Informational;
    private bool _hasLoadError;
    private bool _isLoading;
    private DataSourceOption? _selectedDataSource;

    public string PageStatus
    {
        get => _pageStatus;
        private set => SetProperty(ref _pageStatus, value);
    }

    public string OverviewStatusTitle
    {
        get => _overviewStatusTitle;
        private set => SetProperty(ref _overviewStatusTitle, value);
    }

    public string OverviewStatusMessage
    {
        get => _overviewStatusMessage;
        private set => SetProperty(ref _overviewStatusMessage, value);
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

    public InfoBarSeverity OverviewSeverity
    {
        get => _overviewSeverity;
        private set => SetProperty(ref _overviewSeverity, value);
    }

    public bool HasLoadError
    {
        get => _hasLoadError;
        private set
        {
            if (SetProperty(ref _hasLoadError, value))
            {
                OnPropertyChanged(nameof(LoadErrorVisibility));
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
        ? "当前为最新采集数据"
        : "当前为历史数据，点击切换到最新数据";

    public string LatestBatchIndicatorAutomationName => IsLatestDataSource
        ? "当前为最新采集数据"
        : "当前为历史数据，切换到最新采集数据";

    public Visibility LoadErrorVisibility => HasLoadError ? Visibility.Visible : Visibility.Collapsed;

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

    public ObservableCollection<DashboardRiskRow> Risks { get; } = [];

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
                OnPropertyChanged(nameof(LatestBatchIndicatorOpacity));
                OnPropertyChanged(nameof(HistoricalBatchIndicatorOpacity));
                OnPropertyChanged(nameof(LatestBatchIndicatorToolTip));
                OnPropertyChanged(nameof(LatestBatchIndicatorAutomationName));
            }
        }
    }

    public bool IsLatestDataSource => SelectedDataSource is null;

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
            HasLoadError = false;
        }
        catch (Exception ex)
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
            HasLoadError = false;
        }
        catch (Exception ex)
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
        PageStatus = "正在刷新最新采集数据";
        try
        {
            await RefreshDataSourcesAsync(cancellationToken).ConfigureAwait(true);
            SelectedDataSource = DataSources.FirstOrDefault();
            await LoadOverviewAsync(cancellationToken).ConfigureAwait(true);
            HasLoadError = false;
        }
        catch (Exception ex)
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
        if (IsLoading || IsLatestDataSource)
        {
            return;
        }

        IsLoading = true;
        PageStatus = "正在切换到最新采集数据";
        try
        {
            SelectedDataSource = null;
            await LoadOverviewAsync(cancellationToken).ConfigureAwait(true);
            HasLoadError = false;
        }
        catch (Exception ex)
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
        var runs = await collectionRunRepository.ListAsync(80, cancellationToken).ConfigureAwait(true);
        DataSources.Clear();
        foreach (var run in runs.Where(CollectionRunCompleteness.IsCompleteFleetSnapshot))
        {
            DataSources.Add(new DataSourceOption(run));
        }

        SelectedDataSource = selectedRunId is null
            ? null
            : DataSources.FirstOrDefault(option => option.RunId == selectedRunId);
        OnPropertyChanged(nameof(CanChangeDataSource));
    }

    private async Task LoadOverviewAsync(CancellationToken cancellationToken)
    {
        var runId = SelectedDataSource?.RunId;
        PageStatus = runId is null
            ? "正在读取最新采集数据"
            : $"正在读取历史数据批次 #{runId.Value}";
        var overview = await overviewService.LoadAsync(runId, cancellationToken).ConfigureAwait(true);
        Metrics.Clear();
        Risks.Clear();
        StatusDistribution.Clear();
        Buildings.Clear();
        AreaGroups.Clear();

        foreach (var metric in overview.Metrics)
        {
            Metrics.Add(new MetricItem(metric));
        }

        foreach (var risk in overview.Risks)
        {
            Risks.Add(new DashboardRiskRow(risk));
        }

        var summary = overview.Summary;
        StatusDistribution.Add(new StatusDistributionRow("开机", summary.Running, summary.Total, "运行中"));
        StatusDistribution.Add(new StatusDistributionRow("关机", summary.Stopped, summary.Total, "在线待机"));
        StatusDistribution.Add(new StatusDistributionRow("离线", summary.Offline, summary.Total, "通讯异常"));
        StatusDistribution.Add(new StatusDistributionRow("未知", summary.Unknown, summary.Total, "状态待判定"));

        foreach (var building in overview.Summary.Buildings)
        {
            Buildings.Add(new BuildingSummaryRow(building));
        }

        foreach (var group in overview.AreaGroups)
        {
            AreaGroups.Add(new DashboardAreaGroupRow(group));
        }

        OnlineDevices = summary.Online.ToString("N0");
        AttentionDevices = (summary.Offline + summary.Unknown).ToString("N0");
        RunningRate = summary.RunningRate.ToString("P1");
        OfflineRate = summary.OfflineRate.ToString("P1");
        ApplyOverviewStatus(summary, overview.Risks, runId);
        ApplyAreaGroupsStatus(overview.AreaGroupsError);
        PageStatus = runId is null
            ? "已刷新最新采集数据"
            : $"已读取历史数据批次 #{runId.Value}";
    }

    private void SetLoadError(Exception ex)
    {
        HasLoadError = true;
        PageStatus = ex.Message;
        OverviewSeverity = InfoBarSeverity.Error;
        OverviewStatusTitle = "总览读取失败";
        OverviewStatusMessage = ex.Message;
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

    public void OpenRisk(DashboardRiskRow? row)
    {
        if (row?.NavigationRequest is null)
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

        navigationService.NavigateToData(new DataNavigationRequest(AreaGroupId: row.Id));
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
                ? "尚未配置启用的自定义区域组"
                : $"{AreaGroups.Count:N0} 个自定义区域组；点击任一组查看设备、继续筛选并导出";
        NotifyAreaGroupState();
    }

    private void NotifyAreaGroupState()
    {
        OnPropertyChanged(nameof(AreaGroupsListVisibility));
        OnPropertyChanged(nameof(AreaGroupsEmptyVisibility));
        OnPropertyChanged(nameof(AreaGroupsErrorVisibility));
    }

    private void ApplyOverviewStatus(
        FleetSummary summary,
        IReadOnlyList<DashboardRiskItem> risks,
        long? runId)
    {
        if (runId is not null)
        {
            OverviewSeverity = InfoBarSeverity.Informational;
            OverviewStatusTitle = $"历史数据批次 #{runId.Value}";
            OverviewStatusMessage = $"已加载 {summary.Total:N0} 台设备；历史快照为只读数据，不包含实时详情。";
            return;
        }

        var attention = summary.Offline + summary.Unknown;
        if (summary.Total == 0)
        {
            OverviewSeverity = InfoBarSeverity.Warning;
            OverviewStatusTitle = "当前没有设备数据";
            OverviewStatusMessage = "请先运行采集任务并导入 SQLite，再返回总览核验。";
            return;
        }

        var actionableRisks = risks
            .Where(risk => risk.Kind is OverviewMetricKind.Danger or OverviewMetricKind.Warning)
            .ToList();
        if (actionableRisks.Any(risk => risk.Kind == OverviewMetricKind.Danger))
        {
            OverviewSeverity = InfoBarSeverity.Error;
            OverviewStatusTitle = "存在高优先级风险";
            OverviewStatusMessage = actionableRisks[0].Detail;
            return;
        }

        if (actionableRisks.Count > 0)
        {
            OverviewSeverity = InfoBarSeverity.Warning;
            OverviewStatusTitle = "存在需要复核的风险";
            OverviewStatusMessage = $"{actionableRisks.Count:N0} 类风险需要处理；请先查看首页“优先处理”。";
            return;
        }

        if (summary.Unknown > 0)
        {
            OverviewSeverity = InfoBarSeverity.Warning;
            OverviewStatusTitle = "存在未知通讯状态";
            OverviewStatusMessage = $"当前有 {summary.Unknown:N0} 台设备状态未能判定，建议从数据管理筛选“未知”继续复核。";
            return;
        }

        if (attention > 0)
        {
            OverviewSeverity = InfoBarSeverity.Informational;
            OverviewStatusTitle = "当前数据已加载";
            OverviewStatusMessage = $"在线 {summary.Online:N0} 台，离线 {summary.Offline:N0} 台；可从数据管理按楼栋、状态或区域继续筛查。";
            return;
        }

        OverviewSeverity = InfoBarSeverity.Success;
        OverviewStatusTitle = "当前数据状态平稳";
        OverviewStatusMessage = $"已加载 {summary.Total:N0} 台设备，未发现离线或未知通讯状态。";
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

public sealed class MetricItem(OverviewMetric metric)
{
    public string Label { get; } = metric.Label;

    public string Value { get; } = metric.Value;

    public string Detail { get; } = metric.Detail;

    public string Kind { get; } = metric.Kind.ToString().ToLowerInvariant();

    public DataNavigationRequest? NavigationRequest => string.IsNullOrWhiteSpace(metric.CommunicationState) &&
                                                       string.IsNullOrWhiteSpace(metric.AreaType)
        ? null
        : new DataNavigationRequest(
            CommunicationState: metric.CommunicationState,
            AreaType: metric.AreaType);

    public bool CanNavigate => NavigationRequest is not null;

    public string ActionText => CanNavigate ? "查看筛选" : string.Empty;
}

public sealed class DashboardAreaGroupRow(DashboardAreaGroupSummary summary)
{
    public long Id { get; } = summary.Id;

    public string Name { get; } = summary.Name;

    public string Description { get; } = string.IsNullOrWhiteSpace(summary.Description)
        ? string.IsNullOrWhiteSpace(summary.AreaLabel) ? "自定义区域" : summary.AreaLabel
        : summary.Description;

    public string Priority { get; } = string.IsNullOrWhiteSpace(summary.Priority) ? "普通" : summary.Priority;

    public string ScopeText { get; } = summary.CoveredAreas == 0
        ? $"{summary.MemberCount:N0} 个已添加范围，暂无设备"
        : $"{summary.CoveredAreas:N0} 个位置 / {summary.MemberCount:N0} 个已添加范围";

    public string Total { get; } = summary.Total.ToString("N0");

    public string PublicTotal { get; } = summary.PublicTotal.ToString("N0");

    public string PrivateTotal { get; } = summary.PrivateTotal.ToString("N0");

    public string Online { get; } = summary.Online.ToString("N0");

    public string Offline { get; } = summary.Offline.ToString("N0");

    public string Running { get; } = summary.Running.ToString("N0");

    public string Stopped { get; } = summary.Stopped.ToString("N0");

    public string Unknown { get; } = summary.Unknown.ToString("N0");

    public string PublicRunning { get; } = summary.PublicRunning.ToString("N0");

    public string PublicStopped { get; } = summary.PublicStopped.ToString("N0");

    public string PublicOffline { get; } = summary.PublicOffline.ToString("N0");

    public string PublicUnknown { get; } = summary.PublicUnknown.ToString("N0");

    public string RunningRate { get; } = summary.PublicRunningRate.ToString("P1");

    public double RunningPercent { get; } = summary.PublicRunningRate * 100;

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

    public string AutomationName { get; } = $"区域组 {summary.Name}，设备 {summary.Total:N0} 台，在线 {summary.Online:N0} 台，离线 {summary.Offline:N0} 台，开机 {summary.Running:N0} 台，关机 {summary.Stopped:N0} 台，公区开机 {summary.PublicRunning:N0} 台，公区关机 {summary.PublicStopped:N0} 台";
}

public sealed class DashboardRiskRow(DashboardRiskItem risk)
{
    public string Title { get; } = risk.Title;

    public string Detail { get; } = risk.Detail;

    public string Source { get; } = risk.Source;

    public string CountText { get; } = risk.Count > 0 ? risk.Count.ToString("N0") : "--";

    public string SeverityText { get; } = risk.Kind switch
    {
        OverviewMetricKind.Danger => "高",
        OverviewMetricKind.Warning => "中",
        OverviewMetricKind.Success => "正常",
        OverviewMetricKind.Info => "提示",
        _ => "信息",
    };

    public string Glyph { get; } = risk.Kind switch
    {
        OverviewMetricKind.Danger => "\uE783",
        OverviewMetricKind.Warning => "\uE7BA",
        OverviewMetricKind.Success => "\uE930",
        _ => "\uE946",
    };

    public DataNavigationRequest? NavigationRequest { get; } = string.IsNullOrWhiteSpace(risk.CommunicationState)
        ? null
        : new DataNavigationRequest(CommunicationState: risk.CommunicationState);

    public string ActionText { get; } = string.IsNullOrWhiteSpace(risk.CommunicationState)
        ? string.Empty
        : string.IsNullOrWhiteSpace(risk.ActionLabel) ? "查看数据" : risk.ActionLabel;
}
