namespace EmsScout.Tests;

public sealed class HomePageUiContractTests
{
    [Fact]
    public void HomePageUsesHistoryBatchSelectorAndLatestRefreshWithoutSourceStatusBlock()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "HomeViewModel.cs"));

        Assert.Contains("Text=\"历史批次\"", xaml);
        Assert.Contains("ViewModel.HistoricalDataSources", xaml);
        Assert.Contains("ViewModel.SelectedDataSource", xaml);
        Assert.Contains("SelectionChanged=\"DataSource_SelectionChanged\"", xaml);
        Assert.DoesNotContain("ViewModel.SourcePath", xaml);
        Assert.DoesNotContain("ViewModel.SourceUpdatedAt", xaml);
        Assert.Contains("ViewModel.RefreshLatestAsync()", codeBehind);
        Assert.Contains("SelectDataSourceAsync", codeBehind);
        Assert.Contains("ICollectionRunRepository collectionRunRepository", viewModel);
        Assert.Contains("LoadAsync(runId, cancellationToken)", viewModel);
    }

    [Fact]
    public void MainWindowDeclaresUsableMinimumClientSize()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Services", "WindowSizeConstraint.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("MinimumClientWidth = 1200", source);
        Assert.Contains("MinimumClientHeight = 800", source);
        Assert.Contains("WindowSizeConstraint.Attach(this)", mainWindow);
        Assert.Contains("Activated += MainWindow_Activated", mainWindow);
        Assert.Contains("AppWindow.Resize(WindowSizeConstraint.ScaleSizeForWindow(this, new SizeInt32(1280, 820)))", mainWindow);
        Assert.Contains("WindowSizeConstraint.Restore(this)", mainWindow);
    }

    [Fact]
    public void MainWindowSetsInitialSizeSynchronouslyWhenActivated()
    {
        var root = LocateRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("AppWindow.Resize(WindowSizeConstraint.ScaleSizeForWindow(this, new SizeInt32(1280, 820)))", mainWindow);
        Assert.Contains("Activated += MainWindow_Activated", mainWindow);
        Assert.DoesNotContain("DispatcherQueue.TryEnqueue", mainWindow);
    }

    [Fact]
    public void MainWindowRestoresAfterApplyingInitialSize()
    {
        var root = LocateRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));

        var normalized = mainWindow.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(
            "AppWindow.Resize(WindowSizeConstraint.ScaleSizeForWindow(this, new SizeInt32(1280, 820)));\n        WindowSizeConstraint.Restore(this);",
            normalized);
    }

    [Fact]
    public void MainWindowUsesUnifiedCustomTitleBar()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"AppTitleBar\"", xaml);
        Assert.Contains("Background=\"{ThemeResource LayerFillColorDefaultBrush}\"", xaml);
        Assert.Contains("x:Name=\"NavView\"", xaml);
        Assert.Contains("ExtendsContentIntoTitleBar = true", codeBehind);
        Assert.Contains("SetTitleBar(AppTitleBar)", codeBehind);
    }

    [Fact]
    public void HomePageUsesVisualLatestIndicatorHistorySelectorAndNativeRefreshIcon()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "HomeViewModel.cs"));

        Assert.Contains("Text=\"历史批次\"", xaml);
        Assert.Contains("LatestBatchIndicator", xaml);
        Assert.Contains("<Ellipse", xaml);
        Assert.Contains("<SymbolIcon Symbol=\"Refresh\" />", xaml);
        Assert.Contains("Padding=\"0\"", xaml);
        Assert.Contains("VerticalAlignment=\"Center\"", xaml);
        Assert.DoesNotContain("Header=\"数据批次\"", xaml);
        Assert.DoesNotContain("Glyph=\"&#xE72C;\"", xaml);
        Assert.Contains("LatestBatch_Click", codeBehind);
        Assert.Contains("UseLatestDataSourceAsync", codeBehind);
        Assert.Contains("HistoricalDataSources", viewModel);
    }

    [Fact]
    public void HomePageKeepsHistoryBatchActionsInTheTopRightHeaderColumn()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml.cs"));

        Assert.Contains("x:Name=\"HeaderActions\"", xaml);
        Assert.Contains("Grid.Column=\"1\"", xaml);
        Assert.Contains("Text=\"历史批次\"", xaml);
        Assert.DoesNotContain("Header=\"历史批次\"", xaml);
        Assert.DoesNotContain("Grid.SetColumn(HeaderActions", codeBehind);
        Assert.DoesNotContain("Grid.SetRow(HeaderActions", codeBehind);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "out")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
}
