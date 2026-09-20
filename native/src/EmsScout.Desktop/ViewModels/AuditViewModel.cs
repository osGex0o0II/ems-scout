using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Collection;
using EmsScout.Application.Quality;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class AuditViewModel(
    ICollectionRunRepository collectionRunRepository,
    ICollectionIssueService collectionIssueService) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRunCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "等待审计数据";

    [ObservableProperty]
    public partial string RunsStatusText { get; private set; } = "尚未读取历史批次";

    [ObservableProperty]
    public partial string LastRefreshedText { get; private set; } = "尚未刷新";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteRunCommand))]
    public partial CollectionRunRow? SelectedRun { get; set; }

    [ObservableProperty]
    public partial CollectionIssueCategoryRow? SelectedIssueCategory { get; set; }

    [ObservableProperty]
    public partial string SelectedIssueBuilding { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IssueSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentAuditSection { get; set; } = "history";

    public ObservableCollection<CollectionRunRow> Runs { get; } = [];
    public ObservableCollection<CollectionRunRow> FilteredRuns { get; } = [];
    public ObservableCollection<CollectionIssueCategoryRow> IssueCategories { get; } = [];
    public ObservableCollection<CollectionIssueRow> IssueRecords { get; } = [];
    public ObservableCollection<string> IssueBuildingOptions { get; } = ["全部楼栋"];

    private IReadOnlyList<CollectionIssueRecord> _allIssueRecords = [];

    public bool CanDeleteSelectedRun => SelectedRun is not null && !IsBusy;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusText = "正在刷新审计中心";
        try
        {
            var runs = await collectionRunRepository.ListAsync(null, cancellationToken).ConfigureAwait(true);
            var selectedId = SelectedRun?.Id;
            Runs.Clear();
            FilteredRuns.Clear();
            foreach (var run in runs)
            {
                var row = new CollectionRunRow(run);
                Runs.Add(row);
                FilteredRuns.Add(row);
            }

            SelectedRun = selectedId.HasValue
                ? FilteredRuns.FirstOrDefault(row => row.Id == selectedId.Value)
                : FilteredRuns.FirstOrDefault();
            RunsStatusText = Runs.Count == 0 ? "暂无历史批次" : $"已读取 {Runs.Count:N0} 个历史批次";
            LastRefreshedText = $"最后更新 {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}";
            StatusText = Runs.Count == 0 ? "暂无可审计批次" : "审计中心已刷新";
            if (SelectedRun is null)
            {
                ClearIssues("请选择一个历史批次查看采集问题");
            }
        }
        catch (Exception ex)
        {
            Runs.Clear();
            FilteredRuns.Clear();
            SelectedRun = null;
            ClearIssues("历史批次读取失败：" + ex.Message);
            RunsStatusText = "历史批次读取失败：" + ex.Message;
            StatusText = RunsStatusText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task DeleteRunAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedRun is null)
        {
            return;
        }

        IsBusy = true;
        var runId = SelectedRun.Id;
        try
        {
            var result = await collectionRunRepository.DeleteAsync(runId, cancellationToken).ConfigureAwait(true);
            var cleanup = result.ArtifactCleanup;
            StatusText = cleanup is { IsComplete: false }
                ? $"已删除批次 #{result.RunId} 的数据库数据；{cleanup.PendingPaths.Count} 个文件未清理"
                : $"已删除批次 #{result.RunId}：{result.DeletedCards:N0} 张卡片及关联本地文件";
            SelectedRun = null;
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = "删除批次失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<RunDeleteImpact?> GetSelectedDeleteImpactAsync(CancellationToken cancellationToken = default)
    {
        return SelectedRun is null
            ? null
            : await collectionRunRepository.GetDeleteImpactAsync(SelectedRun.Id, cancellationToken).ConfigureAwait(true);
    }

    public void ShowIssueDetails(CollectionIssueCategoryRow? category = null)
    {
        SelectedIssueCategory = category;
        CurrentAuditSection = "details";
        ApplyIssueFilter();
    }

    public void ShowIssues() => CurrentAuditSection = "issues";

    [RelayCommand]
    private void ClearIssueFilter()
    {
        SelectedIssueCategory = null;
        SelectedIssueBuilding = "全部楼栋";
        IssueSearchText = string.Empty;
        ApplyIssueFilter();
    }

    partial void OnSelectedRunChanged(CollectionRunRow? value) => _ = LoadIssuesAsync(value?.Id);
    partial void OnSelectedIssueCategoryChanged(CollectionIssueCategoryRow? value) => ApplyIssueFilter();
    partial void OnSelectedIssueBuildingChanged(string value) => ApplyIssueFilter();
    partial void OnIssueSearchTextChanged(string value) => ApplyIssueFilter();

    private async Task LoadIssuesAsync(long? runId)
    {
        if (!runId.HasValue)
        {
            ClearIssues("请选择一个历史批次查看采集问题");
            return;
        }

        try
        {
            var report = await collectionIssueService.LoadForRunAsync(runId.Value).ConfigureAwait(true);
            _allIssueRecords = report.Records;
            IssueCategories.Clear();
            foreach (var category in report.Categories)
            {
                IssueCategories.Add(new CollectionIssueCategoryRow(category));
            }

            IssueBuildingOptions.Clear();
            IssueBuildingOptions.Add("全部楼栋");
            foreach (var building in report.Records.Select(record => record.Building)
                         .Where(building => !string.IsNullOrWhiteSpace(building))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(building => building, StringComparer.OrdinalIgnoreCase))
            {
                IssueBuildingOptions.Add(building);
            }

            if (!IssueBuildingOptions.Contains(SelectedIssueBuilding, StringComparer.OrdinalIgnoreCase))
            {
                SelectedIssueBuilding = "全部楼栋";
            }

            ApplyIssueFilter();
            StatusText = report.Warnings.Count == 0
                ? $"批次 #{runId.Value} 已加载 {report.Records.Count:N0} 条问题记录"
                : $"批次 #{runId.Value} 已加载问题记录；{report.Warnings.Count} 个报告需复核";
        }
        catch (Exception ex)
        {
            ClearIssues("问题报告读取失败：" + ex.Message);
        }
    }

    private void ApplyIssueFilter()
    {
        IssueRecords.Clear();
        var category = SelectedIssueCategory?.Code;
        var building = string.Equals(SelectedIssueBuilding, "全部楼栋", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : SelectedIssueBuilding.Trim();
        var search = IssueSearchText.Trim();
        foreach (var record in _allIssueRecords.Where(record =>
                     string.IsNullOrWhiteSpace(category) || string.Equals(record.IssueType, category, StringComparison.OrdinalIgnoreCase))
                 .Where(record => string.IsNullOrWhiteSpace(building) || string.Equals(record.Building, building, StringComparison.OrdinalIgnoreCase))
                 .Where(record => string.IsNullOrWhiteSpace(search) || MatchesSearch(record, search)))
        {
            IssueRecords.Add(new CollectionIssueRow(record));
        }
    }

    private void ClearIssues(string status)
    {
        _allIssueRecords = [];
        IssueCategories.Clear();
        IssueRecords.Clear();
        IssueBuildingOptions.Clear();
        IssueBuildingOptions.Add("全部楼栋");
        SelectedIssueCategory = null;
        SelectedIssueBuilding = "全部楼栋";
        StatusText = status;
    }

    private static bool MatchesSearch(CollectionIssueRecord record, string search) =>
        string.Join(" ", record.IssueType, record.Building, record.Floor, record.Zone, record.PageName,
            record.DeviceName, record.DeviceId, record.Evidence, record.Reason).Contains(search, StringComparison.OrdinalIgnoreCase);
}
