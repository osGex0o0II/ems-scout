# Audit Center Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the mixed audit workspace with historical batch management, a unified collection issue center, full issue details, safe batch artifact deletion, and a separate local-log clear action.

**Architecture:** Keep the existing quality and realtime report generators because collection tasks still depend on them, but add a unified Application-level issue service that reads complete report details for one batch. Keep batch deletion in the SQLite repository/artifact cleaner with identity checks, and add a path-confined log cleanup service for Settings. Rebuild the Audit page/ViewModel around three views and remove comparison, restore, and standalone audit commands from that UI.

**Tech Stack:** C#/.NET, WinUI 3 XAML, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, Node.js report generators, xUnit tests.

**Spec:** `docs/superpowers/specs/2026-09-21-audit-center-redesign.md`

## Global Constraints

- Do not upload or commit local field data, SQLite files, JSON, NDJSON, reports, or logs.
- Batch deletion must remove only artifacts whose batch identity is verified; protected shared/latest artifacts stay untouched.
- Settings log clearing deletes only log files inside the configured data directory and never SQLite, JSON, NDJSON, reports, configuration, or exports.
- Collection modes displayed in the UI are exactly `稳定模式` and `快速模式`.
- Issue detail lists contain all records; no UI or generator may limit records to the first 50 samples.
- Preserve unrelated existing area-group changes in the worktree.
- Every test that writes data uses a temporary directory and temporary SQLite database.

---

### Task 1: Make report generators retain complete issue records

**Files:**
- Modify: `scripts/quality-report.js`
- Modify: `scripts/audit-realtime-data.js`
- Test: `scripts/self-test.js` or a new isolated Node test under `scripts/`

**Interfaces:**
- Produces report JSON detail arrays consumed by the C# issue adapter.
- Keeps existing summary fields and existing collection-task report filenames compatible.

- [ ] **Step 1: Add a failing fixture assertion for complete quality details.**

Create a temporary report fixture with more than 50 records for one quality code and assert that the generated report contains every record in a dedicated full-detail collection, while the summary count equals the full collection length.

- [ ] **Step 2: Run the focused Node test and verify it fails.**

Run:

```powershell
node scripts/self-test.js
```

Expected: the new detail assertion fails because the current report exposes only `samples.*` arrays capped with `slice(0, 50)`.

- [ ] **Step 3: Emit complete normalized detail rows.**

Keep `samples` only for legacy text-report compatibility, but add a complete `details` object to `quality_report*.json`. Each row must preserve the existing location fields (`building`, `floor`, `sub_area`, `page_name`, `name`) and add `issue_code`, `severity`, `message`, `observed_value`, `evidence`, `collector_decision`, and `attribution` where available. Build details from the full arrays before any display-only slicing.

For realtime output, add complete `details.collection_errors` and `details.device_anomalies` arrays. Preserve the existing category/building summaries and put every row/event in the details arrays.

- [ ] **Step 4: Re-run the focused Node test.**

Run:

```powershell
node scripts/self-test.js
```

Expected: PASS, including a count greater than 50 and preservation of the existing report summary contract.

- [ ] **Step 5: Commit the generator change.**

```powershell
git add scripts/quality-report.js scripts/audit-realtime-data.js scripts/self-test.js
git commit -m "feat: retain complete collection issue details"
```

### Task 2: Add a unified Application issue model and JSON adapter

**Files:**
- Create: `native/src/EmsScout.Application/Quality/CollectionIssues.cs`
- Create: `native/src/EmsScout.Infrastructure/Quality/JsonCollectionIssueService.cs`
- Modify: `native/src/EmsScout.Desktop/App.xaml.cs`
- Test: `native/tests/EmsScout.Tests/CollectionIssueServiceTests.cs`

**Interfaces:**
- Produces `ICollectionIssueService.LoadForRunAsync(long, CancellationToken)`.
- `CollectionIssueRecord` fields are `BatchId`, `BatchUid`, `IssueType`, `Severity`, `Building`, `Floor`, `Zone`, `PageName`, `DeviceName`, `DeviceId`, `CollectedAt`, `ObservedValue`, `Evidence`, `CollectorDecision`, `Reason`, `Attribution`, `ResolutionState`, `SourceArtifact`, and `SourcePath`.
- `CollectionIssueReport` contains `RunId`, `Records`, and `Categories`.

Use this exact public shape:

```csharp
public interface ICollectionIssueService
{
    Task<CollectionIssueReport> LoadForRunAsync(
        long runId,
        CancellationToken cancellationToken = default);
}

public sealed record CollectionIssueReport(
    long RunId,
    IReadOnlyList<CollectionIssueRecord> Records,
    IReadOnlyList<CollectionIssueCategory> Categories,
    IReadOnlyList<string> Warnings);
```

