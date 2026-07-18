param(
  [Parameter(Mandatory)]
  [string]$PackageRoot,
  [Parameter(Mandatory)]
  [string]$OwnershipMarkerPath,
  [Parameter(Mandatory)]
  [ValidatePattern('^[A-Fa-f0-9]{40}$')]
  [string]$ExpectedSignerThumbprint,
  [int]$LaunchTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$PackageIdentityName = '1FACE092-146B-4AE5-83DB-3990E6AE8371'

if ($LaunchTimeoutSeconds -lt 5 -or $LaunchTimeoutSeconds -gt 120) {
  throw 'LaunchTimeoutSeconds must be between 5 and 120.'
}

$resolvedPackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
$mainPackages = @(
  Get-ChildItem -LiteralPath $resolvedPackageRoot -Recurse -File -Filter '*.msix' |
    Where-Object {
      $_.FullName -notmatch '[\\/]Dependencies[\\/]' -and
      $_.Name -like 'EmsScout.Desktop*.msix'
    }
)
if ($mainPackages.Count -ne 1) {
  throw "Expected one EMS Scout MSIX, found $($mainPackages.Count) in $resolvedPackageRoot."
}

$mainPackage = $mainPackages[0]
$null = & (Join-Path $PSScriptRoot 'verify-msix-signature.ps1') `
  -PackagePath $mainPackage.FullName `
  -ExpectedSignerThumbprint $ExpectedSignerThumbprint

$existingPackages = @(Get-AppxPackage -Name $PackageIdentityName -ErrorAction Stop)
if ($existingPackages.Count -gt 0) {
  throw "EMS Scout package is already registered. Use a clean Windows user for install smoke."
}

$dependencyDirectory = Join-Path $mainPackage.Directory.FullName 'Dependencies\x64'
$dependencyPackages = @(
  Get-ChildItem -LiteralPath $dependencyDirectory -File -Filter '*.msix' -ErrorAction Stop
)
if ($dependencyPackages.Count -eq 0) {
  throw "No x64 MSIX dependencies were found: $dependencyDirectory"
}

$OwnershipMarkerPath = [IO.Path]::GetFullPath($OwnershipMarkerPath)
if (Test-Path -LiteralPath $OwnershipMarkerPath) {
  throw "Install ownership marker already exists: $OwnershipMarkerPath"
}
$ownershipDirectory = Split-Path -Parent $OwnershipMarkerPath
New-Item -ItemType Directory -Force -Path $ownershipDirectory | Out-Null
[ordered]@{
  contractVersion = 'ems.msix-install-ownership/v1'
  packageIdentityName = $PackageIdentityName
  packageSha256 = (Get-FileHash -LiteralPath $mainPackage.FullName -Algorithm SHA256).Hash
} | ConvertTo-Json | Set-Content -LiteralPath $OwnershipMarkerPath -Encoding UTF8

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace EmsScout.PackageSmoke
{
    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            uint options,
            out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    public class ApplicationActivationManager
    {
    }
}
'@

function Get-InstalledPackage {
  @(Get-AppxPackage -Name $PackageIdentityName -ErrorAction Stop) | Select-Object -First 1
}

function Install-TestPackage {
  Add-AppxPackage `
    -Path $mainPackage.FullName `
    -DependencyPath $dependencyPackages.FullName `
    -ForceApplicationShutdown `
    -ErrorAction Stop

  $installed = Get-InstalledPackage
  if ($null -eq $installed) {
    throw 'EMS Scout package was not registered after Add-AppxPackage.'
  }
  if ($installed.IsDevelopmentMode) {
    throw 'Install smoke registered a development-mode package instead of the MSIX.'
  }

  $installed
}

function Start-AndVerifyPackage([object]$InstalledPackage) {
  $manifest = Get-AppxPackageManifest -Package $InstalledPackage.PackageFullName
  $applicationId = [string]$manifest.Package.Applications.Application.Id
  if ([string]::IsNullOrWhiteSpace($applicationId)) {
    throw 'Installed package manifest does not contain an application id.'
  }

  $aumid = "$($InstalledPackage.PackageFamilyName)!$applicationId"
  $activationManager = [EmsScout.PackageSmoke.ApplicationActivationManager]::new()
  $activationInterface = [EmsScout.PackageSmoke.IApplicationActivationManager]$activationManager
  [uint32]$processId = 0
  $result = $activationInterface.ActivateApplication($aumid, '', 0, [ref]$processId)
  if ($result -ne 0) {
    [Runtime.InteropServices.Marshal]::ThrowExceptionForHR($result)
  }

  $deadline = (Get-Date).AddSeconds($LaunchTimeoutSeconds)
  $process = $null
  while ($null -eq $process -and (Get-Date) -lt $deadline) {
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
      Start-Sleep -Milliseconds 250
    }
  }
  if ($null -eq $process) {
    throw "Activated EMS Scout process $processId did not remain available."
  }

  try {
    Start-Sleep -Seconds 3
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
      throw "Activated EMS Scout process $processId exited during startup verification."
    }
    Write-Host "EMS Scout activation verified: $aumid (PID $processId)"
  }
  finally {
    Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    Wait-Process -Id $processId -Timeout 10 -ErrorAction SilentlyContinue
  }
}

function Remove-TestPackage {
  $installed = Get-InstalledPackage
  if ($null -ne $installed) {
    Remove-AppxPackage -Package $installed.PackageFullName -ErrorAction Stop
  }
}

$lifecycleError = $null
$cleanupError = $null
try {
  foreach ($round in 1..2) {
    Write-Host "Starting MSIX lifecycle round $round."
    $installedPackage = Install-TestPackage
    Start-AndVerifyPackage $installedPackage
    Remove-TestPackage
    if ($null -ne (Get-InstalledPackage)) {
      throw "Package cleanup failed after lifecycle round $round."
    }
    Write-Host "Completed MSIX lifecycle round $round."
  }
}
catch {
  $lifecycleError = $_
}
finally {
  try {
    Remove-TestPackage
  }
  catch {
    $cleanupError = $_
  }
}

if ($null -ne $lifecycleError) {
  if ($null -ne $cleanupError) {
    throw "MSIX lifecycle failed: $($lifecycleError.Exception.Message) Cleanup also failed: $($cleanupError.Exception.Message)"
  }
  throw $lifecycleError
}
if ($null -ne $cleanupError) {
  throw $cleanupError
}

if ($null -ne (Get-InstalledPackage)) {
  throw 'Package cleanup failed after install smoke.'
}

Remove-Item -LiteralPath $OwnershipMarkerPath -Force

Write-Host 'MSIX install, activation, reinstall, and uninstall smoke passed.'
