namespace EmsScout.Tests;

public sealed class SettingsLegacyMigrationUiContractTests
{
    [Fact]
    public void SettingsPageExposesTheWalSafeLegacyMigrationAction()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "SettingsPage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "SettingsPage.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "SettingsViewModel.cs"));

        Assert.Contains("Click=\"MigrateLegacyOut_Click\"", xaml);
        Assert.Contains("MigrateLegacyOutAsync(path)", codeBehind);
        Assert.Contains("LegacyOutMigrationService", viewModel);
        Assert.Contains("var configuredPaths = pathService.Capture()", viewModel);
        Assert.Contains("MigrateIfNeededAsync(legacyOutDirectory, configuredPaths.DataDirectory", viewModel);
        Assert.Contains("schemaMigrator", viewModel);
        Assert.Contains("MigrateAsync(configuredPaths.DatabasePath", viewModel);
        Assert.Contains("DestinationAlreadyInitialized", viewModel);
        Assert.Contains("请先保存当前数据目录，再迁移旧数据", viewModel);
        Assert.Contains("await MigrateExistingDatabaseAsync(settings)", viewModel);
        Assert.Contains("设置未保存：", viewModel);
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

        throw new DirectoryNotFoundException("Cannot locate the repository root for the settings migration contract test.");
    }
}
