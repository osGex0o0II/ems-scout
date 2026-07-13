param(
  [string]$OutputDirectory,
  [string]$CacheDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'ems-scout-sidecar-cache'),
  [switch]$SkipSmoke
)

$ErrorActionPreference = 'Stop'
$NodeVersion = 'v24.18.0'
$NodeArchive = "node-$NodeVersion-win-x64.zip"
$NodeArchiveSha256 = '0ae68406b42d7725661da979b1403ec9926da205c6770827f33aac9d8f26e821'
$NodeDownloadUrl = "https://nodejs.org/dist/$NodeVersion/$NodeArchive"
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'prepare-sidecar-helpers.ps1')

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $OutputDirectory = Join-Path $Root 'artifacts\sidecar\win-x64'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$CacheDirectory = [System.IO.Path]::GetFullPath($CacheDirectory)

$ownedOutputRoot = Join-Path $Root 'artifacts\sidecar'
$ownedCacheRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'ems-scout-sidecar-cache'
Assert-SafeOwnedDirectory $OutputDirectory 'Sidecar output' $ownedOutputRoot
Assert-SafeOwnedDirectory $CacheDirectory 'Sidecar cache' $ownedCacheRoot

function Assert-File([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Required sidecar file is missing: $Path"
  }
}

function Assert-Directory([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    throw "Required sidecar directory is missing: $Path"
  }
}

function Copy-RelativeFile([string]$RelativePath, [string]$DestinationRoot) {
  $source = Join-Path $Root $RelativePath
  Assert-File $source
  $destination = Join-Path $DestinationRoot $RelativePath
  $parent = Split-Path -Parent $destination
  New-Item -ItemType Directory -Force -Path $parent | Out-Null
  Copy-Item -LiteralPath $source -Destination $destination -Force
}

