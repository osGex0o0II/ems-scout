param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [switch]$SkipLaunch
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-package-common.ps1')

$manifest = Read-NativePackageManifest $PackageDirectory
$packageRoot = Resolve-NativePackageDirectory $PackageDirectory
$mainPackage = Join-Path $packageRoot $manifest.MainPackage
$certificatePath = Join-Path $packageRoot $manifest.CertificateFile
$rootCertificatePath = $null
if ($manifest.RootCertificateFile) {
    $rootCertificatePath = Join-Path $packageRoot $manifest.RootCertificateFile
}
$dependencyDirectory = Join-Path $packageRoot (Join-Path 'Dependencies' $manifest.Architecture)
$dependencies = @(Get-ChildItem -LiteralPath $dependencyDirectory -File -Filter '*.msix' -ErrorAction SilentlyContinue)

Stop-NativeDesktopProcess
Import-NativePackageCertificate -CertificatePath $certificatePath -RootCertificatePath $rootCertificatePath -InstallMachineTrust
foreach ($dependency in $dependencies) {
    try {
        Add-AppxPackage -Path $dependency.FullName -ForceApplicationShutdown -ErrorAction Stop
    } catch {
        if ($_.Exception.Message -notmatch '0x80073D06') {
            throw
        }
        Write-Warning "Skipping older dependency because Windows already has a newer compatible version: $($dependency.Name)"
    }
}
Add-AppxPackage -Path $mainPackage -ForceApplicationShutdown

$installed = Get-NativeInstalledPackage
if ($null -eq $installed -or ([version]$installed.Version -ne (Assert-NativeVersion $manifest.Version))) {
    throw "Installed package version does not match manifest $($manifest.Version)."
}

$workspaceRoot = Set-NativeWorkspaceMarker $PackageDirectory
$shortcut = Set-NativeShortcutWithRetry $installed.InstallLocation
if (-not $SkipLaunch) {
    Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList "shell:AppsFolder\$($manifest.AppUserModelId)"
}

Write-Output "Native package installed: $($installed.Version)"
if ($workspaceRoot) { Write-Output "Workspace root: $workspaceRoot" }
Write-Output "Desktop shortcut: $shortcut"
