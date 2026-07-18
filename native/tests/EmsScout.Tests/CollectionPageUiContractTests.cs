namespace EmsScout.Tests;

public sealed class CollectionPageUiContractTests
{
    [Fact]
    public void CollectionBrowserIsAnExplicitToggleLockedDuringCollection()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "TasksPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "CollectionTaskViewModel.cs"));

        Assert.Contains("ToggleCollectionBrowserCommand", xaml);
        Assert.Contains("StartPrimaryButtonVisibility", xaml);
        Assert.Contains("StartSecondaryButtonVisibility", xaml);
        Assert.Contains("CollectionBrowserPrimaryButtonVisibility", xaml);
        Assert.Contains("CollectionBrowserSecondaryButtonVisibility", xaml);
        Assert.Contains("public Visibility StartPrimaryButtonVisibility => CanStartTask ? Visibility.Visible : Visibility.Collapsed;", viewModel);
        Assert.Contains("public Visibility CollectionBrowserPrimaryButtonVisibility => ShouldEmphasizeCollectionBrowser", viewModel);
        Assert.Contains("private bool ShouldEmphasizeCollectionBrowser", viewModel);
        Assert.Contains("!IsCollectionBrowserOpen", viewModel);
        Assert.Contains("(plan.RunEnumeration || plan.RunRealtimeDetails)", viewModel);
        Assert.Contains("!_cdpReachable", viewModel);
        Assert.Contains("NotifyActionButtonPriorityChanged", viewModel);
        Assert.Contains("private bool CanToggleCollectionBrowser() => !IsRunning && !IsCheckingEnvironment;", viewModel);
        Assert.Contains("nameof(ToggleCollectionBrowserCommand)", viewModel);
        Assert.Contains("CollectionBrowserActionText = \"关闭采集浏览器\"", viewModel);
        Assert.Contains("采集期间不能关闭浏览器", viewModel);
        Assert.Contains("private bool TryDisposeOwnedBrowser()", viewModel);
        Assert.Contains("ClearLogsCommand", xaml);
        Assert.Contains("ToggleLogsExpandedCommand", xaml);
        Assert.Contains("IsLogsExpanded", viewModel);
        Assert.Contains("AutomationProperties.Name=\"清空运行记录\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"展开运行记录\"", xaml);
        Assert.Contains("Logs.Clear()", viewModel);
        Assert.Contains("public bool IsLogsExpanded", viewModel);
        Assert.Contains("OnPropertyChanged(nameof(CanClearLogs))", viewModel);
        Assert.Contains("Grid.RowSpan=\"{x:Bind ViewModel.LogsGridRowSpan, Mode=OneWay}\"", xaml);
        Assert.Contains("x:Name=\"SetupColumn\" MinWidth=\"0\"", xaml);
        Assert.Contains("x:Name=\"ExecutionColumn\" MinWidth=\"0\"", xaml);
        Assert.Contains("HorizontalScrollMode=\"Disabled\"", xaml);
        Assert.Contains("其他楼栋保\\u2060持不变", viewModel);
        Assert.Contains("CurrentDataImpactVisibility", xaml);
        Assert.Contains("public Visibility CurrentDataImpactVisibility", viewModel);
        Assert.Contains("selected.Count == Buildings.Count", viewModel);
        Assert.Contains("已选择全部 {selected.Count} 栋楼", viewModel);
        Assert.Contains("private bool AllBuildingsSelected", viewModel);
        Assert.Contains("Buildings.Count > 0 && Buildings.All(building => building.IsSelected)", viewModel);
        Assert.Contains("OnPropertyChanged(nameof(CurrentDataImpactVisibility))", viewModel);
        var impactTextIndex = xaml.IndexOf("CurrentDataImpactText", StringComparison.Ordinal);
        Assert.True(impactTextIndex >= 0);
        var impactTextBlock = xaml.Substring(impactTextIndex, Math.Min(220, xaml.Length - impactTextIndex));
        Assert.Contains("TextWrapping=\"Wrap\"", impactTextBlock);
        Assert.Contains("--edge-skip-compat-layer-relaunch", viewModel);
        Assert.DoesNotContain("--auto-launch", viewModel);
        Assert.DoesNotContain("DefaultCollectionMode.Equals(\"auto-launch\"", viewModel);
        Assert.Contains("var mode = \"--edge\";", viewModel);
        Assert.Contains("var browserMode = \"cdp\";", viewModel);
        Assert.Contains("[\"REALTIME_BROWSER_MODE\"] = \"cdp\"", viewModel);
    }

    [Fact]
    public void CollectionPageKeepsTheDailyWorkflowFocused()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "TasksPage.xaml"));
        var settingsXaml = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "SettingsPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "CollectionTaskViewModel.cs"));
        var modeCatalog = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Application",
            "Collection",
            "CollectionTaskModes.cs"));

        Assert.Contains("new(\"1号\", \"1号楼\", true)", viewModel);
        Assert.Contains("new(\"6号\", \"6号楼\", true)", viewModel);
        Assert.DoesNotContain("科研综合楼", viewModel);
        Assert.DoesNotContain("CollectionTaskModeValues.CollectImport or", viewModel);
        Assert.Contains("CollectionTaskModeValues.Full", viewModel);
        Assert.Contains("CollectionTaskModeValues.Recapture", viewModel);
        Assert.Contains("mode => mode.Value == CollectionTaskModeValues.Full", viewModel);
        Assert.Contains("new(CollectionTaskModeValues.Full, \"采集\"", modeCatalog);
        Assert.Contains("new(CollectionTaskModeValues.Recapture, \"补采指定区域\"", modeCatalog);
        Assert.DoesNotContain("\"标准采集\"", modeCatalog);
        Assert.Contains("[NotifyCanExecuteChangedFor(nameof(SelectAllBuildingsCommand))]", viewModel);
        Assert.Contains("[NotifyCanExecuteChangedFor(nameof(ClearBuildingSelectionCommand))]", viewModel);
        Assert.Contains("public bool IsRecaptureMode", viewModel);
        Assert.DoesNotContain("x:Name=\"RecaptureLocationInput\"", xaml);
        Assert.Contains("x:Name=\"RecaptureBuildingSelector\"", xaml);
        Assert.Contains("x:Name=\"RecaptureSeatSelector\"", xaml);
        Assert.Contains("x:Name=\"RecaptureFloorSelector\"", xaml);
        Assert.Contains("SelectedRecaptureBuilding", xaml);
        Assert.Contains("SelectedRecaptureSeat", xaml);
        Assert.Contains("SelectedRecaptureFloor", xaml);
        Assert.Contains("当前数据中没有可用的补采位置，请先完成一次采集", viewModel);
        Assert.Contains("Text=\"高级设置\"", xaml);
        Assert.Contains("Text=\"采集方式、补采和诊断选项\"", xaml);
        Assert.Contains("PreflightDetailsHeader", xaml);
        Assert.Contains("x:Load=\"{x:Bind ViewModel.IsRecaptureMode", xaml);
        Assert.DoesNotContain("Header=\"日志类别\"", xaml);
        Assert.DoesNotContain("Header=\"实时批量设备数\"", xaml);
        Assert.DoesNotContain("Header=\"跳过设备清单检查\"", xaml);
        Assert.DoesNotContain("默认采集模式", settingsXaml);
        Assert.DoesNotContain("自动启动 Edge", settingsXaml);

        Assert.Contains("CollectionTaskModeValues.ValidateOnly", modeCatalog);
        Assert.Contains("CollectionTaskModeValues.ImportOnly", modeCatalog);
        Assert.Contains("CollectionTaskModeValues.QualityOnly", modeCatalog);
        Assert.Contains("CollectionTaskModeValues.RealtimeAuditOnly", modeCatalog);
    }

    [Fact]
    public void PreflightChecksStayVisibleAndAnimateStatusChanges()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "TasksPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "CollectionTaskViewModel.cs"));
        var row = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "PreflightCheckRow.cs"));

        Assert.Contains("x:Name=\"PreflightStatusPanel\"", xaml);
        Assert.DoesNotContain("IsExpanded=\"False\"", xaml);
        Assert.Contains("PreflightProgressValue", xaml);
        Assert.Contains("EntranceThemeTransition", xaml);
        Assert.Contains("RepositionThemeTransition", xaml);
        Assert.Contains("Foreground=\"{Binding IconForeground}\"", xaml);
        Assert.Contains("Background=\"{ThemeResource SubtleFillColorSecondaryBrush}\"", xaml);
        Assert.DoesNotContain("Background=\"{Binding StatusBackground}\"", xaml);
        Assert.Contains("public partial double PreflightProgressValue", viewModel);
        Assert.DoesNotContain("检查详情（", viewModel);
        Assert.Contains("public bool IsPassed", row);
        Assert.Contains("public bool IsBlocked", row);
        Assert.Contains("public bool IsPending", row);
        Assert.Contains("public Brush IconForeground", row);
        Assert.DoesNotContain("public Brush StatusBackground", row);
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
