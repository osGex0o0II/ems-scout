namespace EmsScout.Tests;

public sealed class GroupSettingsUiContractTests
{
    [Fact]
    public void CustomGroupsOpenDataManagementWithTheirGroupId()
    {
        var root = LocateRepositoryRoot();
        var rowPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupSummaryRow.cs");
        var source = File.ReadAllText(rowPath);
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.Contains("GroupId is not null", source);
        Assert.Contains("AreaGroupId: SelectedGroup.GroupId", viewModel);
        Assert.Contains("\"不可编辑\"", source);
    }

    [Fact]
    public void GroupSettingsExplainsThatGroupsDriveDashboardFilteringAndExport()
    {
        var root = LocateRepositoryRoot();
        var viewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.Contains("数据管理中筛选并导出", source);
        Assert.Contains("显示在首页", source);
        Assert.DoesNotContain("数据管理仅保留基础筛选", source);
    }

    [Fact]
    public void GroupSettingsKeepsOnlyFloorCatalogAndMemberSelectionSettings()
    {
        var root = LocateRepositoryRoot();
        var xamlPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml");
        var xaml = File.ReadAllText(xamlPath);
        var viewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.Contains("楼层候选目录", xaml);
        Assert.Contains("这里仅维护可选楼层。要关注某个楼层，请在下方添加。", xaml);
        Assert.Contains("AutomationProperties.Name=\"添加楼层或设备\"", xaml);
        Assert.Contains("Header=\"更多设置：楼层目录\"", xaml);
        Assert.DoesNotContain("更多设置：关注设备", xaml);
        Assert.DoesNotContain("更多设置：系统区域判断", xaml);
        Assert.DoesNotContain("WatchTimeValidationMessage", xaml);
        Assert.Contains("TargetTypes", source);
        Assert.Contains("name_contains", source);
        Assert.Contains("name_excludes", source);
    }

    [Fact]
    public void GroupSettingsKeepsThePrimaryWorkflowFocusedOnUserGroupsAndAddedScopes()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var source = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml"));
        var home = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml"));

        Assert.Contains("Text=\"我的区域组\"", xaml);
        Assert.Contains("\"已添加的楼层和设备\"", source);
        Assert.Contains("Text=\"尚未添加楼层或设备\"", xaml);
        Assert.Contains("Content=\"区域\" Tag=\"groups\"", mainWindow);
        Assert.Contains("在区域组中添加要关注的楼层或设备", home);
        Assert.Contains("Header=\"添加方式\"", xaml);
        Assert.Contains("Header=\"区域组名\"", xaml);
        Assert.Contains("Header=\"首页显示\"", xaml);
        Assert.Contains("ViewModel.MemberDraftVisibility", xaml);
        Assert.Contains("public Visibility MemberDraftVisibility", source);
        Assert.Contains("SelectionChanged=\"MemberScope_SelectionChanged\"", xaml);
        Assert.Contains("RefreshTargetOptionsAsync", codeBehind);
        Assert.Contains("_targetOptionsLoadVersion", source);
        Assert.Contains("group.GroupKind.Equals(\"custom\"", source);
        Assert.Contains("group.GroupKind.Equals(\"system\"", source);
        Assert.Contains("Header=\"名称条件\"", xaml);
        Assert.DoesNotContain("\"派生筛选\"", source);
        Assert.DoesNotContain("\"健康规则\"", source);
        Assert.DoesNotContain("分组设置", mainWindow);
    }

    [Fact]
    public void GroupSettingsRemovesWatchAndRulePanelsFromAreaPage()
    {
        var root = LocateRepositoryRoot();
        var xamlPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("Watch", xaml);
        Assert.DoesNotContain("系统区域判断", xaml);
    }

    [Fact]
    public void WatchIncidentNavigationUsesExactDeviceScope()
    {
        var root = LocateRepositoryRoot();
        var navigationPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "Services", "INavigationService.cs");
        var navigationSource = File.ReadAllText(navigationPath);
        var dataViewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs");
        var dataSource = File.ReadAllText(dataViewModelPath);
        var groupsViewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs");
        var groupsSource = File.ReadAllText(groupsViewModelPath);

        Assert.Contains("string Floor = \"\"", navigationSource);
        Assert.Contains("string SubArea = \"\"", navigationSource);
        Assert.Contains("string PageName = \"\"", navigationSource);
        Assert.Contains("SelectedFloor = SelectOption(FloorOptions, request.Floor)", dataSource);
        Assert.DoesNotContain("SelectedSubArea = SelectOption(SubAreaOptions, request.SubArea)", dataSource);
        Assert.Contains("SelectedPageName = SelectOption(PageNameOptions, request.PageName)", dataSource);
        Assert.Contains("Floor: incident.Device.FloorLabel", groupsSource);
        Assert.Contains("SubArea: incident.Device.SubArea", groupsSource);
        Assert.Contains("PageName: incident.Device.PageName", groupsSource);
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
