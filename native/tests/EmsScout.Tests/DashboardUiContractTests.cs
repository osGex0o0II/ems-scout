namespace EmsScout.Tests;

public sealed class DashboardUiContractTests
{
    [Fact]
    public void DashboardUsesAnIntentionalHistoryPlaceholderAndUnifiedSurface()
    {
        var root = LocateRepositoryRoot();
        var home = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "HomePage.xaml"));
        var mainWindow = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "MainWindow.xaml"));

        Assert.Contains("PlaceholderText=\"{x:Bind ViewModel.CurrentBatchTimestamp, Mode=OneWay}\"", home);
        Assert.DoesNotContain("ViewModel.PageStatus", home);
        Assert.DoesNotContain("Text=\"历史批次\"", home);
        Assert.Contains("Background=\"{ThemeResource ApplicationPageBackgroundThemeBrush}\"", mainWindow);
        Assert.DoesNotContain("PaneBackground=", mainWindow);
    }

    [Fact]
    public void DashboardRemovesDuplicatedRiskBannerAndPublicMetric()
    {
        var root = LocateRepositoryRoot();
        var home = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "HomePage.xaml"));
        var overviewService = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Application",
            "DashboardOverviewService.cs"));

        Assert.DoesNotContain("<InfoBar", home);
        Assert.DoesNotContain("OverviewStatusMessage", home);
        Assert.DoesNotContain("new OverviewMetric(\"公区空调\"", overviewService);
        Assert.DoesNotContain("LoadRiskContextAsync", overviewService);
        Assert.DoesNotContain("IQualityAuditService", overviewService);
    }

    [Fact]
    public void DashboardHistorySelectorDoesNotReserveHiddenProgressSpace()
    {
        var root = LocateRepositoryRoot();
        var home = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "HomePage.xaml"));
        var headerStart = home.IndexOf("x:Name=\"HeaderActions\"", StringComparison.Ordinal);
        var headerEnd = home.IndexOf("</Grid>\r\n            </Grid>", headerStart, StringComparison.Ordinal);
        if (headerEnd < 0)
        {
            headerEnd = home.IndexOf("</Grid>\n            </Grid>", headerStart, StringComparison.Ordinal);
        }
        var header = home[headerStart..headerEnd];

        Assert.True(headerStart >= 0);
        Assert.True(headerEnd > headerStart);
        Assert.Contains("ColumnSpacing=\"6\"", header);
        Assert.DoesNotContain("<ColumnDefinition Width=\"20\"", header);
        Assert.DoesNotContain("Width=\"270\"", header);
        Assert.DoesNotContain("Width=\"320\"", header);
        Assert.Contains("<ColumnDefinition Width=\"Auto\" />", header);
        Assert.Contains("HorizontalAlignment=\"Left\"", header);
    }

    [Fact]
    public void DashboardAreaGroupWideTableUsesSharedStretchAndCompactFallback()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "HomePage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "HomePage.xaml.cs"));
        var wideStart = xaml.IndexOf("x:Name=\"WideAreaGroupList\"", StringComparison.Ordinal);
        var wideEnd = xaml.IndexOf("</ListView>", wideStart, StringComparison.Ordinal);

        Assert.True(wideStart >= 0);
        Assert.True(wideEnd > wideStart);

        var wide = xaml[wideStart..wideEnd];
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"", wide);
        Assert.Contains("HorizontalContentAlignment=\"Stretch\"", wide);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"0\" />", wide);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", wide);
        Assert.Contains("ColumnSpacing=\"8\"", wide);
        Assert.Contains("<ColumnDefinition Width=\"72\"", wide);
        Assert.Contains("<ColumnDefinition Width=\"96\"", wide);
        Assert.Contains("e.NewSize.Width < 1250", codeBehind);
    }

    [Fact]
    public void DashboardAreaGroupCopyDistinguishesAutomaticAndCustomGroups()
    {
        var root = LocateRepositoryRoot();
        var homeViewModel = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "HomeViewModel.cs"));
        var homeViewModelTail = homeViewModel[
            homeViewModel.IndexOf("private void ApplyAreaGroupsStatus", StringComparison.Ordinal)..];
        var row = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "HomeViewModel.cs"));

        Assert.Contains("启用区域组", homeViewModelTail);
        Assert.Contains("summary.AreaType", row);
        Assert.Contains("summary.AreaType)", row);
    }
    [Fact]
    public void DashboardExposesUserAreaGroupOperationsAndStates()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "HomePage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "HomePage.xaml.cs"));

        Assert.Contains("我的区域组", xaml);
        Assert.Contains("ViewModel.AreaGroups", xaml);
        Assert.Contains("Text=\"设备\"", xaml);
        Assert.Contains("Text=\"在线\"", xaml);
        Assert.Contains("Text=\"离线\"", xaml);
        Assert.Contains("Text=\"开机\"", xaml);
        Assert.Contains("Text=\"关机\"", xaml);
        Assert.Contains("Total", xaml);
        Assert.Contains("Online", xaml);
        Assert.Contains("Offline", xaml);
        Assert.Contains("Running", xaml);
        Assert.Contains("Stopped", xaml);
        Assert.Contains("模式异常", xaml);
        Assert.Contains("温度异常", xaml);
        Assert.Contains("集控锁开", xaml);
        Assert.Contains("集控锁关", xaml);
        Assert.Contains("ModeAbnormal", xaml);
        Assert.Contains("TemperatureAbnormal", xaml);
        Assert.Contains("LockOn", xaml);
        Assert.Contains("LockOff", xaml);
        Assert.DoesNotContain("Text=\"公区开机\"", xaml);
        Assert.DoesNotContain("Text=\"公区关机\"", xaml);
        Assert.DoesNotContain("Text=\"公区\"", xaml);
        Assert.DoesNotContain("Text=\"非公区\"", xaml);
        Assert.DoesNotContain("PublicTotal", xaml);
        Assert.DoesNotContain("PrivateTotal", xaml);
        Assert.Contains("AreaGroupsEmptyVisibility", xaml);
        Assert.Contains("AreaGroupsErrorVisibility", xaml);
        Assert.Contains("SizeChanged=\"Page_SizeChanged\"", xaml);
        Assert.Contains("WideAreaGroupList", xaml);
        Assert.Contains("CompactAreaGroupList", xaml);
        Assert.Contains("e.NewSize.Width < 1250", codeBehind);
        Assert.DoesNotContain("Grid.SetColumn(HeaderActions", codeBehind);
        Assert.DoesNotContain("Grid.SetRow(HeaderActions", codeBehind);
        Assert.Contains("AutomationProperties.Name=\"刷新工作台\"", xaml);
        Assert.Contains("<KeyboardAccelerator Key=\"F5\" />", xaml);
        Assert.Contains("AreaGroups_ItemClick", codeBehind);
        Assert.Contains("OpenAreaGroups_Click", codeBehind);
    }

    [Fact]
    public void DashboardCompactAreaGroupsUseSingleStatsRow()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "HomePage.xaml"));
        var compactStart = xaml.IndexOf("x:Name=\"CompactAreaGroupList\"", StringComparison.Ordinal);
        var compactEnd = xaml.IndexOf("</ListView>", compactStart, StringComparison.Ordinal);

        Assert.True(compactStart >= 0);
        Assert.True(compactEnd > compactStart);

        var compact = xaml[compactStart..compactEnd];
        Assert.Contains("MinHeight=\"78\"", compact);
        Assert.Contains("Padding=\"12,7\"", compact);
        Assert.Contains("RowSpacing=\"4\"", compact);
        Assert.Contains("ColumnSpacing=\"4\"", compact);
        Assert.DoesNotContain("Grid.Row=\"2\"", compact);
        Assert.Contains("模式异常", compact);
        Assert.Contains("温度异常", compact);
        Assert.Contains("集控锁开", compact);
        Assert.Contains("集控锁关", compact);
        Assert.Contains("Text=\"在线\"", compact);
        Assert.Contains("Text=\"离线\"", compact);
        Assert.Contains("Text=\"开机\"", compact);
        Assert.Contains("Text=\"关机\"", compact);
    }

    [Fact]
    public void DashboardRendersGroupNotesInsteadOfPriorityAndStateSummary()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "HomePage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "HomePage.xaml.cs"));
        var audit = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "AuditPage.xaml"));

        Assert.DoesNotContain("优先处理", xaml);
        Assert.Equal(2, xaml.Split("Text=\"{x:Bind Description}\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("{x:Bind Priority}", xaml);
        Assert.DoesNotContain("{x:Bind StateText}", xaml);
        Assert.Contains("public string Description", File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "HomeViewModel.cs")));
        Assert.DoesNotContain("? \"自定义区域\"", File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "HomeViewModel.cs")));
        Assert.DoesNotContain("RiskPanel", xaml);
        Assert.DoesNotContain("Risks_ItemClick", codeBehind);
        Assert.Contains("审计中心", audit);
        Assert.DoesNotContain("运行质量审计", audit);
        Assert.DoesNotContain("运行实时审计", audit);
    }

    [Fact]
    public void DashboardAreaGroupNavigationOpensPrefilteredDataManagement()
    {
        var root = LocateRepositoryRoot();
        var homeViewModel = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "HomeViewModel.cs"));
        var navigation = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Services",
            "INavigationService.cs"));
        Assert.Contains("navigationService.NavigateToData(row.NavigationRequest)", homeViewModel);
        Assert.Contains("long? AreaGroupId = null", navigation);
        Assert.Contains("void NavigateToGroups(long? groupId = null)", navigation);
    }

    [Fact]
    public void DashboardAreaGroupRowsUseAnomalyAndLockMetrics()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "HomePage.xaml"));
        var row = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "HomeViewModel.cs"));

        Assert.Contains("模式异常", xaml);
        Assert.Contains("温度异常", xaml);
        Assert.Contains("集控锁开", xaml);
        Assert.Contains("集控锁关", xaml);
        Assert.Contains("ModeAbnormal", row);
        Assert.Contains("TemperatureAbnormal", row);
        Assert.Contains("LockOn", row);
        Assert.Contains("LockOff", row);
        Assert.DoesNotContain("公区开机", xaml);
        Assert.DoesNotContain("非公区", xaml);
    }

    [Fact]
    public void DashboardBaseAnomalyMetricsRemainVisibleWithoutRealtimeDetails()
    {
        var root = LocateRepositoryRoot();
        var row = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "HomeViewModel.cs"));

        Assert.Contains("public string ModeAbnormal { get; } = summary.ModeAbnormal.ToString(\"N0\");", row);
        Assert.Contains("public string TemperatureAbnormal { get; } = summary.TemperatureAbnormal.ToString(\"N0\");", row);
    }

    [Fact]
    public void TotalMetricCardNavigatesToTheSelectedDataBatch()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "HomeViewModel.cs"));
        var navigation = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Services",
            "INavigationService.cs"));

        Assert.Contains("new MetricItem(metric, runId)", viewModel);
        Assert.Contains("RunId: runId", viewModel);
        Assert.Contains("long? RunId = null", navigation);
    }

    [Fact]
    public void AreaGroupsPageUsesGenericAreaGroupPresentation()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "AreasPage.xaml"));
        var row = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "GroupSummaryRow.cs"));

        Assert.Contains("区域组规则", xaml);
        Assert.Contains("GroupKey", row);
        Assert.Contains("StateLabel", row);
        Assert.DoesNotContain("不可编辑", row);
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
