namespace EmsScout.Tests;

public sealed class NativeShortcutContractTests
{
    [Fact]
    public void ShortcutInstallerTargetsTheRegisteredNativePackage()
    {
        var root = LocateRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "install-native-shortcut.ps1"));

        Assert.Contains("native-package-common.ps1", script);
        Assert.Contains("Get-NativeInstalledPackage", script);
        Assert.Contains("Set-NativeShortcut", script);
        Assert.DoesNotContain("EmsScout.Legacy", script);
    }

    [Fact]
    public void DesktopProjectAcceptsAnInjectedPackageManifest()
    {
        var root = LocateRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "EmsScout.Desktop.csproj"));

        Assert.Contains("AppxManifest Include=\"$(PackageManifestPath)\"", project, StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentLaunchUsesTheRegisteredPackageInsteadOfAnUnpackagedExecutable()
    {
        var root = LocateRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "run-native.ps1"));

        Assert.Contains("Get-NativeInstalledPackage", script, StringComparison.Ordinal);
        Assert.Contains("shell:AppsFolder", script, StringComparison.Ordinal);
        Assert.Contains("Start-Process", script, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EmsScout.Desktop.csproj", script, StringComparison.Ordinal);
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
