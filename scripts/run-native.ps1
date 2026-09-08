param(
  [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-package-common.ps1')

$identity = Get-NativePackageIdentity
$installed = Get-NativeInstalledPackage
if ($null -eq $installed) {
    throw 'EMS Scout is not installed. Build and install the Native MSIX package before launching.'
}

$targetExecutable = Join-Path $installed.InstallLocation 'EmsScout.Desktop.exe'
if (-not (Test-Path -LiteralPath $targetExecutable -PathType Leaf)) {
    throw "Installed Native executable is missing: $targetExecutable"
}

Stop-NativeDesktopProcess
Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') `
    -ArgumentList "shell:AppsFolder\$($identity.AppUserModelId)"

Write-Output "Native package launched: $($installed.Version)"
Write-Output "Install location: $($installed.InstallLocation)"
