param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'windows-sdk-environment.ps1')
Initialize-WindowsSdkEnvironment
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Solution = Join-Path $Root 'native\EmsScout.Native.slnx'
$DesktopProject = Join-Path $Root 'native\src\EmsScout.Desktop\EmsScout.Desktop.csproj'

& dotnet clean $Solution -c $Configuration -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw "Native solution clean failed with exit code $LASTEXITCODE." }

& dotnet clean $DesktopProject -c $Configuration -p:Platform=x64 -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw "Native x64 desktop clean failed with exit code $LASTEXITCODE." }

& dotnet build $Solution -c $Configuration -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }
