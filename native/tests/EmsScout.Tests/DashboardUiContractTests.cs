namespace EmsScout.Tests;

public sealed class DashboardUiContractTests
{
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
        Assert.Contains("Total", xaml);
        Assert.Contains("Text=\"公区\"", xaml);
        Assert.Contains("Text=\"非公区\"", xaml);
        Assert.Contains("PublicTotal", xaml);
        Assert.Contains("PrivateTotal", xaml);
        Assert.Contains("Online", xaml);
        Assert.Contains("Text=\"离线\"", xaml);
        Assert.Contains("Text=\"开机\"", xaml);
        Assert.Contains("Text=\"关机\"", xaml);
        Assert.Contains("PublicRunning", xaml);
        Assert.Contains("PublicStopped", xaml);
        Assert.Contains("Text=\"公区开机\"", xaml);
        Assert.Contains("Text=\"公区关机\"", xaml);
        Assert.Contains("Offline", xaml);
        Assert.Contains("查看设备", xaml);
        Assert.Contains("AreaGroupsEmptyVisibility", xaml);
        Assert.Contains("AreaGroupsErrorVisibility", xaml);
        Assert.Contains("SizeChanged=\"Page_SizeChanged\"", xaml);
        Assert.Contains("WideAreaGroupList", xaml);
        Assert.Contains("CompactAreaGroupList", xaml);
        Assert.Contains("e.NewSize.Width < 1050", codeBehind);
        Assert.Contains("e.NewSize.Width >= 1600", codeBehind);
        Assert.DoesNotContain("Grid.SetColumn(HeaderActions", codeBehind);
        Assert.DoesNotContain("Grid.SetRow(HeaderActions", codeBehind);
        Assert.Contains("AutomationProperties.Name=\"刷新工作台\"", xaml);
        Assert.Contains("<KeyboardAccelerator Key=\"F5\" />", xaml);
        Assert.Contains("AreaGroups_ItemClick", codeBehind);
        Assert.Contains("OpenAreaGroups_Click", codeBehind);
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
        Assert.Contains("navigationService.NavigateToData(new DataNavigationRequest(AreaGroupId: row.Id))", homeViewModel);
        Assert.Contains("long? AreaGroupId = null", navigation);
        Assert.Contains("void NavigateToGroups(long? groupId = null)", navigation);
    }

    [Fact]
    public void AreaGroupsPageExposesPublicAndPrivateStateBreakdown()
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

        Assert.Contains("Text=\"公区\"", xaml);
        Assert.Contains("Text=\"非公区\"", xaml);
        Assert.Contains("Text=\"公区开机\"", xaml);
        Assert.Contains("Text=\"公区关机\"", xaml);
        Assert.Contains("RunningCount", xaml);
        Assert.Contains("StoppedCount", xaml);
        Assert.Contains("PublicCount", row);
        Assert.Contains("PrivateCount", row);
        Assert.Contains("AreaBreakdownText", row);
        Assert.Contains("PublicRunningCount", row);
        Assert.Contains("PublicStoppedCount", row);
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
