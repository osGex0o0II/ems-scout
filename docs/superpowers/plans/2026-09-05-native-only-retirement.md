# EMS Scout Native-Only Retirement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retire the unused Legacy Web/Electron/TUI/report architecture and leave one Native WinUI product backed by only the Node collection scripts, SQLite, quality audit, and filtered Excel export that Native actually invokes.

**Architecture:** `EmsScout.Desktop` is the only product UI and owns navigation, settings, collection orchestration, SQLite browsing, audit, and Excel export. Node remains a narrowly scoped collection runtime launched by Native with explicit script paths and argument arrays; its EMS/Edge CDP code is retained and hardened because it is still required for collection. The two current `EmsScout.Legacy` JSON readers move into Infrastructure as ordinary file-source adapters, after which the Legacy project, Electron server, browser panel, TUI, and old report engines are deleted.

**Tech Stack:** WinUI 3, Windows App SDK, .NET 10, C# xUnit, Node.js CommonJS, Playwright, Edge CDP, better-sqlite3, SQLite, SpreadsheetML/Excel export, PowerShell field E2E.

**Spec:** `AGENTS.md`, `docs/architecture.md`, and the approved Native-only boundary in this request.

## Global Constraints

- The only user-facing application is `native/src/EmsScout.Desktop`; there is no fallback Web panel, Electron desktop, TUI, or legacy report command after this change.
- Keep only Node files that are called by Native collection/audit flows or by the field E2E validation path.
- Do not rewrite the EMS DOM/Shadow DOM collector in C# in this cleanup; preserve `src/enumerate.js` and its proven extraction rules.
- Native must launch Node with an absolute approved runtime and `UseShellExecute=false`; no retained TUI or shell-based wrapper may be reintroduced.
- Every project-launched Edge CDP endpoint binds to `127.0.0.1`; remote CDP requires an explicit advanced opt-in and warning.
- Relative data/export paths remain under the workspace; external paths require explicit confirmation and system-directory denial.
- Retain SQLite and filtered Excel export; the Native Data Management page remains the only product export surface.
- Do not delete user data, `data/**`, production SQLite files, or generated backups as part of source retirement. Remove only source, project, package, and documentation entries proven unused by the reference scan.
- Preserve unrelated existing changes in the dirty worktree. Do not use `git reset --hard` or `git checkout --`.

## Evidence From Reproduction And Network Verification

The current worktree was checked on 2026-09-05.

1. `npm audit --json` reports 8 advisories: 6 High and 2 Moderate. `npm audit --omit=dev --json` reports zero. The vulnerable packages are all in the Electron/electron-builder development tree: `electron 39.8.10`, `extract-zip 2.0.1`, `brace-expansion`, `fast-uri`, `js-yaml`, `tar`, `undici`, and `@xmldom/xmldom`.
2. The npm registry reports Electron `44.2.0` as the fixed upgrade suggested for this old Electron chain. Since Electron is not part of the approved product architecture, removing Electron and electron-builder is lower risk than upgrading and retaining the attack surface.
3. `dotnet list native/EmsScout.Native.slnx package --vulnerable --include-transitive` reports `SQLitePCLRaw.lib.e_sqlite3 2.1.11`, High, `GHSA-2m69-gcr7-jv3q`. NuGet metadata for `Microsoft.Data.Sqlite 10.0.11` declares SQLitePCLRaw `2.1.12`, so the Native SQLite graph still needs a real package upgrade.
4. Re-running the current TUI spawn pattern with an argument containing `&` emitted Node's `DEP0190` warning and created a temporary marker file through shell interpretation. Deleting the TUI removes this vulnerability rather than preserving a compatibility fix.
5. Starting the current panel and calling `GET /api/health` and `POST /api/tasks/stop` without a token returned HTTP 200. Deleting `src/panel` and `web/panel` removes those unauthenticated mutation endpoints rather than adding auth to a product surface that Native no longer uses.
6. `native/src/EmsScout.Desktop/App.xaml.cs` currently references `EmsScout.Legacy` only for `EnumFullV5SnapshotSource` and `RealtimeLatestJsonSource`. `CollectionTaskViewModel` directly invokes the Native-required scripts; it does not invoke the Legacy panel, Electron, or TUI.

External references used for the decision:

