namespace EmsScout.Application.Updates;

public interface IAppVersionProvider
{
    Version CurrentVersion { get; }
}

public interface IAppUpdateLauncher
{
    Task<bool> LaunchAsync(
        Uri appInstallerUri,
        CancellationToken cancellationToken = default);
}

public sealed record AppUpdateOptions(
    Uri ManifestUri,
    string ExpectedPackageName,
    string ExpectedPublisher,
    IReadOnlyCollection<string> AllowedPackageHosts,
    int MaxManifestBytes = 256 * 1024);

public sealed record AppUpdateCheckResult(
    Version CurrentVersion,
    Version AvailableVersion,
    bool IsUpdateAvailable,
    Uri AppInstallerUri);
