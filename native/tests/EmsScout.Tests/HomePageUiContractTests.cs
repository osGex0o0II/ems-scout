using System.Text.RegularExpressions;

namespace EmsScout.Tests;

public sealed class HomePageUiContractTests
{
    [Fact]
    public void AreaGroupMetricValuesNavigateWithTheirSpecificFilterRequest()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "HomeViewModel.cs"));

        Assert.Equal(18, Regex.Matches(xaml, "AreaGroupMetric_Click").Count);
        Assert.Contains("OnlineNavigationRequest", xaml);
        Assert.Contains("ModeAbnormalNavigationRequest", xaml);
        Assert.Contains("TemperatureAbnormalNavigationRequest", xaml);
        Assert.Contains("LockOnNavigationRequest", xaml);
        Assert.Contains("LockOffNavigationRequest", xaml);
        Assert.Contains("OpenAreaGroupMetric", codeBehind);
        Assert.Contains("QuickFilter: \"mode_abnormal\"", viewModel);
        Assert.Contains("QuickFilter: \"temperature_abnormal\"", viewModel);
        Assert.Contains("RealtimeLock: \"开启\"", viewModel);
        Assert.Contains("RealtimeLock: \"关闭\"", viewModel);
    }

    [Fact]
    public void UnresolvedCurrentDataDoesNotBorrowLatestBatchTimestamp()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "HomeViewModel.cs"));
        var dataSource = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "DataSourceOption.cs"));

        Assert.Contains("GetCurrentDataSourceAsync", viewModel);
        Assert.Contains("当前来源未确定", dataSource);
        Assert.DoesNotContain("DataSourceOption.Current(catalog.CurrentRun)", viewModel);
        Assert.Contains("CurrentBatchTimestamp = SelectedDataSource?.Label ?? \"当前来源未确定\"", viewModel);

        var dataViewModel = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "DataViewModel.cs"));
        Assert.DoesNotContain("DataSourceOption.Current(catalog.CurrentRun)", dataViewModel);
        Assert.Contains("GetCurrentDataSourceAsync", dataViewModel);
    }

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
        Assert.Contains("InitialClientWidth = MinimumClientWidth", source);
        Assert.Contains("InitialClientHeight = MinimumClientHeight", source);
        Assert.Contains("CurrentPlacementVersion = 2", source);
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
    public void MainWindowMakesThePaneHeaderAreaToggleTheNavigationPane()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("PointerPressed=\"NavView_PointerPressed\"", xaml);
        Assert.Contains("NavView_PointerPressed", codeBehind);
        Assert.Contains("NavView.IsPaneOpen = !NavView.IsPaneOpen", codeBehind);
        Assert.Contains("OpenPaneLength", codeBehind);
        Assert.Contains("e.Handled = true", codeBehind);
    }

    [Fact]
    public void MainWindowMakesThePaneToggleAreaVisiblyInteractive()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml"));
        var app = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "App.xaml"));

        Assert.Contains("PaneToggleButtonStyle=\"{StaticResource EmsPaneToggleButtonStyle}\"", xaml);
        Assert.DoesNotContain("x:Key=\"EmsPaneToggleButtonStyle\"", xaml);
        Assert.Contains("x:Key=\"EmsPaneToggleButtonStyle\"", app);
        Assert.Contains("BasedOn=\"{StaticResource PaneToggleButtonStyle}\"", app);
        Assert.Contains("Property=\"HorizontalAlignment\" Value=\"Stretch\"", app);
        Assert.Contains("Property=\"ToolTipService.ToolTip\" Value=\"切换导航栏\"", app);
        Assert.Contains("Property=\"AutomationProperties.Name\" Value=\"切换导航栏\"", app);
    }

    [Fact]
    public void HomePageUsesTheEnglishBrandAndKeepsTheBatchLabelReadable()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml"));

        Assert.Contains("Text=\"EMS Scout\"", xaml);
        Assert.DoesNotContain("Text=\"EMS 运维工作台\"", xaml);
        Assert.DoesNotContain("Width=\"320\"", xaml);
        Assert.Contains("<ColumnDefinition Width=\"Auto\" />", xaml);
        Assert.Contains("HorizontalAlignment=\"Left\"", xaml);
        Assert.Contains("<ComboBox.ItemTemplate>", xaml);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml);
        Assert.DoesNotContain("DisplayMemberPath=\"DisplayLabel\"", xaml);
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
        Assert.DoesNotContain("PageStatus", xaml);
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
        Assert.DoesNotContain("当前 SQLite 数据", dataSource);
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
    public void HomePageStatusCardsShowPercentagesAndNavigateAsWholeCards()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "HomeViewModel.cs"));

        Assert.Contains("IsItemClickEnabled=\"True\"", xaml);
        Assert.Contains("ItemClick=\"Metrics_ItemClick\"", xaml);
        Assert.Contains("Text=\"{x:Bind Detail}\"", xaml);
        Assert.DoesNotContain("Text=\"{x:Bind ActionText}\"", xaml);
        Assert.DoesNotContain("ActionText", viewModel);
        Assert.DoesNotContain("CanNavigate => NavigationRequest", viewModel);
        Assert.DoesNotContain("ViewModel.AreaGroupsStatus", xaml);
        Assert.Contains("public void OpenMetric(MetricItem? item)", viewModel);
        Assert.Contains("navigationService.NavigateToData(item.NavigationRequest)", viewModel);
        Assert.Contains("CommunicationState: metric.CommunicationState", viewModel);
        Assert.Contains("AreaType: metric.AreaType", viewModel);
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
