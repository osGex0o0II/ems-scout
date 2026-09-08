# 历史数据管理重构 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 `AuditPage` 重构为以历史批次为主对象的历史数据管理工作区，并提供可验证的对比、审计和安全恢复流程。

**Architecture:** 保留现有 `AuditPage` 路由、`AuditViewModel` 和 `ICollectionRunRepository` 的核心边界，在 Application 层增加批次元数据/对比结果模型，在 Infrastructure 层增加幂等迁移和快照对比查询，在 Desktop 层用三个 Pivot 工作区替换当前双栏嵌套滚动布局。恢复仍由现有仓储执行，但 UI 必须先完成对比并通过二次确认。

**Tech Stack:** C# 10/.NET 10, WinUI 3, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, xUnit, Node.js 自检脚本。

**Spec:** `docs/superpowers/specs/2026-09-08-history-data-management-design.md`

## Global Constraints

- 不删除或重建现有 SQLite 表，不丢失旧批次、设备备注、标签和人工覆盖。
- 旧数据库新字段使用 `source=采集导入`、`data_version=v1.0.0`、`operator=本机` 默认值。
- 全量恢复仍必须覆盖 1-6 号楼；异常批次、未完成批次和无有效快照批次不可恢复。
- 不修改采集、导入和现有数据管理页的公开行为。
- 页面只保留一个外层滚动区域，所有新增操作必须有禁用态、异常提示和可测试的 UI 契约。

---

### Task 1: 定义批次元数据、筛选和对比契约

**Files:**
- Modify: `native/src/EmsScout.Application/Collection/CollectionRuns.cs`
- Create: `native/src/EmsScout.Application/Collection/CollectionRunMetadata.cs`
- Create: `native/src/EmsScout.Application/Collection/CollectionRunComparison.cs`
- Create: `native/src/EmsScout.Application/Collection/CollectionRunFilter.cs`
- Test: `native/tests/EmsScout.Tests/CollectionRunComparisonTests.cs`
- Test: `native/tests/EmsScout.Tests/CollectionRunMetadataTests.cs`

**Interfaces:**
- `CollectionRunMetadata` exposes `Source`, `DataVersion`, `Operator`, `IsCurrent` and defaulting helpers for legacy records.
- `CollectionRunFilter` contains nullable `From`, `To`, `Building`, `Source`, `Status`, `Keyword`.
- `CollectionRunComparison` contains current/snapshot counts, `AddedCount`, `MissingCount`, `ChangedCount`, `IsRestorable`, `BlockingReason` and `IReadOnlyList<CollectionRunBuildingDifference>`.

- [ ] **Step 1: Write failing tests** for legacy metadata defaults, current-batch selection, keyword/building filtering, and comparison summary calculations.
- [ ] **Step 2: Run focused tests** with `dotnet test ... --filter FullyQualifiedName~CollectionRun` and verify the new contracts fail before implementation.
- [ ] **Step 3: Add immutable records and pure helper methods** with deterministic timestamp parsing and case-insensitive matching.
- [ ] **Step 4: Run focused tests** and verify all new contract tests pass.
- [ ] **Step 5: Commit** with `git add native/src/EmsScout.Application/Collection native/tests/EmsScout.Tests/CollectionRun* && git commit -m "feat: define history batch comparison contracts"`.

### Task 2: Add SQLite metadata migration and comparison queries

**Files:**
- Modify: `scripts/schema.sql`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteCollectionRunRepository.cs`
- Modify: `native/src/EmsScout.Application/Collection/CollectionRuns.cs`
- Test: `native/tests/EmsScout.Tests/CollectionRunRepositoryTests.cs`

**Interfaces:**
- Extend `ICollectionRunRepository` with `CompareCurrentAsync(long runId, CancellationToken)` returning `CollectionRunComparison`.
- `ListAsync` returns records with source/version/operator metadata populated from columns or legacy defaults.

- [ ] **Step 1: Add failing SQLite tests** for opening a legacy schema, adding metadata columns, loading defaults, and comparing a full/partial snapshot to current data.
- [ ] **Step 2: Run the focused repository tests** and confirm failures identify missing columns/query methods.
- [ ] **Step 3: Add idempotent column migration** for `source`, `data_version`, `operator_name`, and `restored_from_run_id`; update `schema.sql` with the same additive definitions.
- [ ] **Step 4: Implement comparison queries** using `run_buildings`/`run_cards` versus `buildings`/`cards`, deriving per-building counts and restoration eligibility without mutating data.
- [ ] **Step 5: Run repository tests twice against a temporary database** to prove migration idempotence and verify restore tests remain green.
- [ ] **Step 6: Commit** with `git add scripts/schema.sql native/src/EmsScout.Application/Collection/CollectionRuns.cs native/src/EmsScout.Infrastructure/Sqlite/SqliteCollectionRunRepository.cs native/tests/EmsScout.Tests/CollectionRunRepositoryTests.cs && git commit -m "feat: persist and compare history batch metadata"`.

### Task 3: Implement history management ViewModel behavior

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/AuditViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionRunRow.cs`
- Create: `native/src/EmsScout.Desktop/ViewModels/CollectionRunDetail.cs`
- Create: `native/src/EmsScout.Desktop/ViewModels/CollectionRunBuildingDifferenceRow.cs`
- Test: `native/tests/EmsScout.Tests/HistoryDataViewModelTests.cs`

