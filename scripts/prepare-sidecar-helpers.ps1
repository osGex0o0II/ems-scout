Set-StrictMode -Version Latest

function Test-PathWithin {
  param([string]$Path, [string]$Root)
  $candidate = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
  $allowed = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
  if ($candidate.Equals($allowed, [System.StringComparison]::OrdinalIgnoreCase)) {
    return $true
  }
  return $candidate.StartsWith(
    $allowed + [System.IO.Path]::DirectorySeparatorChar,
    [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-SafeOwnedDirectory {
  param(
    [string]$Path,
    [string]$Purpose,
    [string]$AllowedRoot
  )
  $resolved = [System.IO.Path]::GetFullPath($Path)
  $allowed = [System.IO.Path]::GetFullPath($AllowedRoot)
  if (-not (Test-PathWithin $resolved $allowed)) {
    throw "$Purpose must remain inside its owned directory: $allowed"
  }

  $current = $resolved
  while (-not [string]::IsNullOrWhiteSpace($current)) {
    if (Test-Path -LiteralPath $current) {
      $item = Get-Item -LiteralPath $current -Force
      if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Purpose must not cross a reparse point: $current"
      }
    }
    $parent = Split-Path -Parent $current
    if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) { break }
    $current = $parent
  }
}
