namespace EmsScout.Tests;

public sealed class DesktopStartupMigrationContractTests
{
    [Fact]
    public void DesktopMigratesBeforeActivationAndKeepsRecoveryUiAvailableOnFailure()
    {
        var root = LocateRepositoryRoot();
        var appPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "App.xaml.cs");
        var initializerPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "Services", "StartupDatabaseInitializer.cs");
        var windowPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs");
        var windowXamlPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml");
        var app = File.ReadAllText(appPath);
        var initializer = File.ReadAllText(initializerPath);
        var window = File.ReadAllText(windowPath);
        var windowXaml = File.ReadAllText(windowXamlPath);

        var initialize = app.IndexOf("InitializeAsync()", StringComparison.Ordinal);
        var windowCreation = app.IndexOf("new MainWindow(startupFailure)", StringComparison.Ordinal);
        var windowActivation = app.IndexOf("_window.Activate()", StringComparison.Ordinal);

        Assert.True(initialize >= 0, "Desktop startup must invoke the database initializer.");
        Assert.True(windowCreation > initialize, "The window must be created after the first migration attempt.");
        Assert.True(windowActivation > windowCreation, "The window must activate after receiving startup status.");
        Assert.Contains("catch (Exception ex)", app);
        Assert.Contains("LegacyOutMigrationService", initializer);
        Assert.Contains("SqliteSchemaMigrator", initializer);
        Assert.Contains("File.Exists(paths.DatabasePath)", initializer);
        Assert.Contains(".MigrateAsync(paths.DatabasePath", initializer);
        Assert.Contains(".CreateNewAsync(paths.DatabasePath", initializer);
        Assert.Contains("Path.Combine(pathService.WorkspaceRoot, \"out\")", initializer);
        Assert.Contains("var paths = pathService.Capture()", initializer);
        Assert.Contains("IApplicationLogger logger", initializer);
        Assert.Contains("ApplicationLogLevel.Critical", initializer);
        Assert.Contains("ErrorCode: failure.Code", initializer);
        Assert.Contains("[\"databasePath\"] = paths.DatabasePath", initializer);
        Assert.Contains("AlwaysWrite: true", initializer);
        Assert.Contains("RetryStartupMigration_Click", window);
        Assert.Contains("OpenSettings_Click", window);
        Assert.Contains("StartupFailureBar", windowXaml);
        Assert.Contains("错误日志", window);
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

        throw new DirectoryNotFoundException("Cannot locate the repository root for the desktop startup contract test.");
    }
}
