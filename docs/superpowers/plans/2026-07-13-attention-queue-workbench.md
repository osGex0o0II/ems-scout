# Attention Queue And Workbench Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the existing dashboard risk aggregation into a persistent, actionable attention queue with stable issue identities, auditable status changes, safe auto-resolution, and a compact workbench table.

**Architecture:** Keep the existing quality, realtime, reconciliation, collection-run, and watch analyzers as evidence sources. Add an application-layer queue contract and a SQLite v5 repository that stores only issue projection/state/history; original collection and audit evidence remains immutable. `DashboardOverviewService` synchronizes the queue only for current data, while historical contexts render read-only projections without writes.

**Tech Stack:** .NET 10, WinUI 3, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, xUnit source/runtime contracts.

## Global Constraints

- Never open `out/ac.db`, `data/ac.db`, `data/1号楼/ac.db`, or `data/2号楼/ac.db` through SQLite during implementation or verification.
- All migration and repository tests use databases created under a unique temporary directory.
- Runtime validation uses only `scripts/run-native.ps1 -NoBuild -UiValidation`.
- The queue may store derived state and evidence references, but must never modify cards, pages, collection snapshots, or audit reports.
- Historical data contexts remain read-only: no synchronize, acknowledge, ignore, or resolve write is allowed.
- Ignoring an issue requires a non-empty reason. Automatic resolution is allowed only for sources successfully observed in the current refresh.
- Do not stage, commit, clean, or revert the shared dirty worktree.

---

### Task 1: Define Queue Contracts And Transition Rules

**Files:**
- Create: `native/src/EmsScout.Application/Attention/AttentionIssues.cs`
- Create: `native/tests/EmsScout.Tests/AttentionIssuePolicyTests.cs`
- Test: `native/tests/EmsScout.Tests/AttentionIssuePolicyTests.cs`

**Interfaces:**
- Consumes: current dashboard source names and `OverviewMetricKind` severity.
- Produces: `AttentionIssueCandidate`, `AttentionIssueRecord`, `AttentionQueueSnapshot`, `IAttentionIssueRepository`, and `AttentionIssuePolicy.ValidateTransition`.

- [ ] **Step 1: Write failing policy tests**

Cover the four statuses `unprocessed`, `acknowledged`, `ignored`, and `resolved`; require a trimmed reason for `ignored`; allow reopening an automatically resolved issue as `unprocessed`; reject unknown statuses.

- [ ] **Step 2: Run the focused test and verify RED**

```powershell
dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false --filter FullyQualifiedName~AttentionIssuePolicyTests
```

Expected: compilation fails because the attention contracts do not exist.

- [ ] **Step 3: Implement the minimal application contracts**

Use an immutable navigation payload:

```csharp
public sealed record AttentionNavigationTarget(
    string Destination,
    string CommunicationState = "",
    string RealtimeMatch = "",
    string RealtimePoints = "",
    string QuickFilter = "",
    string WatchState = "");
```

`AttentionQueueSnapshot` contains candidates plus `ObservedSources`; the repository synchronizes only those sources. Status validation stays in the application project so SQLite and UI share one rule.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the Step 2 command. Expected: all policy tests pass.

### Task 2: Add SQLite V5 Persistence And History

**Files:**
- Create: `native/src/EmsScout.Infrastructure/Migrations/Sql/V005__attention_queue.sql`
- Create: `native/src/EmsScout.Infrastructure/Sqlite/SqliteAttentionIssueRepository.cs`
- Create: `native/tests/EmsScout.Tests/AttentionIssueRepositoryTests.cs`
- Modify: `native/src/EmsScout.Infrastructure/Migrations/BaselineSchema.cs`
- Modify: `native/src/EmsScout.Infrastructure/Migrations/BaselineSql.cs`
- Modify: `native/src/EmsScout.Infrastructure/Migrations/SqliteSchemaMigrator.cs`
- Modify: `native/tests/EmsScout.Tests/SqliteSchemaMigratorTests.cs`
- Modify: migration-backed test fixtures that assert `PRAGMA user_version = 4`

**Interfaces:**
- Consumes: `IAttentionIssueRepository` and `AttentionQueueSnapshot` from Task 1.
- Produces: schema version 5 with `attention_issues`, `attention_issue_history`, source/status indexes, transactional synchronization, and status updates.

- [ ] **Step 1: Write failing repository tests**

Test that synchronization inserts stable IDs, preserves acknowledged/ignored state while evidence remains, auto-resolves missing issues only for observed sources, reopens reappearing resolved issues, records every status change, and rejects ignored updates without a reason.

- [ ] **Step 2: Run repository and migration tests and verify RED**

```powershell
dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false --filter "FullyQualifiedName~AttentionIssueRepositoryTests|FullyQualifiedName~SqliteSchemaMigratorTests"
```

Expected: repository type and v5 schema assertions fail.

- [ ] **Step 3: Add the additive v5 migration**

