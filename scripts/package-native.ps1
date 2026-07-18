param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release',
  [switch]$SkipTests,
  [switch]$SkipSidecarSmoke,
  [switch]$SkipSidecarPrepare,
  [string]$PackageCertificateThumbprint,
  [string]$PackageTimestampServerUrl,
  [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
  [string]$PackageVersion = '1.0.0.0'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'windows-sdk-environment.ps1')
Initialize-WindowsSdkEnvironment
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Solution = Join-Path $Root 'native\EmsScout.Native.slnx'
$SidecarOutput = Join-Path $Root 'artifacts\sidecar\win-x64'
$DesktopProject = Join-Path $Root 'native\src\EmsScout.Desktop\EmsScout.Desktop.csproj'
$SourcePackageManifest = Join-Path $Root 'native\src\EmsScout.Desktop\Package.appxmanifest'
$TestsProject = Join-Path $Root 'native\tests\EmsScout.Tests\EmsScout.Tests.csproj'
$PackageOutput = Join-Path $Root 'artifacts\packages\win-x64'

if ($SkipSidecarPrepare) {
  $sidecarManifest = Join-Path $SidecarOutput 'payload-manifest.json'
  if (-not (Test-Path -LiteralPath $sidecarManifest)) {
    throw "-SkipSidecarPrepare requires an existing payload manifest: $sidecarManifest"
  }
}
else {
  & (Join-Path $PSScriptRoot 'prepare-sidecar.ps1') -OutputDirectory $SidecarOutput -SkipSmoke:$SkipSidecarSmoke
}

if (-not $SkipTests) {
  & dotnet test $TestsProject -c $Configuration /p:UseSharedCompilation=false `
    --filter 'Fixture!=ProductionEvidence' `
    --logger "trx;LogFileName=native-$Configuration.trx"
  if ($LASTEXITCODE -ne 0) {
    throw "Native tests failed with exit code $LASTEXITCODE."
  }
}

& dotnet clean $Solution -c $Configuration -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
  throw "Native solution clean failed with exit code $LASTEXITCODE."
}

& dotnet clean $DesktopProject -c $Configuration -p:Platform=x64 -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
  throw "Native x64 desktop clean failed with exit code $LASTEXITCODE."
}

