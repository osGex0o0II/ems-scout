# EMS Native UI And Data Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Confirm the source of the navigation crash and device-count discrepancy, then make the Native WinUI 3 application the single reliable UI with correct history batches, area-group summaries, visual styling, minimum window behavior, and shortcut parity.

**Architecture:** Keep the current Native pipeline: WinUI 3 Desktop -> Application services -> SQLite and the retained Node collector. UI state changes stay in the existing ViewModels and repositories. Required JSON readers move into Infrastructure before the unused Legacy project and retired UI entry points are removed; no legacy compatibility UI is restored.

**Tech Stack:** WinUI 3, C#, .NET 10, SQLite, xUnit, Node.js collector scripts, PowerShell packaging/launch scripts.

**Spec:** `AGENTS.md` and the current Native-only contract in `native/docs/MIGRATION_PLAN.md`.

## Global Constraints

- The only user-facing application remains `native/src/EmsScout.Desktop`.
- Historical batches are read-only; collection and import do not write production data during UI verification.
- The current `out/ac.db` is not edited or replaced while diagnosing the count discrepancy.
- Latest-batch status is represented by color/light state in the persistent UI; no persistent “最新批次” label is added.
- Existing user changes in the worktree are preserved; each implementation task is independently testable.

---

### Task 1: Freeze the Baseline And Reproduce The Failures

**Files:**
- Inspect: `native/src/EmsScout.Desktop/MainWindow.xaml.cs`, `native/src/EmsScout.Desktop/Pages/DataPage.xaml.cs`, `native/src/EmsScout.Desktop/ViewModels/DataViewModel.cs`
- Test: `native/tests/EmsScout.Tests/DesktopCrashGuardTests.cs`
- Add diagnostic output only if required: `native/src/EmsScout.Desktop/Services/DesktopDiagnosticsService.cs`

**Interfaces:**
- Consumes: the existing development launch script and the desktop shortcut target.
- Produces: a reproducible action matrix with process exit code, exception/log evidence, current DB path, selected batch id, and navigation sequence.

- [ ] Record `git status`, the resolved `out/ac.db` path, its file hash, the latest complete run id, and the desktop shortcut target.
- [ ] Run the exact sequences separately: `关于 -> 总览`, `采集 -> 总览`, `总览 -> 数据`, `数据页打开 -> 选择历史批次`, and `总览 -> 数据 -> 总览`.
- [ ] Capture Windows Application Error/.NET Runtime events and application diagnostic files for every process exit; do not classify a silent exit as fixed.
- [ ] If no stack trace is emitted, add temporary boundary diagnostics around `NavFrame.Navigate`, page load/unload, ViewModel refresh, and repository calls. Log start/end/cancel/fault with correlation ids.
- [ ] Convert the stable reproduction into a regression test or a deterministic diagnostic harness before changing behavior.

### Task 2: Prove The Device-Count Data Contract

**Files:**
- Inspect: `native/src/EmsScout.Infrastructure/Sqlite/SqliteDeviceReadRepository.cs`, `native/src/EmsScout.Application/DashboardOverview.cs`, `native/src/EmsScout.Application/Collection/CollectionRunCompleteness.cs`, `native/src/EmsScout.Desktop/ViewModels/HomeViewModel.cs`
- Test: `native/tests/EmsScout.Tests/DashboardOverviewTests.cs`, `native/tests/EmsScout.Tests/SqliteDeviceReadRepositoryTests.cs`

**Interfaces:**
- Consumes: `cards`, `run_cards`, collection-run metadata, the resolved application DB path, and the dashboard query result.
- Produces: one documented count contract and assertions that every displayed total comes from the same selected complete batch.

