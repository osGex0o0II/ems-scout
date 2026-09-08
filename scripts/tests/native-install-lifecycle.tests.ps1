$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$requiredScripts = @(
    'native-package.ps1',
    'native-install.ps1',
    'native-update.ps1',
    'native-uninstall.ps1',
    'native-package-common.ps1'
)

foreach ($scriptName in $requiredScripts) {
    $scriptPath = Join-Path $repositoryRoot "scripts\$scriptName"
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw "Missing lifecycle script: $scriptName"
    }
}

$project = Get-Content -Raw (Join-Path $repositoryRoot 'native\src\EmsScout.Desktop\EmsScout.Desktop.csproj')
if ($project -notmatch 'AppxManifest Include="\$\(PackageManifestPath\)"') {
    throw 'Desktop project must accept a temporary package manifest path.'
}

$uninstall = Get-Content -Raw (Join-Path $repositoryRoot 'scripts\native-uninstall.ps1')
foreach ($requiredText in @('PurgeData', 'Remove-AppxPackage', 'EMS Scout', 'WhatIf')) {
    if ($uninstall -notlike "*$requiredText*") {
        throw "Uninstall script is missing required contract: $requiredText"
    }
}

$packageScript = Get-Content -Raw (Join-Path $repositoryRoot 'scripts\native-package.ps1')
foreach ($requiredText in @('CertificateThumbprint', 'CreateDevelopmentCertificate', 'AppxPackageSigningEnabled=true')) {
    if ($packageScript -notlike "*$requiredText*") {
        throw "Package script is missing signing contract: $requiredText"
    }
}

$common = Get-Content -Raw (Join-Path $repositoryRoot 'scripts\native-package-common.ps1')
if ($common -match 'certutil\.exe') {
    throw 'Certificate trust setup must not invoke certutil interactively.'
}
if ($common -notlike '*function Set-NativeShortcut*Remove-NativeShortcut*') {
    throw 'Shortcut replacement must remove the previous link before writing a new one.'
}
if ($common -notmatch '(?s)function Set-NativeShortcut\s*\{.*?\$shortcut\.Save\(\).*?catch') {
    throw 'Shortcut Save failures must be verified before being reported as deployment failures.'
}
if ($common -notlike '*EMS Scout.lnk*' -or $common -notlike '*Get-NativeLegacyShortcutPath*') {
    throw 'Shortcut paths must be Windows PowerShell-safe and clean up the legacy Chinese shortcut.'
}
foreach ($requiredText in @('Cert:\LocalMachine\Root', 'Cert:\LocalMachine\TrustedPeople')) {
    if ($common -notlike "*$requiredText*") {
        throw "Certificate trust setup is missing machine-store handling: $requiredText"
    }
}

$install = Get-Content -Raw (Join-Path $repositoryRoot 'scripts\native-install.ps1')
$update = Get-Content -Raw (Join-Path $repositoryRoot 'scripts\native-update.ps1')
foreach ($scriptText in @($install, $update)) {
    if ($scriptText -notlike '*-InstallMachineTrust*') {
        throw 'Install and update must request deterministic machine certificate trust handling.'
    }
    if ($scriptText -notlike '*Set-NativeShortcutWithRetry*') {
        throw 'Install and update must retry shortcut creation after package registration.'
    }
}

Write-Output 'Native install lifecycle contract tests passed.'
