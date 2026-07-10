param(
    [string]$Building = "1号",
    [string]$CdpUrl = "http://127.0.0.1:9222",
    [string]$EmsUrl = "http://172.29.248.4:8000/ui",
    [switch]$RunSingleBuilding,
    [switch]$RunAllBuildings,
    [switch]$LaunchEdge,
    [switch]$PrepareLoginSession,
    [int]$LoginWaitSeconds = 120,
    [switch]$KeepBrowser,
    [switch]$KeepProfile,
    [switch]$SkipVerify,
    [switch]$KeepGoing,
    [switch]$AllowRemoteCdp
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "== $Message =="
}

function Resolve-FullPath {
    param([string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-NotProductionPath {
    param(
        [string]$Candidate,
        [string]$Production,
        [string]$Label
    )
    $candidateFull = Resolve-FullPath $Candidate
    $productionFull = Resolve-FullPath $Production
    if ([string]::Equals($candidateFull, $productionFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label resolves to production path: $candidateFull"
    }
}

function Get-FileSnapshot {
    param([string]$Path)
    if (-not (Test-Path $Path)) {
        return $null
    }

    $item = Get-Item -LiteralPath $Path
    return @{
        FullName = Resolve-FullPath $Path
        Length = $item.Length
        LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString("o")
    }
}

function Assert-FileSnapshotUnchanged {
    param(
        [hashtable]$Before,
        [string]$Path,
        [string]$Label
    )

    if ($null -eq $Before) {
        if (Test-Path $Path) {
            throw "$Label was created unexpectedly: $(Resolve-FullPath $Path)"
        }
        return
    }

    if (-not (Test-Path $Path)) {
        throw "$Label was deleted unexpectedly: $($Before.FullName)"
    }

    $after = Get-FileSnapshot $Path
    if ($Before.Length -ne $after.Length -or $Before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc) {
        throw "$Label changed during field E2E. Before length=$($Before.Length) mtime=$($Before.LastWriteTimeUtc); after length=$($after.Length) mtime=$($after.LastWriteTimeUtc)"
    }
}

function Invoke-Checked {
    param(
        [string]$Label,
        [string]$FileName,
        [string[]]$Arguments,
        [hashtable]$Environment = @{},
        [switch]$AllowFailure
    )

    Write-Step $Label
    $startedAt = (Get-Date).ToUniversalTime().ToString("o")
    $old = @{}
    foreach ($key in $Environment.Keys) {
        $old[$key] = [Environment]::GetEnvironmentVariable($key, "Process")
        [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key], "Process")
        Write-Host "env:$key=$($Environment[$key])"
    }

    try {
        Write-Host ("> " + $FileName + " " + ($Arguments -join " "))
        & $FileName @Arguments
        $code = $LASTEXITCODE
        if ($null -eq $code) { $code = 0 }
        if ($script:FieldE2EManifestPath) {
            Add-RunManifestStage $script:FieldE2EManifestPath @{
                label = $Label
                file = $FileName
                arguments = $Arguments
                started_at = $startedAt
                ended_at = (Get-Date).ToUniversalTime().ToString("o")
                exit_code = $code
                allow_failure = $AllowFailure.IsPresent
            }
        }
        if ($code -ne 0 -and -not $AllowFailure) {
            throw "$Label failed with exit code $code"
        }
        if ($code -ne 0) {
            Write-Warning "$Label failed with exit code $code"
        }
        return $code
    }
    finally {
        foreach ($key in $Environment.Keys) {
            [Environment]::SetEnvironmentVariable($key, $old[$key], "Process")
        }
    }
}

function Update-RunManifest {
    param(
        [string]$Path,
        [hashtable]$Patch
    )

    $manifest = @{}
    if (Test-Path $Path) {
        try {
            $existing = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
            foreach ($property in $existing.PSObject.Properties) {
                $manifest[$property.Name] = $property.Value
            }
        }
        catch {
        }
    }
    foreach ($key in $Patch.Keys) {
        $manifest[$key] = $Patch[$key]
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Add-RunManifestStage {
    param(
        [string]$Path,
        [hashtable]$Stage
    )

    $manifest = @{}
    if (Test-Path $Path) {
        try {
            $existing = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
            foreach ($property in $existing.PSObject.Properties) {
                $manifest[$property.Name] = $property.Value
            }
        }
        catch {
        }
    }

    $stages = @()
    if ($manifest.ContainsKey("stages") -and $null -ne $manifest["stages"]) {
        $stages += @($manifest["stages"])
    }
    $stages += [pscustomobject]$Stage
    $manifest["stages"] = $stages
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Test-CdpEndpoint {
    param(
        [string]$Url,
        [string]$TargetEmsUrl
    )
    Write-Step "CDP endpoint check"
    try {
        $version = Invoke-RestMethod "$Url/json/version" -TimeoutSec 3
        Write-Host "CDP reachable: $($version.Browser)"
        try {
            $pages = Invoke-RestMethod "$Url/json/list" -TimeoutSec 3
            $targetHost = ""
            $targetPath = "/ui"
            try {
                $targetUri = [System.Uri]$TargetEmsUrl
                $targetHost = $targetUri.Host
                if (-not [string]::IsNullOrWhiteSpace($targetUri.AbsolutePath)) {
                    $targetPath = $targetUri.AbsolutePath
                }
            }
            catch {
            }
            $emsPages = @($pages | Where-Object {
                $_.url -and (
                    ($targetHost -and $_.url -like "*$targetHost*") -or
                    ($targetPath -and $_.url -like "*$targetPath*")
                )
            })
            Write-Host "CDP pages: $(@($pages).Count); EMS-like pages: $($emsPages.Count)"
            foreach ($page in $emsPages | Select-Object -First 5) {
                Write-Host "  EMS page: $($page.title) <$($page.url)>"
            }
        }
        catch {
            Write-Warning "CDP page list unavailable: $($_.Exception.Message)"
        }
        return $true
    }
    catch {
        Write-Warning "CDP not reachable: $($_.Exception.Message)"
        return $false
    }
}

function Get-CdpPort {
    param([string]$Url)
    try {
        $uri = [System.Uri]$Url
        if ($uri.Port -gt 0) { return $uri.Port }
    }
    catch {
    }
    return 9222
}

function Get-CdpBaseUrl {
    param([string]$Url)
    try {
        $uri = [System.Uri]$Url
        return "$($uri.Scheme)://$($uri.Host):$($uri.Port)"
    }
    catch {
        return $Url.TrimEnd("/")
    }
}

function Test-LoopbackCdpUrl {
    param([string]$Url)
    try {
        $uri = [System.Uri]$Url
        return $uri.Scheme -in @("http", "https") -and
            $uri.Host -in @("127.0.0.1", "localhost", "::1")
    }
    catch {
        return $false
    }
}

function Test-CdpPortReachable {
    param([string]$Url)
    try {
        Invoke-RestMethod "$Url/json/version" -TimeoutSec 1 | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Find-EdgeExecutable {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:EDGE_PATH)) {
        $candidates += $env:EDGE_PATH
    }
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "Microsoft\Edge\Application\msedge.exe"
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates += Join-Path $env:ProgramFiles "Microsoft\Edge\Application\msedge.exe"
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LocalAppData)) {
        $candidates += Join-Path $env:LocalAppData "Microsoft\Edge\Application\msedge.exe"
    }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return (Resolve-FullPath $candidate)
        }
    }

    throw "Microsoft Edge was not found. Set EDGE_PATH to msedge.exe or install Edge."
}

function Start-FieldEdge {
    param(
        [string]$Url,
        [string]$TargetEmsUrl,
        [string]$ProfileDirectory
    )
    Write-Step "Launch isolated Edge CDP"
    $edge = Find-EdgeExecutable
    $port = Get-CdpPort $Url
    New-Item -ItemType Directory -Force -Path $ProfileDirectory | Out-Null
    Write-Host "Edge: $edge"
    Write-Host "CDP: $Url"
    Write-Host "Profile: $ProfileDirectory"
    Write-Host "EMS: $TargetEmsUrl"
    $args = @(
        "--remote-debugging-port=$port",
        "--remote-debugging-address=127.0.0.1",
        "--user-data-dir=$ProfileDirectory",
        "--new-window",
        "--no-first-run",
        "--disable-default-apps",
        "--start-maximized",
        $TargetEmsUrl
    )
    $process = Start-Process -FilePath $edge -ArgumentList $args -PassThru -WindowStyle Normal
    Write-Host "Started Edge pid=$($process.Id). If login is required, use the opened Edge window to log into EMS."
    return $process.Id
}

function Stop-FieldEdge {
    param([string]$ProfileDirectory)
    if ([string]::IsNullOrWhiteSpace($ProfileDirectory)) {
        return @()
    }

    $profile = Resolve-FullPath $ProfileDirectory
    $processes = @(Get-CimInstance Win32_Process -Filter "name = 'msedge.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine -like "*$profile*" })
    if ($processes.Count -eq 0) {
        Write-Host "No field Edge processes to stop."
        return @()
    }

    Write-Host "Stopping field Edge processes for profile: $profile"
    $ids = @()
    foreach ($process in $processes) {
        $ids += [int]$process.ProcessId
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    foreach ($id in $ids) {
        try {
            Wait-Process -Id $id -Timeout 5 -ErrorAction SilentlyContinue
        }
        catch {
        }
    }
    return $ids
}

function Remove-ProfileWithRetry {
    param([string]$ProfileDirectory)
    if (-not (Test-Path $ProfileDirectory)) {
        return
    }

    for ($i = 1; $i -le 6; $i++) {
        try {
            Remove-Item -LiteralPath $ProfileDirectory -Recurse -Force
            Write-Host "Removed field Edge profile: $ProfileDirectory"
            return
        }
        catch {
            if ($i -eq 6) {
                throw "Failed to remove field Edge profile: $($_.Exception.Message)"
            }
            Start-Sleep -Seconds 1
        }
    }
}

function Wait-CdpEndpoint {
    param(
        [string]$Url,
        [int]$TimeoutSeconds = 30
    )
    Write-Step "Wait for CDP"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $version = Invoke-RestMethod "$Url/json/version" -TimeoutSec 2
            Write-Host "CDP ready: $($version.Browser)"
            return $true
        }
        catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Milliseconds 500
        }
    }
    Write-Warning "CDP did not become ready: $lastError"
    return $false
}

function Wait-EmsPage {
    param(
        [string]$Url,
        [string]$TargetEmsUrl,
        [int]$TimeoutSeconds = 120
    )
    Write-Step "Wait for EMS page in CDP"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $targetHost = ""
    try { $targetHost = ([System.Uri]$TargetEmsUrl).Host } catch {}
    while ((Get-Date) -lt $deadline) {
        try {
            $pages = @(Invoke-RestMethod "$Url/json/list" -TimeoutSec 2)
            $emsPages = @($pages | Where-Object {
                $_.url -and (
                    ($targetHost -and $_.url -like "*$targetHost*") -or
                    $_.url -like "*/ui*"
                )
            })
            if ($emsPages.Count -gt 0) {
                foreach ($page in $emsPages | Select-Object -First 3) {
                    Write-Host "EMS page detected: $($page.title) <$($page.url)>"
                }
                return $true
            }
        }
        catch {
        }
        Write-Host "Waiting for EMS tab/login... remaining $([int](($deadline - (Get-Date)).TotalSeconds))s"
        Start-Sleep -Seconds 2
    }
    Write-Warning "EMS page was not detected in CDP before timeout."
    return $false
}

function Test-EmsHttp {
    param([string]$Url)
    Write-Step "EMS HTTP check"
    try {
        $response = Invoke-WebRequest $Url -TimeoutSec 5 -UseBasicParsing
        Write-Host "EMS HTTP status: $([int]$response.StatusCode)"
        return $true
    }
    catch {
        Write-Warning "EMS HTTP check failed: $($_.Exception.Message)"
        return $false
    }
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root
$stamp = Get-Date -Format "yyyyMMdd_HHmmss_fff"
$runSuffix = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$runDir = Join-Path $root "out\field-e2e-$stamp-$runSuffix"
$qualityDir = Join-Path $runDir "quality"
$exportDir = Join-Path $runDir "export"
$edgeProfileDir = Join-Path $runDir ".edge_profile"
$jsonPath = Join-Path $runDir "enum_full_v5.json"
$dbPath = Join-Path $runDir "ac.db"
$manifestPath = Join-Path $runDir "manifest.json"
$script:FieldE2EManifestPath = $manifestPath
$productionDb = Join-Path $root "out\ac.db"
$productionDbSnapshots = @(
    @{ Label = "Production DB"; Path = $productionDb; Snapshot = Get-FileSnapshot $productionDb },
    @{ Label = "Production DB WAL"; Path = "$productionDb-wal"; Snapshot = Get-FileSnapshot "$productionDb-wal" },
    @{ Label = "Production DB SHM"; Path = "$productionDb-shm"; Snapshot = Get-FileSnapshot "$productionDb-shm" }
)

New-Item -ItemType Directory -Force -Path $runDir, $qualityDir, $exportDir | Out-Null
Assert-NotProductionPath $dbPath $productionDb "EMS_DB_PATH"
Assert-NotProductionPath $jsonPath (Join-Path $root "out\enum_full_v5.json") "EMS_JSON_PATH"

Write-Host "Field E2E run dir: $runDir"
if ($PrepareLoginSession -and -not $LaunchEdge) {
    throw "-PrepareLoginSession requires -LaunchEdge."
}

Write-Host "Mode: verify=$(-not $SkipVerify); single-building=$($RunSingleBuilding.IsPresent); all-buildings=$($RunAllBuildings.IsPresent); launch-edge=$($LaunchEdge.IsPresent); prepare-login=$($PrepareLoginSession.IsPresent); building=$Building"
Update-RunManifest $manifestPath @{
    started_at = (Get-Date).ToUniversalTime().ToString("o")
    root = (Resolve-FullPath $root)
    run_dir = (Resolve-FullPath $runDir)
    ems_url = $EmsUrl
    requested_cdp_url = $CdpUrl
    building = $Building
    run_single_building = $RunSingleBuilding.IsPresent
    run_all_buildings = $RunAllBuildings.IsPresent
    launch_edge = $LaunchEdge.IsPresent
    prepare_login_session = $PrepareLoginSession.IsPresent
    skip_verify = $SkipVerify.IsPresent
    production_db_snapshots_before = $productionDbSnapshots
    stages = @()
}

$launchedEdge = $false
try {
    if ($RunSingleBuilding -and $RunAllBuildings) {
        throw "Choose either -RunSingleBuilding or -RunAllBuildings, not both."
    }

    if (-not $LaunchEdge -and -not $AllowRemoteCdp -and -not (Test-LoopbackCdpUrl $CdpUrl)) {
        throw "Non-loopback CDP URL is refused by default: $CdpUrl. Use -AllowRemoteCdp only for an intentional remote browser."
    }

    if ($LaunchEdge) {
        if (-not (Test-LoopbackCdpUrl $CdpUrl)) {
            throw "-LaunchEdge only supports loopback CDP URLs such as http://127.0.0.1:<port>."
        }
        $port = Get-FreeLoopbackPort
        $CdpUrl = "http://127.0.0.1:$port"
        Write-Host "LaunchEdge uses isolated run port: $CdpUrl"
        Update-RunManifest $manifestPath @{ cdp_url = $CdpUrl }
        $null = Start-FieldEdge $CdpUrl $EmsUrl $edgeProfileDir
        $launchedEdge = $true
        if (-not (Wait-CdpEndpoint $CdpUrl 30)) {
            throw "Launched Edge CDP did not become ready."
        }
        $null = Wait-EmsPage $CdpUrl $EmsUrl $LoginWaitSeconds
        if ($PrepareLoginSession) {
            Write-Step "Login session prepared"
            Write-Host "Use the opened Edge window to log into EMS, then run field-e2e with:"
            Write-Host "  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\field-e2e.ps1 -CdpUrl $CdpUrl -Building $Building -RunSingleBuilding"
            Write-Host "Keep this Edge window open until collection finishes."
            Update-RunManifest $manifestPath @{
                prepared_at = (Get-Date).ToUniversalTime().ToString("o")
                status = "prepared_login_session"
                cdp_url = $CdpUrl
                keep_browser_required = $true
                edge_profile_dir = (Resolve-FullPath $edgeProfileDir)
            }
            $KeepBrowser = $true
            $KeepProfile = $true
            return
        }
        Invoke-Checked "Wait EMS login" "node" @(
            "scripts\wait-ems-login.js",
            "--cdp-url=$CdpUrl",
            "--ems-url=$EmsUrl",
            "--timeout-seconds=$LoginWaitSeconds"
        )
    }

    $cdpOk = Test-CdpEndpoint $CdpUrl $EmsUrl
    $emsOk = Test-EmsHttp $EmsUrl
    if (-not $cdpOk -and -not $KeepGoing) {
        throw "CDP is not reachable. Start Edge with remote debugging and log into EMS, then rerun this script."
    }
    if (-not $emsOk -and -not $KeepGoing) {
        throw "EMS HTTP endpoint is not reachable. Check network/VPN/EMS URL, then rerun this script."
    }

    $nodeEnv = @{
        EMS_OUT_DIR = $runDir
        EMS_JSON_PATH = $jsonPath
        EMS_DB_PATH = $dbPath
        EMS_QUALITY_OUT = $qualityDir
        EMS_URL = $EmsUrl
        CDP_URL = $CdpUrl
    }

    if (-not $SkipVerify) {
        $verifyArgs = @(
            "src\enumerate.js",
            "--edge",
            "--verify",
            "--bldg=$Building",
            "--out-dir=$runDir",
            "--ems-url=$EmsUrl",
            "--cdp-url=$CdpUrl",
            "--log-level=DEBUG",
            "--log-category=ENUM,QUALITY,CRASH",
            "--log-file",
            "--fail-if-not-logged-in"
        )
        Invoke-Checked "Live EMS verify" "node" $verifyArgs $nodeEnv
    }

    if (-not $RunSingleBuilding -and -not $RunAllBuildings) {
        Write-Step "Done"
        Write-Host "Verify-only run finished. Add -RunSingleBuilding or -RunAllBuildings to collect into the temp DB and smoke-test Excel export."
        Write-Host "Run directory: $runDir"
        return
    }

    $enumArgs = @(
        "src\enumerate.js",
        "--edge",
        "--out-dir=$runDir",
        "--ems-url=$EmsUrl",
        "--cdp-url=$CdpUrl",
        "--log-level=DEBUG",
        "--log-category=ENUM,QUALITY,CRASH",
        "--log-file",
        "--fail-if-not-logged-in"
    )
    if ($RunSingleBuilding) {
        $enumArgs += "--bldg=$Building"
    }
    $collectionLabel = "All-buildings collection"
    if ($RunSingleBuilding) {
        $collectionLabel = "Single-building collection"
    }
    Invoke-Checked $collectionLabel "node" $enumArgs $nodeEnv
    if (-not (Test-Path $jsonPath)) {
        throw "Collection did not create temp JSON: $jsonPath"
    }

    $validateArgs = @("scripts\validate-enum.js")
    $importArgs = @("scripts\import.js")
    if ($RunSingleBuilding) {
        $validateArgs += "--bldg=$Building"
        $importArgs += "--bldg=$Building"
    }
    Invoke-Checked "Validate temp enum JSON" "node" $validateArgs $nodeEnv
    Invoke-Checked "Import into temp SQLite" "node" $importArgs $nodeEnv
    if (-not (Test-Path $dbPath)) {
        throw "Import did not create temp DB: $dbPath"
    }
    Assert-NotProductionPath $dbPath $productionDb "EMS_DB_PATH"

    Invoke-Checked "Quality report on temp DB" "node" @("scripts\quality-report.js", "--run-id=latest-run") $nodeEnv

    $exportArgs = @(
        "run",
        "--project",
        "native\tools\EmsScout.ExportSmoke\EmsScout.ExportSmoke.csproj",
        "-c",
        "Debug",
        "--no-restore",
        "--",
        "--db=$dbPath",
        "--out=$exportDir",
        "--workspace-root=$root",
        "--realtime-dir=$runDir",
        "--area=公区"
    )
    if ($RunSingleBuilding) {
        $exportArgs += "--building=$Building"
    }
    Invoke-Checked "Excel export smoke" "dotnet" $exportArgs

    foreach ($snapshot in $productionDbSnapshots) {
        Assert-FileSnapshotUnchanged $snapshot.Snapshot $snapshot.Path $snapshot.Label
    }

    Write-Step "Field E2E complete"
    Write-Host "Run directory: $runDir"
    Write-Host "Temp JSON: $jsonPath"
    Write-Host "Temp DB: $dbPath"
    Write-Host "Quality: $qualityDir"
    Write-Host "Excel export: $exportDir"
    Update-RunManifest $manifestPath @{
        completed_at = (Get-Date).ToUniversalTime().ToString("o")
        status = "complete"
        json_path = (Resolve-FullPath $jsonPath)
        db_path = (Resolve-FullPath $dbPath)
        quality_dir = (Resolve-FullPath $qualityDir)
        export_dir = (Resolve-FullPath $exportDir)
        production_db_snapshots_after = @(
            @{ Label = "Production DB"; Path = $productionDb; Snapshot = Get-FileSnapshot $productionDb },
            @{ Label = "Production DB WAL"; Path = "$productionDb-wal"; Snapshot = Get-FileSnapshot "$productionDb-wal" },
            @{ Label = "Production DB SHM"; Path = "$productionDb-shm"; Snapshot = Get-FileSnapshot "$productionDb-shm" }
        )
    }
}
catch {
    if (Test-Path $manifestPath) {
        Update-RunManifest $manifestPath @{
            failed_at = (Get-Date).ToUniversalTime().ToString("o")
            status = "failed"
            error = $_.Exception.Message
        }
    }
    throw
}
finally {
    if ($LaunchEdge -and $launchedEdge -and -not $KeepBrowser) {
        $null = Stop-FieldEdge $edgeProfileDir
    }
    if ($LaunchEdge -and -not $KeepProfile -and (Test-Path $edgeProfileDir)) {
        Remove-ProfileWithRetry $edgeProfileDir
    }
    if (Test-Path $manifestPath) {
        $statusPatch = @{
            finished_at = (Get-Date).ToUniversalTime().ToString("o")
            edge_profile_exists_after_cleanup = (Test-Path $edgeProfileDir)
        }
        Update-RunManifest $manifestPath $statusPatch
    }
}
