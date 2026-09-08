# Native Install Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and verify a versioned MSIX install, update, and uninstall lifecycle for the Native EMS Scout application.

**Architecture:** Keep MSIX package identity stable and put versioned artifacts in `out/native-packages/<version>`. PowerShell scripts own package validation and lifecycle operations, while a small shared helper provides safe path, version, manifest, and shortcut operations. Uninstall preserves `%LOCALAPPDATA%\EMS Scout` unless `-PurgeData` is explicitly supplied.

**Tech Stack:** PowerShell 5+/7, MSBuild/Windows App SDK MSIX packaging, Appx/ MSIX PowerShell cmdlets, xUnit, .NET 10 WinUI 3.

**Spec:** `docs/superpowers/specs/2026-09-07-native-install-lifecycle-design.md`

## Global Constraints

- MSIX is the only supported distribution format.
- Package identity remains `1FACE092-146B-4AE5-83DB-3990E6AE8371`.
- Versions are four-part numeric values and update rejects equal or lower versions.
- Output is isolated under `out/native-packages/<version>/`.
- Default uninstall preserves `%LOCALAPPDATA%\EMS Scout`, repository `out`, and `data`.
- `-PurgeData` may remove only `%LOCALAPPDATA%\EMS Scout` after confirmation.
- Existing unrelated worktree changes must be preserved.

---

### Task 1: Add lifecycle contract tests

**Files:**
- Create: `scripts/tests/native-install-lifecycle.tests.ps1`
- Create: `native/tests/EmsScout.Tests/NativePackageContractTests.cs`
- Modify: `package.json`

**Interfaces:**
- PowerShell script tests invoke scripts with temporary fixture roots and assert exit codes/files.
- .NET tests assert package identity, version token support, and stable AppUserModelID.

- [ ] **Step 1: Write failing contract tests** for the required script names, parameters, package output manifest, update downgrade rejection, and uninstall purge boundary.
- [ ] **Step 2: Run the focused tests** and verify failure is caused by missing lifecycle scripts/contracts.
- [ ] **Step 3: Add `native:package`, `native:install`, `native:update`, `native:uninstall`, and `native:lifecycle-test` npm entries.
- [ ] **Step 4: Run focused tests again** and keep the expected failures limited to unimplemented script behavior.

### Task 2: Implement shared packaging helpers and version injection

**Files:**
- Create: `scripts/native-package-common.ps1`
- Modify: `native/src/EmsScout.Desktop/Package.appxmanifest`
- Modify: `native/src/EmsScout.Desktop/EmsScout.Desktop.csproj`

**Interfaces:**
- `Get-NativePackageIdentity` returns `Name`, `Publisher`, `DisplayName`, and `AppUserModelId`.
- `Assert-NativeVersion` validates `Major.Minor.Build.Revision`.
- `Resolve-NativePackageDirectory` returns a validated package fixture directory.
- `Get-NativeInstalledPackage` returns the installed Appx package or `$null`.

- [ ] **Step 1: Add version property support** so MSBuild can pass `PackageVersion` without permanently editing the manifest.
- [ ] **Step 2: Implement the helper functions** with explicit path validation and package identity checks.
- [ ] **Step 3: Add manifest/identity contract tests** and run them.

### Task 3: Implement versioned MSIX packaging

**Files:**
- Create: `scripts/native-package.ps1`
- Modify: `.gitignore`

**Interfaces:**
- Parameters: `-Version`, `-Configuration Release|Debug`, `-Platform x64|x86|ARM64`, optional `-OutputRoot`.
- Output: `<OutputRoot>/<Version>/` containing the main `.msix`, dependency packages, and `package-manifest.json`.

- [ ] **Step 1: Add package script validation** for version, platform, output path, and existing installed version.
- [ ] **Step 2: Invoke MSBuild** with `GenerateAppxPackageOnBuild=true`, `AppxPackageSigningEnabled=false`, and the supplied package version.
- [ ] **Step 3: Copy only generated MSIX and dependency packages** into the isolated output directory.
- [ ] **Step 4: Write and verify `package-manifest.json`** with identity, version, package paths, and SHA-256 hashes.
- [ ] **Step 5: Run packaging in a temporary output root** and verify the artifact list.

### Task 4: Implement install and shortcut verification

**Files:**
- Create: `scripts/native-install.ps1`
- Modify: `scripts/install-native-shortcut.ps1`

**Interfaces:**
- Parameters: `-PackageDirectory`, optional `-SkipLaunch`.
- Installs dependencies first, then the main MSIX; validates identity/version/AppID.
- Reuses the current desktop shortcut target format `explorer.exe shell:AppsFolder\<AppUserModelId>`.

- [ ] **Step 1: Add install failure tests** for missing manifest, wrong identity, and missing package.
- [ ] **Step 2: Implement dependency installation and main package installation** with `Add-AppxPackage` and error propagation.
- [ ] **Step 3: Update shortcut creation** to consume the registered package instead of a build path.
- [ ] **Step 4: Launch only after package and shortcut verification** unless `-SkipLaunch` is set.
- [ ] **Step 5: Run install against a temporary signed/test package fixture or the locally generated package** and inspect registration.

### Task 5: Implement update with data preservation

**Files:**
- Create: `scripts/native-update.ps1`

**Interfaces:**
- Parameters: `-PackageDirectory`, optional `-SkipLaunch`.
- Requires installed EMS Scout package; rejects version `<=` installed version.
- Captures `%LOCALAPPDATA%\EMS Scout\settings.json` hash before update and verifies unchanged content after update.

- [ ] **Step 1: Add failing tests** for equal-version rejection, downgrade rejection, and settings marker preservation.
- [ ] **Step 2: Implement installed-version comparison and process shutdown** limited to EMS Scout.
- [ ] **Step 3: Install the new package and verify the registered version.**
- [ ] **Step 4: Verify settings preservation and recreate the shortcut.**
- [ ] **Step 5: Run the v1-to-v2 smoke path in an isolated test profile.**

### Task 6: Implement safe uninstall

**Files:**
- Create: `scripts/native-uninstall.ps1`

**Interfaces:**
- Parameters: `-PurgeData`, `-WhatIf`.
- Default behavior removes app registration and desktop shortcut only.
- `-PurgeData` prompts and removes only `%LOCALAPPDATA%\EMS Scout` after path validation.

- [ ] **Step 1: Add failing tests** asserting default uninstall preserves settings/database and purge cannot target repository paths.
- [ ] **Step 2: Implement process shutdown, shortcut removal, and `Remove-AppxPackage`.**
- [ ] **Step 3: Implement explicit purge confirmation and exact local-app-data target validation.**
- [ ] **Step 4: Run uninstall in the isolated smoke sequence and verify package removal plus retained marker.**

### Task 7: Complete documentation and full verification

**Files:**
- Modify: `README.md`
- Modify: `native/README.md`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Document the four lifecycle commands and data retention behavior.**
- [ ] **Step 2: Run `npm run native:lifecycle-test`.**
- [ ] **Step 3: Run `npm run native:build`, `npm run native:test`, `npm run self-test`, and `git diff --check`.**
- [ ] **Step 4: Confirm package output and installed package state with read-only checks.**
