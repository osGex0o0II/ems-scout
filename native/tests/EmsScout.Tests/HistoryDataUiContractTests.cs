namespace EmsScout.Tests;

public sealed class HistoryDataUiContractTests
{
    [Fact]
    public void AuditPageExposesHistoryIssuesAndDetailsAsTheOnlyAuditWorkspaces()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AuditPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AuditViewModel.cs"));

        Assert.Contains("Header=\"历史批次\"", xaml);
        Assert.Contains("Header=\"采集问题\"", xaml);
        Assert.Contains("Header=\"问题详情\"", xaml);
        Assert.Contains("完成时间", xaml);
        Assert.Contains("用时", xaml);
        Assert.Contains("范围", xaml);
        Assert.Contains("卡片数量", xaml);
        Assert.Contains("采集模式", xaml);
        Assert.Contains("版本", xaml);
        Assert.Contains("DeleteRun_Click", xaml);
        Assert.Contains("IssueCategories", xaml);
        Assert.Contains("IssueRecords", xaml);
        Assert.Contains("ICollectionIssueService", viewModel);
    }

    [Fact]
    public void AuditPageRemovesComparisonRestoreAndStandaloneAuditActions()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AuditPage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AuditPage.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AuditViewModel.cs"));

        foreach (var removed in new[] { "数据对比", "恢复当前数据", "基础审计", "实时审计", "运行质量审计", "运行实时审计", "刷新对比", "SelectedComparison", "RestoreRunAsync", "CompareCurrentAsync" })
        {
            Assert.DoesNotContain(removed, xaml);
            Assert.DoesNotContain(removed, codeBehind);
            Assert.DoesNotContain(removed, viewModel);
        }
    }

    [Fact]
    public void AuditPageShowsAllIssueDetailFieldsAndBatchDeleteScope()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AuditPage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AuditPage.xaml.cs"));

        Assert.Contains("页面现象 / 证据", xaml);
        Assert.Contains("采集判断 / 原因", xaml);
        Assert.Contains("设备", xaml);
        Assert.Contains("清除筛选", xaml);
        Assert.Contains("SQLite 数据、批次 JSON、NDJSON、质量报告和实时报告", codeBehind);
        Assert.Contains("日志文件不会随批次删除", codeBehind);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "native")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
}
