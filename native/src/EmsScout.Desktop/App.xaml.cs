using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
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
using EmsScout.Infrastructure.Sqlite;
using EmsScout.Legacy;

namespace EmsScout.Desktop;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;

    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
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
        services.AddTransient<CollectionTaskViewModel>();
        services.AddTransient<DataViewModel>();
        services.AddTransient<AuditViewModel>();
        services.AddTransient<GroupsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<DiagnosticsViewModel>();
        return services.BuildServiceProvider();
    }
}
