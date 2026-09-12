# EMS Data Integrity Guards Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent structurally incomplete collection results and unbound realtime data from being presented or imported as trustworthy EMS Scout data without treating a historical card count as a universal baseline.

**Architecture:** Keep the current database unchanged, strengthen the shared JSON validation gate before import, and let quality reporting downgrade a completed run to `needs_review` when blocking findings exist. Validate each batch against its own declared buildings, snapshot counts and hierarchy; preserve the existing realtime batch-id rejection and expose its coverage/missing state through tests and user-facing status data.

**Tech Stack:** Node.js, better-sqlite3, C#/.NET 10, xUnit, WinUI 3.

**Spec:** User request: execute the non-recapture repairs identified by the cross-validation of collection completeness, batch state, and realtime linkage.

## Global Constraints

- Do not modify or fabricate the existing `out/ac.db` data in this change.
- Do not require a live EMS recapture for the fixes in this plan.
- Preserve the known 6号楼 BM inline placeholder exception.
- Existing partial-import fixtures may continue to use `EMS_SKIP_ENUM_VALIDATION=1` in tests only.
- A batch with blocking quality findings must not remain `completed` after quality reporting.

### Task 1: Strengthen JSON collection validation

**Files:**
- Modify: `src/rules.js`
- Modify: `src/enum-validator.js`
- Modify: `scripts/import.js`
- Test: `scripts/self-test.js`

**Interfaces:**
- `validateEnumData(data, options)` remains the import gate.
- Add an explicit error for non-inline empty subareas, subarea/page errors and unresolved quality reasons. Do not reject a structurally complete batch because its card or sub-area counts differ from an older capture.

- [x] Write failing self-tests for a non-inline empty 6号楼 subarea and a `template_values_unconfirmed` page.
- [x] Run `npm run self-test` and verify those cases fail before implementation.
- [x] Implement the validation rules while exempting only the known 6号楼 BM inline placeholder.
- [x] Run the focused self-test and verify the new cases are rejected while valid partial fixtures still pass with the explicit test bypass.

### Task 2: Make quality status authoritative

**Files:**
- Modify: `scripts/quality-report.js`
- Modify: `native/src/EmsScout.Application/Collection/CollectionRuns.cs`
- Modify: `native/src/EmsScout.Application/Collection/CollectionRunCompleteness.cs`
- Test: `scripts/self-test.js`
- Test: `native/tests/EmsScout.Tests/CollectionRunCompletenessTests.cs` (create if absent)

**Interfaces:**
- Quality report persistence updates a completed run to `needs_review` when any non-INFO issue remains.
- A clean report keeps or sets the run to `completed`.
- `CollectionRunRecord.StatusLabel` exposes `needs_review` as `需复核`.
- Complete fleet snapshot selection continues to exclude non-completed and quality-blocked runs.

- [x] Add a failing test proving a blocking report downgrades a completed run.
- [x] Run the focused test and verify it fails because status stays `completed`.
- [x] Implement status persistence and the label mapping.
- [x] Run Node and .NET tests for the status behavior.

### Task 3: Preserve realtime unavailability and prevent false aggregate zeros

**Files:**
- Modify: `native/src/EmsScout.Application/DashboardOverview.cs`
- Modify: `native/src/EmsScout.Application/DashboardOverviewService.cs`
- Modify: `native/src/EmsScout.Application/DashboardAreaGroupBuilder.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/HomeViewModel.cs`
- Test: `native/tests/EmsScout.Tests/DashboardAreaGroupBuilderTests.cs`
- Test: `native/tests/EmsScout.Tests/RealtimeLatestJsonSourceTests.cs`

**Interfaces:**
- Dashboard data carries a realtime availability/status string.
- Mode, temperature, and lock aggregates are marked unavailable rather than reported as meaningful zero when the selected batch has no realtime details.
- Historical batches with no snapshot retain the existing explicit `RealtimeDetailSet.StatusText`.

- [x] Add a failing test for an overview built from rows with realtime unavailable.
- [x] Run the focused .NET test and verify the aggregate currently reports ordinary zero without an availability signal.
- [x] Implement the status propagation and non-misleading dashboard state.
- [x] Run the focused and full .NET tests.

### Task 4: Full verification and delivery evidence

**Files:**
- Modify: `CHANGELOG.md` only if the repository convention requires an entry after verification.

- [x] Run `npm run self-test`.
- [x] Run `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`.
- [x] Run the JSON validator against the current `enum_full_v5.json` and confirm it rejects the known incomplete current result without modifying the database.
- [x] Re-run read-only database checks for `run24`, realtime snapshot count, and worktree status.
- [x] Review the diff and report remaining limitations: the existing `#24` data remains unchanged and requires a future recapture before it can become trustworthy.
