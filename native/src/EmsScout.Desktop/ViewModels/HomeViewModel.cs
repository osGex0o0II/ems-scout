using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmsScout.Application;
using EmsScout.Domain;
using EmsScout.Desktop.Services;
using Microsoft.UI.Xaml.Controls;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class HomeViewModel(
    DashboardOverviewService overviewService,
    INavigationService navigationService) : ObservableObject
{
    private string _pageStatus = "正在读取当前采集数据";
    private string _sourcePath = string.Empty;
    private string _sourceUpdatedAt = "--";
    private string _overviewStatusTitle = "当前数据";
    private string _overviewStatusMessage = "正在读取当前采集数据";
    private string _onlineDevices = "--";
    private string _attentionDevices = "--";
    private string _runningRate = "--";
    private string _offlineRate = "--";
    private InfoBarSeverity _overviewSeverity = InfoBarSeverity.Informational;
    private bool _hasLoadError;
    private bool _isLoading;

    public string PageStatus
    {
        get => _pageStatus;
        private set => SetProperty(ref _pageStatus, value);
    }

    public string SourcePath
    {
        get => _sourcePath;
        private set => SetProperty(ref _sourcePath, value);
    }

    public string SourceUpdatedAt
    {
        get => _sourceUpdatedAt;
        private set => SetProperty(ref _sourceUpdatedAt, value);
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

    public InfoBarSeverity OverviewSeverity
    {
        get => _overviewSeverity;
        private set => SetProperty(ref _overviewSeverity, value);
    }

    public bool HasLoadError
    {
        get => _hasLoadError;
        private set => SetProperty(ref _hasLoadError, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(CanRefresh));
            }
        }
    }

    public bool CanRefresh => !IsLoading;

    public ObservableCollection<MetricItem> Metrics { get; } = [];

    public ObservableCollection<DashboardRiskRow> Risks { get; } = [];

    public ObservableCollection<StatusDistributionRow> StatusDistribution { get; } = [];

    public ObservableCollection<BuildingSummaryRow> Buildings { get; } = [];

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
            var overview = await overviewService.LoadAsync(cancellationToken).ConfigureAwait(true);
            Metrics.Clear();
            Risks.Clear();
            StatusDistribution.Clear();
            Buildings.Clear();

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

            SourcePath = overview.SourcePath;
            SourceUpdatedAt = overview.SourceUpdatedAt.ToString("yyyy-MM-dd HH:mm:ss");
            OnlineDevices = summary.Online.ToString("N0");
            AttentionDevices = (summary.Offline + summary.Unknown).ToString("N0");
            RunningRate = summary.RunningRate.ToString("P1");
            OfflineRate = summary.OfflineRate.ToString("P1");
            ApplyOverviewStatus(summary, overview.Risks);
            HasLoadError = false;
            PageStatus = "已刷新当前采集数据";
        }
        catch (Exception ex)
        {
            HasLoadError = true;
            PageStatus = ex.Message;
            OverviewSeverity = InfoBarSeverity.Error;
            OverviewStatusTitle = "总览读取失败";
            OverviewStatusMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
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

    private void ApplyOverviewStatus(FleetSummary summary, IReadOnlyList<DashboardRiskItem> risks)
    {
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

    private string? CommunicationFilter { get; } = metric.Label switch
    {
        "开机" => "开机",
        "关机" => "关机",
        "离线" => "离线",
        "未知" => "未知",
        _ => null,
    };

    public DataNavigationRequest? NavigationRequest => string.IsNullOrWhiteSpace(CommunicationFilter)
        ? null
        : new DataNavigationRequest(CommunicationState: CommunicationFilter);

    public bool CanNavigate => NavigationRequest is not null;

    public string ActionText => CanNavigate ? "查看筛选" : string.Empty;
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