- [ ] Compare `cards` count, latest complete `run_cards` snapshot count, per-building counts, distinct device keys, and `SearchAsync` result count using the same DB connection and run id.
- [ ] Trace the value through repository -> `DashboardOverview` -> `HomeViewModel` -> XAML binding and identify whether the extra two rows are virtual rows, duplicated joins, stale UI state, or a different database file.
- [ ] Verify six-building completeness and the expected historical-batch count without deleting duplicates; same-name cards with independent positions remain valid records.
- [ ] Add a fixture test for count equality and a regression test for mismatched snapshot/display totals.
- [ ] Keep the current value `6471` as an observed fact, not a hard-coded expected total, until the source of the screenshot’s `6473` is proven.

### Task 3: Remove The Navigation Crash At Its Lifecycle Boundary

**Files:**
- Modify: `native/src/EmsScout.Desktop/MainWindow.xaml.cs`, `native/src/EmsScout.Desktop/Pages/DataPage.xaml.cs`, `native/src/EmsScout.Desktop/ViewModels/DataViewModel.cs`
- Review: `native/src/EmsScout.Desktop/Services/NavigationService.cs`
- Test: `native/tests/EmsScout.Tests/DesktopCrashGuardTests.cs` plus a runtime navigation regression test where WinUI test hosting permits it.

**Interfaces:**
- Consumes: the Task 1 failing navigation sequence and the existing cancellation/error patterns.
- Produces: navigation that cancels stale page work, ignores expected navigation cancellation, surfaces repository failures in-page, and never terminates the process because a previous page completed after disposal.

- [ ] Trace every asynchronous operation started by `DataPage`, including historical-batch loading, filtering, refresh, and export preparation, to its cancellation token and owner.
- [ ] Confirm whether navigation can call `Navigate` twice for one selection or leave an old `DataViewModel` subscribed after the frame changes.
- [ ] Introduce one navigation gate and page-lifetime cancellation; cancel and await/observe old work before replacing the page state.
- [ ] Wrap expected SQLite/file/network faults into an error state with retry; preserve unexpected exceptions in diagnostics with the full stack rather than silently swallowing them.
- [ ] Re-run all five sequences from Task 1 repeatedly, including rapid alternating clicks, and require zero process exits.

### Task 4: Make Historical Batches Consistent Across Overview And Data

**Files:**
- Modify: `native/src/EmsScout.Desktop/Pages/HomePage.xaml`, `native/src/EmsScout.Desktop/ViewModels/HomeViewModel.cs`, `native/src/EmsScout.Desktop/Pages/DataPage.xaml`, `native/src/EmsScout.Desktop/ViewModels/DataViewModel.cs`, `native/src/EmsScout.Desktop/ViewModels/DataSourceOption.cs`
- Test: `native/tests/EmsScout.Tests/HomePageUiContractTests.cs`, `native/tests/EmsScout.Tests/DataManagementUiContractTests.cs`, and ViewModel batch-selection tests.

**Interfaces:**
- Consumes: the complete-batch list from the existing history service.
- Produces: a shared selected-batch contract, placeholder state, latest-state visual indicator, refresh command, and stable top-right placement.

- [ ] Add the missing overview placeholder so an unset selection is visibly intentional rather than blank.
- [ ] Keep history selection in the top-right toolbar on both relevant pages and ensure it does not move when the pane opens/closes or the window reaches minimum size.
- [ ] Define selection semantics: unset means latest complete batch for overview display; explicit historical selection is read-only; refresh reloads the newest complete batch and clears stale selection state only when appropriate.
- [ ] Implement the latest indicator as a theme-aware color/light treatment with tooltip and automation metadata, without persistent visible “latest” text.
- [ ] Test empty, one-batch, many-batch, malformed-batch, loading, cancellation, and repository-failure states.

### Task 5: Correct System Area-Group Presentation

**Files:**
- Modify: `native/src/EmsScout.Application/DashboardAreaGroupBuilder.cs`, `native/src/EmsScout.Application/DashboardOverview.cs`, `native/src/EmsScout.Desktop/ViewModels/HomeViewModel.cs`, `native/src/EmsScout.Desktop/Pages/HomePage.xaml`
- Test: `native/tests/EmsScout.Tests/DashboardAreaGroupBuilderTests.cs`, `native/tests/EmsScout.Tests/DashboardUiContractTests.cs`

