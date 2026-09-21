Set-StrictMode -Version Latest

$script:NativePackageName = '1FACE092-146B-4AE5-83DB-3990E6AE8371'
$script:NativePublisher = 'CN=EMS Scout'
$script:NativePublisherId = 'ggf25w21tn4m2'
$script:NativeAppUserModelId = "$($script:NativePackageName)_$($script:NativePublisherId)!App"
$script:NativeDisplayName = 'EMS Scout'
$script:NativeWorkspaceMarkerFileName = 'workspace-root.txt'

function Get-NativePackageIdentity {
    [pscustomobject]@{
        Name = $script:NativePackageName
        Publisher = $script:NativePublisher
        PublisherId = $script:NativePublisherId
        DisplayName = $script:NativeDisplayName
        AppUserModelId = $script:NativeAppUserModelId
    }
}

function Assert-NativeVersion {
    param([Parameter(Mandatory)][string]$Version)

    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        throw "Version must be four numeric components, for example 1.0.1.0: $Version"
    }

    $parsed = [version]$Version
    if ($parsed.Major -gt 65535 -or $parsed.Minor -gt 65535 -or $parsed.Build -gt 65535 -or $parsed.Revision -gt 65535) {
        throw "Version components must be <= 65535: $Version"
    }

    return $parsed
}

function Resolve-NativePackageDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $resolved.Path -PathType Container)) {
        throw "Package directory does not exist: $Path"
    }

    $manifestPath = Join-Path $resolved.Path 'package-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "package-manifest.json is missing: $resolved"
    }

    return $resolved.Path
}

function Read-NativePackageManifest {
    param([Parameter(Mandatory)][string]$PackageDirectory)

    $path = Join-Path (Resolve-NativePackageDirectory $PackageDirectory) 'package-manifest.json'
    $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $identity = Get-NativePackageIdentity
    if ($manifest.IdentityName -ne $identity.Name) {
        throw "Package identity mismatch: $($manifest.IdentityName)"
    }

    Assert-NativeVersion $manifest.Version | Out-Null
    $mainPackage = Join-Path (Split-Path $path -Parent) $manifest.MainPackage
    if (-not (Test-Path -LiteralPath $mainPackage -PathType Leaf)) {
        throw "Main MSIX package is missing: $mainPackage"
    }

    return $manifest
}

function Assert-NativeBuiltPackage {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Platform,
        [Parameter(Mandatory)][string]$CertificateThumbprint
    )
    $signature = Get-AuthenticodeSignature -LiteralPath $PackagePath
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
        throw "MSIX signature validation failed: $($signature.Status)"
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $entry) { throw 'MSIX manifest is missing.' }
        $reader = New-Object IO.StreamReader($entry.Open())
        try { $xml = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        $node = $xml.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
        $identity = Get-NativePackageIdentity
        if ($null -eq $node -or $node.Version -ne $Version -or $node.Name -ne $identity.Name -or
            $node.Publisher -ne $identity.Publisher -or $node.ProcessorArchitecture -ne $Platform) {
            throw 'Built MSIX identity/version/architecture mismatch.'
        }
    } finally { $archive.Dispose() }
}

function Get-NativeInstalledPackage {
    $identity = Get-NativePackageIdentity
    return Get-AppxPackage -Name $identity.Name -ErrorAction SilentlyContinue |
        Sort-Object Version -Descending |
        Select-Object -First 1
}

function Stop-NativeDesktopProcess {
    $processes = @(Get-Process -Name 'EmsScout.Desktop' -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        if ($process.HasExited) {
            continue
        }

        if ($process.MainWindowHandle -ne 0) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) {
                Stop-Process -Id $process.Id -Force
            }
        } else {
            Stop-Process -Id $process.Id -Force
        }
    }
}

function Get-NativeShortcutPath {
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)) 'EMS Scout.lnk'
}

