namespace EmsScout.Tests;

public sealed class SettingsTaskLockContractTests
{
    [Fact]
    public void CollectionWorkflowLocksCriticalSettingsUntilItsLeaseIsDisposed()
    {
        var root = LocateRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "App.xaml.cs"));
        var collection = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "CollectionTaskViewModel.cs"));
        var settings = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "SettingsViewModel.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "SettingsPage.xaml"));
        var state = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Application", "Workflows", "ApplicationOperationState.cs"));

        Assert.Contains("AddSingleton<ApplicationOperationState>()", app);
        Assert.Contains("operationLease = operationState.BeginCollectionTask()", collection);
        Assert.Contains("using var operationLeaseScope = operationLease", collection);
        Assert.Contains("var settings = settingsService.Current;", collection);
        Assert.Contains("CanEditCriticalSettings => !operationState.IsCollectionTaskRunning", settings);
        Assert.Contains("RelayCommand(CanExecute = nameof(CanChangeSettings))", settings);
        Assert.Contains("采集任务运行中，不能迁移或切换数据目录", settings);
        Assert.True(Count(xaml, "IsEnabled=\"{x:Bind ViewModel.CanEditCriticalSettings, Mode=OneWay}\"") >= 4);
        Assert.DoesNotMatch(@"<(?:Border|StackPanel)\b[^>]*\bIsEnabled=", xaml);
        Assert.True(Count(xaml, "<ContentControl") >= 3);
        Assert.Contains("Interlocked.Exchange(ref _owner, null)", state);
        Assert.Contains("Interlocked.CompareExchange(ref _activeOperation, CollectionOperation, Idle)", state);
    }

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

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