**Interfaces:**
- Consumes: the existing `public` and `non_public` automatic classifications and user-created groups.
- Produces: overview rows that use the same visual treatment as other status groups while clearly distinguishing automatic classifications from editable groups.

- [ ] Replace the incorrect custom-group-only count text with separate system-classification and custom-group counts.
- [ ] Change system-group scope text from “added locations” to an automatic-classification description.
- [ ] Preserve real public/non-public device counts in the system rows unless the UI contract proves that a neutral placeholder is required; do not hide data to make the table look simpler.
- [ ] Verify clicking each system row opens the matching data filter and does not mutate user-defined area-group settings.
- [ ] Add tests for zero devices, all-public, all-non-public, mixed data, and no custom groups.

### Task 6: Normalize WinUI 3 Visuals And Window Geometry

**Files:**
- Modify: `native/src/EmsScout.Desktop/MainWindow.xaml`, `native/src/EmsScout.Desktop/MainWindow.xaml.cs`, `native/src/EmsScout.Desktop/Styles/*`, `native/src/EmsScout.Desktop/Services/WindowSizeConstraint.cs`
- Test: `native/tests/EmsScout.Tests/HomePageUiContractTests.cs` and screenshot/manual UI verification.

**Interfaces:**
- Consumes: the current WinUI theme resources and minimum-content requirements.
- Produces: a unified title-bar/navigation/content surface and a first-launch window that opens at a useful size while never shrinking below the complete minimum layout.

- [ ] Use one theme-aware surface resource or explicitly coordinated resources for title bar, root content, and NavigationView pane; verify light and dark themes.
- [ ] Replace the default pane-toggle visual treatment that appears as an isolated white square with a coordinated navigation style, while retaining hover, pressed, focus, and keyboard states.
- [ ] Calculate minimum width/height from the required overview/data layout and verify the first-launch size is distinct from the minimum size; do not let persisted geometry reopen an unusably oversized window.
- [ ] Validate 100%, 125%, and 150% DPI plus narrow and wide desktop screenshots; check title bar, history controls, system rows, and table columns for clipping or overlap.

### Task 7: Finish Native-Only Architecture And Shortcut Parity

**Files:**
- Modify: `native/src/EmsScout.Infrastructure/`, `native/src/EmsScout.Desktop/`, `native/tests/EmsScout.Tests/`, `native/EmsScout.Native.slnx`, `scripts/self-test.js`, `package.json`, desktop shortcut/install scripts as applicable
- Remove only after reference audit: `native/src/EmsScout.Legacy/` and retired Web/Electron/TUI/report entry points

**Interfaces:**
- Consumes: the reference graph and package-entry audit from the current Native-only migration plan.
- Produces: one supported application binary and one shortcut target that launch the same published Native build.

- [ ] Move the two still-required JSON adapters out of `EmsScout.Legacy` into Infrastructure and update namespaces/tests.
- [ ] Search the full repository excluding generated output for project, namespace, package, script, and shortcut references to retired architectures.
- [ ] Remove orphaned Legacy/Electron/TUI/report code only after the reference search and Native build pass; retain the Node collector, importer, audit, and field-E2E tools required by the current product.
- [ ] Rebuild the published app, update the desktop shortcut to that exact executable/package entry, and verify the shortcut and development launch resolve the same version and DB path.

### Task 8: Full Verification And Handoff

**Files:**
- Update: `CHANGELOG.md`, `.context-summary.md`
- Verify: all changed source and test files

- [ ] Run `dotnet test native/EmsScout.Native.slnx` and record the result.
- [ ] Build the desktop package with zero warnings and zero errors.
- [ ] Run `git diff --check` and the project’s Node self-test.
- [ ] Run the UI action matrix from Task 1 against both development launch and desktop shortcut.
- [ ] Verify history selection, refresh, system area rows, data-page navigation, Excel export, and audit navigation with the production-shaped local DB without writing it.
- [ ] Record remaining environmental limitations separately from verified fixes; do not call a local test a real EMS field E2E pass.
