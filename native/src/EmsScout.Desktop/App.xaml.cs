using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using EmsScout.Application.Collection;
using EmsScout.Application;
using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Application.Logging;
using EmsScout.Application.Quality;
using EmsScout.Application.Settings;
using EmsScout.Application.Watch;
using EmsScout.Application.Workflows;
using EmsScout.Desktop.Services;
using EmsScout.Desktop.ViewModels;
using EmsScout.Domain;
using EmsScout.Infrastructure.Importing;
using EmsScout.Infrastructure.Errors;
using EmsScout.Infrastructure.Logging;
using EmsScout.Infrastructure.Migrations;
using EmsScout.Infrastructure.Quality;
using EmsScout.Infrastructure.Realtime;
using EmsScout.Infrastructure.Sidecar;
using EmsScout.Infrastructure.Sqlite;
using EmsScout.Infrastructure.Storage;

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

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        string? startupFailure = null;
        try
        {
            await Services.GetRequiredService<StartupDatabaseInitializer>()
                .InitializeAsync()
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            startupFailure = ApplicationFailureClassifier.Classify(ex).DisplayText;
        }

        _window = new MainWindow(startupFailure);
        _window.Activate();
    }

    private static IServiceProvider ConfigureServices()
    {
        var workspaceRoot = WorkspaceLocator.LocateRepositoryRoot();

        var services = new ServiceCollection();
        services.AddSingleton(new InventorySummarizer());
        services.AddSingleton<IApplicationLogger>(provider => new NdjsonApplicationLogger(
            Path.Combine(AppStorageDefaults.ProductDirectory, "logs"),
            enabled: () => provider.GetRequiredService<AppSettingsService>().Load().SaveNdjsonLog));
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton(provider => new AppDataPathService(
            workspaceRoot,
            provider.GetRequiredService<AppSettingsService>()));
        services.AddSingleton<LegacyOutMigrationService>();
        services.AddSingleton<SqliteSchemaMigrator>();
        services.AddSingleton<StartupDatabaseInitializer>();
        services.AddSingleton<ApplicationOperationState>();
        services.AddSingleton<AppUiSettingsService>();
        services.AddSingleton<IInventorySnapshotSource>(provider => new SqliteInventorySnapshotSource(
            () => provider.GetRequiredService<AppDataPathService>().DatabasePath));
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
        services.AddSingleton<CollectionSnapshotReader>();
        services.AddSingleton(provider => new CollectionSnapshotImporter(
            provider.GetRequiredService<CollectionSnapshotReader>()));
        services.AddSingleton(provider => new SqliteQualityAuditService(
            () => provider.GetRequiredService<AppDataPathService>().DatabasePath,
            () => Path.Combine(AppContext.BaseDirectory, "Config", "quality-known-findings.json")));
        services.AddSingleton<INativeQualityAuditService>(provider =>
            provider.GetRequiredService<SqliteQualityAuditService>());
        services.AddSingleton<IQualityAuditService>(provider =>
            provider.GetRequiredService<SqliteQualityAuditService>());
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
        services.AddSingleton<CollectionEnvironmentProbe>();
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
