namespace EmsScout.Tests;

public sealed class DataPageCrashRegressionTests
{
    [Fact]
    public void DataPageCancelsPageOwnedWorkWhenNavigationLeavesThePage()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml.cs"));

        Assert.Contains("CancellationTokenSource", source);
        Assert.Contains("OnNavigatedFrom", source);
        Assert.Contains("_pageLifetime.Token", source);
    }

    [Fact]
    public void MainWindowSuppressesSelectionChangedDuringProgrammaticNavigation()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("_suppressNavigationSelection", source);
        Assert.Contains("if (_suppressNavigationSelection)", source);
    }

    [Fact]
    public void DataOperationsDoNotConvertPageCancellationIntoADataError()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));

        Assert.Contains("catch (Exception ex) when (ex is not OperationCanceledException)", source);
    }

    [Fact]
    public void HomePageOwnsAndCancelsItsLoadLifetime()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml.cs"));

        Assert.Contains("CancellationTokenSource", source);
        Assert.Contains("OnNavigatedFrom", source);
        Assert.Contains("_pageLifetime.Token", source);
    }

    [Fact]
    public void TasksPageDoesNotOwnDiagnosticLogControls()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "TasksPage.xaml.cs"));

        Assert.DoesNotContain("Logs_CollectionChanged", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AttachLogs", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyLogs_Click", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearLogs_Click", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppWritesUnhandledUiExceptionsToTheUserLog()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "native", "src", "EmsScout.Desktop", "App.xaml.cs"));

        Assert.Contains("UnhandledException +=", source, StringComparison.Ordinal);
        Assert.Contains("Exception.ToString()", source, StringComparison.Ordinal);
        Assert.Contains("ui-crash.log", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TasksPageReportsInitializationFailuresWithoutEscapingLoadedCallback()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "TasksPage.xaml.cs"));
        var handlerStart = source.IndexOf("private async void Page_Loaded", StringComparison.Ordinal);
        var handlerEnd = source.IndexOf("private void Page_Unloaded", handlerStart, StringComparison.Ordinal);

        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        var handler = source[handlerStart..handlerEnd];
        Assert.Contains("catch (Exception ex)", handler, StringComparison.Ordinal);
        Assert.Contains("ReportInitializationError", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void TasksPageMonitorsAndStopsTheBrowserLoginState()
    {
        var root = LocateRepositoryRoot();
        var page = File.ReadAllText(
            Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "TasksPage.xaml.cs"));
        var viewModel = File.ReadAllText(
            Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "CollectionTaskViewModel.cs"));

        Assert.Contains("StartEnvironmentMonitoring", page, StringComparison.Ordinal);
        Assert.Contains("StopEnvironmentMonitoring", page, StringComparison.Ordinal);
        Assert.Contains("ClientWebSocket", viewModel, StringComparison.Ordinal);
        Assert.Contains("LoginVerified", viewModel, StringComparison.Ordinal);
        Assert.Contains("!cdpStatus.LoginVerified", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void AppRedirectsSecondActivationToTheTrayResidentInstance()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "native", "src", "EmsScout.Desktop", "App.xaml.cs"));

        Assert.Contains("FindOrRegisterForKey", source, StringComparison.Ordinal);
        Assert.Contains("RedirectActivationToAsync", source, StringComparison.Ordinal);
        Assert.Contains("TryEnqueue", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DataInitializationErrorDoesNotRescanAnInvalidExportDirectory()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));
        var start = source.IndexOf("private void SetDataError", StringComparison.Ordinal);
        var end = source.IndexOf("public async Task ApplyBuildingSelectionAsync", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        Assert.DoesNotContain("RefreshRecentExports();", source[start..end]);
    }

    [Fact]
    public void RecentExportScanDegradesWhenConfiguredDirectoryCannotBeResolved()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));
        var start = source.IndexOf("private void RefreshRecentExports", StringComparison.Ordinal);
        var end = source.IndexOf("private void SetLastExportFilePath", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var method = source[start..end];
        Assert.Contains("try", method);
        Assert.Contains("catch (Exception)", method);
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

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
