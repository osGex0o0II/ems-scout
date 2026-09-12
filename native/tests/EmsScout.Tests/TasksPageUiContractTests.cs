namespace EmsScout.Tests;

public sealed class TasksPageUiContractTests
{
    [Fact]
    public void NarrowTaskLayoutUsesContentHeightAndPageScrolling()
    {
        var root = LocateRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "TasksPage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "TasksPage.xaml.cs"));

        Assert.Contains("x:Name=\"WorkflowScrollViewer\"", page);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", page);
        Assert.DoesNotContain("new GridLength(0.48, GridUnitType.Star)", codeBehind);
        Assert.DoesNotContain("new GridLength(0.52, GridUnitType.Star)", codeBehind);
        Assert.Contains("GridUnitType.Auto", codeBehind);
    }

    [Fact]
    public void TaskProgressSurfacesFailureSummaryWithoutDiagnosticModule()
    {
        var root = LocateRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "TasksPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "CollectionTaskViewModel.cs"));

        Assert.DoesNotContain("诊断详情", page);
        Assert.DoesNotContain("LogsList", page);
        Assert.DoesNotContain("StagesList", page);
        Assert.DoesNotContain("RunLogsExpanded", page);
        Assert.DoesNotContain("日志筛选", page);
        Assert.Contains("HasTaskIssue", page);
        Assert.Contains("TaskSummaryText", page);
        Assert.Contains("失败位置：", viewModel);
        Assert.Contains("失败原因：", viewModel);
        Assert.Contains("已运行：", viewModel);
    }

    [Fact]
    public void AllCollectionFailurePathsUseProgressSummaryAndKeepOnlyProgressParsing()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "CollectionTaskViewModel.cs"));

        Assert.Contains("SetTaskIssueSummary", viewModel);
        Assert.Contains("启动前检查 · EMS 登录态", viewModel);
        Assert.Contains("环境检查", viewModel);
        Assert.Contains("_progressTimer?.Stop()", viewModel);
        Assert.Contains("_dispatcherQueue.TryEnqueue", viewModel);
        Assert.Contains("ApplyProgressEvent(message)", viewModel);
        var environmentSuccessStart = viewModel.IndexOf("EnvironmentText = $\"Node", StringComparison.Ordinal);
        var environmentSuccessEnd = viewModel.IndexOf("catch (Exception ex)", environmentSuccessStart, StringComparison.Ordinal);
        Assert.True(environmentSuccessStart >= 0 && environmentSuccessEnd > environmentSuccessStart);
        var environmentSuccess = viewModel[environmentSuccessStart..environmentSuccessEnd];
        Assert.Contains("HasTaskIssue = false", environmentSuccess);
        Assert.Contains("TaskSummaryText = string.Empty", environmentSuccess);
        Assert.DoesNotContain("SelectedLogSeverity", viewModel);
        Assert.DoesNotContain("LogSeverityOptions", viewModel);
        Assert.DoesNotContain("ObservableCollection<CollectionTaskLogRow>", viewModel);
        Assert.DoesNotContain("FilteredLogs", viewModel);
    }

    [Fact]
    public void RealtimeSnapshotPersistenceUsesImportedRunIdentity()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "CollectionTaskViewModel.cs"));
        var persistStart = viewModel.IndexOf("private async Task PersistRealtimeSnapshotAsync", StringComparison.Ordinal);
        var persistEnd = viewModel.IndexOf("private Task RunRealtimeAuditAsync", persistStart, StringComparison.Ordinal);
        Assert.True(persistStart >= 0 && persistEnd > persistStart);
        var persistMethod = viewModel[persistStart..persistEnd];

        var importScript = File.ReadAllText(Path.Combine(root, "scripts", "import.js"));
        Assert.Contains("History run:", importScript);
        Assert.Contains("_targetRunId", viewModel);
        Assert.DoesNotContain("collectionRunRepository.ListAsync(1", persistMethod);
        Assert.Contains("runId", persistMethod);
    }

    [Fact]
    public void RealtimeCollectionCommandCarriesExplicitRunIdentity()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "CollectionTaskViewModel.cs"));
        var start = viewModel.IndexOf("private Task RunRealtimeDetailsAsync", StringComparison.Ordinal);
        var end = viewModel.IndexOf("private async Task PersistRealtimeSnapshotAsync", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var method = viewModel[start..end];

        Assert.Contains("long runId", method);
        Assert.Contains("args.Add(\"--run-id=\" + runId)", method);
    }

    [Fact]
    public void QueuedProgressEventsCannotOverwriteTerminalTaskState()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "CollectionTaskViewModel.cs"));

        Assert.Contains("_taskGeneration", viewModel);
        Assert.Contains("_acceptProgressEvents", viewModel);
        Assert.Contains("generation != _taskGeneration", viewModel);
        Assert.Contains("if (!_acceptProgressEvents", viewModel);
    }

    [Fact]
    public void QualityStageDoesNotClaimSuccessWhenReportIsMissingOrStale()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "CollectionTaskViewModel.cs"));

        Assert.Contains("_qualityRequiresReview", viewModel);
        Assert.Contains("质量报告缺失或已过期", viewModel);
        Assert.Contains("report.IsStale || report.Summary.IssueCount > 0", viewModel);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
