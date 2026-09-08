using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Diagnostics;
using EmsScout.Application.Collection;
using EmsScout.Application;
using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Application.Quality;
using EmsScout.Application.Settings;
using EmsScout.Application.Watch;
using EmsScout.Desktop.Services;
using EmsScout.Desktop.ViewModels;
using EmsScout.Domain;
using EmsScout.Infrastructure.Quality;
using EmsScout.Infrastructure.Importing;
using EmsScout.Infrastructure.Realtime;
using EmsScout.Infrastructure.Sqlite;

namespace EmsScout.Desktop;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;
    private AppInstance? _mainInstance;
    private readonly DispatcherQueue _dispatcherQueue;

    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        UnhandledException += App_UnhandledException;
        Services = ConfigureServices();
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EMS Scout",
                "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "ui-crash.log"),
                $"[{DateTimeOffset.Now:O}] {args.Exception.ToString()}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never replace the original UI exception.
        }
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _mainInstance = AppInstance.FindOrRegisterForKey("EMS-Scout-main");
        if (!_mainInstance.IsCurrent)
        {
            var activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
            await _mainInstance.RedirectActivationToAsync(activationArguments);
            Process.GetCurrentProcess().Kill();
            return;
        }

        _mainInstance.Activated += MainInstance_Activated;
        _window = new MainWindow();
        _window.Activate();
        if (_window is MainWindow mainWindow)
        {
            mainWindow.ApplyStartupState();
        }
        var settings = Services.GetRequiredService<AppSettingsService>().Current;
        await Services.GetRequiredService<StartupTaskService>().ApplyAsync(settings.LaunchAtLogin);
        try
        {
            Services.GetRequiredService<SendToShortcutService>().Apply(settings.ShowInSendTo);
        }
        catch
        {
            // Optional shell integration must never prevent the application from opening.
        }
    }

    private void MainInstance_Activated(object? sender, AppActivationArguments args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_window is MainWindow mainWindow)
            {
                mainWindow.ShowFromActivation();
            }
        });
    }

    private static IServiceProvider ConfigureServices()
    {
        var workspaceRoot = WorkspaceLocator.LocateRepositoryRoot();

        var services = new ServiceCollection();
        services.AddSingleton(new InventorySummarizer());
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton(provider => new AppDataPathService(
            workspaceRoot,
            provider.GetRequiredService<AppSettingsService>()));
        services.AddSingleton<AppUiSettingsService>();
        services.AddSingleton<StartupTaskService>();
        services.AddSingleton<SendToShortcutService>();
        services.AddSingleton<IInventorySnapshotSource>(provider => new EnumFullV5SnapshotSource(
            () => provider.GetRequiredService<AppDataPathService>().EnumJsonPath));
        services.AddSingleton<IRealtimeDetailSource>(provider => new RealtimeLatestJsonSource(
            workspaceRoot,
            () => provider.GetRequiredService<AppDataPathService>().DataDirectory));
        services.AddSingleton<IDeviceWatchRepository>(provider => new SqliteDeviceWatchRepository(
            () => provider.GetRequiredService<AppDataPathService>().DatabasePath));
        services.AddSingleton<IDeviceReadRepository>(provider => new SqliteDeviceReadRepository(
            () => provider.GetRequiredService<AppDataPathService>().DatabasePath,
            provider.GetRequiredService<IRealtimeDetailSource>(),
            provider.GetRequiredService<IDeviceWatchRepository>()));
        services.AddSingleton<IDeviceExportService>(provider => new SqliteDeviceExportService(
            provider.GetRequiredService<IDeviceReadRepository>()));
        services.AddSingleton<IDeviceAnnotationService>(provider => new SqliteDeviceAnnotationService(
            () => provider.GetRequiredService<AppDataPathService>().DatabasePath));
        services.AddSingleton<IRealtimeReconciliationService>(provider => new SqliteRealtimeReconciliationService(
            () => provider.GetRequiredService<AppDataPathService>().DatabasePath,
            provider.GetRequiredService<IRealtimeDetailSource>()));
        services.AddSingleton<IQualityAuditService>(provider => new JsonQualityAuditService(
            () => provider.GetRequiredService<AppDataPathService>().QualityOutputDirectory,
            () => provider.GetRequiredService<AppDataPathService>().DatabasePath));
        services.AddSingleton<IRealtimeQualityAuditService>(provider => new JsonRealtimeQualityAuditService(
            () => provider.GetRequiredService<AppDataPathService>().QualityOutputDirectory));
        services.AddSingleton<ICollectionRunRepository>(provider => new SqliteCollectionRunRepository(
            () => provider.GetRequiredService<AppDataPathService>().DatabasePath));
        services.AddSingleton<IAreaGroupRepository>(provider => new SqliteAreaGroupRepository(
            () => provider.GetRequiredService<AppDataPathService>().DatabasePath));
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(provider => provider.GetRequiredService<NavigationService>());
        services.AddSingleton<WindowHandleProvider>();
        services.AddSingleton<DashboardOverviewService>();
        services.AddSingleton(new NodeCollectionTaskRunner(workspaceRoot));
        services.AddTransient<HomeViewModel>();
        services.AddSingleton<CollectionTaskViewModel>();
        services.AddTransient<DataViewModel>();
        services.AddTransient<AuditViewModel>();
        services.AddTransient<GroupsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<DiagnosticsViewModel>();
        return services.BuildServiceProvider();
    }
}
