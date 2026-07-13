namespace EmsScout.Tests;

public sealed class DateManagementUiContractTests
{
    [Fact]
    public void DateManagementBelongsToRulesAndPlansAndUsesVisibleMultiSelectCalendar()
    {
        var root = LocateRepositoryRoot();
        var navigation = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml"));
        var page = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DateManagementPage.xaml"));
        var groups = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));

        Assert.Contains("Content=\"规则与计划\" Tag=\"rules\"", navigation);
        Assert.DoesNotContain("Content=\"日期管理\" Tag=\"dates\"", navigation);
        Assert.Contains("<CalendarView", page);
        Assert.Contains("SelectionMode=\"Multiple\"", page);
        Assert.Contains("应用到选中日期", page);
        Assert.Contains("添加时间段", page);
        Assert.Contains("设置适用对象", page);
        Assert.Contains("打开日期管理", groups);
        Assert.Contains("Legacy inline schedule editor intentionally retired", groups);
    }

    [Fact]
    public void BatchDateOperationsAreExposedByRepositoryContract()
    {
        var root = LocateRepositoryRoot();
        var contract = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Application", "Groups", "AreaGroups.cs"));
        var repository = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Infrastructure", "Sqlite", "SqliteAreaGroupRepository.cs"));

        Assert.Contains("SaveScheduleRulesAsync", contract);
        Assert.Contains("DeleteScheduleRulesAsync", contract);
        Assert.Contains("BeginTransaction(deferred: false)", repository);
        Assert.Contains("ON CONFLICT(schedule_group_id, calendar_date) DO UPDATE", repository);
    }

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
