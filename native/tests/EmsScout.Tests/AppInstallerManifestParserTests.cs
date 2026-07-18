using EmsScout.Application.Updates;

namespace EmsScout.Tests;

public sealed class AppInstallerManifestParserTests
{
    [Fact]
    public void ParseReadsTheMainPackageFromANamespacedManifest()
    {
        var manifest = AppInstallerManifestParser.Parse(ValidManifest());

        Assert.Equal(new Uri("https://github.com/osGex0o0II/ems-scout/releases/latest/download/EmsScout.appinstaller"), manifest.AppInstallerUri);
        Assert.Equal("1FACE092-146B-4AE5-83DB-3990E6AE8371", manifest.PackageName);
        Assert.Equal("CN=EMS Scout", manifest.Publisher);
        Assert.Equal(new Version(1, 2, 3, 4), manifest.Version);
        Assert.Equal(new Uri("https://github.com/osGex0o0II/ems-scout/releases/download/v1.2.3.4/EmsScout-1.2.3.4-x64.msix"), manifest.PackageUri);
    }

    [Fact]
    public void ParseRejectsDocumentTypeDeclarations()
    {
        var xml = "<!DOCTYPE AppInstaller [<!ENTITY xxe SYSTEM 'file:///windows/win.ini'>]>" +
                  ValidManifest().Replace("EMS Scout", "&xxe;", StringComparison.Ordinal);

        Assert.Throws<FormatException>(() => AppInstallerManifestParser.Parse(xml));
    }

    [Fact]
    public void ParseRejectsMissingMainPackage()
    {
        const string xml = """
            <AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018"
                          Uri="https://github.com/example/EmsScout.appinstaller"
                          Version="1.0.0.0" />
            """;

        var error = Assert.Throws<FormatException>(() => AppInstallerManifestParser.Parse(xml));
        Assert.Contains("MainPackage", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsInvalidFourPartVersion()
    {
        var xml = ValidManifest().Replace("Version=\"1.2.3.4\"", "Version=\"1.2.3\"", StringComparison.Ordinal);

        var error = Assert.Throws<FormatException>(() => AppInstallerManifestParser.Parse(xml));
        Assert.Contains("four-part", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseRejectsRelativePackageUri()
    {
        var xml = ValidManifest().Replace(
            "https://github.com/osGex0o0II/ems-scout/releases/download/v1.2.3.4/EmsScout-1.2.3.4-x64.msix",
            "EmsScout.msix",
            StringComparison.Ordinal);

        var error = Assert.Throws<FormatException>(() => AppInstallerManifestParser.Parse(xml));
        Assert.Contains("absolute", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ValidManifest(
        string packageName = "1FACE092-146B-4AE5-83DB-3990E6AE8371",
        string publisher = "CN=EMS Scout",
        string version = "1.2.3.4",
        string packageUri = "https://github.com/osGex0o0II/ems-scout/releases/download/v1.2.3.4/EmsScout-1.2.3.4-x64.msix") => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018"
                      Uri="https://github.com/osGex0o0II/ems-scout/releases/latest/download/EmsScout.appinstaller"
                      Version="{{version}}">
          <MainPackage Name="{{packageName}}"
                       Publisher="{{publisher}}"
                       Version="{{version}}"
                       ProcessorArchitecture="x64"
                       Uri="{{packageUri}}" />
          <UpdateSettings>
            <OnLaunch HoursBetweenUpdateChecks="24" ShowPrompt="true" UpdateBlocksActivation="false" />
          </UpdateSettings>
        </AppInstaller>
        """;
}
