# Realtime Snapshot Binding and Run ID Reuse Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bind current device data to its local SQLite realtime snapshot and allocate the smallest available collection batch ID after historical batches are deleted.

**Architecture:** The current repository will prefer the SQLite snapshot store for the selected current run, while retaining the metadata-validated JSON source as a fallback only when no snapshot exists. New collection runs will allocate the lowest positive unused ID inside the same transaction, preserving all existing foreign-key relationships and leaving historical run IDs unchanged.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite, xUnit, Node.js, better-sqlite3, Node built-in test runner.

**Spec:** User request: repair the 17 September realtime display and prevent new batches from continuing to count deleted IDs.

## Global Constraints

- Current user-facing data must never bind an unverified legacy realtime JSON file to a database batch.
- Local field data remains local and must not be committed or pushed.
- Existing run foreign keys and retained historical IDs must remain valid.
- Security regression tests must remain in place.

### Task 1: Current realtime snapshot preference

**Files:**
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteDeviceReadRepository.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteRealtimeReconciliationService.cs`
- Modify: `native/src/EmsScout.Desktop/App.xaml.cs`
- Test: `native/tests/EmsScout.Tests/SqliteDeviceReadRepositoryTests.cs`
- Test: `native/tests/EmsScout.Tests/RealtimeReconciliationTests.cs`

**Interfaces:**
- Consumes: `IRealtimeSnapshotStore.LoadAsync(runId, buildings, cancellationToken)`.
- Produces: current-data realtime rows sourced from the selected batch snapshot when available; controlled JSON fallback otherwise.

- [x] Write a failing repository test with a legacy JSON source and a valid SQLite snapshot, asserting the snapshot fields are attached to current rows.
- [x] Run the focused test and confirm it fails because the current path calls JSON instead of the snapshot store.
- [x] Add snapshot-first loading for current data and preserve the existing history path.
- [x] Pass the snapshot store into the reconciliation service and apply the same current-data rule there.
- [x] Run the focused repository and reconciliation tests.

### Task 2: Reuse the lowest available batch ID

**Files:**
- Modify: `src/data-history.js`
- Modify: `scripts/import.js` if required by the import entry point
- Test: `scripts/self-test.js` or a focused Node test under `tests/`

**Interfaces:**
- Consumes: the existing transactional collection-run insertion path.
- Produces: `createRun`/import-created runs use the smallest positive unused `collection_runs.id` while preserving `run_key` uniqueness.

- [x] Add a failing test using a temporary database containing run IDs 1 and 3, asserting the next imported run receives ID 2.
- [x] Run the focused test and confirm it fails because SQLite AUTOINCREMENT continues after the deleted maximum.
- [x] Implement explicit lowest-unused ID allocation in the existing import transaction and native restore-backup path.
- [x] Run the focused batch-ID test and the existing Node self-test.

### Task 3: Full verification and data safety check

**Files:**
- No production data files added or committed.

- [x] Run the complete native test suite.
- [x] Run the security test `node --test tests/security/realtime-browser-security.test.js`.
- [x] Verify `out/ac.db` still contains the retained realtime snapshots and that no ignored field files are staged.
- [x] Review the diff and report the exact behavior change and any residual fallback condition.

### Task 4: Post-review hardening

**Files:**
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteCollectionRunRepository.cs`
- Test: `native/tests/EmsScout.Tests/SqliteDeviceReadRepositoryTests.cs`
- Test: `native/tests/EmsScout.Tests/RealtimeReconciliationTests.cs`

- [x] Replace fixed live-device counts in regression tests with dynamic positive-behavior assertions.
- [x] Use an immediate SQLite write transaction for native restore-backup run-ID allocation.
- [x] Re-run full tests and build after the hardening changes.
