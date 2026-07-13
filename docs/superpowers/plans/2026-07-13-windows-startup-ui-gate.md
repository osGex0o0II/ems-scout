# Windows Startup And Runtime UI Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the packaged Debug launch self-contained, fail clearly when launch tooling fails, and prove that the native EMS Scout window can be inspected without opening a protected production database.

**Architecture:** Keep the existing WinApp packaged launch path and package identity. Add a Debug-only self-contained build property so the activated app does not depend on a machine-wide .NET runtime, strengthen `run-native.ps1` error propagation, then validate the real window only after resolving the configured database path and rejecting protected repository databases.

**Tech Stack:** .NET 10, WinUI 3, single-project MSIX, PowerShell 5.1, xUnit, Windows Computer Use.

## Global Constraints

- The display brand remains `EMS Scout`; .NET identifiers remain `EmsScout`.
- Windows minimum version remains `10.0.26100.0`; target architecture remains `win-x64`.
- Do not enable trimming; `PublishTrimmed` remains `false`.
- Do not open `out/ac.db`, `data/ac.db`, `data/1号楼/ac.db`, or `data/2号楼/ac.db` during launch validation.
- Do not delete, rewrite, or stage existing user changes, production evidence, WAL, or SHM files.
- Runtime UI evidence is local Windows evidence, not a real EMS end-to-end result.
- This plan does not change navigation, pages, collection behavior, schema, or export behavior.

---

## File Map

- Modify `native/src/EmsScout.Desktop/EmsScout.Desktop.csproj`: make Debug packaged output self-contained while preserving the Release publish profile.
- Modify `scripts/run-native.ps1`: initialize the Windows SDK environment and propagate non-zero `dotnet run` exit codes.
- Create `native/tests/EmsScout.Tests/NativeLaunchConfigurationTests.cs`: enforce the Debug self-contained and launch-script contracts.
- Modify `native/README.md`: document why local Debug launch is self-contained and what still requires the SDK.
- Create `docs/validation/2026-07-13-native-debug-launch.md`: record the isolated local launch evidence without modifying the user's in-progress Windows checklist.

## Task 1: Lock The Native Launch Contract

**Files:**
- Create: `native/tests/EmsScout.Tests/NativeLaunchConfigurationTests.cs`
- Modify: `native/src/EmsScout.Desktop/EmsScout.Desktop.csproj`
- Modify: `scripts/run-native.ps1`
- Test: `native/tests/EmsScout.Tests/NativeLaunchConfigurationTests.cs`

**Interfaces:**
- Consumes: MSBuild properties `Configuration`, `RuntimeIdentifier`, and `SelfContained`; PowerShell `$LASTEXITCODE`.
- Produces: Debug `AppX` output containing the .NET runtime and a launch script that throws on failed `dotnet run`.

- [ ] **Step 1: Write the failing launch-configuration tests**

Create `native/tests/EmsScout.Tests/NativeLaunchConfigurationTests.cs`:

```csharp
using System.Xml.Linq;

namespace EmsScout.Tests;

public sealed class NativeLaunchConfigurationTests
{
    [Fact]
    public void DebugPackagedLaunchIsSelfContained()
    {
        var root = LocateRepositoryRoot();
        var projectPath = Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "EmsScout.Desktop.csproj");
        var project = XDocument.Load(projectPath);
        var debugGroup = project.Root!
            .Elements("PropertyGroup")
            .Single(element => string.Equals(
                (string?)element.Attribute("Condition"),
                "'$(Configuration)' == 'Debug'",
                StringComparison.Ordinal));

        Assert.True(string.Equals(
            debugGroup.Element("SelfContained")?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NativeRunInitializesSdkEnvironmentAndPropagatesFailure()
    {
        var root = LocateRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "run-native.ps1"));
        var invocation = script.IndexOf("& dotnet @args", StringComparison.Ordinal);
        var exitCheck = script.IndexOf("if ($LASTEXITCODE -ne 0)", StringComparison.Ordinal);

        Assert.Contains("windows-sdk-environment.ps1", script);
        Assert.Contains("Initialize-WindowsSdkEnvironment", script);
        Assert.True(invocation >= 0, "run-native.ps1 must invoke dotnet run.");
        Assert.True(exitCheck > invocation, "run-native.ps1 must check the launch exit code.");
        Assert.Contains("Native application launch failed with exit code", script);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "native")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate the EMS Scout repository root.");
    }
}
```

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```powershell
dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj `
  -c Debug --no-restore /p:UseSharedCompilation=false `
  --filter FullyQualifiedName~NativeLaunchConfigurationTests
```