Create `attention_issues` with `issue_id` primary key, source/type/severity, nullable `run_id`, title/detail/scope/count, navigation JSON, status, ignore reason, first/last seen and resolved timestamps. Create `attention_issue_history` with before/after status and reason. Add foreign key and source/status/history indexes; register both tables and indexes in `BaselineSchema`.

- [ ] **Step 4: Implement transactional synchronization**

Upsert current candidates, retain manual state, append history for reopen/auto-resolve/manual transitions, and auto-resolve only rows whose `source_key` is in `ObservedSources`. Serialize `AttentionNavigationTarget` with `System.Text.Json` rather than manual string parsing.

- [ ] **Step 5: Run repository and migration tests and verify GREEN**

Run the Step 2 command. Expected: all focused tests pass and fresh databases report `user_version = 5`.

### Task 3: Synchronize Existing Evidence Into The Queue

**Files:**
- Modify: `native/src/EmsScout.Application/DashboardOverview.cs`
- Modify: `native/src/EmsScout.Application/DashboardRiskBuilder.cs`
- Modify: `native/src/EmsScout.Application/DashboardOverviewService.cs`
- Modify: `native/tests/EmsScout.Tests/DashboardRiskBuilderTests.cs`
- Create: `native/tests/EmsScout.Tests/DashboardAttentionQueueTests.cs`

**Interfaces:**
- Consumes: existing dashboard evidence sources and `IAttentionIssueRepository`.
- Produces: stable IDs such as `inventory:communication:offline`, `quality:summary:issues`, `realtime:devices:invalid`, `reconciliation:summary:difference`, `runs:anomaly`, and `watch:state:abnormal`.

- [ ] **Step 1: Write failing aggregation tests**

Assert stable IDs, source keys, severity, count, batch ID, and navigation payload for every current evidence category. Assert that failed source reads are not included in `ObservedSources` and that historical `runId` loads never call repository synchronization.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false --filter "FullyQualifiedName~DashboardRiskBuilderTests|FullyQualifiedName~DashboardAttentionQueueTests"
```

Expected: stable identity and synchronization assertions fail.

- [ ] **Step 3: Map risks to queue candidates**

Keep one candidate per actionable category. Generic success rows remain transient and are never persisted. Replace raw exception messages in dashboard rows with safe source-specific recovery text. Current-data loads call `SynchronizeAsync`; historical loads use read-only projected rows with no queue mutation.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Step 2 command. Expected: aggregation and no-write historical tests pass.

### Task 4: Build The Actionable Workbench Table

**Files:**
- Modify: `native/src/EmsScout.Desktop/App.xaml.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/HomeViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/HomePage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/HomePage.xaml.cs`
- Create: `native/tests/EmsScout.Tests/WorkbenchAttentionQueueUiContractTests.cs`

**Interfaces:**
- Consumes: synchronized attention records and `IAttentionIssueRepository.SetStatusAsync`.
- Produces: compact workbench columns for severity, source, issue, scope/count, status, updated time and actions; commands for locate, acknowledge, ignore with reason, and reopen.

- [ ] **Step 1: Write the failing UI contract test**

Require visible status/update columns, buttons with automation names for locate/acknowledge/ignore/reopen, an ignore-reason dialog path, and disabled status writes when `DataContext.IsReadOnly` is true.

- [ ] **Step 2: Run the UI contract and verify RED**

```powershell
dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false --filter FullyQualifiedName~WorkbenchAttentionQueueUiContractTests
```

Expected: the queue status/action controls are absent.

- [ ] **Step 3: Implement view-model commands and row projection**

Preserve the last successful table during refresh errors. Route communication filters to device data and audit-only evidence to audit. After a status update, refresh the queue projection without clearing unrelated rows. Expose `CanChangeAttentionState = !DataContext.IsReadOnly && !IsLoading`.

- [ ] **Step 4: Implement the compact table and reason dialog**

Use fixed/minimum column widths and a horizontal scroll strategy for 1280px. The ignore action opens a `ContentDialog` with a required `TextBox`; no status change occurs for an empty or cancelled reason. Use WinUI icons, tooltips, and `AutomationProperties.Name` on icon actions.

- [ ] **Step 5: Run the focused UI contract and full native suite**

```powershell
npm run native:test
npm run native:build
```

Expected: all non-production native tests pass; build exits 0 with no XAML compiler error.

- [ ] **Step 6: Inspect the isolated runtime**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-native.ps1 -NoBuild -UiValidation
```

Verify the queue fits at 1280x720 and 1366x768, status actions do not overlap, historical context visibly disables writes, all navigation targets open, and no `EmsScout.Desktop` process remains after closing.

- [ ] **Step 7: Update project evidence**

Update `.context-summary.md`, `CHANGELOG.md`, and a dated `docs/validation/` note with exact test/build/runtime evidence. Do not describe isolated synthetic validation as real EMS or full MSIX lifecycle validation.