function Get-NativeLegacyShortcutPath {
    $legacyName = 'EMS ' +
        [string]([char]0x7a7a) +
        [string]([char]0x8c03) +
        [string]([char]0x63a7) +
        [string]([char]0x5236) +
        [string]([char]0x53f0) +
        '.lnk'
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)) $legacyName
}

function Remove-NativeShortcut {
    foreach ($shortcutPath in @((Get-NativeShortcutPath), (Get-NativeLegacyShortcutPath))) {
        if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
            Remove-Item -LiteralPath $shortcutPath -Force
        }
    }
    return (Get-NativeShortcutPath)
}

function Set-NativeShortcut {
    param([Parameter(Mandatory)][string]$InstalledLocation)

    $identity = Get-NativePackageIdentity
    $registeredApp = Get-StartApps | Where-Object { $_.AppID -eq $identity.AppUserModelId } | Select-Object -First 1
    if ($null -eq $registeredApp) {
        throw "Native MSIX AppID is not registered: $($identity.AppUserModelId)"
    }

    $targetExecutable = Join-Path $InstalledLocation 'EmsScout.Desktop.exe'
    if (-not (Test-Path -LiteralPath $targetExecutable -PathType Leaf)) {
        throw "Installed Native executable is missing: $targetExecutable"
    }

    $shortcutPath = Get-NativeShortcutPath
    if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
        Remove-NativeShortcut | Out-Null
    }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut([string]$shortcutPath)
    $shortcut.TargetPath = (Join-Path $env:WINDIR 'explorer.exe')
    $shortcut.Arguments = "shell:AppsFolder\$($identity.AppUserModelId)"
    $shortcut.WorkingDirectory = $InstalledLocation
    $shortcut.IconLocation = "$targetExecutable,0"
    $shortcut.Description = 'EMS Scout Native WinUI 3'
    $saveError = $null
    try {
        $shortcut.Save()
    } catch {
        $saveError = $_
    }

    $verifiedShortcut = $shell.CreateShortcut([string]$shortcutPath)
    $shortcutIsValid = $verifiedShortcut.TargetPath -eq (Join-Path $env:WINDIR 'explorer.exe') -and
        $verifiedShortcut.Arguments -eq "shell:AppsFolder\$($identity.AppUserModelId)" -and
        $verifiedShortcut.WorkingDirectory -eq $InstalledLocation
    if ($shortcutIsValid) {
        return $shortcutPath
    }
    if ($null -ne $saveError) {
        throw $saveError.Exception
    }
    if (-not $shortcutIsValid) {
        throw "Desktop shortcut verification failed: $shortcutPath"
    }
}

function Set-NativeShortcutWithRetry {
    param(
        [Parameter(Mandatory)][string]$InstalledLocation,
        [int]$MaxAttempts = 20
    )

    $lastError = $null
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            return Set-NativeShortcut $InstalledLocation
        } catch {
            $lastError = $_
            if ($attempt -lt $MaxAttempts) {
                Start-Sleep -Milliseconds 500
            }
        }
    }

    throw $lastError.Exception
}

function Get-NativeUserDataPath {
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'EMS Scout'
}