Expected: 2 failed tests. The project has no Debug `SelfContained` property, and `run-native.ps1` has no SDK initialization or exit-code check.

- [ ] **Step 3: Make Debug packaged output self-contained**

Add this property group immediately after the main property group in `native/src/EmsScout.Desktop/EmsScout.Desktop.csproj`:

```xml
  <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
    <!-- Packaged activation cannot discover a repository-local .NET runtime. -->
    <SelfContained>true</SelfContained>
  </PropertyGroup>
```

Do not change the existing Release publish profile or `PublishTrimmed` setting.

- [ ] **Step 4: Make the launch script initialize prerequisites and propagate failure**

Update the setup block in `scripts/run-native.ps1` to:

```powershell
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'windows-sdk-environment.ps1')
Initialize-WindowsSdkEnvironment
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'native\src\EmsScout.Desktop\EmsScout.Desktop.csproj'
```

Add this immediately after `& dotnet @args`:

```powershell
if ($LASTEXITCODE -ne 0) {
  throw "Native application launch failed with exit code $LASTEXITCODE."
}
```

- [ ] **Step 5: Run focused tests and build the native application**

Run:

```powershell
dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj `
  -c Debug --no-restore /p:UseSharedCompilation=false `
  --filter FullyQualifiedName~NativeLaunchConfigurationTests
npm run native:build
```

Expected: 2 tests passed; native build exits 0 with no XAML compiler errors.

- [ ] **Step 6: Verify the Debug AppX contains the self-contained runtime**

Run:

```powershell
$appx = Resolve-Path 'native\src\EmsScout.Desktop\bin\Debug\net10.0-windows10.0.26100.0\win-x64\AppX'
$required = 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll'
$missing = @($required | Where-Object { -not (Test-Path (Join-Path $appx $_)) })
if ($missing.Count -ne 0) {
  throw "Debug AppX is not self-contained; missing: $($missing -join ', ')"
}
$required | ForEach-Object { Get-Item (Join-Path $appx $_) | Select-Object Name, Length }
```

Expected: all three runtime files exist and have non-zero lengths.

- [ ] **Step 7: Commit the launch contract**

```powershell
git add -- `
  native/src/EmsScout.Desktop/EmsScout.Desktop.csproj `
  scripts/run-native.ps1 `
  native/tests/EmsScout.Tests/NativeLaunchConfigurationTests.cs
