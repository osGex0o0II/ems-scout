using System.Xml.Linq;

namespace EmsScout.Tests;

public sealed class NavigationInformationArchitectureTests
{
    [Fact]
    public void ShellUsesFiveWorkflowItemsAndTwoFooterTools()
    {
        var root = LocateRepositoryRoot();
        var shellPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml");
        var shell = XDocument.Load(shellPath);
        var primaryHost = shell.Descendants().Single(element => element.Name.LocalName == "NavigationView.MenuItems");
        var footerHost = shell.Descendants().Single(element => element.Name.LocalName == "NavigationView.FooterMenuItems");

        var primary = primaryHost.Elements()
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .Select(Item)
            .ToArray();
        var footer = footerHost.Elements()
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .Select(Item)
            .ToArray();

        Assert.Equal(
            [
                ("工作台", "workbench"),
                ("采集", "collection"),
                ("设备数据", "devices"),
                ("区域组", "rules"),
                ("审计", "audit"),
            ],
            primary);
        Assert.Equal([("系统设置", "settings"), ("诊断", "diagnostics")], footer);
        Assert.DoesNotContain(primary, item => item.Tag == "dates");
    }

    [Fact]
    public void AreaManagementRetiresTheRulesAndPlansTitleFromVisibleDesktopXaml()
    {
        var root = LocateRepositoryRoot();
        var desktop = Path.Combine(root, "native", "src", "EmsScout.Desktop");
        var sources = Directory.EnumerateFiles(desktop, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToArray();

        Assert.Contains(sources, source => source.Contains("区域组", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Contains("区域组与关注", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Contains("关注设备", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Contains("规则与计划", StringComparison.Ordinal));
    }

    [Fact]
    public void RemainingDeepLinksSelectTheirOwningWorkflowDestination()
    {
        var root = LocateRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("SelectNavigationItem(\"devices\")", code);
        Assert.Contains("SelectNavigationItem(\"audit\")", code);
        Assert.DoesNotContain("SelectNavigationItem(\"rules\")", code);
        Assert.Contains("NavView.FooterMenuItems", code);
    }

    private static (string Content, string Tag) Item(XElement element) =>
        ((string?)element.Attribute("Content") ?? string.Empty, (string?)element.Attribute("Tag") ?? string.Empty);

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