$expectedPackageOutput = [IO.Path]::GetFullPath((Join-Path $Root 'artifacts\packages\win-x64'))
if ([IO.Path]::GetFullPath($PackageOutput) -ne $expectedPackageOutput) {
  throw "Package output escaped its owned directory: $PackageOutput"
}
if (Test-Path -LiteralPath $PackageOutput) {
  Remove-Item -LiteralPath $PackageOutput -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $PackageOutput | Out-Null

$versionedManifestPath = Join-Path `
  ([IO.Path]::GetTempPath()) `
  ("ems-scout-package-" + [Guid]::NewGuid().ToString('N') + '.appxmanifest')
$manifestDocument = [Xml.XmlDocument]::new()
$manifestDocument.PreserveWhitespace = $true
$manifestDocument.Load($SourcePackageManifest)
$manifestNamespaces = [Xml.XmlNamespaceManager]::new($manifestDocument.NameTable)
$manifestNamespaces.AddNamespace('p', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$manifestIdentity = $manifestDocument.SelectSingleNode('/p:Package/p:Identity', $manifestNamespaces)
if ($null -eq $manifestIdentity) {
  throw "Package manifest Identity is missing: $SourcePackageManifest"
}
$manifestIdentity.SetAttribute('Version', $PackageVersion)
$manifestWriterSettings = [Xml.XmlWriterSettings]::new()
$manifestWriterSettings.Encoding = [Text.UTF8Encoding]::new($false)
$manifestWriterSettings.Indent = $true
$manifestWriter = [Xml.XmlWriter]::Create($versionedManifestPath, $manifestWriterSettings)
try {
  $manifestDocument.Save($manifestWriter)
}
finally {
  $manifestWriter.Dispose()
}

$publishArgs = @(
  'publish',
  $DesktopProject,
  '-c', $Configuration,
  '-r', 'win-x64',
  '-p:Platform=x64',
  '-p:PublishProfile=win-x64',
  '-p:GenerateAppxPackageOnBuild=true',
  '-p:AppxBundle=Never',
  "-p:EmsScoutPackageManifestPath=$versionedManifestPath",
  "-p:AppxPackageDir=$PackageOutput\"
)

if (-not [string]::IsNullOrWhiteSpace($PackageCertificateThumbprint)) {
  $certificatePath = "Cert:\CurrentUser\My\$PackageCertificateThumbprint"
  if (-not (Test-Path -LiteralPath $certificatePath)) {
    throw "Package signing certificate was not found: $certificatePath"
  }

  $publishArgs += '-p:AppxPackageSigningEnabled=true'
  $publishArgs += "-p:PackageCertificateThumbprint=$PackageCertificateThumbprint"
  if (-not [string]::IsNullOrWhiteSpace($PackageTimestampServerUrl)) {
    $timestampUri = $null
    if (-not [Uri]::TryCreate($PackageTimestampServerUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
        $timestampUri.Scheme -notin 'http', 'https') {
      throw 'Package timestamp server URL must be an absolute HTTP or HTTPS URI.'
    }
    $publishArgs += "-p:AppxPackageSigningTimestampServerUrl=$PackageTimestampServerUrl"
    $publishArgs += '-p:AppxPackageSigningTimestampDigestAlgorithm=SHA256'
  }
}
elseif (-not [string]::IsNullOrWhiteSpace($PackageTimestampServerUrl)) {
  throw 'Package timestamping requires a signing certificate thumbprint.'
}

try {
  & dotnet @publishArgs
  if ($LASTEXITCODE -ne 0) {
    throw "Native package build failed with exit code $LASTEXITCODE."
  }
}
finally {
  if (Test-Path -LiteralPath $versionedManifestPath) {
    Remove-Item -LiteralPath $versionedManifestPath -Force
  }
}

$mainPackages = @(
  Get-ChildItem -LiteralPath $PackageOutput -Recurse -File -Filter '*.msix' |
    Where-Object {
      $_.FullName -notmatch '[\\/]Dependencies[\\/]' -and
      $_.Name -like 'EmsScout.Desktop*.msix' -and
      (($Configuration -eq 'Debug' -and $_.Name -like '*_Debug.msix') -or
       ($Configuration -eq 'Release' -and $_.Name -notlike '*_Debug.msix'))
    }
)
if ($mainPackages.Count -ne 1) {
  throw "Expected one EMS Scout MSIX, found $($mainPackages.Count) in $PackageOutput."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($mainPackages[0].FullName)
try {
  $manifestEntry = $archive.GetEntry('AppxManifest.xml')
  if ($null -eq $manifestEntry) {
    throw "MSIX AppxManifest.xml is missing: $($mainPackages[0].FullName)"
  }
  $manifestReader = [IO.StreamReader]::new($manifestEntry.Open())
  try {
    [xml]$embeddedManifest = $manifestReader.ReadToEnd()
  }
  finally {
    $manifestReader.Dispose()
  }
}
finally {
  $archive.Dispose()
}

$embeddedNamespaces = [Xml.XmlNamespaceManager]::new($embeddedManifest.NameTable)
$embeddedNamespaces.AddNamespace('p', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$embeddedIdentity = $embeddedManifest.SelectSingleNode('/p:Package/p:Identity', $embeddedNamespaces)
if ($null -eq $embeddedIdentity) {
  throw 'Embedded MSIX Identity is missing.'
}
if ($embeddedIdentity.Name -ne '1FACE092-146B-4AE5-83DB-3990E6AE8371') {
  throw "Embedded MSIX identity is invalid: $($embeddedIdentity.Name)"
}
if ($embeddedIdentity.Publisher -ne 'CN=EMS Scout') {
  throw "Embedded MSIX publisher is invalid: $($embeddedIdentity.Publisher)"
}
if ($embeddedIdentity.Version -ne $PackageVersion) {
  throw "Embedded MSIX version is $($embeddedIdentity.Version), expected $PackageVersion."
}
if ($Configuration -eq 'Release') {
  $runtimeDependency = $embeddedManifest.SelectSingleNode(
    "/p:Package/p:Dependencies/p:PackageDependency[@Name='Microsoft.WindowsAppRuntime.2']",
    $embeddedNamespaces)
  if ($null -ne $runtimeDependency) {
    throw 'Release MSIX must bundle Windows App SDK instead of requiring Microsoft.WindowsAppRuntime.2.'
  }
}

if (-not [string]::IsNullOrWhiteSpace($PackageCertificateThumbprint)) {
  $requireTimestamp = -not [string]::IsNullOrWhiteSpace($PackageTimestampServerUrl)
  $signatureVerification = & (Join-Path $PSScriptRoot 'verify-msix-signature.ps1') `
    -PackagePath $mainPackages[0].FullName `
    -ExpectedSignerThumbprint $PackageCertificateThumbprint `
    -RequireTimestamp:($requireTimestamp)

  Write-Host "Signed MSIX verified ($($signatureVerification.Status)): $($mainPackages[0].FullName)"
}

Write-Host "Native x64 package output: $PackageOutput"
