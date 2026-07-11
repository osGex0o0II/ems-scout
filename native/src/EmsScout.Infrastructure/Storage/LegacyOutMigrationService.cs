using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Storage;

public sealed class LegacyOutMigrationService(TimeProvider? timeProvider = null)
{
    public const string MarkerFileName = "legacy-out-migration-v1.json";

    private static readonly JsonSerializerOptions MarkerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly string[] SnapshotFileNames =
    [
        "enum_full_v5.json",
        "collection_snapshot_v1.json",
    ];

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<LegacyOutMigrationResult> MigrateIfNeededAsync(
        string legacyOutDirectory,
        string destinationDataDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyOutDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDataDirectory);

        var sourceDirectory = Path.GetFullPath(legacyOutDirectory);
        var destinationDirectory = Path.GetFullPath(destinationDataDirectory);
        var sourceDatabasePath = Path.Combine(sourceDirectory, "ac.db");
        var destinationDatabasePath = Path.Combine(destinationDirectory, "ac.db");

        if (PathsEqual(sourceDirectory, destinationDirectory))
        {
            return Result(LegacyOutMigrationStatus.SourceAndDestinationAreSame);
        }

        if (File.Exists(destinationDatabasePath))
        {
            return Result(LegacyOutMigrationStatus.DestinationAlreadyInitialized);
        }

        if (!File.Exists(sourceDatabasePath))
        {
            return Result(LegacyOutMigrationStatus.SourceMissing);
        }

        Directory.CreateDirectory(destinationDirectory);
        var lockPath = Path.Combine(destinationDirectory, ".legacy-out-migration.lock");
        FileStream? migrationLock = null;
        string? stagingDirectory = null;

        try
        {
            migrationLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous);

            // Another process may have completed the migration before this process took the lock.
            if (File.Exists(destinationDatabasePath))
            {
                return Result(LegacyOutMigrationStatus.DestinationAlreadyInitialized);
            }

            stagingDirectory = Path.Combine(
                destinationDirectory,
                ".legacy-out-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            var stagedDatabasePath = Path.Combine(stagingDirectory, "ac.db");
            await CreateWalSafeSnapshotAsync(
                sourceDatabasePath,
                stagedDatabasePath,
                cancellationToken).ConfigureAwait(false);

            var stagedArtifacts = new List<StagedArtifact>
            {
                await DescribeAsync(stagedDatabasePath, cancellationToken).ConfigureAwait(false),
            };
            foreach (var sourcePath in EnumerateCompatibilityArtifacts(sourceDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(sourcePath);
                var destinationPath = Path.Combine(destinationDirectory, fileName);
                if (File.Exists(destinationPath))
                {
                    continue;
                }

                var stagedPath = Path.Combine(stagingDirectory, fileName);
                await CopyStableFileAsync(sourcePath, stagedPath, cancellationToken).ConfigureAwait(false);
                stagedArtifacts.Add(await DescribeAsync(stagedPath, cancellationToken).ConfigureAwait(false));
            }

            var completedAt = _timeProvider.GetUtcNow();
            var marker = new LegacyOutMigrationMarker(
                "ems.legacy-out-migration/v1",
                sourceDirectory,
                destinationDirectory,
                completedAt,
                stagedArtifacts.Select(artifact => new LegacyOutMigrationArtifact(
                    artifact.FileName,
                    artifact.Length,
                    artifact.Sha256)).ToArray());
            var stagedMarkerPath = Path.Combine(stagingDirectory, MarkerFileName);
            await File.WriteAllTextAsync(
                stagedMarkerPath,
                JsonSerializer.Serialize(marker, MarkerJsonOptions),
                cancellationToken).ConfigureAwait(false);

            // Compatibility files can be retried safely. Move the database last so its
            // presence remains the single initialization commit point.
            foreach (var artifact in stagedArtifacts.Where(artifact => artifact.FileName != "ac.db"))
            {
                File.Move(
                    Path.Combine(stagingDirectory, artifact.FileName),
                    Path.Combine(destinationDirectory, artifact.FileName));
            }
            File.Move(stagedDatabasePath, destinationDatabasePath);
            File.Move(stagedMarkerPath, Path.Combine(destinationDirectory, MarkerFileName));

            return new LegacyOutMigrationResult(
                LegacyOutMigrationStatus.Migrated,
                sourceDatabasePath,
                destinationDatabasePath,
                Path.Combine(destinationDirectory, MarkerFileName),
                marker.Artifacts);
        }
        finally
        {
            if (stagingDirectory is not null) TryDeleteDirectory(stagingDirectory);
            if (migrationLock is not null) await migrationLock.DisposeAsync().ConfigureAwait(false);
            TryDeleteFile(lockPath);
        }

        LegacyOutMigrationResult Result(LegacyOutMigrationStatus status) => new(
            status,
            sourceDatabasePath,
            destinationDatabasePath,
            null,
            []);
    }

    private static async Task CreateWalSafeSnapshotAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

        await using var source = new SqliteConnection(sourceConnectionString);
        await using var destination = new SqliteConnection(destinationConnectionString);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);

        await using var check = destination.CreateCommand();
        check.CommandText = "PRAGMA quick_check;";
        var value = Convert.ToString(
            await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (!string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The migrated SQLite snapshot failed PRAGMA quick_check: " + value);
        }
    }

    private static IEnumerable<string> EnumerateCompatibilityArtifacts(string sourceDirectory)
    {
        foreach (var fileName in SnapshotFileNames)
        {
            var path = Path.Combine(sourceDirectory, fileName);
            if (File.Exists(path)) yield return path;
        }

        foreach (var path in Directory
                     .EnumerateFiles(sourceDirectory, "realtime_*_latest.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            yield return path;
        }
    }

    private static async Task CopyStableFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StagedArtifact> DescribeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new StagedArtifact(Path.GetFileName(path), stream.Length, Convert.ToHexStringLower(hash));
    }

    private static bool PathsEqual(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(first),
        Path.TrimEndingDirectorySeparator(second),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record StagedArtifact(string FileName, long Length, string Sha256);

    private sealed record LegacyOutMigrationMarker(
        string ContractVersion,
        string SourceDirectory,
        string DestinationDirectory,
        DateTimeOffset CompletedAt,
        IReadOnlyList<LegacyOutMigrationArtifact> Artifacts);
}

public enum LegacyOutMigrationStatus
{
    Migrated,
    SourceMissing,
    DestinationAlreadyInitialized,
    SourceAndDestinationAreSame,
}

public sealed record LegacyOutMigrationArtifact(string FileName, long Length, string Sha256);

public sealed record LegacyOutMigrationResult(
    LegacyOutMigrationStatus Status,
    string SourceDatabasePath,
    string DestinationDatabasePath,
    string? MarkerPath,
    IReadOnlyList<LegacyOutMigrationArtifact> Artifacts);
