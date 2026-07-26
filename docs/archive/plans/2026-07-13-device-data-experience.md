# Device Data Experience Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the P0 device-data workflow with problem quick filters, persistent query context, a useful device detail panel, resilient refresh behavior, and one immutable query scope for result counts and Excel export.

**Architecture:** Keep `DeviceQuery` as the sole immutable filter contract shared by SQLite reads and Excel export. Add a small application-layer snapshot/version helper so the desktop ViewModel can retain the last successful rows and reject stale responses without coupling that behavior to WinUI. Render quick-filter counts from `DeviceFacets`, keep selected-device detail presentation in `DataDeviceRow`, and make the export preview use the exact query snapshot that is passed to the export service.

**Tech Stack:** .NET 10, C#, WinUI 3 XAML, CommunityToolkit.Mvvm, SQLite, xUnit, Open XML workbook writer.

## Global Constraints

- Never open or mutate `out/ac.db`, `data/ac.db`, `data/1号楼/ac.db`, `data/2号楼/ac.db`, or their WAL/SHM files during implementation or validation.
- Use only isolated temporary databases and `scripts/run-native.ps1 -UiValidation` for runtime validation.
- Preserve “设备数据当前筛选 Excel” as the only user-facing data export route.
- Historical batches remain read-only and cannot be exported.
- Do not modify collection rules or communication-state mappings.
- Support a minimum 1280x720 window without overlapping essential actions.

---

### Task 1: Immutable query snapshot and quick-filter contract

**Files:**
- Create: `native/src/EmsScout.Application/Devices/DeviceDataQuerySnapshot.cs`
- Modify: `native/src/EmsScout.Application/Devices/DeviceHealthRules.cs`
- Modify: `native/src/EmsScout.Application/Devices/DeviceFacets.cs`
- Test: `native/tests/EmsScout.Tests/DeviceDataQuerySnapshotTests.cs`
- Test: `native/tests/EmsScout.Tests/SqliteDeviceReadRepositoryTests.cs`

**Interfaces:**
- Consumes: immutable `DeviceQuery` and `DeviceListResult`.
- Produces: `DeviceDataQuerySnapshot.Begin(DeviceQuery)`, version tokens, current-response checks, and quick-filter keys `offline`, `unknown`, `temp_abnormal`, `realtime_missing`, `needs_review`.

- [x] **Step 1: Write failing tests** for monotonic request versions, stale-response rejection, all five quick filters, and facet counts under a location/status base query.
- [x] **Step 2: Run the focused tests** with `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter "FullyQualifiedName~DeviceDataQuerySnapshotTests|FullyQualifiedName~SqliteDeviceReadRepositoryTests"` and confirm failure is caused by the missing snapshot behavior.
- [x] **Step 3: Implement the minimal application contracts** without adding schema or dependencies.
- [x] **Step 4: Run the focused tests** and confirm they pass.

### Task 2: Stable refresh and active-filter summary

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/DataViewModel.cs`
- Modify: `native/tests/EmsScout.Tests/DataManagementUiContractTests.cs`

**Interfaces:**
- Consumes: `DeviceDataQuerySnapshot`, `DeviceQuery`, `DeviceFacets`.
- Produces: `ActiveFilterSummary`, `HasLoadError`, `LoadErrorText`, `HasStaleResults`, `ApplyQuickFilterAsync(string)`, and query-snapshot based page loading.

- [x] **Step 1: Write failing source-contract tests** requiring explicit active-filter text, a retryable page error state, preservation of `Devices` on read failure, and stale-response guards.
- [x] **Step 2: Run the UI contract test** and confirm the new assertions fail.
- [x] **Step 3: Refactor page loading** so rows, totals, facets, and selection are replaced only after a current request succeeds; failures retain the last successful table and filters.
- [x] **Step 4: Add quick-filter selection and reset behavior** while preserving existing location/status filters.
- [x] **Step 5: Run the focused test** and confirm it passes.

### Task 3: Quick actions, states, and device detail panel

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/DataDeviceRow.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/DataPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/DataPage.xaml.cs`
- Test: `native/tests/EmsScout.Tests/DataManagementUiContractTests.cs`

**Interfaces:**
- Consumes: `DataViewModel` quick counts/state and the selected `DataDeviceRow`.
- Produces: five count-bearing quick-filter buttons; an active-condition/result strip; a retryable error bar; persistent current/history labels; and a right-side detail panel covering identity, captured values, realtime values, quality/watch evidence, and history availability.

- [x] **Step 1: Write failing XAML/code-behind contract assertions** for the five quick filters, accessible names/tooltips, error retry, selected-device details, and historical read-only copy.
- [x] **Step 2: Run the UI contract test** and verify expected failures.
- [x] **Step 3: Implement the XAML and handlers** using existing WinUI resources and fixed responsive columns; do not nest cards.
- [x] **Step 4: Build the desktop project** to catch binding/XAML compiler errors.
- [x] **Step 5: Run the UI contract test** and confirm it passes.

### Task 4: Export preview and row-count consistency

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/DataViewModel.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteDeviceExportService.cs`
- Modify: `native/tests/EmsScout.Tests/DeviceExportTests.cs`
- Modify: `native/tests/EmsScout.Tests/DataManagementUiContractTests.cs`

**Interfaces:**
- Consumes: the current successful immutable `DeviceQuery` snapshot and its total.
- Produces: `ExportPreviewText`, an export confirmation dialog showing the exact row count, and a service invariant that `DeviceExportResult.RowCount` equals both the repository result total and written workbook data rows.

- [x] **Step 1: Write failing tests** that capture the query sent to preview/search and export, assert filter equality except pagination, and reject a service result whose row count diverges from the query result.
- [x] **Step 2: Run focused export tests** and confirm expected failure.
- [x] **Step 3: Implement query snapshot reuse and pre-export confirmation**; if filters change, require a successful refresh before enabling export.
- [x] **Step 4: Assert writer/service row counts** and preserve the existing 50,000-row limit and history prohibition.
- [x] **Step 5: Run focused export and UI tests** and confirm they pass.

### Task 5: Full verification and evidence

**Files:**
- Modify: `.context-summary.md`
- Modify: `CHANGELOG.md`
- Create: `docs/validation/2026-07-13-device-data-experience.md`

**Interfaces:**
- Consumes: the completed P0-D implementation.
- Produces: reproducible automated and isolated runtime evidence without touching production databases.

- [x] **Step 1: Run `npm run native:test`** and record the exact pass/fail count.
- [x] **Step 2: Run `npm run native:build`** and record warnings/errors.
- [x] **Step 3: Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native.ps1 -NoBuild -UiValidation`** and inspect the available 949x540 stress window and 1280x768 maximized window, including empty data, quick filters, details, loading/error/read-only state, and export preview.
- [x] **Step 4: Audit only the temporary UI-validation database** with schema version and quick-check commands; verify packaged user settings remain unchanged.
- [x] **Step 5: Run `git diff --check`** and inspect the scoped diff for protected database paths.
- [x] **Step 6: Update context, changelog, and dated validation evidence** with only observed results and explicitly distinguish local isolated validation from real EMS field E2E.
