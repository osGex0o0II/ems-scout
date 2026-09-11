param(
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('x64', 'x86', 'ARM64')][string]$Platform = 'x64',
    [string]$OutputRoot,
    [string]$CertificateThumbprint,
    [switch]$CreateDevelopmentCertificate
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-package-common.ps1')

$parsedVersion = Assert-NativeVersion $Version
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repositoryRoot 'native\src\EmsScout.Desktop\EmsScout.Desktop.csproj'
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $repositoryRoot 'out\native-packages'
}

$outputRootFull = Assert-NativePackageDirectoryIsSafe $OutputRoot
$packageDirectory = Join-Path $outputRootFull $Version
if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

$installed = Get-NativeInstalledPackage
if ($null -ne $installed -and ([version]$installed.Version -ge $parsedVersion)) {
    throw "Package version $Version is not newer than installed version $($installed.Version)."
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "ems-scout-package-$([guid]::NewGuid().ToString('N'))"
$buildOutput = "$(Join-Path $tempRoot 'build')\"
$manifestPath = Join-Path $tempRoot 'Package.appxmanifest'
$certificatePath = Join-Path $tempRoot 'EMS-Scout-signing.cer'
$rootCertificatePath = Join-Path $tempRoot 'EMS-Scout-signing-root.cer'
New-Item -ItemType Directory -Path $buildOutput -Force | Out-Null
$nodeRuntime = (Get-Command node.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $nodeRuntime) {
    throw 'Node.js runtime is required to build a functional EMS Scout package.'
}
try {
    New-NativeVersionedManifest `
        -SourceManifestPath (Join-Path $repositoryRoot 'native\src\EmsScout.Desktop\Package.appxmanifest') `
        -DestinationManifestPath $manifestPath `
        -Version $Version

    $signing = Get-NativeSigningCertificate -Thumbprint $CertificateThumbprint -CreateDevelopmentCertificate:$CreateDevelopmentCertificate
    $certificate = $signing.Signer
    Export-Certificate -Cert $certificate -FilePath $certificatePath -Type CERT | Out-Null
    if ($null -ne $signing.Root) {
        Export-Certificate -Cert $signing.Root -FilePath $rootCertificatePath -Type CERT | Out-Null
    }

    $dotnetArguments = @(
        'build', $project,
        '-c', $Configuration,
        "/p:Platform=$Platform",
        '/p:GenerateAppxPackageOnBuild=true',
        '/p:AppxPackageSigningEnabled=true',
        '/p:AppxBundle=Never',
        '/p:PublishTrimmed=false',
        '/p:PublishReadyToRun=false',
        "/p:PackageManifestPath=$manifestPath",
        "/p:AppxPackageDir=$buildOutput",
        "/p:PackageCertificateThumbprint=$($certificate.Thumbprint)",
        "/p:NativeNodeRuntimePath=$nodeRuntime",
        '--no-restore',
        '-v:minimal'
    )
    & dotnet @dotnetArguments
    if ($LASTEXITCODE -ne 0) {
        throw "MSIX build failed with exit code $LASTEXITCODE."
    }

    $builtMainPackage = Get-ChildItem -LiteralPath $buildOutput -Recurse -File -Filter 'EmsScout.Desktop_*.msix' |
        Where-Object { $_.Name -notlike 'Microsoft.WindowsAppRuntime*' } |
        Select-Object -First 1
    if ($null -eq $builtMainPackage) {
        throw "MSIX output was not generated under $buildOutput."
    }

    Copy-Item -LiteralPath $builtMainPackage.FullName -Destination $packageDirectory -Force
    Copy-Item -LiteralPath $certificatePath -Destination (Join-Path $packageDirectory 'EMS-Scout-signing.cer') -Force
    if ($null -ne $signing.Root) {
        Copy-Item -LiteralPath $rootCertificatePath -Destination (Join-Path $packageDirectory 'EMS-Scout-signing-root.cer') -Force
    }
    $relativeMainPackage = $builtMainPackage.Name
    $testLayout = Get-ChildItem -LiteralPath $buildOutput -Directory -Filter '*_Test' | Select-Object -First 1
    if ($null -eq $testLayout) {
        throw "MSIX test layout was not generated under $buildOutput."
    }
    Write-Output "MSIX test layout: $($testLayout.FullName)"
    $dependencyRoot = Join-Path $testLayout.FullName 'Dependencies'
    if (Test-Path -LiteralPath $dependencyRoot -PathType Container) {
        foreach ($dependency in Get-ChildItem -LiteralPath $dependencyRoot -Recurse -File -Filter '*.msix') {
            $relative = $dependency.FullName.Substring($testLayout.FullName.Length).TrimStart('\')
            $destination = Join-Path $packageDirectory $relative
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $dependency.FullName -Destination $destination -Force
        }
    }
    $files = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Where-Object { $_.Name -ne 'package-manifest.json' })
    $fileRecords = foreach ($file in $files) {
        [pscustomobject]@{
            Path = $file.FullName.Substring($packageDirectory.Length).TrimStart('\').Replace('\', '/')
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    $manifest = [pscustomobject]@{
        IdentityName = (Get-NativePackageIdentity).Name
        Publisher = (Get-NativePackageIdentity).Publisher
        AppUserModelId = (Get-NativePackageIdentity).AppUserModelId
        Version = $Version
        Architecture = $Platform
        CertificateFile = 'EMS-Scout-signing.cer'
        RootCertificateFile = if ($null -ne $signing.Root) { 'EMS-Scout-signing-root.cer' } else { $null }
        CertificateThumbprint = $certificate.Thumbprint
        MainPackage = $relativeMainPackage
        Files = @($fileRecords)
        CreatedUtc = [DateTime]::UtcNow.ToString('o')
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageDirectory 'package-manifest.json') -Encoding UTF8
    Read-NativePackageManifest $packageDirectory | Out-Null
    Write-Output "Native package created: $packageDirectory"
} finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