function Get-RelativePath([string]$BasePath, [string]$Path) {
  $baseUri = New-Object System.Uri(($BasePath.TrimEnd('\') + '\'))
  $pathUri = New-Object System.Uri($Path)
  return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

New-Item -ItemType Directory -Force -Path $CacheDirectory | Out-Null
$archivePath = Join-Path $CacheDirectory $NodeArchive
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or
    (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $NodeArchiveSha256) {
  Write-Host "Downloading pinned Node runtime $NodeVersion..."
  Invoke-WebRequest -UseBasicParsing -Uri $NodeDownloadUrl -OutFile $archivePath
}

$actualArchiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualArchiveHash -ne $NodeArchiveSha256) {
  throw "Node archive SHA-256 mismatch. Expected $NodeArchiveSha256, got $actualArchiveHash."
}

$extractRoot = Join-Path $CacheDirectory "node-$NodeVersion-win-x64"
$nodeExecutable = Join-Path $extractRoot 'node.exe'
if (-not (Test-Path -LiteralPath $nodeExecutable -PathType Leaf)) {
  $temporaryExtract = Join-Path $CacheDirectory ("extract-" + [Guid]::NewGuid().ToString('N'))
  try {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $temporaryExtract -Force
    $expanded = Join-Path $temporaryExtract "node-$NodeVersion-win-x64"
    Assert-Directory $expanded
    if (Test-Path -LiteralPath $extractRoot) {
      Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
    Move-Item -LiteralPath $expanded -Destination $extractRoot
  }
  finally {
    if (Test-Path -LiteralPath $temporaryExtract) {
      Remove-Item -LiteralPath $temporaryExtract -Recurse -Force
    }
  }
}

Assert-Directory (Join-Path $Root 'node_modules\playwright')
Assert-Directory (Join-Path $Root 'node_modules\playwright-core')

if (Test-Path -LiteralPath $OutputDirectory) {
  Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
$runtimeDirectory = Join-Path $OutputDirectory 'runtime'
$appDirectory = Join-Path $OutputDirectory 'app'
New-Item -ItemType Directory -Force -Path $runtimeDirectory, $appDirectory | Out-Null

Copy-Item -LiteralPath $nodeExecutable -Destination (Join-Path $runtimeDirectory 'node.exe') -Force
Copy-Item -LiteralPath (Join-Path $extractRoot 'LICENSE') -Destination (Join-Path $runtimeDirectory 'NODE-LICENSE.txt') -Force

$applicationFiles = @(
  'contracts\collection-snapshot-v1.schema.json',
  'contracts\workflow-control-v1.schema.json',
  'contracts\workflow-event-v1.schema.json',
  'sidecar\collect.js',
  'sidecar\legacy-line-adapter.js',
  'sidecar\runner.js',
  'sidecar\snapshot-adapter.js',
  'sidecar\workflow-event.js',
  'src\capture-polling.js',
  'src\capture-quality.js',
  'src\capture-result.js',
  'src\enum-validator.js',
  'src\enumerate.js',
  'src\enumerate-options.js',
  'src\enumerate-output.js',
  'src\logger.js',
  'src\page-navigation.js',
  'src\rules.js',
  'src\url-sanitizer.js',
  'scripts\audit-contracts.js',
  'scripts\audit-realtime-data.js',
  'scripts\collect-building-realtime-batch.js',
  'scripts\collect-building-realtime-details.js',
  'scripts\collect-realtime-all-batch.js',
  'scripts\realtime-browser.js',
  'scripts\realtime-logger.js'
)
foreach ($relativePath in $applicationFiles) {
  Copy-RelativeFile $relativePath $appDirectory
}

$nodeModulesDirectory = Join-Path $appDirectory 'node_modules'
New-Item -ItemType Directory -Force -Path $nodeModulesDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $Root 'node_modules\playwright') -Destination $nodeModulesDirectory -Recurse -Force
Copy-Item -LiteralPath (Join-Path $Root 'node_modules\playwright-core') -Destination $nodeModulesDirectory -Recurse -Force

# Recorder, trace-viewer, and dashboard assets are development tooling. Their
# content-hashed names are parsed as invalid PRI qualifiers by the MSIX build.
$playwrightViteDirectory = Join-Path $nodeModulesDirectory 'playwright-core\lib\vite'
if (Test-Path -LiteralPath $playwrightViteDirectory) {
  Assert-SafeOwnedDirectory $playwrightViteDirectory 'Playwright tooling prune' $OutputDirectory
  Remove-Item -LiteralPath $playwrightViteDirectory -Recurse -Force
}

$playwrightPackage = Get-Content -LiteralPath (Join-Path $Root 'node_modules\playwright\package.json') -Raw | ConvertFrom-Json
$manifestPath = Join-Path $OutputDirectory 'payload-manifest.json'
$files = Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File |
  Where-Object { $_.FullName -ne $manifestPath } |
  Sort-Object FullName |
  ForEach-Object {
    [ordered]@{
      path = Get-RelativePath $OutputDirectory $_.FullName
      bytes = $_.Length
      sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
  }
$manifest = [ordered]@{
  contractVersion = 'ems.sidecar-payload/v1'
  platform = 'win-x64'
  node = [ordered]@{
    version = $NodeVersion
    archiveSha256 = $NodeArchiveSha256
  }
  playwright = [ordered]@{
    version = [string]$playwrightPackage.version
  }
  files = @($files)
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

if (-not $SkipSmoke) {
  $bundledNode = Join-Path $runtimeDirectory 'node.exe'
  $runner = Join-Path $appDirectory 'sidecar\runner.js'
  $smokeScript = @"
require('playwright');
require('./sidecar/snapshot-adapter');
require('./src/capture-polling');
require('./src/capture-quality');
require('./src/capture-result');
require('./src/enumerate-options');
require('./src/enumerate-output');
require('./src/page-navigation');
require('./src/url-sanitizer');
process.stderr.write('playwright-snapshot-and-enumerator-modules-ready\n');
"@
  Push-Location $appDirectory
  try {
    & $bundledNode $runner '--workflow-id=package-smoke' '--stage=preflight' '--' $bundledNode '-e' $smokeScript
    if ($LASTEXITCODE -ne 0) {
      throw "Bundled sidecar smoke failed with exit code $LASTEXITCODE."
    }
  }
  finally {
    Pop-Location
  }
}

Write-Host "Sidecar payload ready: $OutputDirectory"
Write-Host "Manifest: $manifestPath"
