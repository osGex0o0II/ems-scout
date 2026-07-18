using System.Diagnostics;
using System.Xml.Linq;

namespace EmsScout.Tests;

public sealed class AppInstallerReleaseContractTests
{
    [Fact]
    public async Task GeneratorCreatesAValidatedAppInstallerDocument()
    {
        var root = LocateRepositoryRoot();
        var script = Path.Combine(root, "scripts", "new-appinstaller.ps1");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "ems-appinstaller-test-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(outputDirectory, "EmsScout.appinstaller");

        try
        {
            var result = await RunPowerShellAsync(
                script,
                "-Version", "1.2.3.4",
                "-AppInstallerUri", "https://github.com/osGex0o0II/ems-scout/releases/latest/download/EmsScout.appinstaller",
                "-PackageUri", "https://github.com/osGex0o0II/ems-scout/releases/download/v1.2.3.4/EmsScout-1.2.3.4-x64.msix",
                "-OutputPath", output);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(output), result.StandardError);
            var document = XDocument.Load(output);
            var rootElement = Assert.IsType<XElement>(document.Root);
            var mainPackage = Assert.Single(rootElement.Elements(), x => x.Name.LocalName == "MainPackage");
            var onLaunch = Assert.Single(rootElement.Descendants(), x => x.Name.LocalName == "OnLaunch");

            Assert.Equal("1.2.3.4", rootElement.Attribute("Version")?.Value);
            Assert.Equal(
                "https://github.com/osGex0o0II/ems-scout/releases/latest/download/EmsScout.appinstaller",
                rootElement.Attribute("Uri")?.Value);
            Assert.Equal("1FACE092-146B-4AE5-83DB-3990E6AE8371", mainPackage.Attribute("Name")?.Value);
            Assert.Equal("CN=EMS Scout", mainPackage.Attribute("Publisher")?.Value);
            Assert.Equal("1.2.3.4", mainPackage.Attribute("Version")?.Value);
            Assert.Equal("x64", mainPackage.Attribute("ProcessorArchitecture")?.Value);
            Assert.Equal("true", onLaunch.Attribute("ShowPrompt")?.Value);
            Assert.Equal("false", onLaunch.Attribute("UpdateBlocksActivation")?.Value);
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratorRejectsNonFourPartVersionBeforeWriting()
    {
        var root = LocateRepositoryRoot();
        var script = Path.Combine(root, "scripts", "new-appinstaller.ps1");
        var output = Path.Combine(Path.GetTempPath(), "ems-invalid-" + Guid.NewGuid().ToString("N") + ".appinstaller");

        var result = await RunPowerShellAsync(
            script,
            "-Version", "1.2.3",
            "-AppInstallerUri", "https://github.com/example/EmsScout.appinstaller",
            "-PackageUri", "https://github.com/example/EmsScout.msix",
            "-OutputPath", output);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void NativePackagerPassesTheRequestedVersionToMsixBuild()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "scripts", "package-native.ps1"));
        var project = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "EmsScout.Desktop.csproj"));

        Assert.Contains("[ValidatePattern('^\\d+\\.\\d+\\.\\d+\\.\\d+$')]", source);
        Assert.Contains("[string]$PackageVersion = '1.0.0.0'", source);
        Assert.Contains("XmlNamespaceManager", source);
        Assert.Contains("EmsScoutPackageManifestPath", source);
        Assert.Contains("Embedded MSIX version", source);
        Assert.Contains("Microsoft.WindowsAppRuntime.2", source);
        Assert.Contains("<AppxManifest Include=\"$(EmsScoutPackageManifestPath)\"", project);
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string script, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
