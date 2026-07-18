namespace EmsScout.Tests;

public sealed class GroupSettingsUiContractTests
{
    [Fact]
    public void GroupsViewModelUsesReconciliationRepositoryWithoutWatchRuntime()
    {
        var source = ReadDesktop("ViewModels", "GroupsViewModel.cs");

        Assert.Contains("IAreaGroupReconciliationRepository", source);
        Assert.Contains("AreaGroupManagementSnapshot", source);
        Assert.Contains("PendingAddCount", source);
        Assert.Contains("PendingRemoveCount", source);
        Assert.DoesNotContain("IDeviceWatchRepository", source);
        Assert.DoesNotContain("Watch", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FloorCatalog", source);
    }

    [Fact]
    public void GroupSettingsSupportEditableRulesMembersAndExceptionNotes()
    {
        var source = ReadDesktop("Pages", "AreasPage.xaml");

        Assert.Contains("SaveRuleCommand", source);
        Assert.Contains("DeleteRule", source);
        Assert.Contains("AddManualMemberCommand", source);
        Assert.Contains("DeleteManualMember", source);
        Assert.Contains("UpdateExceptionNote", source);
        Assert.Contains("DeleteException", source);
        Assert.Contains("UpdateMemberNote", source);
        Assert.Contains("SelectedRule", source);
        Assert.Contains("EditRuleEnabled", source);
        Assert.Contains("Header=\"备注\"", source);
        Assert.Contains("设备名 / 关键字", source);
        Assert.Contains("设备名精确匹配", source);
    }

    [Fact]
    public void RuleEditorSupportsGlobalNameRulesButKeepsFloorRulesBuildingScoped()
    {
        var xaml = ReadDesktop("Pages", "AreasPage.xaml");
        var viewModel = ReadDesktop("ViewModels", "GroupsViewModel.cs");

        Assert.Contains("ItemsSource=\"{x:Bind ViewModel.RuleBuildingOptions}\"", xaml);
        Assert.Contains("DisplayMemberPath=\"Label\"", xaml);
        Assert.Contains("SelectedValuePath=\"Value\"", xaml);
        Assert.Contains("SelectedValue=\"{x:Bind ViewModel.RuleBuilding, Mode=TwoWay}\"", xaml);
        Assert.Contains("new(string.Empty, \"全部楼栋\")", viewModel);
        Assert.Contains("SelectedRuleType.Value == \"floor\"", viewModel);
        Assert.Contains("!string.IsNullOrWhiteSpace(RuleBuilding)", viewModel);
        Assert.Contains("!string.IsNullOrWhiteSpace(RuleFloorLabel)", viewModel);
        Assert.Contains("SelectedRuleType.Value is \"name_exact\" or \"name_keyword\"", viewModel);
        Assert.Contains("!string.IsNullOrWhiteSpace(RuleKeyword)", viewModel);
        Assert.Contains("RuleBuilding = value.Building;", viewModel);
        Assert.Contains("public partial string RuleBuilding { get; set; } = string.Empty;", viewModel);
        Assert.Contains("SelectedRuleType.Value,\n                    RuleBuilding,", viewModel);
    }

    [Fact]
    public void PresetsUseOrdinaryEditableGroupActions()
    {
        var source = ReadDesktop("Pages", "AreasPage.xaml");
        var viewModel = ReadDesktop("ViewModels", "GroupsViewModel.cs");

        Assert.Contains("AutomationProperties.Name=\"保存分组\"", source);
        Assert.Contains("AutomationProperties.Name=\"删除分组\"", source);
        Assert.Contains("new(\"area_public\", \"预设公区分类\")", viewModel);
        Assert.Contains("new(\"area_non_public\", \"预设非公区分类\")", viewModel);
        Assert.DoesNotContain("SystemEditorVisibility", source);
        Assert.DoesNotContain("系统区域不能删除", source);
    }

    [Fact]
    public void CurrentDeviceDirectorySupportsFloorRuleOrSingleDeviceButNoSubAreaCreation()
    {
        var source = ReadDesktop("Pages", "AreasPage.xaml");
        var viewModel = ReadDesktop("ViewModels", "GroupsViewModel.cs");

        Assert.Contains("现有设备目录", source);
        Assert.Contains("选择楼层作为持续规则", source);
        Assert.Contains("将设备直接加入分组", source);
        Assert.Contains("DeviceDirectorySearchText", source);
        Assert.DoesNotContain("新增子区", source);
        Assert.DoesNotContain("\"sub_area\"", viewModel);
    }

    [Fact]
    public void CurrentDeviceDirectoryReportsCompleteCountOrExplicitFailure()
    {
        var viewModel = ReadDesktop("ViewModels", "GroupsViewModel.cs");

        Assert.Contains("现有设备目录已完整加载", viewModel);
        Assert.Contains("读取现有设备目录失败", viewModel);
    }

    [Fact]
    public void ReloadingDeviceDirectoryFailsClosedBeforeAnOverLimitRepositoryError()
    {
        var source = ReadDesktop("ViewModels", "GroupsViewModel.cs");
        var loadStart = source.IndexOf("private async Task LoadDeviceDirectoryAsync", StringComparison.Ordinal);
        var loadEnd = source.IndexOf("private bool CanAddManualMember", loadStart, StringComparison.Ordinal);
        Assert.True(loadStart >= 0 && loadEnd > loadStart, "Cannot locate device-directory loading method.");

        var loadMethod = source[loadStart..loadEnd];
        var clearPosition = loadMethod.IndexOf("ClearDeviceDirectoryState();", StringComparison.Ordinal);
        var repositoryPosition = loadMethod.IndexOf(".LoadTargetOptionsAsync(", StringComparison.Ordinal);
        Assert.True(
            clearPosition >= 0 && repositoryPosition > clearPosition,
            "The previous directory must be cleared before a new repository request can fail.");
        Assert.Contains("}, \"读取现有设备目录失败\")", loadMethod);

        var clearStart = source.IndexOf("private void ClearDeviceDirectoryState()", StringComparison.Ordinal);
        var clearEnd = source.IndexOf("private void ClearManagementState()", clearStart, StringComparison.Ordinal);
        Assert.True(clearStart >= 0 && clearEnd > clearStart, "Cannot locate device-directory fail-closed cleanup.");

        var clearMethod = source[clearStart..clearEnd];
        Assert.Contains("_loadedDeviceDirectory.Clear();", clearMethod);
        Assert.Contains("DeviceDirectory.Clear();", clearMethod);
        Assert.Contains("SelectedDevice = null;", clearMethod);
    }

    [Fact]
    public void PendingReminderNavigatesToFilteredAudit()
    {
        var source = ReadDesktop("Pages", "AreasPage.xaml");
        var codeBehind = ReadDesktop("Pages", "AreasPage.xaml.cs");

        Assert.Contains("AutomationProperties.Name=\"打开分组审计\"", source);
        Assert.Contains("OpenAudit", source);
        Assert.Contains("NavigateToAudit", codeBehind);
    }

    [Fact]
    public void DestructiveAreaActionsRequireConfirmationAndExposeStableAutomationNames()
    {
        var source = ReadDesktop("Pages", "AreasPage.xaml");
        var codeBehind = ReadDesktop("Pages", "AreasPage.xaml.cs");
        var audit = ReadDesktop("Pages", "AuditPage.xaml");

        Assert.Contains("ConfirmDestructiveActionAsync", codeBehind);
        Assert.Contains("DeleteGroup_Click", codeBehind);
        Assert.Contains("AutomationProperties.Name=\"删除持续规则\"", source);
        Assert.Contains("AutomationProperties.Name=\"移除或屏蔽正式成员\"", source);
        Assert.Contains("AutomationProperties.Name=\"撤销分组例外\"", source);
        Assert.Contains("AutomationProperties.Name=\"确认加入正式成员\"", audit);
        Assert.Contains("AutomationProperties.Name=\"拒绝加入并长期屏蔽\"", audit);
        Assert.Contains("AutomationProperties.Name=\"确认移除正式成员\"", audit);
        Assert.Contains("AutomationProperties.Name=\"拒绝移除并手动保留\"", audit);
    }

    private static string ReadDesktop(params string[] pathParts)
    {
        var path = new[] { LocateRepositoryRoot(), "native", "src", "EmsScout.Desktop" }
            .Concat(pathParts)
            .ToArray();
        return File.ReadAllText(Path.Combine(path)).ReplaceLineEndings("\n");
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !(File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                 Directory.Exists(Path.Combine(directory.FullName, "native"))))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
