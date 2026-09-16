param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [switch]$SkipLaunch
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-package-common.ps1')
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$manifest = Read-NativePackageManifest $PackageDirectory
$targetVersion = Assert-NativeVersion $manifest.Version
$installed = Get-NativeInstalledPackage
if ($null -eq $installed) {
    throw 'EMS Scout is not installed. Use native-install.ps1 first.'
}
if ([version]$installed.Version -ge $targetVersion) {
    throw "Update version $targetVersion must be greater than installed version $($installed.Version)."
}

Stop-NativeDesktopProcess

# Closing the WinUI window may persist the latest placement. Snapshot after the
# graceful shutdown so the update guard detects external changes, not our own close.
$settingsPath = Join-Path (Get-NativeUserDataPath) 'settings.json'
$settingsHash = $null
if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
    $settingsHash = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
}

$packageRoot = Resolve-NativePackageDirectory $PackageDirectory
$certificatePath = Join-Path $packageRoot $manifest.CertificateFile
$rootCertificatePath = $null
if ($manifest.RootCertificateFile) {
    $rootCertificatePath = Join-Path $packageRoot $manifest.RootCertificateFile
}
Import-NativePackageCertificate -CertificatePath $certificatePath -RootCertificatePath $rootCertificatePath -InstallMachineTrust
$dependencyDirectory = Join-Path $packageRoot (Join-Path 'Dependencies' $manifest.Architecture)
foreach ($dependency in @(Get-ChildItem -LiteralPath $dependencyDirectory -File -Filter '*.msix' -ErrorAction SilentlyContinue)) {
    try {
        Add-AppxPackage -Path $dependency.FullName -ForceApplicationShutdown -ErrorAction Stop
    } catch {
        if ($_.Exception.Message -notmatch '0x80073D06') {
            throw
        }
        Write-Warning "Skipping older dependency because Windows already has a newer compatible version: $($dependency.Name)"
    }
}
Add-AppxPackage -Path (Join-Path $packageRoot $manifest.MainPackage) -ForceApplicationShutdown -ForceUpdateFromAnyVersion

$updated = Get-NativeInstalledPackage
if ($null -eq $updated -or ([version]$updated.Version -ne $targetVersion)) {
    throw "Installed package version does not match update target $targetVersion."
}
if ($null -ne $settingsHash) {
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        throw 'User settings disappeared during update.'
    }
    $updatedHash = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
    if ($updatedHash -ne $settingsHash) {
        throw 'User settings changed during update.'
    }
}

$workspaceRoot = Set-NativeWorkspaceMarker $PackageDirectory -WorkspaceRoot $repositoryRoot
$shortcut = Set-NativeShortcutWithRetry $updated.InstallLocation
if (-not $SkipLaunch) {
    Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList "shell:AppsFolder\$($manifest.AppUserModelId)"
}

Write-Output "Native package updated: $($updated.Version)"
if ($workspaceRoot) { Write-Output "Workspace root: $workspaceRoot" }
Write-Output "Desktop shortcut: $shortcut"
