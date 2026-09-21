$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('ems-package-safety-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $testRoot 'scripts') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts\native-package.ps1') -Destination (Join-Path $testRoot 'scripts\native-package.ps1')
# Execute the complete production entrypoint, replacing only external build/OS boundaries.
$stubs = @'
function Assert-NativeVersion($Version) { [version]$Version }
function Assert-NativePackageDirectoryIsSafe($Path) { [IO.Path]::GetFullPath($Path) }
function Get-NativeInstalledPackage { if ($env:EMS_PACKAGE_TEST_CASE -eq 'version') { [pscustomobject]@{Version='9.0.0.0'} } }
function Get-NativeSigningCertificate {
    if ($env:EMS_PACKAGE_TEST_CASE -eq 'certificate') { throw 'certificate failure' }
    [pscustomobject]@{ Signer=[pscustomobject]@{Thumbprint='TEST'}; Root=$null }
}
function Get-Command($Name, $ErrorAction) {
    if ($Name -eq 'node.exe' -and $env:EMS_PACKAGE_TEST_CASE -eq 'node') { return }
    [pscustomobject]@{Source='test-tool.exe'}
}
function Export-Certificate($Cert, $FilePath, $Type) { Set-Content -LiteralPath $FilePath -Value 'certificate' }
function New-NativeVersionedManifest($SourceManifestPath, $DestinationManifestPath, $Version) { Set-Content -LiteralPath $DestinationManifestPath -Value $Version }
function Get-NativePackageIdentity { [pscustomobject]@{Name='test'; Publisher='test'; AppUserModelId='test!App'} }
function Assert-NativeBuiltPackage {
    param($PackagePath, $Version, $Platform, $CertificateThumbprint)
    if ($env:EMS_PACKAGE_TEST_CASE -eq 'signature') { throw 'signature failure' }
}
function Read-NativePackageManifest($PackageDirectory) {
    $manifest = Get-Content -LiteralPath (Join-Path $PackageDirectory 'package-manifest.json') -Raw | ConvertFrom-Json
    foreach ($file in $manifest.Files) {
        if ((Get-FileHash -LiteralPath (Join-Path $PackageDirectory $file.Path)).Hash -ne $file.Sha256) { throw 'hash mismatch' }
    }
    $manifest
}
function dotnet {
    $global:LASTEXITCODE = 0
    if ($env:EMS_PACKAGE_TEST_CASE -eq 'build') { $global:LASTEXITCODE = 1; return }
    $output = ($args | Where-Object { $_ -like '/p:AppxPackageDir=*' }).Substring('/p:AppxPackageDir='.Length)
    New-Item -ItemType Directory -Path (Join-Path $output 'Package_Test\Dependencies\x64') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $output 'EmsScout.Desktop_test.msix') -Value 'package'
    Set-Content -LiteralPath (Join-Path $output 'Package_Test\Dependencies\x64\dependency.msix') -Value 'dependency'
}
'@
Set-Content -LiteralPath (Join-Path $testRoot 'scripts\native-package-common.ps1') -Value $stubs -Encoding UTF8
try {
    foreach ($scenario in @('existing','version','certificate','node','build','signature','success')) {
        $output = Join-Path $testRoot $scenario
        $oldDirectory = Join-Path $output '1.0.0.0'
        New-Item -ItemType Directory -Path $oldDirectory -Force | Out-Null
        $sentinel = Join-Path $oldDirectory 'previous.msix'
        Set-Content -LiteralPath $sentinel -Value 'previous successful artifact'
        $hash = (Get-FileHash -LiteralPath $sentinel).Hash
        $targetVersion = if ($scenario -eq 'existing') { '1.0.0.0' } else { '2.0.0.0' }
        $env:EMS_PACKAGE_TEST_CASE = $scenario
        $failure = $null
        try { & (Join-Path $testRoot 'scripts\native-package.ps1') -Version $targetVersion -OutputRoot $output }
        catch { $failure = $_ }
        if (-not (Test-Path -LiteralPath $sentinel) -or (Get-FileHash -LiteralPath $sentinel).Hash -ne $hash) { throw "$scenario destroyed previous artifacts" }
        if ($scenario -eq 'success') {
            if ($failure) { throw $failure }
            $manifest = Get-Content -LiteralPath (Join-Path $output '2.0.0.0\package-manifest.json') -Raw | ConvertFrom-Json
            if ($manifest.Files.Count -ne 3) { throw 'Incomplete published manifest' }
        } else {
            if (-not $failure) { throw "$scenario unexpectedly succeeded" }
            if ($scenario -ne 'existing' -and (Test-Path -LiteralPath (Join-Path $output '2.0.0.0'))) { throw "$scenario published a failed build" }
        }
        if (@(Get-ChildItem -LiteralPath $output -Directory -Filter '.staging-*').Count -ne 0) { throw "$scenario leaked staging" }
        Write-Output "PASS $scenario (previous artifact unchanged)"
    }
} finally {
    Remove-Item Env:\EMS_PACKAGE_TEST_CASE -ErrorAction SilentlyContinue
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolvedTestRoot -Leaf) -like 'ems-package-safety-*') {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
