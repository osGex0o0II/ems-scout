namespace EmsScout.Tests;

public sealed class GroupSettingsUiContractTests
{
    [Fact]
    public void GroupSummaryDoesNotExposeCustomGroupsAsDataManagementFilters()
    {
        var root = LocateRepositoryRoot();
        var rowPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupSummaryRow.cs");
        var source = File.ReadAllText(rowPath);

        Assert.DoesNotContain("MonitorGroupIds", source);
        Assert.DoesNotContain("ToDeviceQuery", source);
        Assert.DoesNotContain("!string.IsNullOrWhiteSpace(QuickFilter)", source);
        Assert.Contains("!string.IsNullOrWhiteSpace(AreaFilter)", source);
        Assert.Contains("!string.IsNullOrWhiteSpace(CommunicationFilter)", source);
        Assert.Contains("\"不可编辑\"", source);
    }

    [Fact]
    public void GroupSettingsCopyDoesNotClaimCustomMembersDriveDataManagementExport()
    {
        var root = LocateRepositoryRoot();
        var viewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.DoesNotContain("数据管理和 Excel 导出会使用同一组成员", source);
        Assert.DoesNotContain("数据管理和 Excel 命中范围", source);
        Assert.Contains("数据管理仅保留基础筛选，不按自定义成员跳转", source);
        Assert.Contains("用于分组统计和关注规则", source);
    }

    [Fact]
    public void GroupSettingsClarifiesFloorCatalogAndWatchTimeValidation()
    {
        var root = LocateRepositoryRoot();
        var xamlPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml");
        var xaml = File.ReadAllText(xamlPath);
        var viewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.Contains("楼层候选目录", xaml);
        Assert.Contains("不会把楼层加入当前分组", xaml);
        Assert.Contains("AutomationProperties.Name=\"新增成员\"", xaml);
        Assert.Contains("WatchTimeValidationMessage", xaml);
        Assert.Contains("结束时间必须晚于开始时间", source);
        Assert.Contains("CanMaintainWatch && IsWatchWindowValid", source);
        var watchEditorStart = xaml.LastIndexOf(
            "<Grid ColumnSpacing=\"10\" RowSpacing=\"8\">",
            xaml.IndexOf("Header=\"规则名称\"", StringComparison.Ordinal),
            StringComparison.Ordinal);
        var watchEditor = xaml[watchEditorStart..xaml.IndexOf("WatchTimeValidationMessage", StringComparison.Ordinal)];
        Assert.DoesNotContain("<ColumnDefinition Width=\"1.1*\" />", watchEditor);
        Assert.Contains("Grid.ColumnSpan=\"3\"", watchEditor);
        Assert.Contains("Grid.Row=\"1\"", watchEditor);
        Assert.Contains("Grid.Row=\"2\"", watchEditor);
    }

    [Fact]
    public void GroupSettingsProtectsWatchEditorFromStaleAsyncLoads()
    {
        var root = LocateRepositoryRoot();
        var viewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.Contains("private long _watchLoadVersion", source);
        Assert.Contains("ResetWatchEdit();", source);
        Assert.Contains("var watchLoadVersion = ++_watchLoadVersion", source);
        Assert.Contains("IsCurrentWatchGroup(groupId, watchLoadVersion)", source);
        Assert.Contains("var groupId = group.GroupId.Value", source);
        Assert.Contains("var watchRuleId = _watchRuleId", source);
        Assert.Contains("DeleteRuleAsync(watchRuleId, groupId)", source);
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
