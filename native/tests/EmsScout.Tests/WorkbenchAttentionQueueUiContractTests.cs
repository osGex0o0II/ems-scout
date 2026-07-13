namespace EmsScout.Tests;

public sealed class WorkbenchAttentionQueueUiContractTests
{
    [Fact]
    public void WorkbenchExposesQueueStateTimeAndAccessibleActions()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml"));

        Assert.Contains("Text=\"状态\"", xaml);
        Assert.Contains("Text=\"更新时间\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"定位待处理事项\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"确认待处理事项\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"忽略待处理事项\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"重新打开待处理事项\"", xaml);
        Assert.Contains("Click=\"AcknowledgeAttention_Click\"", xaml);
        Assert.Contains("Click=\"IgnoreAttention_Click\"", xaml);
        Assert.Contains("Click=\"ReopenAttention_Click\"", xaml);
    }

    [Fact]
    public void IgnoreRequiresDialogReasonAndHistoricalContextDisablesWrites()
    {
        var root = LocateRepositoryRoot();
        var pageCode = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "HomeViewModel.cs"));
        var context = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Services", "DataContextService.cs"));

        Assert.Contains("new ContentDialog", pageCode);
        Assert.Contains("Header = \"忽略原因\"", pageCode);
        Assert.Contains("string.IsNullOrWhiteSpace(reasonBox.Text)", pageCode);
        Assert.Contains("IgnoreAttentionAsync", pageCode);
        Assert.Contains("CanChangeAttentionState => !IsLoading && !DataContext.IsReadOnly", viewModel);
        Assert.Contains("DataContext.IsReadOnly", viewModel);
        Assert.Contains("public bool IsReadOnly", context);
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
