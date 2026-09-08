param(
    [switch]$PurgeData,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-package-common.ps1')

$installed = Get-NativeInstalledPackage
$shortcutPath = Get-NativeShortcutPath
if ($WhatIf) {
    Write-Output "Would stop EMS Scout, remove package $($installed.PackageFullName), and remove $shortcutPath."
    if ($PurgeData) {
        Write-Output "Would remove user data: $(Get-NativeUserDataPath)"
    }
    exit 0
}

Stop-NativeDesktopProcess
Remove-NativeShortcut | Out-Null
if ($null -ne $installed) {
    Remove-AppxPackage -Package $installed.PackageFullName
}

if ($PurgeData) {
    $userDataPath = Get-NativeUserDataPath
    Assert-NativeUserDataPath $userDataPath
    $confirmation = Read-Host "Type PURGE to remove $userDataPath"
    if ($confirmation -ne 'PURGE') {
        throw 'Purge cancelled; application was uninstalled but user data was preserved.'
    }
    if (Test-Path -LiteralPath $userDataPath) {
        Remove-Item -LiteralPath $userDataPath -Recurse -Force
    }
}

if ($null -ne (Get-NativeInstalledPackage)) {
    throw 'EMS Scout package is still registered after uninstall.'
}

Write-Output 'Native package uninstalled. User data was preserved unless PURGE was confirmed.'
