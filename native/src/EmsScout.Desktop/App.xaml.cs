using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using EmsScout.Application.Collection;
using EmsScout.Application;
using EmsScout.Application.Attention;
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
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var appActivationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        var desktopLaunchArguments = appActivationArguments.Data is ILaunchActivatedEventArgs launchArguments
            ? launchArguments.Arguments
            : args.Arguments;
        var launchOptions = AppLaunchOptions.Parse(desktopLaunchArguments);
        Services = ConfigureServices(launchOptions);

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
        _window.Closed += (_, _) =>
        {
            if (Services is IDisposable disposable) disposable.Dispose();
        };
        _window.Activate();
    }

    private static IServiceProvider ConfigureServices(AppLaunchOptions launchOptions)
    {
        var workspaceRoot = WorkspaceLocator.LocateRepositoryRoot();

        var services = new ServiceCollection();
        services.AddSingleton(new InventorySummarizer());
        services.AddSingleton<IApplicationLogger>(provider => new NdjsonApplicationLogger(
            Path.Combine(AppStorageDefaults.ProductDirectory, "logs"),
            enabled: () => provider.GetRequiredService<AppSettingsService>().Load().SaveNdjsonLog));
        services.AddSingleton(launchOptions.SettingsPathOverride is null
            ? new AppSettingsService()
            : new AppSettingsService(launchOptions.SettingsPathOverride));
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
        services.AddSingleton<IRecaptureLocationSource>(provider => new SqliteRecaptureLocationSource(
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
        services.AddSingleton<IAttentionIssueRepository>(provider => new SqliteAttentionIssueRepository(
            () => provider.GetRequiredService<AppDataPathService>().DatabasePath));
        services.AddSingleton<IAreaGroupRepository>(provider => new SqliteAreaGroupRepository(
            () => provider.GetRequiredService<AppDataPathService>().DatabasePath));
        services.AddSingleton<NavigationService>();
        services.AddSingleton<DataContextService>();
        services.AddSingleton<INavigationService>(provider => provider.GetRequiredService<NavigationService>());
        services.AddSingleton<WindowHandleProvider>();
        services.AddSingleton<DashboardOverviewService>();
        services.AddSingleton(new NodeCollectionTaskRunner(workspaceRoot));
        services.AddSingleton<CollectionEnvironmentProbe>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<CollectionTaskViewModel>();
        services.AddSingleton<DataViewModel>();
        services.AddSingleton<AuditViewModel>();
        services.AddTransient<GroupsViewModel>();
        services.AddTransient<DateManagementViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<DiagnosticsViewModel>();
        return services.BuildServiceProvider();
    }
}