- [ ] **Step 1: Write failing adapter tests.**

Cover these fixtures:

1. one quality report plus one realtime report merge into separate records;
2. more than 50 detail rows are all returned;
3. category counts equal the complete record set;
4. duplicate records from the same source are deduplicated by batch, issue type, location, device, and evidence;
5. the same device with different issue types remains as separate records;
6. missing or malformed reports produce a warning record/result instead of throwing;
7. missing location/evidence fields become empty values for the UI to render as `-`.

- [ ] **Step 2: Run the focused .NET tests and verify they fail.**

Run:

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter FullyQualifiedName~CollectionIssueServiceTests
```

Expected: compile failure because the new service and model do not exist.

- [ ] **Step 3: Implement the immutable model and adapter.**

The adapter resolves `quality_report_run{runId}.json` and the run-bound realtime quality report, validates `run_id`, `batch_uid`, and `run_key` against the selected batch when available, maps quality `details` rows and realtime `details` rows into `CollectionIssueRecord`, and sorts by severity, building, floor, page, and device. It must return warnings for missing/stale/malformed artifacts without failing the page load.

- [ ] **Step 4: Register the service and rerun tests.**

Register `ICollectionIssueService` in `App.xaml.cs` using the configured output directory and database path resolvers. Run the focused test command again and expect PASS.

- [ ] **Step 5: Commit the issue model and adapter.**

```powershell
git add native/src/EmsScout.Application/Quality/CollectionIssues.cs native/src/EmsScout.Infrastructure/Quality/JsonCollectionIssueService.cs native/src/EmsScout.Desktop/App.xaml.cs native/tests/EmsScout.Tests/CollectionIssueServiceTests.cs
git commit -m "feat: unify collection issue records"
```

### Task 3: Complete safe batch deletion and add local-log cleanup

**Files:**
- Modify: `native/src/EmsScout.Application/Collection/CollectionRuns.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteCollectionRunRepository.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/CollectionRunArtifactCleaner.cs`
- Create: `native/src/EmsScout.Application/Settings/LocalLogCleanupService.cs`
- Test: `native/tests/EmsScout.Tests/CollectionRunRepositoryTests.cs`
- Test: `native/tests/EmsScout.Tests/LocalLogCleanupServiceTests.cs`

**Interfaces:**
- `RunDeleteImpact` exposes the dynamic confirmation values and artifact candidates.
- `CollectionRunDeleteResult` exposes SQLite counts plus per-artifact pending reasons.
- `LocalLogCleanupService.Preview()` returns file count and total bytes; `Clear()` returns deleted, skipped, failed paths and byte totals.

- [ ] **Step 1: Add deletion and log-cleanup failing tests.**

Use a temporary root containing two batches, shared latest reports, a database file, JSON, NDJSON, quality report, realtime report, and unrelated logs. Assert that deleting batch A removes only A's verified DB rows and artifacts, retains batch B and shared/latest files, and returns pending reasons for unverified files. Assert that log cleanup removes all `.log`/`.ndjson.log` log files under the root but leaves SQLite, JSON, NDJSON data, reports, settings, and exports untouched.

- [ ] **Step 2: Run focused tests and verify failure.**

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter "FullyQualifiedName~CollectionRunRepositoryTests|FullyQualifiedName~LocalLogCleanupServiceTests"
```

Expected: the new assertions fail for missing report coverage or missing log cleanup service.

- [ ] **Step 3: Extend artifact discovery and deletion.**

Add batch-bound JSON, NDJSON, quality, and realtime report patterns to `CollectionRunArtifactCleaner`, retaining protection for `ac.db`, `enum_full_v5.json`, generic/latest reports, and any path whose identity cannot be proven. Keep logs outside batch deletion. Make partial cleanup results explicit.

- [ ] **Step 4: Add path-confined local-log cleanup.**

Resolve the configured data directory with `AppDataPathService`, reject paths outside that directory, enumerate only log filename extensions/patterns, and return a structured result for missing, skipped, and failed files. Do not use a recursive delete of the data directory.

- [ ] **Step 5: Rerun focused tests and commit.**

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter "FullyQualifiedName~CollectionRunRepositoryTests|FullyQualifiedName~LocalLogCleanupServiceTests"
git add native/src/EmsScout.Application/Collection/CollectionRuns.cs native/src/EmsScout.Infrastructure/Sqlite/SqliteCollectionRunRepository.cs native/src/EmsScout.Infrastructure/Sqlite/CollectionRunArtifactCleaner.cs native/src/EmsScout.Application/Settings/LocalLogCleanupService.cs native/tests/EmsScout.Tests/CollectionRunRepositoryTests.cs native/tests/EmsScout.Tests/LocalLogCleanupServiceTests.cs
git commit -m "feat: safely delete batch artifacts and clear logs"
```

Expected: PASS and no production `out/` files changed.

### Task 4: Rebuild AuditViewModel around the three audit workflows

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/AuditViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionRunRow.cs`
- Create: `native/src/EmsScout.Desktop/ViewModels/CollectionIssueCategoryRow.cs`
- Create: `native/src/EmsScout.Desktop/ViewModels/CollectionIssueRow.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/AuditPage.xaml.cs`
- Test: `native/tests/EmsScout.Tests/HistoryDataUiContractTests.cs`
- Create: `native/tests/EmsScout.Tests/AuditIssueViewModelTests.cs`

