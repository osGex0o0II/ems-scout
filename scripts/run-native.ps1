param(
  [switch]$NoBuild,
  [switch]$UiValidation
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'native\src\EmsScout.Desktop\EmsScout.Desktop.csproj'

. (Join-Path $PSScriptRoot 'windows-sdk-environment.ps1')
Initialize-WindowsSdkEnvironment

Get-Process EmsScout.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force

$args = @(
  'run',
  '--project', $project,
  '-c', 'Debug'
)

if ($NoBuild) {
  $args += '--no-build'
}

if ($UiValidation) {
  $validationDirectory = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("ems-scout-ui-validation-" + [Guid]::NewGuid().ToString('N'))
  $dataDirectory = Join-Path $validationDirectory 'data'
  $exportDirectory = Join-Path $validationDirectory 'exports'
  $settingsPath = Join-Path $validationDirectory 'settings.json'

  New-Item -ItemType Directory -Path $dataDirectory, $exportDirectory -Force | Out-Null
  @{
    DataDirectory = $dataDirectory
    ExportDirectory = $exportDirectory
  } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8

  $settingsArgument = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($settingsPath))
  $args += "/p:WinAppLaunchArgs=--ui-validation-settings-base64=$settingsArgument"
  Write-Output "UI_VALIDATION_DIRECTORY=$validationDirectory"
}

& dotnet @args

if ($LASTEXITCODE -ne 0) {
  throw "Native application launch failed with exit code $LASTEXITCODE."
}
