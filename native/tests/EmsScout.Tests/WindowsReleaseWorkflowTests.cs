namespace EmsScout.Tests;

public sealed class WindowsReleaseWorkflowTests
{
    [Fact]
    public void ReleaseWorkflowFailsClosedAndPublishesSignedAppInstallerAssets()
    {
        var workflow = ReadRequiredFile(".github", "workflows", "release-windows-x64.yml");

        Assert.Contains("tags:", workflow);
        Assert.Contains("'v*.*.*.*'", workflow);
        Assert.Contains("workflow_dispatch:", workflow);
        Assert.Contains("if: github.ref_type == 'tag'", workflow);
        Assert.DoesNotContain("pull_request:", workflow);
        Assert.Contains("contents: write", workflow);
        Assert.Contains("^v(\\d+\\.\\d+\\.\\d+\\.\\d+)$", workflow);

        Assert.Contains("secrets.EMS_SIGNING_CERTIFICATE_BASE64", workflow);
        Assert.Contains("secrets.EMS_SIGNING_CERTIFICATE_PASSWORD", workflow);
        Assert.Contains("Import-PfxCertificate", workflow);
        Assert.Contains("Cert:\\CurrentUser\\My", workflow);
        Assert.Contains("HasPrivateKey", workflow);
        Assert.Contains("Subject -ne 'CN=EMS Scout'", workflow);
        Assert.Contains("NotBefore", workflow);
        Assert.Contains("NotAfter", workflow);
        Assert.Contains("AddDays(30)", workflow);
        Assert.Contains("EphemeralKeySet", workflow);

        var privateKeyCheck = workflow.IndexOf("HasPrivateKey", StringComparison.Ordinal);
        var subjectCheck = workflow.IndexOf("Subject -ne 'CN=EMS Scout'", StringComparison.Ordinal);
        var notBeforeCheck = workflow.IndexOf("NotBefore", StringComparison.Ordinal);
        var expiryCheck = workflow.IndexOf("NotAfter", StringComparison.Ordinal);
        var certificateImport = workflow.IndexOf("Import-PfxCertificate", StringComparison.Ordinal);
        Assert.True(privateKeyCheck < certificateImport, "Private-key validation must happen before store import.");
        Assert.True(subjectCheck < certificateImport, "Subject validation must happen before store import.");
        Assert.True(notBeforeCheck < certificateImport, "Certificate start-date validation must happen before store import.");
        Assert.True(expiryCheck < certificateImport, "Expiry validation must happen before store import.");

        var importStep = workflow.IndexOf("Import production signing certificate", StringComparison.Ordinal);
        var packageStep = workflow.IndexOf("Build signed release package", StringComparison.Ordinal);
        var verifyStep = workflow.IndexOf("Verify release package signature", StringComparison.Ordinal);
        var publishStep = workflow.IndexOf("Publish GitHub Release", StringComparison.Ordinal);
        var cleanupStep = workflow.IndexOf("Cleanup production signing material", StringComparison.Ordinal);
        Assert.True(importStep >= 0);
        Assert.True(packageStep > importStep);
        Assert.True(verifyStep > packageStep);
        Assert.True(publishStep > verifyStep);
        Assert.True(cleanupStep > publishStep);

        var beforeImport = workflow[..importStep];
        Assert.DoesNotContain("secrets.EMS_SIGNING_CERTIFICATE_BASE64", beforeImport);
        Assert.DoesNotContain("secrets.EMS_SIGNING_CERTIFICATE_PASSWORD", beforeImport);

        Assert.Contains("-PackageVersion '${{ steps.version.outputs.value }}'", workflow);
        Assert.Contains("scripts/new-appinstaller.ps1", workflow);
        Assert.Contains("releases/latest/download/EmsScout.appinstaller", workflow);
        Assert.Contains("EmsScout-${version}-x64.msix", workflow);
        Assert.Contains("scripts/verify-msix-signature.ps1", workflow);
        Assert.Contains("http://timestamp.digicert.com", workflow);
        Assert.Contains("-RequireTimestamp", workflow);
        Assert.Contains("gh release upload", workflow);
        Assert.Contains("EmsScout.appinstaller", workflow);
        Assert.Contains("--clobber", workflow);

        var cleanup = workflow[cleanupStep..];
        Assert.Contains("if: always()", cleanup);
        Assert.Contains("ems-scout-release-thumbprint.txt", workflow);
        Assert.Contains("Get-Content -LiteralPath $thumbprintPath", cleanup);
        Assert.Contains("Remove-Item -LiteralPath $pfxPath", cleanup);
        Assert.Contains("Remove-Item -LiteralPath $thumbprintPath", cleanup);
        Assert.Contains("Remove-Item -LiteralPath $certificatePath", cleanup);
        Assert.Contains("-DeleteKey", cleanup);
        Assert.DoesNotContain("EMS_SIGNING_CERTIFICATE_BASE64 }}' |", workflow);
    }

    private static string ReadRequiredFile(params string[] segments)
    {
        var path = Path.Combine(new[] { LocateRepositoryRoot() }.Concat(segments).ToArray());
        Assert.True(File.Exists(path), $"Required repository file is missing: {path}");
        return File.ReadAllText(path);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "native")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate the EMS Scout repository root.");
    }
}
