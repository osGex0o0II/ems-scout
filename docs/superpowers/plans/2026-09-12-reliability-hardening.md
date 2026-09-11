# EMS Scout Reliability Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复历史批次、实时快照、错误展示、任务状态和安装部署链路中的数据一致性问题，使 EMS Scout 达到可交付验证标准。

**Architecture:** 将数据库 schema 迁移从读取路径移出，读取路径只使用只读连接；实时详情必须绑定明确的 collection run，并按楼栋增量保存；UI 以明确的快照可用性和错误状态区分“无数据”“离线”和“读取失败”。安装发布使用可写的用户工作区并在安装后验证快捷方式实际启动的包版本。

**Tech Stack:** .NET 10, WinUI 3, SQLite/Microsoft.Data.Sqlite, Node.js, better-sqlite3, MSIX/Windows App SDK, xUnit.

**Spec:** 当前会话中的深度核查清单及项目根目录 `AGENTS.md`。

## Global Constraints

- 保留并兼容工作树中已有用户修改，不回退无关文件。
- 历史读取不得隐式创建表、ALTER TABLE 或写入业务数据。
- 实时详情必须带明确 `runId`，禁止通过“最新批次”猜测归属。
- 部分楼栋保存不得删除同批次其他楼栋的实时快照。
- 质量问题必须在任务状态和数据页可见，不得伪装成空结果。
- 不提交或推送远端；完成前必须验证源码、数据库、安装包和桌面快捷方式。

---

### Task 1: Establish Regression Coverage for Batch and Snapshot Boundaries

**Files:**
- Modify: `native/tests/EmsScout.Tests/SqliteRealtimeSnapshotStoreTests.cs`
- Modify: `native/tests/EmsScout.Tests/SqliteCollectionRunRepositoryTests.cs`
- Modify: `native/tests/EmsScout.Tests/SqliteDeviceReadRepositoryTests.cs`
- Modify: `native/tests/EmsScout.Tests/DataManagementUiContractTests.cs`

**Interfaces:**
- Consumes: current snapshot store, collection run repository, device read repository and data page behavior.
- Produces: failing tests for read-only schema behavior, per-building snapshot replacement, missing-snapshot semantics and visible data errors.

- [ ] Add tests that fail when `LoadAsync` creates `run_realtime_details` for a database without that table.
- [ ] Add tests that save building `1号`, then save building `2号` for the same run, and assert both remain.
- [ ] Add tests that a history run without a snapshot is represented as unavailable snapshot data rather than indistinguishable device-level “无实时数据”.
- [ ] Add a UI contract test requiring the data page to bind and display the view model error/status text.
- [ ] Run the focused tests and confirm each new test fails for the expected pre-fix reason.

### Task 2: Split SQLite Read Paths from Schema Migration

**Files:**
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteRealtimeSnapshotStore.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteCollectionRunRepository.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteDeviceReadRepository.cs` if needed for explicit missing-table handling
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteSchemaMigrator.cs` or create it if no existing migration owner exists
- Modify: `native/src/EmsScout.Desktop/App.xaml.cs` to run migrations once at startup or before write operations

**Interfaces:**
- Consumes: Task 1 tests and current `scripts/schema.sql` contract.
- Produces: read-only repositories that never execute schema creation or ALTER statements; a single explicit migration entry point.

- [ ] Implement a migration service that creates missing tables/columns only in an explicit writable startup/write path.
- [ ] Change history listing/comparison and realtime snapshot loading to `Mode=ReadOnly` and query-only behavior.
- [ ] Return a typed empty/unavailable snapshot result when `run_realtime_details` is absent, without mutating the database.
- [ ] Run focused tests and then the full .NET suite.

### Task 3: Bind Realtime Details to Runs and Preserve Partial-Building Data

**Files:**
- Modify: `native/src/EmsScout.Application/Devices/IRealtimeSnapshotStore.cs`
- Modify: `native/src/EmsScout.Application/Devices/RealtimeDetailSet.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteRealtimeSnapshotStore.cs`
- Modify: `native/src/EmsScout.Infrastructure/Realtime/RealtimeLatestJsonSource.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs`
- Modify: `scripts/collect-realtime-all-batch.js` and its run metadata output
- Modify: `native/tests/EmsScout.Tests/RealtimeLatestJsonSourceTests.cs`
- Modify: `native/tests/EmsScout.Tests/SqliteRealtimeSnapshotStoreTests.cs`

**Interfaces:**
- Consumes: an explicit target run ID and the current building selection.
- Produces: snapshot rows with source run metadata; stale/mismatched source rejection; per-building upsert/replace behavior.

- [ ] Add a failing test showing a realtime file older than the selected run is rejected.
- [ ] Add a failing test showing an explicit run ID is used instead of querying the latest run.
- [ ] Add source metadata (`runId` and capture timestamp) to realtime output and validate it on import.
- [ ] Replace whole-run deletion with deletion/replacement scoped to the selected buildings.
- [ ] Reject or visibly mark files with missing/mismatched run metadata as stale instead of attaching them automatically.
- [ ] Run focused tests and a temporary SQLite integration test for full plus partial-building saves.

### Task 4: Make Missing Data and Failures Explicit in the UI

