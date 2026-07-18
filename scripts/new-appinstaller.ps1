param(
  [Parameter(Mandatory)]
  [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
  [string]$Version,

  [Parameter(Mandatory)]
  [ValidateScript({ $_.IsAbsoluteUri -and $_.Scheme -eq 'https' })]
  [Uri]$AppInstallerUri,

  [Parameter(Mandatory)]
  [ValidateScript({ $_.IsAbsoluteUri -and $_.Scheme -eq 'https' })]
  [Uri]$PackageUri,

  [Parameter(Mandatory)]
  [string]$OutputPath,

  [string]$PackageName = '1FACE092-146B-4AE5-83DB-3990E6AE8371',
  [string]$Publisher = 'CN=EMS Scout'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($fullOutputPath)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
  throw 'OutputPath must include a parent directory.'
}

[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$settings.NewLineChars = "`n"
$settings.NewLineHandling = [Xml.NewLineHandling]::Replace

$namespace = 'http://schemas.microsoft.com/appx/appinstaller/2018'
$writer = [Xml.XmlWriter]::Create($fullOutputPath, $settings)
try {
  $writer.WriteStartDocument()
  $writer.WriteStartElement('AppInstaller', $namespace)
  $writer.WriteAttributeString('Uri', $AppInstallerUri.AbsoluteUri)
  $writer.WriteAttributeString('Version', $Version)

  $writer.WriteStartElement('MainPackage', $namespace)
  $writer.WriteAttributeString('Name', $PackageName)
  $writer.WriteAttributeString('Publisher', $Publisher)
  $writer.WriteAttributeString('Version', $Version)
  $writer.WriteAttributeString('ProcessorArchitecture', 'x64')
  $writer.WriteAttributeString('Uri', $PackageUri.AbsoluteUri)
  $writer.WriteEndElement()

  $writer.WriteStartElement('UpdateSettings', $namespace)
  $writer.WriteStartElement('OnLaunch', $namespace)
  $writer.WriteAttributeString('HoursBetweenUpdateChecks', '24')
  $writer.WriteAttributeString('ShowPrompt', 'true')
  $writer.WriteAttributeString('UpdateBlocksActivation', 'false')
  $writer.WriteEndElement()
  $writer.WriteEndElement()

  $writer.WriteEndElement()
  $writer.WriteEndDocument()
}
finally {
  $writer.Dispose()
}

Write-Output $fullOutputPath
