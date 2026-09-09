namespace EmsScout.Tests;

public sealed class NativePackageContractTests
{
    [Fact]
    public void PackageScriptsKeepStableIdentityAndVersionedOutput()
    {
        var root = LocateRepositoryRoot();
        var common = File.ReadAllText(Path.Combine(root, "scripts", "native-package-common.ps1"));
        var package = File.ReadAllText(Path.Combine(root, "scripts", "native-package.ps1"));

        Assert.Contains("1FACE092-146B-4AE5-83DB-3990E6AE8371", common, StringComparison.Ordinal);
        Assert.Contains("ggf25w21tn4m2", common, StringComparison.Ordinal);
        Assert.Contains("Assert-NativeVersion", package, StringComparison.Ordinal);
        Assert.Contains("package-manifest.json", package, StringComparison.Ordinal);
        Assert.Contains("$outputRootFull $Version", package, StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleScriptsExposeInstallUpdateAndSafeUninstallContracts()
    {
        var root = LocateRepositoryRoot();
        var install = File.ReadAllText(Path.Combine(root, "scripts", "native-install.ps1"));
        var update = File.ReadAllText(Path.Combine(root, "scripts", "native-update.ps1"));
        var uninstall = File.ReadAllText(Path.Combine(root, "scripts", "native-uninstall.ps1"));

        Assert.Contains("Add-AppxPackage", install, StringComparison.Ordinal);
        Assert.Contains("ForceUpdateFromAnyVersion", update, StringComparison.Ordinal);
        Assert.Contains("settingsHash", update, StringComparison.Ordinal);
        Assert.Contains("PurgeData", uninstall, StringComparison.Ordinal);
        Assert.Contains("Assert-NativeUserDataPath", uninstall, StringComparison.Ordinal);
        Assert.Contains("Set-NativeWorkspaceMarker", install, StringComparison.Ordinal);
        Assert.Contains("Set-NativeWorkspaceMarker", update, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateSnapshotsSettingsAfterGracefulShutdown()
    {
        var root = LocateRepositoryRoot();
        var update = File.ReadAllText(Path.Combine(root, "scripts", "native-update.ps1"));

        var stopIndex = update.IndexOf("Stop-NativeDesktopProcess", StringComparison.Ordinal);
        var hashIndex = update.IndexOf("$settingsHash =", StringComparison.Ordinal);

        Assert.True(stopIndex >= 0);
        Assert.True(hashIndex > stopIndex, "The settings hash must be captured after the app saves its placement on shutdown.");
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
