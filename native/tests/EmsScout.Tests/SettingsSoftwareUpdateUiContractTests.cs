namespace EmsScout.Tests;

public sealed class SettingsSoftwareUpdateUiContractTests
{
    [Fact]
    public void SettingsRegistersUpdateServicesAndExposesACollectionSafeUpdateCard()
    {
        var root = LocateRepositoryRoot();
        var app = Read(root, "native", "src", "EmsScout.Desktop", "App.xaml.cs");
        var viewModel = Read(root, "native", "src", "EmsScout.Desktop", "ViewModels", "SettingsViewModel.cs");
        var collection = Read(root, "native", "src", "EmsScout.Desktop", "ViewModels", "CollectionTaskViewModel.cs");
        var launcher = Read(root, "native", "src", "EmsScout.Desktop", "Services", "WindowsAppUpdateLauncher.cs");
        var xaml = Read(root, "native", "src", "EmsScout.Desktop", "Pages", "SettingsPage.xaml");

        Assert.Contains("AddSingleton<IAppVersionProvider, PackageAppVersionProvider>()", app);
        Assert.Contains("AddSingleton<IAppUpdateLauncher, WindowsAppUpdateLauncher>()", app);
        Assert.Contains("AddSingleton<AppUpdateService>()", app);
        Assert.Contains("https://github.com/osGex0o0II/ems-scout/releases/latest/download/EmsScout.appinstaller", app);

        Assert.Contains("CurrentVersionText", viewModel);
        Assert.Contains("AvailableVersionText", viewModel);
        Assert.Contains("UpdateStatusText", viewModel);
        Assert.Contains("IsCheckingForUpdate", viewModel);
        Assert.Contains("CanInstallUpdate =>", viewModel);
        Assert.Contains("!operationState.IsCollectionTaskRunning", viewModel);
        Assert.Contains("CheckForUpdateAsync", viewModel);
        Assert.Contains("InstallUpdateAsync", viewModel);
        Assert.Contains("operationState.BeginUpdateInstall()", viewModel);
        Assert.Contains("采集结束后可安装", viewModel);
        Assert.Contains("InstallUpdateCommand.NotifyCanExecuteChanged()", viewModel);
        Assert.Contains("operationState.IsUpdateInstallPending", collection);
        Assert.Contains("Microsoft.UI.Xaml.Application.Current.Exit()", launcher);

        Assert.Contains("Text=\"软件更新\"", xaml);
        Assert.Contains("Text=\"当前版本\"", xaml);
        Assert.Contains("ViewModel.CurrentVersionText", xaml);
        Assert.Contains("ViewModel.AvailableVersionText", xaml);
        Assert.Contains("ViewModel.UpdateStatusText", xaml);
        Assert.Contains("ViewModel.IsCheckingForUpdate", xaml);
        Assert.Contains("ViewModel.CheckForUpdateCommand", xaml);
        Assert.Contains("ViewModel.InstallUpdateCommand", xaml);
        Assert.Contains("Text=\"检查更新\"", xaml);
        Assert.Contains("Text=\"安装更新\"", xaml);
        var updateCard = UpdateCardSlice(xaml);
        Assert.DoesNotContain("Foreground=\"#", updateCard, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Background=\"#", updateCard, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Color=\"#", updateCard, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpdateApplicationModuleDoesNotDependOnDataOrMigrationServices()
    {
        var root = LocateRepositoryRoot();
        var updateDirectory = Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Application",
            "Updates");
        var source = string.Join(
            Environment.NewLine,
            Directory.GetFiles(updateDirectory, "*.cs").Select(File.ReadAllText));

        Assert.DoesNotContain("Sqlite", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Database", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Migration", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppDataPathService", source, StringComparison.Ordinal);
    }

    private static string UpdateCardSlice(string xaml)
    {
        var start = xaml.IndexOf("Text=\"软件更新\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Software update card is missing.");
        var end = xaml.IndexOf("Text=\"高级\"", start, StringComparison.Ordinal);
        Assert.True(end > start, "Software update card must appear before Advanced settings.");
        return xaml[start..end];
    }

    private static string Read(string root, params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { root }.Concat(segments).ToArray()));

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
}
