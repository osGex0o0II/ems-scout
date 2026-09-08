param([string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-package-common.ps1')

$installed = Get-NativeInstalledPackage
if ($null -eq $installed) {
  throw 'Native MSIX AppID is not registered. Install the package first with scripts/native-install.ps1.'
}

$shortcutPath = Set-NativeShortcut $installed.InstallLocation
Write-Output "Updated desktop shortcut: $shortcutPath"
Write-Output "Installed version: $($installed.Version)"
