namespace EmsScout.Tests;

public sealed class AreaManagementVisualContractTests
{
    [Fact]
    public void AreaPageRetiresWatchAndLegacyFloorCandidateSurface()
    {
        var source = ReadDesktop("Pages", "AreasPage.xaml");
        var viewModel = ReadDesktop("ViewModels", "GroupsViewModel.cs");

        Assert.DoesNotContain("关注设备", source);
        Assert.DoesNotContain("关注事件", source);
        Assert.DoesNotContain("保存关注", source);
        Assert.DoesNotContain("Watch", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("楼层候选目录", source);
        Assert.DoesNotContain("新增子区", source);
        Assert.DoesNotContain("IDeviceWatchRepository", viewModel);
        Assert.DoesNotContain("Watch", viewModel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AreaPageShowsRulesCurrentDevicesMembersExceptionsAndPendingReminder()
    {
        var source = ReadDesktop("Pages", "AreasPage.xaml");

        Assert.Contains("持续规则", source);
        Assert.Contains("现有设备目录", source);
        Assert.Contains("正式成员", source);
        Assert.Contains("例外名单", source);
        Assert.Contains("待确认加入", source);
        Assert.Contains("待确认移除", source);
        Assert.Contains("打开审计", source);
        Assert.Contains("设备名关键字", source);
        Assert.Contains("成员须经用户确认", source);
        Assert.Contains("Header=\"楼栋（可选）\"", source);
        Assert.DoesNotContain("命中结果由用户确认后才成为正式成员", source);
        Assert.DoesNotContain("Header=\"楼栋（设备名规则可选）\"", source);
        Assert.Contains("长期屏蔽", source);
        Assert.Contains("手动保留", source);
        Assert.Contains("AutomationProperties.Name=\"打开分组审计\"", source);
    }

    [Fact]
    public void AreaPageKeepsResponsiveMasterDetailAndScrollableWorkbench()
    {
        var source = ReadDesktop("Pages", "AreasPage.xaml");

        Assert.Contains("x:Name=\"GroupListColumn\"", source);
        Assert.Contains("x:Name=\"DetailColumn\"", source);
        Assert.Contains("x:Name=\"DetailScrollViewer\"", source);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", source);
        Assert.Contains("HorizontalScrollMode=\"Disabled\"", source);
        Assert.Contains("x:Name=\"NarrowAreaLayout\"", source);
        Assert.Contains("x:Name=\"WideAreaLayout\"", source);
        Assert.Contains("<AdaptiveTrigger MinWindowWidth=\"1100\" />", source);
    }

    [Fact]
    public void AreaAndAuditPrimaryColumnsFitA960LogicalPixelWindow()
    {
        var areas = ReadDesktop("Pages", "AreasPage.xaml");
        var audit = ReadDesktop("Pages", "AuditPage.xaml");

        Assert.Contains("Target=\"GroupListColumn.Width\" Value=\"240\"", areas);
        Assert.DoesNotContain("<StackPanel MinWidth=\"560\"", areas);
        Assert.Contains("Width=\"1.05*\" MinWidth=\"380\"", audit);
        Assert.Contains("Width=\"1.35*\" MinWidth=\"430\"", audit);
        Assert.Contains("x:Name=\"RuleEditorActions\"", areas);
        Assert.Contains("x:Name=\"DeviceDirectoryActions\"", areas);
    }

    [Fact]
    public void AuditPageOffersExplicitAddAndRemoveDecisionsWithNotes()
    {
        var source = ReadDesktop("Pages", "AuditPage.xaml");
        var viewModel = ReadDesktop("ViewModels", "AuditViewModel.cs");

        Assert.Contains("分组成员变更", source);
        Assert.Contains("确认加入", source);
        Assert.Contains("拒绝并屏蔽", source);
        Assert.Contains("确认移除", source);
        Assert.Contains("拒绝并保留", source);
        Assert.Contains("DecisionNote", source);
        Assert.DoesNotContain("DecisionNote / 处理备注", source);
        Assert.Contains("SelectedAreaGroup", source);
        Assert.Contains("SelectedGroupAction", source);
        Assert.Contains("DecideChangeAsync", viewModel);
        Assert.Contains("IAreaGroupReconciliationRepository", viewModel);
        Assert.Contains("Text=\"{x:Bind MatchReason}\" TextTrimming=\"CharacterEllipsis\"", source);
        Assert.DoesNotContain("Text=\"{x:Bind MatchReason}\" TextWrapping=\"WrapWholeWords\"", source);
        Assert.Contains("AutomationProperties.Name=\"恢复历史批次为当前数据\"", source);
        Assert.Contains("AutomationProperties.Name=\"删除历史批次\"", source);
    }

    private static string ReadDesktop(params string[] pathParts)
    {
        var path = new[] { LocateRepositoryRoot(), "native", "src", "EmsScout.Desktop" }
            .Concat(pathParts)
            .ToArray();
        return File.ReadAllText(Path.Combine(path));
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
