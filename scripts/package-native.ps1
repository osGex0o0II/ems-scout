param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release',
  [switch]$SkipTests,
  [switch]$SkipSidecarSmoke,
  [switch]$SkipSidecarPrepare
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'windows-sdk-environment.ps1')
Initialize-WindowsSdkEnvironment
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Solution = Join-Path $Root 'native\EmsScout.Native.slnx'
$SidecarOutput = Join-Path $Root 'artifacts\sidecar\win-x64'
$DesktopProject = Join-Path $Root 'native\src\EmsScout.Desktop\EmsScout.Desktop.csproj'
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

New-Item -ItemType Directory -Force -Path $PackageOutput | Out-Null
& dotnet publish $DesktopProject `
  -c $Configuration `
  -r win-x64 `
  -p:Platform=x64 `
  -p:PublishProfile=win-x64 `
  -p:GenerateAppxPackageOnBuild=true `
  -p:AppxBundle=Never `
  -p:AppxPackageDir="$PackageOutput\"
if ($LASTEXITCODE -ne 0) {
  throw "Native package build failed with exit code $LASTEXITCODE."
}

Write-Host "Native x64 package output: $PackageOutput"