function Set-NativeWorkspaceMarker {
    param(
        [Parameter(Mandatory)][string]$PackageDirectory,
        [Parameter(Mandatory)][string]$WorkspaceRoot
    )

    $packageRoot = Resolve-NativePackageDirectory $PackageDirectory
    $userDataPath = Get-NativeUserDataPath
    New-Item -ItemType Directory -Path $userDataPath -Force | Out-Null

    $resolvedWorkspaceRoot = (Resolve-Path -LiteralPath $WorkspaceRoot -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedWorkspaceRoot 'package.json') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $resolvedWorkspaceRoot 'out') -PathType Container)) {
        throw "Workspace root is not an EMS Scout repository: $resolvedWorkspaceRoot"
    }

    $markerPaths = [System.Collections.Generic.List[string]]::new()
    $markerPaths.Add((Join-Path $userDataPath $script:NativeWorkspaceMarkerFileName))

    # Packaged WinUI can redirect LocalApplicationData into either of these
    # LocalCache locations. Refresh both so switching checkouts cannot keep
    # running an older repository through a stale package-local marker.
    $identity = Get-NativePackageIdentity
    $packageFamily = "$($identity.Name)_$($identity.PublisherId)"
    $localAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
    if ($localAppData) {
        $packageCacheRoot = Join-Path $localAppData "Packages\$packageFamily\LocalCache"
        foreach ($relativeDirectory in @('EMS Scout', 'Local\EMS Scout')) {
            $directory = Join-Path $packageCacheRoot $relativeDirectory
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            $markerPaths.Add((Join-Path $directory $script:NativeWorkspaceMarkerFileName))
        }
    }

    foreach ($markerPath in $markerPaths) {
        Set-Content -LiteralPath $markerPath -Value $resolvedWorkspaceRoot -Encoding UTF8
    }
    return $resolvedWorkspaceRoot
}

function Assert-NativeUserDataPath {
    param([Parameter(Mandatory)][string]$Path)

    $expected = [IO.Path]::GetFullPath((Get-NativeUserDataPath).TrimEnd('\'))
    $actual = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate on non-user-data path: $actual"
    }
}

function New-NativeVersionedManifest {
    param(
        [Parameter(Mandatory)][string]$SourceManifestPath,
        [Parameter(Mandatory)][string]$DestinationManifestPath,
        [Parameter(Mandatory)][string]$Version
    )

    Assert-NativeVersion $Version | Out-Null
    $xml = [xml](Get-Content -LiteralPath $SourceManifestPath -Raw)
    $identityNode = $xml.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    if ($null -eq $identityNode) {
        throw "Package Identity node is missing: $SourceManifestPath"
    }

    $identityNode.SetAttribute('Version', $Version)
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $settings.Indent = $true
    $writer = [System.Xml.XmlWriter]::Create($DestinationManifestPath, $settings)
    try {
        $xml.Save($writer)
    } finally {
        $writer.Dispose()
    }
}

function Get-NativeSigningCertificate {
    param(
        [string]$Thumbprint,
        [switch]$CreateDevelopmentCertificate
    )

    $certificate = $null
    $rootCertificate = $null
    if ($CreateDevelopmentCertificate) {
        $rootCertificate = New-SelfSignedCertificate `
            -Type Custom `
            -Subject "CN=$($script:NativeDisplayName) Local Root CA" `
            -FriendlyName 'EMS Scout development root certificate' `
            -CertStoreLocation Cert:\CurrentUser\My `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -HashAlgorithm SHA256 `
            -KeyUsage CertSign,CRLSign `
            -TextExtension @('2.5.29.19={text}CA=true&pathlength=1') `
            -NotAfter (Get-Date).AddYears(2)
        $certificate = New-SelfSignedCertificate `
            -Type Custom `
            -Subject "CN=$($script:NativeDisplayName)" `
            -FriendlyName 'EMS Scout development MSIX signing certificate' `
            -CertStoreLocation Cert:\CurrentUser\My `
            -Signer $rootCertificate `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -HashAlgorithm SHA256 `
            -KeyUsage DigitalSignature `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3') `
            -NotAfter (Get-Date).AddYears(2)
        return [pscustomobject]@{ Signer = $certificate; Root = $rootCertificate }
    }

    if ($Thumbprint) {
        $normalized = $Thumbprint.Replace(' ', '').ToUpperInvariant()
        $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
            $_.Thumbprint -eq $normalized
        } | Select-Object -First 1
        if ($null -eq $certificate) {
            throw "Signing certificate was not found in CurrentUser\\My: $Thumbprint"
        }
    } elseif (-not $CreateDevelopmentCertificate) {
        $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
            $_.Subject -eq "CN=$($script:NativeDisplayName)" -and $_.HasPrivateKey
        } | Sort-Object NotAfter -Descending | Select-Object -First 1
    }

    if ($null -eq $certificate) {
        throw 'No signing certificate was found. Pass -CertificateThumbprint or -CreateDevelopmentCertificate.'
    }
    if ($certificate.Subject -ne "CN=$($script:NativeDisplayName)" -or -not $certificate.HasPrivateKey) {
        throw "Signing certificate must have subject CN=$($script:NativeDisplayName) and a private key."
    }

    return [pscustomobject]@{ Signer = $certificate; Root = $null }
}

