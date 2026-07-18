# Settings Software Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a trusted Settings-page update flow backed by a validated App Installer manifest and a production-signed GitHub Release pipeline.

**Architecture:** Pure update parsing and comparison live in `EmsScout.Application.Updates`; Windows package version discovery and App Installer launching live in `EmsScout.Desktop.Services`. `SettingsViewModel` owns UI state and uses the existing collection operation lease to prevent installation during collection. A tag-only workflow generates a versioned signed MSIX and stable `EmsScout.appinstaller` release asset.

**Tech Stack:** .NET 10, WinUI 3, CommunityToolkit.Mvvm, `HttpClient`, `System.Xml.Linq`, Windows App Installer, MSIX, PowerShell, GitHub Actions, xUnit.

## Global Constraints

- The manifest URL is `https://github.com/osGex0o0II/ems-scout/releases/latest/download/EmsScout.appinstaller`.
- Expected package identity is `1FACE092-146B-4AE5-83DB-3990E6AE8371` and expected Publisher is `CN=EMS Scout`.
- Only HTTPS package URIs hosted by `github.com` are accepted.
- Update checks never read or write production databases, WAL/SHM files, backups, or collection output.
- Installation is disabled while `ApplicationOperationState.IsCollectionTaskRunning` is true.
- Production release fails closed without the configured production signing certificate secrets.
- No forced update, silent installation, downgrade, rollback, or update-channel selector is added.

---

### Task 1: Update manifest contract and checker

**Files:**
- Create: `native/src/EmsScout.Application/Updates/AppInstallerManifest.cs`
- Create: `native/src/EmsScout.Application/Updates/AppInstallerManifestParser.cs`
- Create: `native/src/EmsScout.Application/Updates/AppUpdateContracts.cs`
- Create: `native/src/EmsScout.Application/Updates/AppUpdateService.cs`
- Test: `native/tests/EmsScout.Tests/AppInstallerManifestParserTests.cs`
- Test: `native/tests/EmsScout.Tests/AppUpdateServiceTests.cs`

**Interfaces:**
- Produces: `IAppVersionProvider.CurrentVersion`, `IAppUpdateLauncher.LaunchAsync(Uri, CancellationToken)`, `AppUpdateService.CheckAsync`, and `AppUpdateService.InstallAsync`.
- `AppInstallerManifestParser.Parse(string)` returns validated XML fields without platform dependencies.

- [ ] Write parser tests for a valid manifest, prohibited DTD, missing `MainPackage`, invalid `Version`, and invalid URI; run the focused test and confirm missing-type failures.
- [ ] Implement the immutable manifest model and parser with `DtdProcessing.Prohibit` and no external XML resolver; rerun focused tests.
- [ ] Write service tests using a real `HttpClient` with a deterministic handler for newer/equal versions, wrong identity, wrong publisher, HTTP URI, wrong host, and oversized response; confirm failures.
- [ ] Implement fixed options, response-size enforcement, identity validation, semantic comparison, and launcher delegation; rerun both test classes and the full native test suite.

### Task 2: Windows adapters and Settings state machine

**Files:**
- Create: `native/src/EmsScout.Desktop/Services/PackageAppVersionProvider.cs`
- Create: `native/src/EmsScout.Desktop/Services/WindowsAppUpdateLauncher.cs`
- Modify: `native/src/EmsScout.Desktop/App.xaml.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/SettingsViewModel.cs`
- Test: `native/tests/EmsScout.Tests/SettingsSoftwareUpdateUiContractTests.cs`

**Interfaces:**
- Consumes: Task 1 update interfaces and `ApplicationOperationState`.
- Produces: `CurrentVersionText`, `UpdateStatusText`, `AvailableVersionText`, `IsCheckingForUpdate`, `CanInstallUpdate`, `CheckForUpdateCommand`, and `InstallUpdateCommand` for XAML binding.

- [ ] Write a UI/DI contract test requiring adapter registration, both commands, collection-state notification, neutral Chinese status copy, and no database dependency; run and confirm failure.
- [ ] Implement package-version fallback, allowlisted App Installer launcher, DI registration, and the ViewModel state transitions.
- [ ] Ensure collection-state changes notify `CanInstallUpdate` and command availability; run focused and full native tests.

### Task 3: Settings UI

**Files:**
- Modify: `native/src/EmsScout.Desktop/Pages/SettingsPage.xaml`

**Interfaces:**
- Consumes: Task 2 bindable properties and commands.

- [ ] Extend the failing UI contract to require a full-width `软件更新` card, current/available version text, progress ring, check button, install button, and collection-running copy.
- [ ] Add the card using `WorkbenchCardStyle`, theme brushes, `ToolbarButtonStyle`, and `PrimaryToolbarButtonStyle`; avoid custom status colors and nested cards.
- [ ] Run the focused contract test and XAML compile through the desktop build.

### Task 4: App Installer artifact generation

**Files:**
- Create: `scripts/new-appinstaller.ps1`
- Modify: `scripts/package-native.ps1`
- Test: `native/tests/EmsScout.Tests/AppInstallerReleaseContractTests.cs`

**Interfaces:**
- Produces: `EmsScout.appinstaller` with stable self URI, versioned `MainPackage.Uri`, exact identity and Publisher, x64 architecture, and non-blocking launch checks.

- [ ] Write contract tests for parameter validation, XML fields, HTTPS enforcement, four-part version enforcement, and package version propagation; confirm failure.
- [ ] Implement the generator using `System.Xml.XmlWriter`, not string concatenation.
- [ ] Add `PackageVersion` to `package-native.ps1`, generate a temporary versioned package manifest, and inspect the embedded MSIX manifest after publish.
- [ ] Run PowerShell parser validation and the focused contract tests.

### Task 5: Production release workflow

**Files:**
- Create: `.github/workflows/release-windows-x64.yml`
- Modify: `native/tests/EmsScout.Tests/WindowsPackagingWorkflowTests.cs`

**Interfaces:**
- Consumes: Task 4 generator and package version parameter.
- Produces: GitHub Release assets `EmsScout-<A.B.C.D>-x64.msix` and `EmsScout.appinstaller`.

- [ ] Add failing workflow tests requiring tag-only dispatch, `contents: write`, both certificate secrets, Subject/private-key/expiry checks, package version propagation, signature verification, `gh release` publishing, and cleanup under `if: always()`.
- [ ] Implement the workflow with a `vA.B.C.D` validation gate, step-scoped production secrets, pre-import certificate validation, RFC3161 timestamping, and private-key cleanup.
- [ ] Keep the existing ephemeral-signing workflow unchanged except for shared script syntax validation.
- [ ] Run focused workflow tests and inspect the generated YAML for literal secret leakage.

### Task 6: Verification and documentation

**Files:**
- Modify: `docs/Windows验证清单.md`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Documents the production certificate prerequisites, release command, first `.appinstaller` installation, and N-1 to N manual acceptance procedure.

- [ ] Add the release prerequisites and exact validation sequence without describing the ephemeral certificate as production-ready.
- [ ] Run `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Release --filter 'Fixture!=ProductionEvidence'`.
- [ ] Run `dotnet format native/EmsScout.Native.slnx --verify-no-changes` and the Windows x64 desktop Release build.
- [ ] Review the diff against every design acceptance item and resolve all Critical/Important review findings before handoff.