**Interfaces:**
- The VM depends on `ICollectionRunRepository`, `ICollectionIssueService`, `INavigationService`, and `AppDataPathService` only as needed; remove audit/reconciliation/runner dependencies from the Audit constructor.
- Expose collections `Runs`, `IssueCategories`, and `IssueRecords`.
- Expose `SelectedRun`, `SelectedIssueCategory`, `SelectedIssueBuilding`, `IssueSearchText`, and `CurrentAuditSection`.
- Commands are `RefreshCommand`, `DeleteRunCommand`, `ShowIssuesCommand`, `ShowIssueDetailsCommand`, `ClearIssueFilterCommand`.

- [ ] **Step 1: Add failing ViewModel tests.**

Assert:

1. duration is derived from `StartedAt`/`CompletedAt`;
2. `stable-full` renders `稳定模式`, `fast-batch` renders `快速模式`, unknown renders `-`;
3. selecting a batch loads all merged issue records;
4. selecting a category filters details to that issue type;
5. clearing the filter restores all records;
6. deleting the selected batch refreshes the list and selects the newest remaining batch;
7. empty batch/report states are explicit.

- [ ] **Step 2: Run focused tests and verify failure.**

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter FullyQualifiedName~AuditIssueViewModelTests
```

Expected: compile or assertion failure against the current VM contract.

- [ ] **Step 3: Remove obsolete VM state and commands.**

Delete comparison, restore, reconciliation, standalone quality/realtime audit execution, anomaly controls, and their generated properties/collections. Preserve only history refresh/delete and issue loading/filtering. Keep repository delete confirmation data available to the page.

- [ ] **Step 4: Implement issue grouping and filtering.**

Load `ICollectionIssueService` after batch selection, group the complete record list into category rows, and filter details in memory by category, severity, building, and search text. Never use `Take`, `Skip`, or sample arrays in the detail collection.

- [ ] **Step 5: Rerun focused tests and commit the VM change.**

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter FullyQualifiedName~AuditIssueViewModelTests
git add native/src/EmsScout.Desktop/ViewModels/AuditViewModel.cs native/src/EmsScout.Desktop/ViewModels/CollectionRunRow.cs native/src/EmsScout.Desktop/ViewModels/CollectionIssueCategoryRow.cs native/src/EmsScout.Desktop/ViewModels/CollectionIssueRow.cs native/src/EmsScout.Desktop/Pages/AuditPage.xaml.cs native/tests/EmsScout.Tests/HistoryDataUiContractTests.cs native/tests/EmsScout.Tests/AuditIssueViewModelTests.cs
git commit -m "refactor: center audit workflow on collection issues"
```

### Task 5: Replace the Audit page UI

**Files:**
- Replace content: `native/src/EmsScout.Desktop/Pages/AuditPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/AuditPage.xaml.cs`
- Test: `native/tests/EmsScout.Tests/HistoryDataUiContractTests.cs`
- Test: `native/tests/EmsScout.Tests/DataManagementUiContractTests.cs`

**Interfaces:**
- Uses only the Task 4 VM properties and commands.
- Keeps the existing navigation route `audit`.

- [ ] **Step 1: Update UI contract tests to require the new surface.**

Require visible sections/headers for `历史批次`, `采集问题`, and `问题详情`; require the batch row fields `完成时间`, `用时`, `范围`, `卡片数量`, `采集模式`, `版本`, `删除`; require the issue category and detail bindings. Assert that `数据对比`, `恢复当前数据`, `基础审计`, `实时审计`, `运行质量审计`, `运行实时审计`, and `刷新对比` are absent.

