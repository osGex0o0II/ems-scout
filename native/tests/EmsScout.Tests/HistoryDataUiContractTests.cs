namespace EmsScout.Tests;

public sealed class HistoryDataUiContractTests
{
    [Fact]
    public void AuditPageMakesHistoryTheDefaultWorkspace()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AuditPage.xaml"));

        Assert.Contains("x:Name=\"WorkspacePivot\"", xaml);
        Assert.Contains("SelectedIndex=\"0\"", xaml);
        Assert.Contains("Header=\"历史数据\"", xaml);
        Assert.Contains("Header=\"数据对比\"", xaml);
        Assert.Contains("Header=\"质量审计\"", xaml);
        Assert.Contains("FilteredRuns", xaml);
        Assert.Contains("HistoryBuildingOptions", xaml);
        Assert.Contains("ApplyHistoryFilterCommand", xaml);
    }

    [Fact]
    public void AuditPageDoesNotKeepNestedDualAuditScrollLayout()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AuditPage.xaml"));

        Assert.DoesNotContain("<ScrollViewer VerticalScrollBarVisibility=\"Auto\">", xaml);
        Assert.DoesNotContain("<ScrollViewer Grid.Column", xaml);
        Assert.Contains("SelectedRunDetail", xaml);
        Assert.Contains("SelectedBuildingDifferences", xaml);
    }

    [Fact]
    public void AuditPageRequiresComparisonBeforeRestore()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AuditPage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AuditPage.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AuditViewModel.cs"));

        Assert.Contains("LoadSelectedComparisonCommand", xaml);
        Assert.Contains("RestoreRun_Click", codeBehind);
        Assert.Contains("SelectedComparison is { IsRestorable: true }", viewModel);
        Assert.Contains("恢复前会自动备份当前数据", codeBehind);
    }

    [Fact]
    public void ComparisonPageProvidesBatchSelectionAndRefreshesTheDefaultSelection()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AuditPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AuditViewModel.cs"));

        Assert.Contains("AutomationProperties.Name=\"选择对比批次\"", xaml);
        Assert.Contains("ItemsSource=\"{x:Bind ViewModel.FilteredRuns, Mode=OneWay}\"", xaml);
        Assert.Contains("SelectedItem=\"{x:Bind ViewModel.SelectedRun, Mode=TwoWay}\"", xaml);
        Assert.Contains("SelectedRun = selectedId.HasValue", viewModel);
        Assert.Contains("FilteredRuns.FirstOrDefault", viewModel);
    }

    [Fact]
    public void ComparisonBatchPickerUsesContentDrivenWidth()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AuditPage.xaml"));

        var pickerStart = xaml.IndexOf("AutomationProperties.Name=\"选择对比批次\"", StringComparison.Ordinal);
        var pickerEnd = xaml.IndexOf(" />", pickerStart, StringComparison.Ordinal);
        Assert.True(pickerStart >= 0);
        Assert.True(pickerEnd > pickerStart);

        var picker = xaml[pickerStart..pickerEnd];
        Assert.DoesNotContain("Width=\"460\"", picker);
        Assert.Contains("HorizontalAlignment=\"Left\"", picker);
        Assert.Contains("PlaceholderText=\"没有可用历史批次\"", picker);
    }

    [Fact]
    public void ComparisonSelectionUpdatesRestoreStateAndRejectsStaleResults()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AuditViewModel.cs"));

        Assert.Contains("[NotifyPropertyChangedFor(nameof(CanRestoreSelectedRun))]", viewModel);
        Assert.Contains("SelectedRun?.Id != runId", viewModel);
        Assert.Contains("CancellationTokenSource", viewModel);
        Assert.Contains("!run.IsCurrent", viewModel);
    }

    [Fact]
    public void SelectedHistoryBatchShowsMissingQualityReportAsBatchSpecificState()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AuditViewModel.cs"));

        Assert.Contains("批次 #{SelectedRun.Id} 没有质量报告", viewModel);
        Assert.Contains("该批次没有可关联的质量审计结果", viewModel);
        Assert.Contains("reportRunId != runId", File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Infrastructure", "Quality", "JsonQualityAuditService.cs")));
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
