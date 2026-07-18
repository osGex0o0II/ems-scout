namespace EmsScout.Tests;

public sealed class ScheduleFeatureRetirementContractTests
{
    [Fact]
    public void DesktopRetiresDateManagementAndWatchDateRange()
    {
        var root = LocateRepositoryRoot();
        var desktop = Path.Combine(root, "native", "src", "EmsScout.Desktop");
        var retiredFiles = new[]
        {
            Path.Combine(desktop, "Pages", "DateManagementPage.xaml"),
            Path.Combine(desktop, "Pages", "DateManagementPage.xaml.cs"),
            Path.Combine(desktop, "ViewModels", "DateManagementViewModel.cs"),
            Path.Combine(desktop, "ViewModels", "ScheduleGroupRow.cs"),
            Path.Combine(desktop, "ViewModels", "ScheduleAuditRow.cs"),
        };

        Assert.All(retiredFiles, path => Assert.False(File.Exists(path), $"Retired desktop file remains: {path}"));

        var desktopSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(desktop, "*", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                               !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
        Assert.DoesNotContain("DateManagement", desktopSource);
        Assert.DoesNotContain("NavigateToDates", desktopSource);
        Assert.DoesNotContain("CalendarView", desktopSource);
        Assert.DoesNotContain("ScheduleAudit", desktopSource);
        Assert.DoesNotContain("计划状态审计", desktopSource);
        Assert.DoesNotContain("打开日期管理", desktopSource);
        Assert.DoesNotContain("Legacy inline schedule editor", desktopSource);

        var areasPage = File.ReadAllText(Path.Combine(desktop, "Pages", "AreasPage.xaml"));
        Assert.Equal(0, CountOccurrences(areasPage, "<DatePicker"));
        Assert.DoesNotContain("WatchStartDate", areasPage);
        Assert.DoesNotContain("WatchEndDate", areasPage);
        Assert.DoesNotContain("关注设备", areasPage);
    }

    [Fact]
    public void SchedulePersistenceRemainsAvailableForExistingData()
    {
        var root = LocateRepositoryRoot();
        var contract = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Application", "Groups", "AreaGroups.cs"));
        var repository = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Infrastructure", "Sqlite", "SqliteAreaGroupRepository.cs"));
        var migration = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Infrastructure",
            "Migrations",
            "Sql",
            "V003__schedule_groups.sql"));

        Assert.Contains("LoadScheduleGroupsAsync", contract);
        Assert.Contains("SaveScheduleRulesAsync", contract);
        Assert.Contains("DeleteScheduleRulesAsync", contract);
        Assert.Contains("EvaluateSchedulesAsync", contract);
        Assert.Contains("BeginTransaction(deferred: false)", repository);
        Assert.Contains("ON CONFLICT(schedule_group_id, calendar_date) DO UPDATE", repository);
        Assert.Contains("CREATE TABLE IF NOT EXISTS schedule_groups", migration);
        Assert.Contains("CREATE TABLE IF NOT EXISTS schedule_rules", migration);
        Assert.Contains("CREATE TABLE IF NOT EXISTS schedule_intervals", migration);
        Assert.Contains("CREATE TABLE IF NOT EXISTS schedule_group_members", migration);
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !(File.Exists(Path.Combine(directory.FullName, "package.json")) && Directory.Exists(Path.Combine(directory.FullName, "native"))))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
