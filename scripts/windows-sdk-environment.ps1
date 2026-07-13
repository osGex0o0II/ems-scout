Set-StrictMode -Version Latest

function Initialize-WindowsSdkEnvironment {
  if (-not [string]::IsNullOrWhiteSpace($env:PROCESSOR_ARCHITECTURE)) {
    return
  }

  $env:PROCESSOR_ARCHITECTURE = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    ([System.Runtime.InteropServices.Architecture]::X64) { 'AMD64' }
    ([System.Runtime.InteropServices.Architecture]::X86) { 'x86' }
    ([System.Runtime.InteropServices.Architecture]::Arm64) { 'ARM64' }
    ([System.Runtime.InteropServices.Architecture]::Arm) { 'ARM' }
    default { throw 'Unable to determine PROCESSOR_ARCHITECTURE for the Windows SDK build.' }
  }
}
