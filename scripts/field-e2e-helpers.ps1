Set-StrictMode -Version Latest

function ConvertTo-WindowsArgument {
    param([AllowEmptyString()][string]$Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $slashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * ($slashes * 2 + 1)))
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) {
            [void]$builder.Append(('\' * $slashes))
            $slashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($slashes -gt 0) {
        [void]$builder.Append(('\' * ($slashes * 2)))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function ConvertTo-WindowsCommandLine {
    param([string[]]$Arguments)
    $quoted = @($Arguments | ForEach-Object { ConvertTo-WindowsArgument -Value ([string]$_) })
    return [string]::Join(' ', $quoted)
}

function Test-ProfileCommandLine {
    param(
        [string]$CommandLine,
        [string]$ProfileDirectory
    )
    if ([string]::IsNullOrWhiteSpace($CommandLine) -or [string]::IsNullOrWhiteSpace($ProfileDirectory)) {
        return $false
    }
    $profile = [System.IO.Path]::GetFullPath($ProfileDirectory)
    $wholeArgument = '"--user-data-dir=' + [regex]::Escape($profile) + '"(?=\s|$)'
    if ([regex]::IsMatch($CommandLine, '(?i)(?:^|\s)' + $wholeArgument)) {
        return $true
    }
    $quoted = '--user-data-dir="' + [regex]::Escape($profile) + '"(?=\s|$)'
    if ([regex]::IsMatch($CommandLine, '(?i)(?:^|\s)' + $quoted)) {
        return $true
    }
    if ($profile -notmatch '\s') {
        $plain = '--user-data-dir=' + [regex]::Escape($profile) + '(?=\s|$)'
        return [regex]::IsMatch($CommandLine, '(?i)(?:^|\s)' + $plain)
    }
    return $false
}

function Get-SanitizedEmsUrl {
    param([string]$Url)
    $uri = [System.Uri]$Url
    $port = if ($uri.IsDefaultPort) { '' } else { ':' + $uri.Port }
    return $uri.Scheme + '://' + $uri.IdnHost + $port + $uri.AbsolutePath
}

function Assert-SafeEmsUrl {
    param([string]$Url)
    $uri = [System.Uri]$Url
    if (-not [string]::IsNullOrWhiteSpace($uri.UserInfo)) {
        throw 'EMS URL must not contain user information.'
    }
}

function Assert-SafeEmsUrlForCommandLine {
    param([string]$Url)
    Assert-SafeEmsUrl $Url
    $uri = [System.Uri]$Url
    if (-not [string]::IsNullOrWhiteSpace($uri.Query) -or -not [string]::IsNullOrWhiteSpace($uri.Fragment)) {
        throw 'EMS URL query or fragment must be supplied through the EMS_URL environment variable, not command-line arguments.'
    }
}
