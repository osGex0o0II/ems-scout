param(
  [string]$Subject = 'CN=EMS Scout',
  [int]$ValidHours = 8
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
$trustedPeople = [Security.Cryptography.X509Certificates.X509Store]::new(
  'TrustedPeople',
  [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
try {
  $trustedPeople.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
  $trustedPeople.Add($certificate)
}
catch {
  if (Test-Path -LiteralPath $personalStorePath) {
    Remove-Item -LiteralPath $personalStorePath -Force
  }
  if (Test-Path -LiteralPath $trustedPeopleStorePath) {
    Remove-Item -LiteralPath $trustedPeopleStorePath -Force
  }
  throw
}
finally {
  $trustedPeople.Close()
}

if (-not (Test-Path -LiteralPath $trustedPeopleStorePath)) {
  if (Test-Path -LiteralPath $personalStorePath) {
    Remove-Item -LiteralPath $personalStorePath -Force
  }
  throw 'The test signing certificate was not added to CurrentUser TrustedPeople.'
}

[pscustomobject]@{
  Thumbprint = $certificate.Thumbprint
  Subject = $certificate.Subject
  PersonalStorePath = $personalStorePath
  TrustedPeopleStorePath = $trustedPeopleStorePath
}