- [ ] **Step 2: Run the contract tests and verify failure.**

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter "FullyQualifiedName~HistoryDataUiContractTests|FullyQualifiedName~DataManagementUiContractTests"
```

Expected: the new requirements fail against the old Pivot layout.

- [ ] **Step 3: Implement the three-section XAML.**

Use a compact Pivot or section navigation with the history timeline as the default. The history row includes an icon delete button. Issue category cards show count/severity and navigate to details. Details use a readable multi-column list with `-` for missing values and no artificial row cap. Keep batch selector in the issue views.

- [ ] **Step 4: Implement dynamic delete confirmation.**

In `AuditPage.xaml.cs`, call `GetDeleteImpactAsync` before showing the dialog. The dialog must include completion time, scope, card count, mapped collection mode, version, rule/file deletion scope, and the irreversible warning. Only after confirmation call `DeleteRunAsync`; show partial artifact cleanup results in the status text.

- [ ] **Step 5: Rerun UI contract tests and commit.**

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter "FullyQualifiedName~HistoryDataUiContractTests|FullyQualifiedName~DataManagementUiContractTests"
git add native/src/EmsScout.Desktop/Pages/AuditPage.xaml native/src/EmsScout.Desktop/Pages/AuditPage.xaml.cs native/tests/EmsScout.Tests/HistoryDataUiContractTests.cs native/tests/EmsScout.Tests/DataManagementUiContractTests.cs
git commit -m "feat: rebuild audit center interface"
```

### Task 6: Add Settings local-log clearing UI

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/SettingsViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/SettingsPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/SettingsPage.xaml.cs`
- Modify: `native/src/EmsScout.Desktop/App.xaml.cs`
- Test: `native/tests/EmsScout.Tests/SettingsUiContractTests.cs`

**Interfaces:**
- Settings VM exposes `PreviewLocalLogCleanupCommand` and `ClearLocalLogsCommand` through the page confirmation flow.
- The page receives `LocalLogCleanupService` from DI and shows count/size before confirmation and deleted/skipped/failed counts afterward.

- [ ] **Step 1: Add failing Settings contract tests.**

Require a visible `清空本地日志` control, confirmation wording that says only local logs are removed, and result wording for deleted/skipped/failed files. Assert that no control text suggests deleting batches or reports.

- [ ] **Step 2: Run the focused UI contract test and verify failure.**

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter FullyQualifiedName~SettingsUiContractTests
```

Expected: FAIL because Settings currently has no local-log cleanup control.

- [ ] **Step 3: Implement the confirmation and cleanup flow.**

Add the control to the advanced/data-directory section. Preview the configured data directory, show file count and total size, require explicit confirmation, invoke the service, and update `StatusText`. Keep the operation disabled while Settings is saving or while cleanup is running.

- [ ] **Step 4: Rerun tests and commit.**

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter FullyQualifiedName~SettingsUiContractTests
git add native/src/EmsScout.Desktop/ViewModels/SettingsViewModel.cs native/src/EmsScout.Desktop/Pages/SettingsPage.xaml native/src/EmsScout.Desktop/Pages/SettingsPage.xaml.cs native/src/EmsScout.Desktop/App.xaml.cs native/tests/EmsScout.Tests/SettingsUiContractTests.cs
git commit -m "feat: add local log cleanup in settings"
```

### Task 7: Full regression and isolated end-to-end verification

**Files:**
- Modify only tests or test fixtures if a verified contract gap is found.
- Do not modify or stage `out/` runtime data.

- [ ] **Step 1: Run focused feature tests.**

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter "FullyQualifiedName~CollectionIssueServiceTests|FullyQualifiedName~CollectionRunRepositoryTests|FullyQualifiedName~LocalLogCleanupServiceTests|FullyQualifiedName~AuditIssueViewModelTests|FullyQualifiedName~HistoryDataUiContractTests|FullyQualifiedName~SettingsUiContractTests"
```

Expected: PASS except for the known pre-existing realtime source-binding failures, which must be listed separately if they remain.

- [ ] **Step 2: Run the complete project test suite and Node self-test.**

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj
node scripts/self-test.js
git diff --check
```

Expected: no new failures, no whitespace errors, and no runtime data modifications.

- [ ] **Step 3: Build the Release application.**

Run:

```powershell
dotnet build native/EmsScout.Native.slnx -c Release --no-restore /p:UseSharedCompilation=false
```

Expected: zero warnings/errors attributable to this change. Confirm the DI container resolves Audit and Settings pages during application startup.

- [ ] **Step 4: Perform isolated UI acceptance.**

Launch the newest build with a temporary data directory containing two synthetic batches and reports. Verify: history fields and mode labels; delete confirmation and artifact removal; shared/latest protection; issue cards; all-record details; category entry filtering; clear-filter behavior; empty states; Settings log preview/confirmation/result. Do not open or modify production `out/ac.db` or field artifacts.

- [ ] **Step 5: Review the final diff and status.**

```powershell
git status --short
git diff --stat HEAD~7..HEAD
git diff --check HEAD~7..HEAD
```

Expected: only source, tests, design/plan documents, and intended commits are present; no logs, SQLite, JSON, NDJSON, reports, MSIX packages, or screenshots are staged.
