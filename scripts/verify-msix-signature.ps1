param(
  [Parameter(Mandatory)]
  [string]$PackagePath,
  [Parameter(Mandatory)]
  [ValidatePattern('^[A-Fa-f0-9]{40}$')]
  [string]$ExpectedSignerThumbprint
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolvedPackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$signature = Get-AuthenticodeSignature -LiteralPath $resolvedPackagePath
if ($null -eq $signature.SignerCertificate) {
  throw "MSIX does not contain an Authenticode signer: $resolvedPackagePath"
}
if (-not [string]::Equals(
    $signature.SignerCertificate.Thumbprint,
    $ExpectedSignerThumbprint,
    [StringComparison]::OrdinalIgnoreCase)) {
  throw "MSIX signer thumbprint does not match $ExpectedSignerThumbprint."
}

if ($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid) {
  return [pscustomobject]@{
    Status = $signature.Status.ToString()
    SignerThumbprint = $signature.SignerCertificate.Thumbprint
    NativeTrustStatus = 0
  }
}
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::UnknownError -or
    $signature.SignerCertificate.Subject -ne $signature.SignerCertificate.Issuer -or
    -not (Test-Path -LiteralPath "Cert:\CurrentUser\TrustedPeople\$ExpectedSignerThumbprint")) {
  throw "MSIX signature status is $($signature.Status): $resolvedPackagePath"
}

if ($null -eq ('EmsScout.Signing.WinTrustVerifier' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace EmsScout.Signing
{
    public static class WinTrustVerifier
    {
        public const uint CertEUntrustedRoot = 0x800B0109;

        private static readonly Guid ActionGenericVerifyV2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructSize;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(
            IntPtr windowHandle,
            ref Guid actionId,
            ref WinTrustData trustData);

        public static uint Verify(string filePath)
        {
            IntPtr filePathPointer = IntPtr.Zero;
            IntPtr fileInfoPointer = IntPtr.Zero;
            try
            {
                filePathPointer = Marshal.StringToCoTaskMemUni(filePath);
                var fileInfo = new WinTrustFileInfo
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    FilePath = filePathPointer
                };
                fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

                var trustData = new WinTrustData
                {
                    StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                    UiChoice = 2,
                    RevocationChecks = 0,
                    UnionChoice = 1,
                    FileInfo = fileInfoPointer,
                    StateAction = 0,
                    ProviderFlags = 0x00000100,
                    UiContext = 0
                };
                var actionId = ActionGenericVerifyV2;
                return unchecked((uint)WinVerifyTrust(
                    new IntPtr(-1),
                    ref actionId,
                    ref trustData));
            }
            finally
            {
                if (fileInfoPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(fileInfoPointer);
                }
                if (filePathPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(filePathPointer);
                }
            }
        }
    }
}
'@
}

$nativeTrustStatus = [EmsScout.Signing.WinTrustVerifier]::Verify($resolvedPackagePath)
if ($nativeTrustStatus -ne [EmsScout.Signing.WinTrustVerifier]::CertEUntrustedRoot) {
  throw "MSIX native trust verification failed with 0x$($nativeTrustStatus.ToString('X8')): $resolvedPackagePath"
}

[pscustomobject]@{
  Status = 'ValidSelfSigned'
  SignerThumbprint = $signature.SignerCertificate.Thumbprint
  NativeTrustStatus = $nativeTrustStatus
}
