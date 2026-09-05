param(
  [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'native\src\EmsScout.Desktop\EmsScout.Desktop.csproj'

Get-Process EmsScout.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force

$args = @(
  'run',
  '--project', $project,
  '-c', 'Debug'
)

if ($NoBuild) {
  $args += '--no-build'
}

& dotnet @args