git diff --cached --check
git commit -m "fix: make native debug launch self-contained"
```

Expected: the commit contains exactly the three listed files.

## Task 2: Prove The Real Window Without Touching Protected Data

**Files:**
- Modify: `native/README.md`
- Create: `docs/validation/2026-07-13-native-debug-launch.md`
- Test: `tests/architecture/current-docs.test.js`

**Interfaces:**
- Consumes: the self-contained Debug AppX and existing `npm run native:run` command.
- Produces: observed packaged-window evidence and documentation that matches the working launch path.

- [ ] **Step 1: Resolve the configured database and refuse protected paths**

Run this read-only preflight before launching the app:

```powershell
$root = (Resolve-Path '.').Path
$settingsPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'EMS Scout\settings.json'
$settings = if (Test-Path -LiteralPath $settingsPath) {
  Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
} else {
  $null
}
$dataDirectory = if ($settings -and -not [string]::IsNullOrWhiteSpace($settings.DataDirectory)) {
  [IO.Path]::GetFullPath($settings.DataDirectory)
} else {
  Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'EMS Scout\data'
}
$databasePath = [IO.Path]::GetFullPath((Join-Path $dataDirectory 'ac.db'))
$protected = @(
  (Join-Path $root 'out\ac.db'),
  (Join-Path $root 'data\ac.db'),
  (Join-Path $root 'data\1号楼\ac.db'),
  (Join-Path $root 'data\2号楼\ac.db')
) | ForEach-Object { [IO.Path]::GetFullPath($_) }
if ($protected -contains $databasePath) {
  throw "Runtime UI validation refused protected database: $databasePath"
}
Write-Output "SAFE_DATABASE_PATH=$databasePath"
```

Expected: output begins with `SAFE_DATABASE_PATH=` and the path is outside the repository `out` and `data` production locations. If it fails, stop; do not rewrite user settings as a workaround.

- [ ] **Step 2: Launch the packaged Debug app**

Run `npm run native:run` in a yielded command session and keep it active while inspecting the window.

Expected: no “You must install or update .NET” dialog appears, an `EmsScout.Desktop` process remains running, and the window title is `EMS Scout`.

- [ ] **Step 3: Inspect the current native UI with Windows Computer Use**

Use the `computer-use` skill to inspect the actual packaged window. Verify these current-state facts before any navigation redesign:

- The title is `EMS Scout`.
- The startup database error bar is absent, or, if present, it contains a safe error message and retry/settings actions rather than a raw exception.
- The navigation exposes 总览、采集任务、数据管理、分组设置、日期管理、审计中心、系统设置、诊断.
- Each of the eight pages opens without a process crash.
- No page overlaps its navigation pane at the current desktop size.

Close the app normally after inspection and confirm the yielded `npm run native:run` command exits 0.

- [ ] **Step 4: Document the verified launch behavior**

Add this paragraph after the Run section in `native/README.md`:

```markdown
Debug packaged launch is self-contained because Windows packaged activation cannot
reliably discover a repository-local .NET runtime. `npm run native:run` still requires
the .NET 10 SDK to build, but the activated Debug AppX carries its runtime.
```

After the runtime inspection passes, create `docs/validation/2026-07-13-native-debug-launch.md` with:

```markdown
# Native Debug Launch Validation - 2026-07-13

## Scope

This is local packaged Debug UI evidence. It is not a real EMS end-to-end result.

## Safety Preflight

- The configured database resolved outside protected repository `out/` and `data/` locations.
- No production database, WAL, or SHM file was opened for this validation.

## Observed Result

- `npm run native:run` launched the packaged self-contained Debug AppX.
- No .NET runtime installation dialog appeared.
- The window title was `EMS Scout`.
- Eight current pages opened: 总览、采集任务、数据管理、分组设置、日期管理、审计中心、系统设置、诊断.
- The application closed normally and the launch command exited successfully.
```

- [ ] **Step 5: Run documentation and launch-contract verification**

Run:

```powershell
node --test tests/architecture/current-docs.test.js
dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj `
  -c Debug --no-restore /p:UseSharedCompilation=false `
  --filter FullyQualifiedName~NativeLaunchConfigurationTests
git diff --check -- native/README.md docs/validation/2026-07-13-native-debug-launch.md
```

Expected: 6 Node documentation tests pass, 2 native launch tests pass, and `git diff --check` reports no errors.

- [ ] **Step 6: Commit the verified documentation**

```powershell
git add -- native/README.md docs/validation/2026-07-13-native-debug-launch.md
git diff --cached --check
git commit -m "docs: record native packaged launch gate"
```

Expected: the commit contains exactly the two documentation files.

## Final Verification

- [ ] Run the complete non-production native test suite:

```powershell
npm run native:test
```

Expected: all non-production-evidence tests pass with zero failures.

- [ ] Rebuild once after the documentation commit:

```powershell
npm run native:build
```

Expected: exit 0 and no XAML compiler errors.

- [ ] Confirm commit and worktree isolation:

```powershell
git show --stat --oneline HEAD~1..HEAD
git diff --cached --name-only
git status --short
```

Expected: the two plan commits contain only their declared files, the index is empty, and all pre-existing unrelated worktree changes remain present and unstaged.
