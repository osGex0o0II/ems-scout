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

        Assert.DoesNotContain("Text=\"历史批次\"", xaml);
        Assert.Contains("ViewModel.HistoricalDataSources", xaml);
        Assert.Contains("ViewModel.SelectedDataSource", xaml);
        Assert.Contains("SelectionChanged=\"DataSource_SelectionChanged\"", xaml);
        Assert.DoesNotContain("ViewModel.SourcePath", xaml);
        Assert.Contains("ViewModel.CurrentBatchTimestamp", xaml);
        Assert.DoesNotContain("PlaceholderText=\"选择历史批次\"", xaml);
        Assert.Contains("RunPageOperationAsync(ViewModel.RefreshLatestAsync)", codeBehind);
        Assert.Contains("SelectDataSourceAsync", codeBehind);
        Assert.Contains("ICollectionRunRepository collectionRunRepository", viewModel);
        Assert.Contains("LoadAsync(runId, cancellationToken)", viewModel);
        Assert.DoesNotContain("历史数据批次 #", viewModel);
    }

    [Fact]
    public void MainWindowDeclaresUsableMinimumClientSize()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Services", "WindowSizeConstraint.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("MinimumClientWidth = 1040", source);
        Assert.Contains("MinimumClientHeight = 680", source);
        Assert.Contains("InitialClientWidth = 1160", source);
        Assert.Contains("InitialClientHeight = 760", source);
        Assert.Contains("WindowSizeConstraint.Attach(this)", mainWindow);
        Assert.Contains("AppWindow.Resize(WindowSizeConstraint.ScaleSizeForWindow(this, new SizeInt32(", mainWindow);
        Assert.Contains("WindowSizeConstraint.InitialClientWidth", mainWindow);
        Assert.Contains("WindowSizeConstraint.InitialClientHeight", mainWindow);
        Assert.Contains("WindowSizeConstraint.Restore(this)", mainWindow);
    }

    [Fact]
    public void MainWindowSetsInitialSizeBeforeActivation()
    {
        var root = LocateRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));
        var constructor = mainWindow[..mainWindow.IndexOf("private void NavView_SelectionChanged", StringComparison.Ordinal)];

        Assert.Contains("AppWindow.Resize(WindowSizeConstraint.ScaleSizeForWindow(this, new SizeInt32(", mainWindow);
        Assert.Contains("WindowSizeConstraint.InitialClientWidth", constructor);
        Assert.Contains("WindowSizeConstraint.InitialClientHeight", constructor);
        Assert.DoesNotContain("Activated +=", constructor);
        Assert.DoesNotContain("MainWindow_Activated", mainWindow);
    }

    [Fact]
    public void MainWindowRestoresBeforeApplyingInitialSize()
    {
        var root = LocateRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));

        var normalized = mainWindow.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(
            "WindowSizeConstraint.Restore(this);\n        AppWindow.Resize(WindowSizeConstraint.ScaleSizeForWindow(this, new SizeInt32(\n            WindowSizeConstraint.InitialClientWidth,\n            WindowSizeConstraint.InitialClientHeight)));",
            normalized);
    }

    [Fact]
    public void MainWindowUsesUnifiedCustomTitleBar()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"AppTitleBar\"", xaml);
        Assert.Contains("Background=\"{ThemeResource ApplicationPageBackgroundThemeBrush}\"", xaml);
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

        Assert.DoesNotContain("Text=\"历史批次\"", xaml);
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
        Assert.DoesNotContain("Text=\"历史批次\"", xaml);
        Assert.DoesNotContain("Header=\"历史批次\"", xaml);
        Assert.DoesNotContain("Grid.SetColumn(HeaderActions", codeBehind);
        Assert.DoesNotContain("Grid.SetRow(HeaderActions", codeBehind);
    }

    [Fact]
    public void HomePageUsesVisualLatestStateInsteadOfLatestTextOption()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "HomeViewModel.cs"));
        var dataSource = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataSourceOption.cs"));

        Assert.DoesNotContain("DataSources.Add(new DataSourceOption())", viewModel);
        Assert.Contains("SelectedDataSource = DataSources.FirstOrDefault()", viewModel);
        Assert.DoesNotContain("SelectedDataSource = null", viewModel);
        Assert.Contains("CurrentBatchTimestamp", viewModel);
        Assert.Contains("DisplayLabel => $\"{Label} · {Detail}\"", dataSource);
        Assert.Contains("yyyy-MM-dd HH:mm:ss", dataSource);
        Assert.DoesNotContain("历史 #", dataSource);
        Assert.DoesNotContain("最新采集数据", dataSource);
    }

    [Fact]
    public void HomePageMetricsStayOnOneHorizontalRow()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "HomeViewModel.cs"));
        var metricsStart = xaml.IndexOf("AutomationProperties.Name=\"核心运行指标\"", StringComparison.Ordinal);
        var metricsEnd = xaml.IndexOf("</GridView>", metricsStart, StringComparison.Ordinal);

        Assert.True(metricsStart >= 0);
        Assert.True(metricsEnd > metricsStart);

        var metrics = xaml[metricsStart..metricsEnd];
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"", metrics);
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Disabled\"", metrics);
        Assert.Contains("<ItemsStackPanel Orientation=\"Horizontal\"", metrics);
        Assert.Contains("Orientation=\"Horizontal\"", metrics);
        Assert.Contains("SizeChanged=\"MetricsGrid_SizeChanged\"", metrics);
        Assert.Contains("ContainerContentChanging=\"MetricsGrid_ContainerContentChanging\"", metrics);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"4,0\" />", metrics);
        Assert.DoesNotContain("<ItemsWrapGrid", metrics);
        Assert.DoesNotContain("<Setter Property=\"Width\"", metrics);
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"0\" />", metrics);
        Assert.DoesNotContain("<RowDefinition Height=\"*\"", metrics);
        Assert.Contains("VerticalContentAlignment=\"Top\"", metrics);

        Assert.Contains("private long? _latestDataSourceRunId", viewModel);
        Assert.Contains("_latestDataSourceRunId = DataSources.FirstOrDefault()?.RunId", viewModel);
        Assert.Contains("SelectedDataSource.RunId == _latestDataSourceRunId", viewModel);
    }

    [Fact]
    public void DataPageOffersHistorySelectionAndLatestRefreshInHeader()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));

        Assert.Contains("ViewModel.DataSources", xaml);
        Assert.Contains("ViewModel.SelectedDataSource", xaml);
        Assert.Contains("SelectionChanged=\"DataSource_SelectionChanged\"", xaml);
        Assert.Contains("LatestBatchIndicator", xaml);
        Assert.Contains("<SymbolIcon Symbol=\"Refresh\" />", xaml);
        Assert.DoesNotContain("CurrentDataSourceTimestamp", xaml);
        Assert.DoesNotContain("PlaceholderText=\"选择历史批次\"", xaml);
        Assert.DoesNotContain("最新采集数据", xaml);
        Assert.Contains("SelectDataSourceAsync", codeBehind);
        Assert.Contains("UseLatestDataSourceAsync", codeBehind);
        Assert.Contains("ICollectionRunRepository collectionRunRepository", viewModel);
        Assert.Contains("RunId: SelectedDataSource?.RunId", viewModel);
        Assert.Contains("CurrentDataSourceTimestamp", viewModel);
        Assert.DoesNotContain("DataSources.Add(new DataSourceOption())", viewModel);
    }

    [Fact]
    public void DataPageContainsAnInitializationErrorBoundary()
    {
        var root = LocateRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml.cs"));

        Assert.Contains("try", codeBehind);
        Assert.Contains("catch (Exception ex)", codeBehind);
        Assert.Contains("ViewModel.ReportInitializationError(ex)", codeBehind);
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
