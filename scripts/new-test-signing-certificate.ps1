param(
  [string]$Subject = 'CN=EMS Scout',
  [int]$ValidHours = 8,
  [switch]$TrustForInstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($ValidHours -lt 1 -or $ValidHours -gt 24) {
  throw 'ValidHours must be between 1 and 24.'
}

$certificate = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject $Subject `
  -CertStoreLocation 'Cert:\CurrentUser\My' `
  -KeyAlgorithm RSA `
  -KeyLength 2048 `
  -HashAlgorithm SHA256 `
  -KeyExportPolicy NonExportable `
  -NotAfter (Get-Date).AddHours($ValidHours)

$personalStorePath = "Cert:\CurrentUser\My\$($certificate.Thumbprint)"
$trustedPeopleStorePath = "Cert:\CurrentUser\TrustedPeople\$($certificate.Thumbprint)"
$installRootStorePath = "Cert:\LocalMachine\Root\$($certificate.Thumbprint)"
$trustedPeople = [Security.Cryptography.X509Certificates.X509Store]::new(
  'TrustedPeople',
  [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
$installRoot = $null
try {
  $trustedPeople.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
  $trustedPeople.Add($certificate)
  if ($TrustForInstall) {
    $principal = [Security.Principal.WindowsPrincipal]::new(
      [Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
      throw '-TrustForInstall requires an elevated Windows process.'
    }
    $installRoot = [Security.Cryptography.X509Certificates.X509Store]::new(
      'Root',
      [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    $installRoot.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $installRoot.Add($certificate)
  }
}
catch {
  if (Test-Path -LiteralPath $personalStorePath) {
    Remove-Item -LiteralPath $personalStorePath -DeleteKey -Force
  }
  if (Test-Path -LiteralPath $trustedPeopleStorePath) {
    Remove-Item -LiteralPath $trustedPeopleStorePath -Force
  }
  if (Test-Path -LiteralPath $installRootStorePath) {
    Remove-Item -LiteralPath $installRootStorePath -Force
  }
  throw
}
finally {
  $trustedPeople.Close()
  if ($null -ne $installRoot) {
    $installRoot.Close()
  }
}

if (-not (Test-Path -LiteralPath $trustedPeopleStorePath) -or
    ($TrustForInstall -and -not (Test-Path -LiteralPath $installRootStorePath))) {
  if (Test-Path -LiteralPath $personalStorePath) {
    Remove-Item -LiteralPath $personalStorePath -DeleteKey -Force
  }
  if (Test-Path -LiteralPath $trustedPeopleStorePath) {
    Remove-Item -LiteralPath $trustedPeopleStorePath -Force
  }
  if (Test-Path -LiteralPath $installRootStorePath) {
    Remove-Item -LiteralPath $installRootStorePath -Force
  }
  throw 'The test signing certificate was not added to every required trust store.'
}

[pscustomobject]@{
  Thumbprint = $certificate.Thumbprint
  Subject = $certificate.Subject
  PersonalStorePath = $personalStorePath
  TrustedPeopleStorePath = $trustedPeopleStorePath
  InstallRootStorePath = $(if ($TrustForInstall) { $installRootStorePath } else { $null })
}