function Test-NativeAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-NativeCertificateInStore {
    param(
        [Parameter(Mandatory)][string]$StorePath,
        [Parameter(Mandatory)][string]$Thumbprint
    )

    return $null -ne (Get-ChildItem -LiteralPath $StorePath -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $Thumbprint } |
        Select-Object -First 1)
}

function Import-NativeCertificateIfMissing {
    param(
        [Parameter(Mandatory)][string]$CertificatePath,
        [Parameter(Mandatory)][string]$StorePath,
        [Parameter(Mandatory)][string]$Thumbprint
    )

    if (-not (Test-NativeCertificateInStore -StorePath $StorePath -Thumbprint $Thumbprint)) {
        Import-Certificate -FilePath $CertificatePath -CertStoreLocation $StorePath -ErrorAction Stop | Out-Null
    }
}

function Import-NativePackageCertificate {
    param(
        [Parameter(Mandatory)][string]$CertificatePath,
        [string]$RootCertificatePath,
        [switch]$InstallMachineTrust
    )

    if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
        throw "Package signing certificate is missing: $CertificatePath"
    }

    $signer = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
    Import-NativeCertificateIfMissing `
        -CertificatePath $CertificatePath `
        -StorePath 'Cert:\CurrentUser\TrustedPeople' `
        -Thumbprint $signer.Thumbprint

    if (-not $InstallMachineTrust) {
        return
    }

    $root = $null
    if ($RootCertificatePath) {
        if (-not (Test-Path -LiteralPath $RootCertificatePath -PathType Leaf)) {
            throw "Package root certificate is missing: $RootCertificatePath"
        }
        $root = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($RootCertificatePath)
    }

    $machineRootThumbprint = if ($null -ne $root) { $root.Thumbprint } else { $signer.Thumbprint }
    $machineRootTrusted = Test-NativeCertificateInStore -StorePath 'Cert:\LocalMachine\Root' -Thumbprint $machineRootThumbprint
    $machinePeopleTrusted = Test-NativeCertificateInStore -StorePath 'Cert:\LocalMachine\TrustedPeople' -Thumbprint $signer.Thumbprint
    if ($machineRootTrusted -or $machinePeopleTrusted) {
        return
    }

    if (-not (Test-NativeAdministrator)) {
        throw "Package signing certificate is not trusted by the Windows AppX deployment service. Run this install/update script from an elevated PowerShell once, or use a certificate already trusted in LocalMachine\\Root/TrustedPeople."
    }

    if ($null -ne $root) {
        Import-NativeCertificateIfMissing `
            -CertificatePath $RootCertificatePath `
            -StorePath 'Cert:\LocalMachine\Root' `
            -Thumbprint $root.Thumbprint
    } else {
        Import-NativeCertificateIfMissing `
            -CertificatePath $CertificatePath `
            -StorePath 'Cert:\LocalMachine\Root' `
            -Thumbprint $signer.Thumbprint
    }
    Import-NativeCertificateIfMissing `
        -CertificatePath $CertificatePath `
        -StorePath 'Cert:\LocalMachine\TrustedPeople' `
        -Thumbprint $signer.Thumbprint
}

function Assert-NativePackageDirectoryIsSafe {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($fullPath -eq [IO.Path]::GetPathRoot($fullPath).TrimEnd('\')) {
        throw "Refusing to use a filesystem root as package output: $fullPath"
    }
    return $fullPath
}
