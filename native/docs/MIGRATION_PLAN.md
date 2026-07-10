# EMS Scout Native UI Refactor Plan

Verified on 2026-07-04 before implementation:

- Windows App SDK stable package used here: `Microsoft.WindowsAppSDK` `2.2.0`.
- WinUI 3 app model: Windows App SDK desktop app with `NavigationView`.
- Runtime: .NET 10.
- Playwright bridge: `Microsoft.Playwright` with Chromium CDP support.

Primary references:

- https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads
- https://learn.microsoft.com/en-us/windows/apps/winui/winui3/
- https://github.com/microsoft/WinUI-Gallery
- https://github.com/microsoft/WindowsAppSDK-Samples
- https://playwright.dev/dotnet/docs/api/class-browsertype

## Product Direction

This is a product/UI refactor, not a one-for-one migration of the old panel. The first native cut focuses on a clean Windows desktop shell with five stable top-level areas:

1. Overview: current data health, device totals, building summaries, and primary workflow status.
2. Collection Tasks: connection checks, building selection, progress, logs, and result summaries.
3. Data Management: dense filtered device list, detail panel, and the only user-facing Excel export.
4. Group Settings: system groups, user groups, matching rules, and impact previews.
5. System Settings: EMS/CDP connection, data/export directories, logging, collection behavior, and appearance.

Advanced quality audit, export history, and about/migration notes are not top-level destinations in the refactored product. They can return later as contextual panels when they support the core workflow.

## Current Cut

The native solution now proves the first refactor path:

1. Start a WinUI 3 NavigationView desktop app.
2. Keep the existing Electron/Node pipeline intact.
3. Read the current legacy `out/enum_full_v5.json`.
4. Convert it into typed C# domain records.
5. Render a real dashboard from existing EMS data.
6. Verify totals with golden tests.
7. Read `out/ac.db` through a native SQLite repository.
8. Render the first native read-only data-management list with building, communication, search, and detail preview.
9. Refactor the top-level NavigationView to Overview, Collection Tasks, Data Management, Group Settings, and System Settings.

## Target Architecture

- `EmsScout.Desktop`: Windows-native shell, pages, view models, commands.
- `EmsScout.Application`: use cases and view-ready orchestration.
- `EmsScout.Collection`: Playwright/Edge CDP collection orchestration and page-side extraction bridge.
- `EmsScout.Domain`: device, building, quality, collection, and data-management rules.
- `EmsScout.Infrastructure`: SQLite, filtered Excel export, file system, logging.
- `EmsScout.Legacy`: adapters over current JSON and SQLite artifacts.
- `EmsScout.Tests`: rule tests, golden-file tests, and refactor parity tests.

## Phase Gates

### Phase 1: Read-Only Native Shell

Status: started.

Acceptance:

- WinUI app builds with zero warnings.
- Home page reads real legacy data.
- Golden tests prove current totals by building and communication state.
- Existing Electron app remains available as a legacy fallback while the native product shape is rebuilt.

### Phase 2: Native Data Workbench

Status: started.

Implemented:

- `Microsoft.Data.Sqlite` read repository over `cards/pages/sub_areas`.
- Native Data Management page with building filter, communication filter, area filter, quick filter, search, 500-row list, and read-only detail preview.
- DB-only health derivation for offline, unknown communication, temperature issues, placeholder names, public/private area, and needs-review state.
- Workbench statistic cards for total, running, stopped, offline, needs review, temperature issues, public area, and private area.
- Realtime latest JSON reader over `out/realtime_*_latest.json`.
- DB-row realtime attachment using exact key first and unique same-name fallback.
- Native display for realtime match status, control lock, point completeness, Modbus address, and DevId.
- Native SQLite loading for `realtime_match_overrides`, including `map_to_db`, `create_virtual`, `ignore_duplicate`, `classify_only`, area overrides, and zuo overrides.
- Native Data Management rows now include virtual managed realtime devices, manual match status, override notes, device notes, and device tags.
- Native query specification for the Data Management workbench covers building, communication state, floor, zuo, tag, area, quick health filters, realtime match state, realtime point state, and search.
- Data Management UI exposes the migrated advanced filters with reset behavior.
- Native Quality Audit page now includes a realtime-source reconciliation workbench with DB/realtime summary, exact/manual/relaxed match counts, diff type counts, diff type filter, search, and read-only detail panel.
- Reconciliation rows now carry native rule explanations, confidence, evidence summaries, rule version, and decision paths.
- Reconciliation detail rows can deep-link into Data Management with building/search/realtime-match filters applied.
- Data Management can export the current filtered device list from native code to `.xlsx` with `summary`, `devices`, and `filters` sheets.
- Native `.xlsx` generation now uses a shared lightweight SpreadsheetML writer based on `ZipArchive` for the Data Management export path.
- Golden tests for current `out/ac.db` counts.
- Golden tests for realtime source rows, DB attachment counts, manual overrides, virtual managed devices, migrated filters, floor options, and zuo options.
- Golden tests for realtime-source reconciliation summary, diff type counts, virtual override filtering, DevId search, rule version, evidence summaries, decision paths, and reconciliation-to-data navigation targets.
- Golden tests for Data Management device workbook structure, full filtered row export, current realtime/virtual baselines, and area-filtered exports.

Remaining acceptance:

- Show a virtualized high-density device list and right-side detail panel.
- Add a dedicated realtime-source parity view or reconciliation mode for the old panel API's one-row-per-realtime-detail perspective.

### Phase 3: Native Quality Audit

Acceptance:

- Port `rules.js` and panel health rules into C#.
- Split issues into P1/P2/P3 queues.
- Golden tests prove quality counts and representative samples match existing reports.
- Quality rows deep-link to the native device detail view.

Started:

- Realtime-source parity view compares SQLite cards to latest realtime detail rows.
- Current native diff categories: `NEW_DEVICE`, `MISSING_IN_REALTIME`, `MATCH_FAILED`, `VIRTUAL_OVERRIDE`, `DUPLICATE_RENDER`, `DATA_NOISE`.
- Native explanation fields follow the legacy `reconcile-v1.0.0` rule catalog shape.

Remaining:

- Add deep links from Data Management rows back to Quality Audit filtered context.

### Phase 4: Native Import And Data Export

Status: started.

Acceptance:

- Replace JSON-to-SQLite import in C#.
- Keep Data Management filtered `.xlsx` export as the only user-facing export path.
- Compare row counts, key columns, and summary totals against current SQLite/Data Management filters.

Started:

- Native data-workbench Excel export writes complete filtered device results, including realtime match, DevId, Modbus address, lock state, point completeness, notes, tags, area, health, and override fields.
- Top-level export/report destinations are removed from the refactored navigation; filtered Excel export lives in Data Management.
- Native TXT/Markdown report generation and reconciliation workbook export were removed from the active plan to avoid multiple competing export paths.

### Phase 5: Native Collection Orchestrator

Acceptance:

- Use Playwright .NET to connect to Edge CDP.
- Keep page-side JavaScript extraction helpers while moving waits, retries, quality gates, crash recovery, and persistence to C#.
- Validate one-building runs against known baselines before full collection.

### Phase 6: Legacy Panel Retirement

Acceptance:

- Native app can collect all buildings, import, audit, export, and browse data.
- Full run matches or intentionally supersedes existing quality baselines.
- Electron panel is retained only as an archived fallback until the native refactor has passed field use.