**Interfaces:**
- Add observable properties for `ActiveWorkspace`, `HistoryFilter`, `FilteredRuns`, `SelectedRunDetail`, `SelectedComparison`, `LastRefreshedText`, and `IsComparisonReady`.
- Add commands `ApplyHistoryFilter`, `LoadSelectedComparison`, `MarkRunAnomaly`, `ClearRunAnomaly`, `DeleteRun`, and `RestoreRun` with `CanExecute` tied to `IsBusy`, selection, comparison readiness, and anomaly state.
- `RestoreRun` must reject until `SelectedComparison.IsRestorable` is true.

- [ ] **Step 1: Write failing ViewModel tests** for default history workspace, filter results, selection detail, comparison loading, and restore command eligibility.
- [ ] **Step 2: Run focused tests** and verify the new behavior is red.
- [ ] **Step 3: Refactor existing run refresh into a single source list** and derive filtered rows, summary metrics, current run and recoverable count from it.
- [ ] **Step 4: Add detail/comparison loading and error status handling** without throwing through the UI event loop.
- [ ] **Step 5: Run focused tests** and verify selection, filters, busy-state gating, and errors pass.
- [ ] **Step 6: Commit** with `git add native/src/EmsScout.Desktop/ViewModels native/tests/EmsScout.Tests/HistoryDataViewModelTests.cs && git commit -m "feat: add history management view model"`.

### Task 4: Replace the AuditPage layout with history, compare and audit workspaces

**Files:**
- Modify: `native/src/EmsScout.Desktop/Pages/AuditPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/AuditPage.xaml.cs`
- Test: `native/tests/EmsScout.Tests/HistoryDataUiContractTests.cs`
- Modify: `native/tests/EmsScout.Tests/DashboardUiContractTests.cs`

**Interfaces:**
- XAML binds the default Pivot item to history data and exposes named automation properties for filters, batch list, detail pane, comparison summary, restore dialog trigger, and audit actions.
- Code-behind handles only Pivot selection, restore/delete confirmation dialogs, and navigation to data management; data operations remain in ViewModel commands.

- [ ] **Step 1: Write failing UI contract tests** asserting `历史数据` is the default workspace, `数据对比` and `质量审计` exist, no nested left/right audit ScrollViewer remains, and restore is behind a comparison action.
- [ ] **Step 2: Run UI contract tests** and verify the old layout fails the new contract.
- [ ] **Step 3: Build the history workspace** with one page-level ScrollViewer, filter row, metric strip, responsive batch list and detail section.
- [ ] **Step 4: Build the comparison workspace** with summary cards, building-difference list, explicit blocking reason and restore button.
- [ ] **Step 5: Move existing quality/reconciliation controls** into the audit workspace while preserving commands and bindings.
- [ ] **Step 6: Add `ContentDialog` confirmation** for restore and delete, with selected run details and irreversible-action copy.
- [ ] **Step 7: Run UI contract tests and Release build**; correct any binding/compiler errors.
- [ ] **Step 8: Commit** with `git add native/src/EmsScout.Desktop/Pages/AuditPage.xaml native/src/EmsScout.Desktop/Pages/AuditPage.xaml.cs native/tests/EmsScout.Tests/HistoryDataUiContractTests.cs native/tests/EmsScout.Tests/DashboardUiContractTests.cs && git commit -m "feat: make history data the audit workspace default"`.

### Task 5: Add integration coverage for migration, comparison, recovery and layout

**Files:**
- Modify: `native/tests/EmsScout.Tests/CollectionRunRepositoryTests.cs`
- Modify: `native/tests/EmsScout.Tests/HistoryDataViewModelTests.cs`
- Modify: `native/tests/EmsScout.Tests/HistoryDataUiContractTests.cs`
- Modify: `native/README.md`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Add regression tests** for legacy DB migration, partial comparison, blocked restore, successful restore backup creation, deleted batch refresh, and audit refresh failure.
- [ ] **Step 2: Run the full .NET test suite** and fix only regressions caused by this change.
- [ ] **Step 3: Run `npm run validate`, `npm run self-test`, and `git diff --check`**.
- [ ] **Step 4: Build the Release MSIX path** with `dotnet build native/EmsScout.Native.slnx -c Release --no-restore /p:UseSharedCompilation=false`.
- [ ] **Step 5: Update user-facing documentation** with the new history-first workflow, comparison-before-restore rule, and legacy migration behavior.
- [ ] **Step 6: Commit** with `git add native/tests native/README.md CHANGELOG.md && git commit -m "test: verify history management workflow"`.

### Task 6: Package and manually verify the delivered application

**Files:**
- Create: `out/native-packages/<next-version>/` generated by the existing packaging script; do not add generated package output to git.

- [ ] **Step 1: Build a version newer than the installed package** using `scripts/native-package.ps1` and the existing signing certificate.
- [ ] **Step 2: Install/update through `scripts/native-update.ps1`** and verify the desktop shortcut points to the new package identity.
- [ ] **Step 3: Launch with `npm run native:run`** and inspect the default history workspace, narrow window, filter selection, batch detail, comparison page, audit page, and restore confirmation.
- [ ] **Step 4: Verify the running package version and process responsiveness** with PowerShell and confirm no crash on navigation.
- [ ] **Step 5: Run `git status --short --branch`** and ensure only intentional source/docs changes remain before reporting completion.