- [Electron security guidance](https://www.electronjs.org/docs/latest/tutorial/security) recommends sandboxing, context isolation, and disabling Node integration in renderers.
- [Electron 44.2.0 registry metadata](https://registry.npmjs.org/electron/44.2.0) confirms the remediation version exists, but it is intentionally not retained.
- [GHSA-jmr9-qjv8-65gv](https://github.com/advisories/GHSA-jmr9-qjv8-65gv) documents the `extract-zip` symlink traversal affecting the retained Electron chain.
- [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) documents the retained Native SQLite transitive vulnerability.
- [Microsoft.Data.Sqlite 10.0.11 nuspec](https://api.nuget.org/v3-flatcontainer/microsoft.data.sqlite/10.0.11/microsoft.data.sqlite.nuspec) declares SQLitePCLRaw `2.1.12`.

## Keep/Delete/Migrate Map

### Keep

- Native solution: `native/src/EmsScout.Application`, `native/src/EmsScout.Collection`, `native/src/EmsScout.Domain`, `native/src/EmsScout.Infrastructure`, `native/src/EmsScout.Desktop`.
- Native tests and `native/tools/EmsScout.ExportSmoke`.
- Collector core: `src/enumerate.js`, `src/enum-validator.js`, `src/rules.js`, `src/logger.js`.
- Native-required scripts: `scripts/import.js`, `scripts/validate-enum.js`, `scripts/quality-report.js`, `scripts/audit-realtime-data.js`, `scripts/collect-realtime-all-batch.js`, `scripts/collect-building-realtime-batch.js`, `scripts/collect-building-realtime-details.js`, `scripts/realtime-browser.js`, `scripts/realtime-logger.js`, `scripts/wait-ems-login.js`, `scripts/verify-live.js`, `scripts/field-e2e.ps1`, `scripts/schema.sql`, and `scripts/self-test.js` after its panel imports are removed.
- `better-sqlite3`, `playwright`, and `xlsx` runtime dependencies.
- `scripts/run-native.ps1`, the Native package/publish files that are present and referenced by the active workflow, current data contracts used by the Native collector, and filtered Excel export code.

### Delete

- `electron/`, `web/panel/`, and `src/panel/` after extracting the shared history functions required by quality reports.
- `src/collect.js` and `src/tui/`.
- `AC-Scout.bat` and `EMS-Panel.bat`.
- `scripts/legacy-gate.js`, `scripts/restore-node-native.js`, `scripts/electron-after-pack.js`.
- `scripts/report.js`, `scripts/report-monitor.js`, `scripts/dump-aircons.js`, `scripts/dump-public.js`, `scripts/reconcile.js`, `scripts/verify-reports.js`, `scripts/monitor-floors.js`, `scripts/dashboard.js`, `scripts/diag-warns.mjs`, `scripts/views.sql`, `scripts/deep-realtime-check.js`, `scripts/realtime-validation-test.js`, and `scripts/inspect-ems-source.js` after the reference scan confirms no active workflow needs them.
- Electron/electron-builder package metadata, Electron builder configuration, legacy package scripts, and any generated Electron packaging hooks.
- The `native/src/EmsScout.Legacy/EmsScout.Legacy.csproj` project after moving its two source files.

### Migrate or Modify

- Move `native/src/EmsScout.Legacy/EnumFullV5SnapshotSource.cs` to `native/src/EmsScout.Infrastructure/Importing/EnumFullV5SnapshotSource.cs` and rename its namespace to `EmsScout.Infrastructure.Importing`.
- Move `native/src/EmsScout.Legacy/RealtimeLatestJsonSource.cs` to `native/src/EmsScout.Infrastructure/Realtime/RealtimeLatestJsonSource.cs` and rename its namespace to `EmsScout.Infrastructure.Realtime`.
- Move the non-UI history functions from `src/panel/history.js` to `src/data-history.js` or another non-panel-owned module, preserving the exact exported functions used by `scripts/quality-report.js` and `scripts/self-test.js`.
- Update Native DI, tests, scripts, `package.json`, documentation, and active CI references to the new ownership model.

### Task 1: Freeze The Reference Graph Before Retirement

**Files:**
- Create: `docs/superpowers/plans/2026-09-05-native-only-retirement.md` (this plan)
- Modify: none initially
- Test: reference scan commands below

**Interfaces:**
- The deletion list is derived from actual consumers, not filename names alone.
- The keep list is the explicit contract for the post-retirement repository.

- [ ] **Step 1: Capture the current reference inventory.**

Run:

```text
rg -n "electron|src/panel|web/panel|src/tui|src/collect\.js|report\.js|dump-aircons|dump-public|report-monitor|reconcile\.js|legacy-gate|restore-node-native|electron-after-pack|EmsScout\.Legacy" --glob '!node_modules/**' --glob '!data/**' --glob '!out/**' .
```

Expected: every result is classified as an active Native/field-E2E dependency, a removable legacy reference, or documentation that must be rewritten.

- [ ] **Step 2: Record the Native script contract from `CollectionTaskViewModel`, `AuditViewModel`, and `field-e2e.ps1`.** Do not delete a script listed in the Keep section until its caller is moved or removed and a replacement test exists.
- [ ] **Step 3: Run the current baseline checks without changing source.**

Run: `npm run self-test; dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`

Expected: record existing failures separately from retirement regressions; do not call this a clean baseline if the dirty migration already causes failures.

- [ ] **Step 4: Commit the approved retirement boundary and inventory.**

```text
git add docs/superpowers/plans/2026-09-05-native-only-retirement.md
git commit -m "docs: define Native-only retirement boundary"
```

### Task 2: Move Required JSON Adapters Out Of The Legacy Project

**Files:**
- Move: `native/src/EmsScout.Legacy/EnumFullV5SnapshotSource.cs` -> `native/src/EmsScout.Infrastructure/Importing/EnumFullV5SnapshotSource.cs`
- Move: `native/src/EmsScout.Legacy/RealtimeLatestJsonSource.cs` -> `native/src/EmsScout.Infrastructure/Realtime/RealtimeLatestJsonSource.cs`
- Delete: `native/src/EmsScout.Legacy/EmsScout.Legacy.csproj`
- Modify: `native/src/EmsScout.Desktop/App.xaml.cs`
- Modify: `native/src/EmsScout.Desktop/EmsScout.Desktop.csproj`
- Modify: `native/EmsScout.Native.slnx`
- Modify: `native/tests/EmsScout.Tests/*.cs` references to `EmsScout.Legacy`
- Test: `native/tests/EmsScout.Tests/EnumFullV5SnapshotSourceTests.cs`
- Test: `native/tests/EmsScout.Tests/RealtimeLatestJsonSourceTests.cs`

**Interfaces:**
- `EmsScout.Infrastructure.Importing.EnumFullV5SnapshotSource` implements `IInventorySnapshotSource`.
- `EmsScout.Infrastructure.Realtime.RealtimeLatestJsonSource` implements `IRealtimeDetailSource`.
- Public constructors and file formats remain unchanged; only ownership and namespace change.

- [ ] **Step 1: Update tests to import the new namespaces before moving implementation.** Keep fixtures for missing files, malformed fields, latest realtime file selection, fallback filenames, and cancellation.
- [ ] **Step 2: Run the focused Native tests and verify they fail because the new namespace/types do not exist yet.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false --filter "FullyQualifiedName~EnumFullV5SnapshotSourceTests|FullyQualifiedName~RealtimeLatestJsonSourceTests"`

Expected: FAIL with missing type/namespace errors.

- [ ] **Step 3: Move the two files and change only their namespaces.** Keep JSON field compatibility (`enum_full_v5.json`, `realtime_*_latest.json`) and retain path resolver constructors used by Native settings.
- [ ] **Step 4: Remove the Legacy project reference from Desktop/tests and the solution.** Confirm Infrastructure already references Application and Domain, so the moved adapters do not introduce a dependency cycle.
- [ ] **Step 5: Update DI and all test references to the Infrastructure namespaces.**
- [ ] **Step 6: Run focused and full Native tests.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`

Expected: PASS with no `EmsScout.Legacy` project or namespace remaining.

- [ ] **Step 7: Commit the adapter migration.**

```text
git add native/src/EmsScout.Infrastructure native/src/EmsScout.Desktop native/tests native/EmsScout.Native.slnx
git commit -m "refactor: move JSON sources into Native infrastructure"
```

### Task 3: Extract Shared Data History And Remove Legacy UI Code

**Files:**
- Create: `src/data-history.js`
- Modify: `scripts/quality-report.js`
- Modify: `scripts/self-test.js`
- Delete: `src/panel/` after extraction
- Delete: `web/panel/`
- Delete: `electron/`
- Delete: `src/collect.js`
- Delete: `src/tui/`
- Delete: `AC-Scout.bat`
- Delete: `EMS-Panel.bat`
- Modify: `package.json`
- Test: Node tests for quality/history and collector syntax

**Interfaces:**
- `src/data-history.js` exports only the non-UI history functions needed by retained scripts: `ensureHistorySchema`, `parseFloorValue`, `normalizeFloorLabel`, `floorLabelFromValue`, `resolveRunId`, `sourceForRun`, `listRuns`, `setRunAnomaly`, `restoreCurrentFromRun`, `deleteRun`, `loadFloorCatalog`, `saveFloorCatalog`, and `seedCurrentRun`.
- No retained script imports `src/panel`, `web/panel`, `electron`, `src/tui`, or `src/collect.js`.

- [ ] **Step 1: Change `scripts/quality-report.js` and `scripts/self-test.js` to import `src/data-history.js`.** Add a failing import test that loads both scripts' required modules without loading any panel route.
- [ ] **Step 2: Run the retained-script smoke test and verify it fails while `src/data-history.js` is absent.**

Run: `node --check scripts/quality-report.js; node --check scripts/self-test.js; npm run self-test`

Expected: the import check fails before the module move; capture the failure as the intended TDD checkpoint.

- [ ] **Step 3: Extract only history/database-batch functions from `src/panel/history.js`.** Remove route/UI assumptions and keep all SQL values parameterized. Do not carry panel HTTP handling, static serving, task state, or web-specific constants into the new module.
- [ ] **Step 4: Run the retained quality/self-test flow.**

Run: `node --check src/data-history.js; node --check scripts/quality-report.js; node --check scripts/self-test.js; npm run self-test`

Expected: quality report code loads without `src/panel` and existing fixture assertions remain valid; the check uses only syntax validation plus the isolated self-test fixtures.

- [ ] **Step 5: Delete the old UI, Electron, TUI, and batch wrapper files listed above.** Remove package `main`, `build`, `legacy:*`, `collect`, and `legacy:quality` entries. Keep `native:*`, collector, import, audit, field-E2E, and self-test commands.
- [ ] **Step 6: Remove the Electron/electron-builder devDependencies and regenerate `package-lock.json`.** The final package manifest contains only `better-sqlite3`, `playwright`, and `xlsx` for Node runtime work.
- [ ] **Step 7: Prove there are no stale references.**

Run: `rg -n "electron|src/panel|web/panel|src/tui|src/collect\.js|EMS_ENABLE_LEGACY_PANEL|legacy:|EmsScout\.Legacy" --glob '!node_modules/**' --glob '!data/**' --glob '!out/**' --glob '!docs/superpowers/plans/**' .`

Expected: zero results in active source, package files, and active documentation. Historical changelog entries may remain only under an explicitly marked historical section.

- [ ] **Step 8: Commit the Legacy UI retirement.**

```text
git add src scripts package.json package-lock.json electron web AC-Scout.bat EMS-Panel.bat
git commit -m "refactor: remove retired legacy UI and TUI"
```

### Task 4: Remove Unused Legacy Reports And Narrow The Node Runtime

**Files:**
- Delete: `scripts/legacy-gate.js`
- Delete: `scripts/restore-node-native.js`
- Delete: `scripts/electron-after-pack.js`
- Delete: `scripts/report.js`
- Delete: `scripts/report-monitor.js`
- Delete: `scripts/dump-aircons.js`
- Delete: `scripts/dump-public.js`
- Delete: `scripts/reconcile.js`
- Delete: `scripts/verify-reports.js`
- Delete: `scripts/monitor-floors.js`
- Delete: `scripts/dashboard.js`
- Delete: `scripts/diag-warns.mjs`
- Delete: `scripts/views.sql`
- Delete: `scripts/deep-realtime-check.js`
- Delete: `scripts/realtime-validation-test.js`
- Delete: `scripts/inspect-ems-source.js`
- Modify: `package.json`
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `docs/architecture.md`
- Modify: `docs/交接.md`
- Modify: `docs/状态.md`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Retained Node scripts have one purpose: collection, validation, import, quality/realtime audit, or field verification invoked by Native.
- There is no `EMS_ENABLE_LEGACY_REPORTS` or `EMS_ENABLE_LEGACY_PANEL` feature flag after deletion.

- [ ] **Step 1: Run the reference scan from Task 1 again after Task 3.** Delete only files with no active caller; if a retained script still imports one, move that small shared function into a named collector/data module first.
- [ ] **Step 2: Remove legacy commands and documentation claims.** Replace “legacy retained/disabled” statements with the Native-only command matrix and remove dead report instructions. Keep historical changelog entries as history, but do not present them as available commands.
- [ ] **Step 3: Add a repository contract test for the allowlist.** Assert that the retained script paths exist and that each deleted path is absent from `package.json` scripts and active workflow files.
- [ ] **Step 4: Run the narrowed Node checks.**

Run: `node --check src/enumerate.js; node --check src/enum-validator.js; node --check scripts/import.js; node --check scripts/validate-enum.js; node --check scripts/quality-report.js; node --check scripts/audit-realtime-data.js; node --check scripts/collect-realtime-all-batch.js; node --check scripts/realtime-browser.js; npm run self-test`

Expected: all retained scripts parse and self-test without any retired module.

- [ ] **Step 5: Commit the script/runtime cleanup.**

```text
git add scripts package.json README.md AGENTS.md docs CHANGELOG.md
git commit -m "refactor: narrow Node runtime to collection workflows"
```

### Task 5: Harden The Retained EMS/CDP And Process Boundaries

**Files:**
- Modify: `scripts/realtime-browser.js`
- Modify: `scripts/field-e2e.ps1`
- Modify: `native/src/EmsScout.Application/Settings/AppSettingsValidator.cs`
- Modify: `native/src/EmsScout.Application/Settings/AppDataPathService.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/Services/NodeCollectionTaskRunner.cs`
- Test: `native/tests/EmsScout.Tests/AppSettingsValidatorTests.cs`
- Test: `native/tests/EmsScout.Tests/AppDataPathServiceTests.cs`
- Test: `native/tests/EmsScout.Tests/NodeCollectionTaskRunnerTests.cs`
- Test: `tests/security/realtime-browser-security.test.js`

**Interfaces:**
- `isAllowedEmsUrl(candidate, configuredUrl)` compares protocol, hostname, effective port, and `/ui` path family; it never uses `includes()`.
- `isAllowedCdpUrl(candidate, allowRemote)` allows HTTP loopback by default and rejects non-loopback endpoints unless an explicit advanced setting is enabled.
- `NodeCollectionTaskRunner` resolves an approved absolute Node executable before creating `ProcessStartInfo`; script arguments remain `ArgumentList` values with `UseShellExecute=false`.

- [ ] **Step 1: Add failing tests for EMS URL prefix confusion, different host/port, credentials, remote CDP, valid loopback CDP, and missing loopback bind arguments.**
- [ ] **Step 2: Add `--remote-debugging-address=127.0.0.1` to every retained Edge launch path.** Keep `field-e2e.ps1` random port, unique profile, temporary output, and cleanup invariants.
- [ ] **Step 3: Implement strict URL validation and sanitized diagnostics.** Use `sanitizeUrlForDisplay` logic inside the retained browser module or a small non-panel utility; never log credentials, query tokens, or raw arbitrary remote URLs.
- [ ] **Step 4: Constrain Native data/export paths.** Canonicalize existing parents, reject `..` escapes, sibling-prefix bypasses, symlink/junction escapes, Windows/Program Files/install directories, and database-file-as-directory targets. Require explicit confirmation for a safe external directory.
- [ ] **Step 5: Remove PATH substitution.** Prefer a packaged/repository-approved Node runtime and standard Edge installation paths. Advanced `EMS_NODE_RUNTIME`/`EDGE_PATH` overrides must be absolute existing files, visible in diagnostics, and never silently resolved from PATH.
- [ ] **Step 6: Run focused tests.**

Run: `node --test tests/security/realtime-browser-security.test.js; dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false --filter "FullyQualifiedName~AppSettingsValidatorTests|FullyQualifiedName~AppDataPathServiceTests|FullyQualifiedName~NodeCollectionTaskRunnerTests"`

Expected: PASS; all retained external-process and filesystem boundaries are explicit and loopback CDP is enforced.

- [ ] **Step 7: Commit retained-boundary hardening.**

```text
git add scripts/realtime-browser.js scripts/field-e2e.ps1 native/src/EmsScout.Application/Settings native/src/EmsScout.Desktop native/tests/EmsScout.Tests tests/security
git commit -m "security: harden retained collection boundaries"
```

### Task 6: Upgrade Native SQLite And Validate Export Safety

**Files:**
- Modify: `native/src/EmsScout.Infrastructure/EmsScout.Infrastructure.csproj`
- Modify: `native/src/EmsScout.Infrastructure/Exporting/SpreadsheetWorkbookWriter.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/*.cs` only where dynamic identifiers are still interpolated
- Test: `native/tests/EmsScout.Tests/DeviceExportTests.cs`
- Test: SQLite repository tests

**Interfaces:**
- Native SQLite resolves `Microsoft.Data.Sqlite >=10.0.11` and `SQLitePCLRaw.lib.e_sqlite3 >=2.1.12`.
- Dynamic SQLite table/column names use static allowlists and quoted identifiers; values remain parameters.
- Spreadsheet values beginning with `=`, `+`, `-`, or `@` remain escaped text, not formulas or hyperlinks.

- [ ] **Step 1: Add failing dependency/output tests.** Assert the package graph floor and export behavior for formula-like device names, notes, and tags.
- [ ] **Step 2: Upgrade `Microsoft.Data.Sqlite` to `10.0.11` or later and restore the solution.** Do not hand-edit `project.assets.json`; let NuGet resolve SQLitePCLRaw `2.1.12` or newer.
- [ ] **Step 3: Add allowlists around any remaining `PRAGMA`, `ALTER TABLE`, or dynamic table-name interpolation.** The deleted panel history SQL is out of scope after Task 3; only retained Native infrastructure is changed.
- [ ] **Step 4: Run dependency and export gates.**

Run: `dotnet restore native/EmsScout.Native.slnx; dotnet list native/EmsScout.Native.slnx package --vulnerable --include-transitive; dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false; dotnet run --project native/tools/EmsScout.ExportSmoke/EmsScout.ExportSmoke.csproj --no-restore`

Expected: no vulnerable packages, all Native tests pass, and export smoke succeeds.

- [ ] **Step 5: Commit the Native dependency/output hardening.**

```text
git add native/src/EmsScout.Infrastructure native/tools/EmsScout.ExportSmoke native/tests/EmsScout.Tests
git commit -m "security: upgrade Native SQLite and verify exports"
```

### Task 7: Rebuild The Native-Only Package And Documentation Contract

**Files:**
- Modify: `package.json`
- Modify: `package-lock.json`
- Modify: `native/EmsScout.Native.slnx`
- Modify: `native/src/EmsScout.Desktop/EmsScout.Desktop.csproj`
- Modify: `native/src/EmsScout.Desktop/Package.appxmanifest`
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `native/README.md`
- Modify: `docs/architecture.md`
- Modify: `CHANGELOG.md`
- Create: `.github/workflows/native-windows.yml`
- Modify: `scripts/run-native.ps1`
- Test: `native/tests/EmsScout.Tests/WindowsPackagingWorkflowTests.cs` if the current worktree restores/retains that contract

**Interfaces:**
- `package.json` is a collector-tool manifest, not an Electron application manifest; it has no `main`, Electron builder block, or Legacy commands.
- Native documentation describes `native:run`, Native build/test, the required Node collector scripts, SQLite, and filtered Excel export as the only supported workflow.

- [ ] **Step 1: Remove stale app identity and packaging metadata.** Delete Electron product name, `afterPack`, NSIS target, Electron files, and legacy shortcut claims. Keep Windows MSIX identity and Native publish settings. Create exactly one Native Windows workflow and do not restore the deleted Electron workflows.
- [ ] **Step 2: Update README/AGENTS architecture diagrams.** Replace `collect.js -> panel -> Electron` descriptions with `Native -> NodeCollectionTaskRunner -> retained scripts -> SQLite -> Native Data Management -> Excel`.
- [ ] **Step 3: Add a contract test that no active file mentions the removed entry points.** Exclude clearly historical changelog records from the assertion only by placing them under a marked “历史记录” section.
- [ ] **Step 4: Build and run the Native application.**

Run: `dotnet build native/EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false; powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native.ps1 -NoBuild`

Expected: Native builds and launches; no Electron process, panel server, TUI, or legacy report is started.

- [ ] **Step 5: Commit Native-only package/documentation changes.**

```text
git add package.json package-lock.json native README.md AGENTS.md docs CHANGELOG.md
git commit -m "refactor: make Native the only supported product"
```

### Task 8: Full Regression, Field E2E, And Final Deletion Gate

**Files:**
- Modify: `scripts/field-e2e.ps1` only for retained-path security assertions
- Modify: `README.md` and `CHANGELOG.md` with actual verification results
- Test: all retained Node, Native, export, packaging, and field-E2E tests

**Interfaces:**
- The final repository has one product launch path and one supported export path.
- A field E2E pass must use a temporary database/output directory and must not be described as a local smoke pass.

- [ ] **Step 1: Run the final stale-reference gate.**

Run: `rg -n "electron|src/panel|web/panel|src/tui|src/collect\.js|EMS_ENABLE_LEGACY_PANEL|EMS_ENABLE_LEGACY_REPORTS|legacy:|EmsScout\.Legacy|report\.js|dump-aircons|dump-public|report-monitor|reconcile\.js" --glob '!node_modules/**' --glob '!data/**' --glob '!out/**' --glob '!CHANGELOG.md' --glob '!docs/superpowers/plans/**' .`

Expected: zero results.

- [ ] **Step 2: Run all local gates.**

```text
npm ci
npm audit --json
npm audit --omit=dev --json
npm run self-test
node --check src/enumerate.js
node --check scripts/import.js
node --check scripts/validate-enum.js
node --check scripts/quality-report.js
node --check scripts/audit-realtime-data.js
node --check scripts/collect-realtime-all-batch.js
node --check scripts/realtime-browser.js
dotnet restore native/EmsScout.Native.slnx
dotnet list native/EmsScout.Native.slnx package --vulnerable --include-transitive
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false
dotnet build native/EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false
dotnet run --project native/tools/EmsScout.ExportSmoke/EmsScout.ExportSmoke.csproj --no-restore
git diff --check
```

Expected: Node and .NET vulnerability scans are clean, tests/build/export pass, and there are no whitespace errors.

- [ ] **Step 3: Exercise the Native UI workflow.** Verify startup, Overview -> Collection -> Overview, collection preflight, one-building collection, history batch selection, refresh to latest data, quality audit, Data Management filters, filtered Excel export, Settings validation, About/Diagnostics, and close/reopen. Verify no old window or server appears.
- [ ] **Step 4: Run the retained field E2E when EMS is available.**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/field-e2e.ps1 -Building 1号 -LaunchEdge -RunSingleBuilding`

Expected: random loopback CDP port, unique `out/field-e2e-*` directory, temporary SQLite, explicit JSON/DB paths, default Edge/profile cleanup, and no modification of production `out/ac.db`. If EMS is unavailable, record the test as not run.

- [ ] **Step 5: Run a clean-machine/package check.** Build the Native Windows package using the active Native workflow, install it in an isolated test environment, launch it without Node/Electron development dependencies, and verify the packaged collector runtime and Excel export.
- [ ] **Step 6: Update the changelog with deletion scope and evidence.** Record that Electron/Legacy/TUI/old reports were removed, SQLite was upgraded, and clearly distinguish local/packaging/field-E2E results.
- [ ] **Step 7: Commit the final verification record.**

```text
git add README.md AGENTS.md native/README.md docs CHANGELOG.md scripts/field-e2e.ps1
git commit -m "test: verify Native-only release boundary"
```

## Final Acceptance Checklist

- [ ] `src/panel`, `web/panel`, `electron`, `src/tui`, `src/collect.js`, and old report engines are absent.
- [ ] `EmsScout.Legacy` project and namespace are absent; its two required readers live under Infrastructure and Native tests pass.
- [ ] `package.json` has no Electron application entry, Electron builder configuration, Legacy flags, or old report commands.
- [ ] Native directly reaches all required collection, import, audit, SQLite, and Excel workflows.
- [ ] Retained Node/Edge boundaries reject remote CDP by default, bind launched CDP to loopback, validate EMS URLs strictly, and avoid shell execution.
- [ ] Native path validation, runtime resolution, SQLite identifiers, and spreadsheet text safety have regression coverage.
- [ ] `npm audit` and .NET vulnerable-package scans report zero vulnerabilities.
- [ ] Native build, tests, export smoke, stale-reference scan, and `git diff --check` pass.
- [ ] Native UI regression confirms the reported crash workflow and the complete current UI workflow.
- [ ] Real EMS E2E is verified with its required invariants or explicitly recorded as not run; it is never replaced by a local smoke claim.
