namespace EmsScout.Tests;

public sealed class SettingsUiContractTests
{
    [Fact]
    public void SettingsModelExposesTheNewUserPreferences()
    {
        var root = LocateRepositoryRoot();
        var settings = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Application", "Settings", "AppSettings.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "SettingsViewModel.cs"));

        Assert.Contains("PageTransitionStyle", settings);
        Assert.DoesNotContain("Language", settings);
        Assert.Contains("SaveWindowPlacement", settings);
        Assert.Contains("StartMinimized", settings);
        Assert.Contains("LaunchAtLogin", settings);
        Assert.Contains("ShowInSendTo", settings);
        Assert.Contains("DashboardNormalMode", settings);
        Assert.Contains("DashboardTemperatureMin", settings);
        Assert.Contains("DashboardTemperatureMax", settings);
        Assert.Contains("PageTransitionStyleIndex", viewModel);
        Assert.DoesNotContain("LanguageIndex", viewModel);
        Assert.Contains("SaveWindowPlacement", viewModel);
        Assert.Contains("StartMinimized", viewModel);
        Assert.Contains("LaunchAtLogin", viewModel);
        Assert.Contains("ShowInSendTo", viewModel);
        Assert.Contains("DashboardNormalMode", viewModel);
        Assert.Contains("DashboardTemperatureMin", viewModel);
        Assert.Contains("DashboardTemperatureMax", viewModel);
    }

    [Fact]
    public void SettingsPageGroupsAppearanceStartupAndSystemIntegrationOptions()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "SettingsPage.xaml"));

        Assert.Contains("Tag=\"behavior\"", xaml);
        Assert.DoesNotContain("Tag=\"integration\"", xaml);
        Assert.Contains("Header=\"页面切换\"", xaml);
        Assert.DoesNotContain("Header=\"语言\"", xaml);
        Assert.Contains("Header=\"保存窗口位置\"", xaml);
        Assert.Contains("Header=\"登录系统后自动启动\"", xaml);
        Assert.Contains("Header=\"在\u201c发送到\u201d菜单中显示 EMS Scout\"", xaml);
        Assert.DoesNotContain("Header=\"减少动效\"", xaml);
        Assert.DoesNotContain("IntegrationSection", xaml);
        Assert.Contains("OpenPaneLength=\"176\"", xaml);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", xaml);
        Assert.Contains("HorizontalContentAlignment=\"Stretch\"", xaml);
        Assert.DoesNotContain("MaxWidth=\"860\"", xaml);
        Assert.DoesNotContain("MaxWidth=\"820\"", xaml);
        Assert.Contains("x:Name=\"DataTableSection\"", xaml);
        Assert.Contains("MaxWidth=\"560\"", xaml);
        Assert.Contains("MaxWidth=\"420\"", xaml);

        var toggleSwitches = System.Text.RegularExpressions.Regex.Matches(
            xaml,
            @"<ToggleSwitch\b[^>]*>");
        Assert.NotEmpty(toggleSwitches);
        Assert.All(toggleSwitches, toggle =>
        {
            Assert.Contains("OnContent=\"开\"", toggle.Value);
            Assert.Contains("OffContent=\"关\"", toggle.Value);
        });
    }

    [Fact]
    public void CollectionParametersLiveInCollectionSettingsAndTaskPageDoesNotRenderLogs()
    {
        var root = LocateRepositoryRoot();
        var settings = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Application", "Settings", "AppSettings.cs"));
        var settingsViewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "SettingsViewModel.cs"));
        var settingsPage = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "SettingsPage.xaml"));
        var tasksPage = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "TasksPage.xaml"));
        var taskViewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "CollectionTaskViewModel.cs"));
        var realtimeAllScript = File.ReadAllText(Path.Combine(root, "scripts", "collect-realtime-all-batch.js"));
        var realtimeBatchScript = File.ReadAllText(Path.Combine(root, "scripts", "collect-building-realtime-batch.js"));

        Assert.Contains("RealtimeBatchSize", settings);
        Assert.Contains("RealtimeReopenEvery", settings);
        Assert.Contains("RealtimeTimeoutMs", settings);
        Assert.Contains("RealtimeBatchSize", settingsViewModel);
        Assert.Contains("RealtimeReopenEvery", settingsViewModel);
        Assert.Contains("RealtimeTimeoutMs", settingsViewModel);
        Assert.Contains("Tag=\"collection\"", settingsPage);
        Assert.Contains("x:Name=\"CollectionSection\"", settingsPage);
        Assert.DoesNotContain("采集模式", settingsPage);
        Assert.DoesNotContain("稳定完整", settingsPage);
        Assert.DoesNotContain("快速批量", settingsPage);
        Assert.Contains("实时采集参数", settingsPage);
        Assert.Contains("实时批量设备数", settingsPage);
        Assert.Contains("单批超时（毫秒）", settingsPage);
        Assert.Contains("设备批次重开间隔", settingsPage);
        var advancedStart = settingsPage.IndexOf("x:Name=\"AdvancedSection\"", StringComparison.Ordinal);
        var advancedEnd = settingsPage.IndexOf("</StackPanel>", advancedStart, StringComparison.Ordinal);
        Assert.True(advancedStart >= 0 && advancedEnd > advancedStart);
        var advancedSection = settingsPage[advancedStart..advancedEnd];
        Assert.DoesNotContain("RealtimeBatchSize", advancedSection);
        Assert.DoesNotContain("RealtimeCollectionStrategy", advancedSection);
        Assert.Contains("总览异常判定", settingsPage);
        Assert.Contains("正常运行模式", settingsPage);
        Assert.Contains("Header=\"设定温度下限（℃）\"", settingsPage);
        Assert.Contains("Header=\"设定温度上限（℃）\"", settingsPage);
        Assert.DoesNotContain("高级设置", tasksPage);
        Assert.DoesNotContain("日志筛选", tasksPage);
        Assert.DoesNotContain("RealtimeBatchSizeOptions", tasksPage);
        Assert.DoesNotContain("RealtimeBatchSizeOptions", taskViewModel);
        Assert.Contains("settings.RealtimeBatchSize", taskViewModel);
        Assert.Contains("settings.RealtimeReopenEvery", taskViewModel);
        Assert.Contains("settings.RealtimeTimeoutMs", taskViewModel);
        Assert.DoesNotContain("RealtimeCollectionStrategyIndex", settingsViewModel);
        Assert.Contains("--strategy=", taskViewModel);
        Assert.Contains("FastRealtimeStrategy", taskViewModel);
        Assert.Contains("COLLECTION_STRATEGY", realtimeAllScript);
        Assert.Contains("enum_full_v5.json", realtimeAllScript);
        Assert.Contains("COLLECTION_STRATEGY", realtimeBatchScript);
        Assert.Contains("Enum snapshot not found for fast batch", realtimeBatchScript);
    }

    [Fact]
    public void MainWindowUsesOneCentralizedPageTransitionPolicy()
    {
        var root = LocateRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));
        var uiSettings = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Services", "AppUiSettingsService.cs"));

        Assert.Contains("PageTransitionStyle", mainWindow);
        Assert.Contains("ReduceMotion", mainWindow);
        Assert.Contains("EntranceNavigationTransitionInfo", mainWindow);
        Assert.Contains("SlideNavigationTransitionInfo", mainWindow);
        Assert.Contains("SuppressNavigationTransitionInfo", mainWindow);
        Assert.Contains("PageTransitionStyle", uiSettings);
    }

    [Fact]
    public void PackageDeclaresStartupTaskAndRuntimeIntegrations()
    {
        var root = LocateRepositoryRoot();
        var manifest = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Package.appxmanifest"));
        var app = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "App.xaml.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("windows.startupTask", manifest);
        Assert.Contains("Executable=\"EmsScout.Desktop.exe\"", manifest);
        Assert.Contains("EntryPoint=\"Windows.FullTrustApplication\"", manifest);
        Assert.Contains("StartupTask", app);
        Assert.Contains("SaveWindowPlacement", mainWindow);
        Assert.Contains("SendToShortcutService", app);
    }

    [Fact]
    public void WindowPlacementRestoresPhysicalPixelsWithoutApplyingDpiTwice()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Services", "WindowSizeConstraint.cs"));

        Assert.Contains("ConstrainPhysicalSizeForWindow", source);
        Assert.Contains("ConstrainPhysicalSizeForWindow(window, new SizeInt32(placement.Width, placement.Height))", source);
        Assert.DoesNotContain("ScaleSizeForWindow(window, new SizeInt32(placement.Width, placement.Height))", source);
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
