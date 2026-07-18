namespace EmsScout.Application.Updates;

public sealed record AppInstallerManifest(
    Uri AppInstallerUri,
    string PackageName,
    string Publisher,
    Version Version,
    Uri PackageUri);