**Files:**
- Modify: `native/src/EmsScout.Application/Devices/DeviceRecord.cs`
- Modify: `native/src/EmsScout.Application/Devices/RealtimeDetailSet.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/DataViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/DataPage.xaml`
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/TasksPage.xaml`
- Modify: `native/tests/EmsScout.Tests/DataManagementUiContractTests.cs`
- Modify: `native/tests/EmsScout.Tests/TasksPageUiContractTests.cs`

**Interfaces:**
- Consumes: typed snapshot availability and task failure details from Tasks 2-3.
- Produces: separate UI states for no snapshot, offline device, unmatched realtime detail, stale source, database error and empty filter result.

- [ ] Add tests for each distinct state before changing UI code.
- [ ] Add a visible status/error binding on the data page without hiding the actual exception behind “没有符合条件的设备”.
- [ ] Show snapshot availability at batch level and keep device offline state separate.
- [ ] Ensure quality exit code 2 is visibly “完成但待复核”, while exit code 1/3/4 is failure with stage, elapsed time and cause.
- [ ] Verify filter options remain selectable after refresh and history selection.

### Task 5: Stabilize Task Event Ordering and Window Settings Migration

**Files:**
- Modify: `native/src/EmsScout.Desktop/Services/NodeCollectionTaskRunner.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs`
- Modify: `native/src/EmsScout.Application/Settings/AppSettingsService.cs`
- Modify: `native/src/EmsScout.Desktop/MainWindow.xaml.cs`
- Modify: `native/src/EmsScout.Desktop/Services/WindowSizeConstraint.cs`
- Modify: `native/tests/EmsScout.Tests/AppSettingsServiceTests.cs`
- Modify: `native/tests/EmsScout.Tests/TasksPageUiContractTests.cs`

**Interfaces:**
- Consumes: task lifecycle and settings contracts.
- Produces: generation-scoped progress events and migrated bounded window placement.

- [ ] Add a failing test for a late event not overwriting terminal task failure state.
- [ ] Add a failing test for old oversized placement being normalized and persisted.
- [ ] Add task generation/token checks around queued dispatcher events.
- [ ] Normalize old placement to minimum/maximum bounds and save the normalized value.
- [ ] Re-run lifecycle and settings tests.

### Task 6: Quality Report Association and Release Data Gate

**Files:**
- Modify: `scripts/quality-report.js`
- Modify: `native/src/EmsScout.Infrastructure/Quality/JsonQualityAuditService.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/AuditViewModel.cs`
- Modify: `native/tests/EmsScout.Tests/HistoryDataUiContractTests.cs`
- Modify: `native/tests/EmsScout.Tests/CollectionRunRepositoryTests.cs`

**Interfaces:**
- Consumes: explicit run IDs and current quality report output.
- Produces: auditable per-run quality reports and release gating that does not present P1 issues as a clean success.

- [ ] Add a failing test for missing run-specific report being reported as unavailable/stale with the selected run ID.
- [ ] Ensure quality run writes `quality_report_run{runId}.json` and report metadata matches the selected batch.
- [ ] Keep known EMS source exceptions annotated without hiding unrelated P1/P2 issues.
- [ ] Verify current data report reflects the 6号楼 -102 baseline delta and does not claim clean completion.

### Task 7: Remove Unused Architecture and Fix Packaging/Shortcut Consistency

**Files:**
- Modify: `native/EmsScout.Native.slnx`
- Modify: `native/README.md`
- Modify: `native/docs/MIGRATION_PLAN.md`
- Remove only after reference audit: `native/src/EmsScout.Collection/`
- Remove only generated residue after verification: `native/src/EmsScout.Legacy/bin/`, `native/src/EmsScout.Legacy/obj/`
- Modify: packaging/build scripts and manifest files identified by the package build

**Interfaces:**
- Consumes: final source tree and deployment path behavior from Tasks 1-6.
- Produces: one supported collection architecture and an installable package whose shortcut launches the newly built version.

- [ ] Prove no production project or script references `EmsScout.Collection` before removal.
- [ ] Ensure packaged scripts/runtime/data root resolve to a writable user workspace, not Program Files.
- [ ] Build a new MSIX with a version higher than `1.0.8.41`.
- [ ] Install/update it, launch through the desktop shortcut, verify executable path/version and clear old crash logs before testing.
- [ ] Exercise overview, collection, data, history selection, audit, settings, close-to-tray, update and uninstall flows.

### Task 8: Final Verification and Review Gate

**Files:**
- No production changes unless a verification failure identifies a concrete regression.

**Interfaces:**
- Consumes: all implementation tasks.
- Produces: evidence-backed delivery decision; no commit or push.

- [ ] Run `dotnet test native/EmsScout.Native.slnx -c Debug --no-restore` and require zero failures.
- [ ] Run `npm run self-test` and require success.
- [ ] Run `dotnet build native/src/EmsScout.Desktop/EmsScout.Desktop.csproj -c Debug --no-restore` and require zero warnings/errors.
- [ ] Run database integrity, temporary DB migration, partial snapshot, quality-report and package smoke tests.
- [ ] Run `git diff --check` and inspect the final diff for unrelated changes.
- [ ] Perform an independent code review of the final diff before delivery.
