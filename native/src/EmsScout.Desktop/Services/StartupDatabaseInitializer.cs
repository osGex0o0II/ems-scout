using EmsScout.Application.Logging;
using EmsScout.Application.Settings;
using EmsScout.Infrastructure.Errors;
using EmsScout.Infrastructure.Migrations;
using EmsScout.Infrastructure.Storage;

namespace EmsScout.Desktop.Services;

public sealed class StartupDatabaseInitializer(
    AppDataPathService pathService,
    LegacyOutMigrationService legacyOutMigrationService,
    SqliteSchemaMigrator schemaMigrator,
    IApplicationLogger logger)
{
    public string LogPath => logger.CurrentLogPath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var paths = pathService.Capture();
        try
        {
            await legacyOutMigrationService
                .MigrateIfNeededAsync(
                    Path.Combine(pathService.WorkspaceRoot, "out"),
                    paths.DataDirectory,
                    cancellationToken)
                .ConfigureAwait(true);
            if (File.Exists(paths.DatabasePath))
            {
                await schemaMigrator
                    .MigrateAsync(paths.DatabasePath, cancellationToken: cancellationToken)
                    .ConfigureAwait(true);
            }
            else
            {
                await schemaMigrator
                    .CreateNewAsync(paths.DatabasePath, cancellationToken)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            var failure = ApplicationFailureClassifier.Classify(ex);
            logger.Write(new ApplicationLogEvent(
                ApplicationLogLevel.Critical,
                "startup",
                "database_initialization_failed",
                failure.Title,
                new ApplicationLogContext(
                    Stage: "database_initialization",
                    ErrorCode: failure.Code,
                    Retryable: failure.IsRetryable),
                ex,
                new Dictionary<string, object?>
                {
                    ["databasePath"] = paths.DatabasePath,
                    ["suggestedAction"] = failure.SuggestedAction,
                },
                AlwaysWrite: true));
            throw;
        }
    }
}
