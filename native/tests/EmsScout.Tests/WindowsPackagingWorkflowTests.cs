namespace EmsScout.Tests;

public sealed class WindowsPackagingWorkflowTests
{
    [Fact]
    public void CertificateHelperCreatesCurrentUserCodeSigningIdentityWithoutPrivateKeyExport()
    {
        var script = ReadRequiredFile("scripts", "new-test-signing-certificate.ps1");

        Assert.Contains("New-SelfSignedCertificate", script);
        Assert.Contains("CN=EMS Scout", script);
        Assert.Contains("Cert:\\CurrentUser\\My", script);
        Assert.Contains("TrustedPeople", script);
        Assert.DoesNotContain("Cert:\\CurrentUser\\Root", script);
        Assert.Contains("TrustForInstall", script);
        Assert.Contains("Cert:\\LocalMachine\\Root", script);
        Assert.Contains("StoreLocation]::LocalMachine", script);
        Assert.Contains("X509Store", script);
        Assert.Contains("-KeyExportPolicy NonExportable", script);
        Assert.Contains("catch", script);
        Assert.Contains("Remove-Item -LiteralPath $personalStorePath", script);
        Assert.Contains("Remove-Item -LiteralPath $personalStorePath -DeleteKey -Force", script);
        Assert.DoesNotContain("Export-PfxCertificate", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".pfx", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageBuildAcceptsThumbprintAndRejectsAnInvalidSignature()
    {
        var script = ReadRequiredFile("scripts", "package-native.ps1");

        Assert.Contains("PackageCertificateThumbprint", script);
        Assert.Contains("AppxPackageSigningEnabled=true", script);
        Assert.Contains("PackageCertificateThumbprint=$PackageCertificateThumbprint", script);
        Assert.Contains("PackageTimestampServerUrl", script);
        Assert.Contains("AppxPackageSigningTimestampServerUrl=$PackageTimestampServerUrl", script);
        Assert.Contains("AppxPackageSigningTimestampDigestAlgorithm=SHA256", script);
        Assert.Contains("-RequireTimestamp:($requireTimestamp)", script);
        Assert.Contains("verify-msix-signature.ps1", script);
    }

    [Fact]
    public void ReleasePackageBundlesTheWindowsAppRuntime()
    {
        var project = ReadRequiredFile(
            "native",
            "src",
            "EmsScout.Desktop",
            "EmsScout.Desktop.csproj");

        Assert.Contains("<WindowsAppSDKSelfContained", project);
        Assert.Contains("Condition=\"'$(Configuration)' != 'Debug'\"", project);
        Assert.Contains(">true</WindowsAppSDKSelfContained>", project);
    }

    [Fact]
    public void InstallSmokeFailsClosedAndRunsTwoCompleteLifecycleRounds()
    {
        var script = ReadRequiredFile("scripts", "test-msix-install.ps1");

        Assert.Contains("1FACE092-146B-4AE5-83DB-3990E6AE8371", script);
        Assert.Contains("Get-AppxPackage", script);
        Assert.Contains("already registered", script);
        Assert.Contains("verify-msix-signature.ps1", script);
        Assert.Contains("ExpectedSignerThumbprint", script);
        Assert.Contains("Add-AppxPackage", script);
        Assert.Contains("IApplicationActivationManager", script);
        Assert.Contains("PackageActivator", script);
        Assert.DoesNotContain("[EmsScout.PackageSmoke.IApplicationActivationManager]$activationManager", script);
        Assert.Contains("Remove-AppxPackage", script);
        Assert.Contains("foreach ($round in 1..2)", script);
        Assert.Contains("OwnershipMarkerPath", script);
        Assert.Contains("Set-Content -LiteralPath $OwnershipMarkerPath", script);
        Assert.Contains("$lifecycleError", script);
        Assert.Contains("$cleanupError", script);
        Assert.Contains("Cleanup also failed", script);
        Assert.Contains("finally", script);
        Assert.Contains("Package cleanup failed", script);

        Assert.DoesNotContain("No x64 MSIX dependencies were found", script);
        Assert.Contains("if ($dependencyPackages.Count -gt 0)", script);
        var dependencyValidation = script.IndexOf("$dependencyPackages = @(", StringComparison.Ordinal);
        var markerWrite = script.IndexOf("Set-Content -LiteralPath $OwnershipMarkerPath", StringComparison.Ordinal);
        Assert.True(markerWrite > dependencyValidation, "Ownership marker must follow dependency discovery.");
    }

    [Fact]
    public void SignatureVerifierAllowsOnlyTheExpectedSelfSignedUntrustedRootStatus()
    {
        var script = ReadRequiredFile("scripts", "verify-msix-signature.ps1");

        Assert.Contains("Get-AuthenticodeSignature", script);
        Assert.Contains("SignerCertificate.Thumbprint", script);
        Assert.Contains("RequireTimestamp", script);
        Assert.Contains("TimeStamperCertificate", script);
        Assert.Contains("WinVerifyTrust", script);
        Assert.Contains("0x800B0109", script);
        Assert.Contains("Cert:\\CurrentUser\\TrustedPeople", script);
        Assert.Contains("throw", script);
    }

    [Fact]
    public void WorkflowSignsInstallsAndAlwaysCleansUpBeforeUploading()
    {
        var workflow = ReadRequiredFile(".github", "workflows", "windows-x64.yml");
        var certificateStep = workflow.IndexOf(
            "Create ephemeral test signing certificate",
            StringComparison.Ordinal);
        var packageStep = workflow.IndexOf("Build Windows x64 package", StringComparison.Ordinal);
        var installStep = workflow.IndexOf(
            "Install, launch, reinstall, and uninstall package",
            StringComparison.Ordinal);
        var packageCleanupStep = workflow.IndexOf(
            "Cleanup owned test package",
            StringComparison.Ordinal);
        var certificateCleanupStep = workflow.IndexOf(
            "Cleanup test signing certificate",
            StringComparison.Ordinal);
        var uploadStep = workflow.IndexOf("Upload verification evidence", StringComparison.Ordinal);

        Assert.True(certificateStep >= 0, "Workflow must create an ephemeral signing certificate.");
        Assert.True(packageStep > certificateStep, "Package build must use the created certificate.");
        Assert.True(installStep > packageStep, "Install smoke must use the newly built package.");
        Assert.True(packageCleanupStep > installStep, "Package cleanup must run after lifecycle smoke.");
        Assert.True(certificateCleanupStep > packageCleanupStep, "Certificate cleanup must be an independent later step.");
        Assert.True(uploadStep > certificateCleanupStep, "Artifact upload must run after both cleanup steps.");
        Assert.Contains("id: test_signing", workflow);
        Assert.Contains("new-test-signing-certificate.ps1 -TrustForInstall", workflow);
        Assert.Contains("Validate packaging PowerShell syntax", workflow);
        Assert.Contains("System.Management.Automation.Language.Parser", workflow);
        Assert.Contains("scripts/verify-msix-signature.ps1", workflow);
        Assert.Contains("UnitTestResult", workflow);
        Assert.Contains("::error title=Native test failed::", workflow);
        Assert.Contains("::error title=MSIX lifecycle failed::", workflow);
        Assert.Contains("steps.test_signing.outputs.thumbprint", workflow);
        Assert.Contains("-PackageCertificateThumbprint", workflow);
        Assert.Contains("test-msix-install.ps1", workflow);
        Assert.Contains("-ExpectedSignerThumbprint '${{ steps.test_signing.outputs.thumbprint }}'", workflow);
        Assert.Contains("-OwnershipMarkerPath artifacts/ci/msix-install-owned.json", workflow);
        Assert.Contains("if: always()", workflow);
        Assert.Contains("Cert:\\CurrentUser\\My", workflow);
        Assert.Contains("Cert:\\CurrentUser\\TrustedPeople", workflow);
        Assert.Contains("Cert:\\LocalMachine\\Root", workflow);
        Assert.Contains("!**/*.pfx", workflow);
        Assert.Contains("!**/*.pvk", workflow);

        var packageCleanup = workflow[packageCleanupStep..certificateCleanupStep];
        var ownershipCheck = packageCleanup.IndexOf(
            "if (Test-Path -LiteralPath $ownershipMarker)",
            StringComparison.Ordinal);
        var packageRemoval = packageCleanup.IndexOf("Get-AppxPackage", StringComparison.Ordinal);
        Assert.True(ownershipCheck >= 0, "Cleanup must require an install ownership marker.");
        Assert.True(packageRemoval > ownershipCheck, "Cleanup must not remove a pre-existing package.");
        Assert.Contains("$ownership.packageSha256", packageCleanup);
        Assert.Contains("Get-FileHash", packageCleanup);

        var certificateCleanup = workflow[certificateCleanupStep..uploadStep];
        Assert.Contains("if: always()", packageCleanup);
        Assert.Contains("if: always()", certificateCleanup);
        Assert.Contains("Cert:\\CurrentUser\\My", certificateCleanup);
        Assert.Contains("Remove-Item -LiteralPath $certificatePath -DeleteKey -Force", certificateCleanup);
        Assert.Contains("Cert:\\CurrentUser\\TrustedPeople", certificateCleanup);
        Assert.Contains("Cert:\\LocalMachine\\Root", certificateCleanup);
    }

    [Fact]
    public void WorkflowMigrationUsesAnEligibleArchivedCoreFixture()
    {
        var workflow = ReadRequiredFile(".github", "workflows", "windows-x64.yml");
        var fixture = ReadRequiredFile(
            "tests",
            "fixtures",
            "schema-baselines",
            "archived-core-v0.sql");

        Assert.Contains("tests/fixtures/schema-baselines/archived-core-v0.sql", workflow);
        Assert.DoesNotContain("tests/contract-audit/fixtures/schema-v0.sql", workflow);
        Assert.Contains("PRAGMA user_version = 0", fixture);
        Assert.Contains("menu_clicked TEXT", fixture);
        Assert.Contains("sub_idx INTEGER", fixture);
        Assert.Contains("on_href TEXT", fixture);
        Assert.Contains("switch TEXT", fixture);
        Assert.Contains("comm TEXT", fixture);
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
