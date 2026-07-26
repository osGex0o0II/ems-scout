# Windows Cloud Install Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the Windows GitHub Actions gate through test signing, install, launch, same-package reinstall, uninstall, and cleanup.

**Architecture:** A focused certificate helper creates an ephemeral CurrentUser code-signing identity, the existing package script accepts its thumbprint, and a separate lifecycle smoke script validates the signed package on a clean runner. C# contract tests inspect the PowerShell and workflow boundaries so security-critical steps cannot disappear silently.

**Tech Stack:** PowerShell 5.1/7, MSBuild single-project MSIX, Windows AppX cmdlets, `IApplicationActivationManager`, GitHub Actions, xUnit contract tests.

## Global Constraints

- Never read, copy, modify, stage, or upload `data/`, `out/`, `*.db`, `*-wal`, or `*-shm`.
- Never persist a signing private key in the repository, workspace, cache, or Artifact.
- The test certificate subject and package Publisher are exactly `CN=EMS Scout`.
- Install smoke must fail closed if an EMS Scout package already exists.
- Production signing, version upgrade, and real EMS collection remain separate external gates.

---

### Task 1: Packaging Workflow Contract

**Files:**
- Create: `native/tests/EmsScout.Tests/WindowsPackagingWorkflowTests.cs`
- Test: `native/tests/EmsScout.Tests/WindowsPackagingWorkflowTests.cs`

**Interfaces:**
- Consumes: repository files under `scripts/` and `.github/workflows/`.
- Produces: contract tests for certificate creation, signed packaging, lifecycle smoke, cleanup, and private-key exclusion.

- [x] **Step 1: Write failing contract tests**

Add tests that require `new-test-signing-certificate.ps1`,
`test-msix-install.ps1`, `PackageCertificateThumbprint`, `Add-AppxPackage`,
`IApplicationActivationManager`, `Remove-AppxPackage`, an `if: always()` cleanup step,
and Artifact exclusions for `*.pfx`/`*.pvk`.

- [x] **Step 2: Verify RED**

Run:

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Release --no-restore /p:UseSharedCompilation=false --filter FullyQualifiedName~WindowsPackagingWorkflowTests
```

Expected: FAIL because the certificate and lifecycle scripts and workflow steps do not exist.

### Task 2: Ephemeral Test Certificate And Signed Package

**Files:**
- Create: `scripts/new-test-signing-certificate.ps1`
- Modify: `scripts/package-native.ps1`
- Test: `native/tests/EmsScout.Tests/WindowsPackagingWorkflowTests.cs`

**Interfaces:**
- Produces: a certificate object containing `Thumbprint`, `Subject`, and store paths.
- Consumes: optional `PackageCertificateThumbprint` in `package-native.ps1`.

- [x] **Step 1: Implement minimal certificate helper**

Use `New-SelfSignedCertificate` with code-signing EKU, subject `CN=EMS Scout`,
`Cert:\CurrentUser\My`, and import the public certificate into
`Cert:\CurrentUser\TrustedPeople`. Do not export a PFX.

- [x] **Step 2: Pass signing properties to MSBuild**

When the thumbprint parameter is non-empty, append:

```powershell
-p:AppxPackageSigningEnabled=true
-p:PackageCertificateThumbprint=$PackageCertificateThumbprint
```

After publish, locate the unique non-dependency EMS Scout MSIX and require
`Get-AuthenticodeSignature` status `Valid`.

- [x] **Step 3: Verify GREEN**

Run the focused xUnit filter and expect all certificate/package contract tests to pass.

### Task 3: MSIX Lifecycle Smoke

**Files:**
- Create: `scripts/test-msix-install.ps1`
- Test: `native/tests/EmsScout.Tests/WindowsPackagingWorkflowTests.cs`

**Interfaces:**
- Consumes: `PackageRoot`, signed main MSIX, x64 dependency packages.
- Produces: nonzero exit on trust/install/activation/reinstall/uninstall failure and no installed package on exit.

- [x] **Step 1: Implement clean-runner preflight**

Require Windows, a unique main MSIX, a `Valid` signature, and no pre-existing
`1FACE092-146B-4AE5-83DB-3990E6AE8371` package.

- [x] **Step 2: Implement install and activation**

Install with `Add-AppxPackage`, derive the AUMID from the installed manifest, activate
with `IApplicationActivationManager`, wait for the returned process, verify it remains
alive, and stop only that process.

- [x] **Step 3: Implement two lifecycle rounds and cleanup**

Run install/launch/uninstall twice with the same package. In `finally`, remove only the
package installed by this script and verify no matching package remains. Write an ownership
marker only after the clean-runner preflight; job-level cleanup may remove the package only
when that marker is present and valid.

- [x] **Step 4: Verify GREEN**

Run focused contract tests. Do not execute the lifecycle script on the current developer
machine because it already has a development-mode package registered.

### Task 4: GitHub Actions Integration And Documentation

**Files:**
- Modify: `.github/workflows/windows-x64.yml`
- Modify: `docs/Windows验证清单.md`
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Modify: `.context-summary.md`
- Test: `native/tests/EmsScout.Tests/WindowsPackagingWorkflowTests.cs`

**Interfaces:**
- Consumes: certificate helper, signed package script, lifecycle smoke script.
- Produces: a fresh public GitHub Actions run and signed package Artifact.

- [x] **Step 1: Wire workflow steps**

Create the certificate after all pre-package tests, pass its thumbprint to
`package-native.ps1`, run `test-msix-install.ps1`, then add `if: always()` cleanup for
the app identity and both certificate stores.

- [x] **Step 2: Protect Artifact contents**

Keep the existing allowlisted upload paths and explicitly exclude `**/*.pfx` and
`**/*.pvk`.

- [x] **Step 3: Update delivery documentation**

State that CI validates a test-signed install lifecycle, while production signing,
upgrade, installed Sidecar collection, and real EMS remain external acceptance gates.

- [x] **Step 4: Run complete local gates**

Run:

```powershell
npm test
npm run self-test
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Release --no-restore /p:UseSharedCompilation=false --filter 'Fixture!=ProductionEvidence'
dotnet format native/EmsScout.Native.slnx --verify-no-changes --no-restore
git diff --check
```

Expected: all commands exit 0.

### Task 5: Review, Commit, Push, And Observe

**Files:**
- Review: all source, test, workflow, and documentation changes.

**Interfaces:**
- Produces: reviewed commits on `codex/collection-recapture-ui` and a completed GitHub Actions run.

- [x] **Step 1: Review data safety and staged paths**

Use explicit `git add` paths. Confirm no `data/`, `out/`, `*.db`, `*-wal`, `*-shm`,
`artifacts/`, `bin/`, or `obj/` entry is staged.

- [x] **Step 2: Request code review**

Review the complete diff for certificate trust, private-key leakage, cleanup behavior,
workflow ordering, and test coverage. Resolve all Critical and Important findings.

- [ ] **Step 3: Commit and push**

Create conventional commits, push `codex/collection-recapture-ui`, and record the
remote SHA.

- [ ] **Step 4: Observe the cloud run**

Wait for GitHub Actions to finish. Report the exact failing step if it fails; if it
passes, report run URL, artifact name, size, digest, and the install lifecycle evidence.
