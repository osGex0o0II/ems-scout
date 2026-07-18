# EMS Scout Area Group Reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace device-watch and the floor candidate catalog with editable area groups backed by persistent matching rules, confirmed members, durable exceptions, and audited add/remove proposals.

**Architecture:** Keep `monitor_groups` as the group aggregate root and add an additive V6 model for rules, materialized device members, per-group exceptions, and change requests. Reconcile inside the collection import transaction for only imported buildings, then expose domain-specific actions on AuditPage and read-only pending summaries on AreasPage.

**Tech Stack:** .NET 10, C# 14, WinUI 3, Microsoft.Data.Sqlite, xUnit, SQLite additive migrations.

## Global Constraints

- Preserve all unrelated working-tree changes; patch overlapping files surgically.
- Do not modify production `data/ac.db` or `out/ac.db`; all verification uses temporary databases.
- Prefer `device_uid`; use the complete legacy location/source identity only when UID is absent.
- V6 is additive and must preserve legacy watch and schedule tables.
- No production behavior change is written before its failing test is captured.

---

### Task 1: V6 persistence and one-time editable presets

**Files:**
- Create: `native/src/EmsScout.Infrastructure/Migrations/Sql/V006__area_group_reconciliation.sql`
- Modify: `native/src/EmsScout.Infrastructure/Migrations/BaselineSql.cs`
- Modify: `native/src/EmsScout.Infrastructure/Migrations/BaselineSchema.cs`
- Modify: `native/src/EmsScout.Infrastructure/Migrations/SqliteSchemaMigrator.cs`
- Test: `native/tests/EmsScout.Tests/SqliteSchemaMigratorTests.cs`

**Interfaces:**
- Produces tables `area_group_rules`, `area_group_members`, `area_group_exceptions`, `area_group_change_requests`.
- Preserves `monitor_groups`, `monitor_group_items`, `device_watch_rules`, and schedule tables.

- [x] **Step 1: Write failing migration tests**

Add `FreshDatabaseAppliesAreaGroupReconciliationV6` and `ExistingPresetGroupsBecomeEditableAndDeletedPresetDoesNotReseed`. Assert `LatestVersion == 6`, required columns/indexes, `locked=0`, ordinary editable group semantics, one public and one non-public preset rule, and no second insertion after deleting a preset and reopening the repository.

- [x] **Step 2: Run the filter and capture RED**

Run:
```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false --filter "FullyQualifiedName~SqliteSchemaMigratorTests.FreshDatabaseAppliesAreaGroupReconciliationV6|FullyQualifiedName~SqliteSchemaMigratorTests.ExistingPresetGroupsBecomeEditableAndDeletedPresetDoesNotReseed"
```
Expected: assertions fail because V6/table/index/preset conversion is absent.

- [x] **Step 3: Implement the additive schema**

Use checks equivalent to:
```sql
rule_type TEXT NOT NULL CHECK (rule_type IN ('area_public','area_non_public','floor','name_exact','name_keyword','legacy_sub_area'));
member_origin TEXT NOT NULL CHECK (member_origin IN ('rule','manual','legacy'));
exception_type TEXT NOT NULL CHECK (exception_type IN ('blocked','retained'));
action TEXT NOT NULL CHECK (action IN ('add','remove'));
status TEXT NOT NULL CHECK (status IN ('pending','accepted','rejected','superseded'));
```
Add foreign keys to `monitor_groups`, optional rule/run references, device identity snapshot fields, partial unique indexes for pending requests, and one-time V6 `UPDATE/INSERT` statements that convert the two presets to unlocked custom groups and seed their classification rules.

- [x] **Step 4: Run migration tests GREEN**

Run the Step 2 command. Expected: both tests pass and legacy schema assertions remain green.

### Task 2: Rule/member/exception repository state machine

**Files:**
- Create: `native/src/EmsScout.Application/Groups/AreaGroupReconciliation.cs`
- Create: `native/src/EmsScout.Infrastructure/Sqlite/SqliteAreaGroupReconciliationRepository.cs`
- Modify: `native/src/EmsScout.Application/Groups/AreaGroups.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteAreaGroupRepository.cs`
- Test: `native/tests/EmsScout.Tests/AreaGroupReconciliationRepositoryTests.cs`

**Interfaces:**
```csharp
public interface IAreaGroupReconciliationRepository
{
    Task<AreaGroupManagementSnapshot> LoadAsync(long? groupId = null, CancellationToken cancellationToken = default);
    Task<AreaGroupRuleRecord> SaveRuleAsync(AreaGroupRuleEdit edit, CancellationToken cancellationToken = default);
    Task DeleteRuleAsync(long ruleId, CancellationToken cancellationToken = default);
    Task<AreaGroupMemberRecord> AddManualMemberAsync(AreaGroupManualMemberEdit edit, CancellationToken cancellationToken = default);
    Task DeleteManualMemberAsync(long memberId, CancellationToken cancellationToken = default);
    Task UpdateMemberNoteAsync(long memberId, string note, CancellationToken cancellationToken = default);
    Task UpdateExceptionNoteAsync(long exceptionId, string note, CancellationToken cancellationToken = default);
    Task DeleteExceptionAsync(long exceptionId, CancellationToken cancellationToken = default);
    Task DecideChangeAsync(long requestId, AreaGroupChangeDecision decision, string note, CancellationToken cancellationToken = default);
}
```

- [x] **Step 1: Write failing repository tests**

Cover floor and keyword rules, classification presets, no whole-building rule, pending proposals without membership mutation, confirm/reject transitions, exception notes/reversal, manual-member immunity, UID identity, legacy fallback, duplicate decision rejection, and cross-group isolation.

- [x] **Step 2: Capture RED**

Run:
```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false --filter "FullyQualifiedName~AreaGroupReconciliationRepositoryTests"
```
Expected: compile/assertion failure for missing contracts and behavior.

- [x] **Step 3: Implement records, validation, loading, and transactions**

Implement `AreaGroupRuleEdit`, `AreaGroupMemberRecord`, `AreaGroupExceptionRecord`, `AreaGroupChangeRequestRecord`, `AreaGroupChangeDecision`, and `AreaGroupManagementSnapshot`. Validate floor rules require building+floor, keywords are trimmed/non-empty, classification rules require no location fields, and pending decisions are group/request atomic.

- [x] **Step 4: Implement reconciliation SQL**

Resolve current devices from `cards/pages/sub_areas`, rank physical identity by UID, evaluate enabled rules, supersede stale pending requests, insert missing `add`, and insert `remove` only for `rule` members. Never mutate confirmed membership during reconciliation.

- [x] **Step 5: Capture GREEN**

Run Step 2. Expected: all state-machine tests pass.

### Task 3: Atomic collection integration

**Files:**
- Modify: `native/src/EmsScout.Infrastructure/Importing/CollectionSnapshotImporter.cs`
- Test: `native/tests/EmsScout.Tests/CollectionSnapshotImporterTests.cs`

- [x] **Step 1: Add failing atomicity/scope tests**

Add the four named C3 tests. Use the existing fault checkpoint to fail immediately after reconciliation and assert cards, run rows, and change requests all roll back.

- [x] **Step 2: Capture RED**

Run the exact C3 filter from the design spec. Expected: no candidates and missing rollback checkpoint.

- [x] **Step 3: Wire reconciliation into the transaction**

Call the reconciler after `SyncFloorCatalogAsync`, passing `runId` and `importedBuildings`. Add rules/members/exceptions to the protected user-data fingerprint; do not add change requests. Preserve the exact-replay early return.

- [x] **Step 4: Capture GREEN**

Run the C3 filter. Expected: all four tests pass.

### Task 4: Retire device-watch user functionality

**Files:**
- Modify: `native/src/EmsScout.Desktop/App.xaml.cs`
- Modify: `native/src/EmsScout.Desktop/MainWindow.xaml`
- Modify: `native/src/EmsScout.Desktop/ViewModels/GroupsViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/AreasPage.xaml`
- Modify: `native/src/EmsScout.Application/DashboardOverviewService.cs`
- Modify: `native/src/EmsScout.Application/DashboardRiskBuilder.cs`
- Modify: `native/src/EmsScout.Application/Devices/DeviceDataQuerySnapshot.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/DataViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/DataDeviceRow.cs`
- Test: relevant UI/dashboard/data contract tests.

- [x] **Step 1: Add failing retirement assertions**

Assert navigation reads `区域组`, Areas/Data/Home contain no user-visible watch copy or controls, DI no longer resolves/uses `IDeviceWatchRepository`, and migrations still retain `device_watch_rules`.

- [x] **Step 2: Capture RED**

Run the C4 filter. Expected: current watch strings/controls cause failures.

- [x] **Step 3: Remove runtime/UI use**

Delete the watch card and commands from Areas, stop dashboard synchronization and watch-derived risk/facet calculation, remove watch filters/details from Data, and remove DI registration. Keep legacy application/infrastructure files and table for compatibility unless compiler reference analysis proves they can be removed without deleting unrelated work.

- [x] **Step 4: Capture GREEN**

Run the C4 filter. Expected: retirement assertions pass.

### Task 5: WinUI design-system contract

**Files:**
- Create: `DESIGN.md`
- Read: `native/src/EmsScout.Desktop/App.xaml`

- [x] **Step 1: Extract existing tokens and primitives**

Document the operational WinUI direction, Fluent theme resources, 4px spacing rhythm, type hierarchy, WorkbenchCard/MetricCard/toolbar primitives, list-detail shell, focus/disabled/error states, responsive width states, CJK rules, and accepted debt. No new token is needed for this feature.

- [x] **Step 2: Verify the contract**

Confirm every new XAML element uses existing theme resources/styles and no raw color or emoji icon is introduced.

### Task 6: Area-group management surface

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/GroupsViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/AreasPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/AreasPage.xaml.cs`
- Create focused row view-model files when a row has independent behavior.
- Test: `AreaManagementVisualContractTests.cs`, `GroupSettingsUiContractTests.cs`.

- [x] **Step 1: Add failing UI contracts**

Assert `现有设备目录`, floor/keyword rule editors, member/exception lists, notes, editable presets, pending counts and `打开审计`; assert `楼层候选目录`, sub-area creation, and watch card are absent.

- [x] **Step 2: Capture RED**

Run the two UI contract classes. Expected: new labels/bindings absent.

- [x] **Step 3: Implement ViewModel commands and state**

Load group snapshot and current devices, debounce directory search, save/delete rules, add/delete manual device members, edit/delete exceptions, expose pending counts, and route to AuditPage with selected group.

- [x] **Step 4: Implement XAML**

Keep the existing master-detail shell. Use cards for group summary, pending reminder, rules, directory, members, and exceptions. Use `AutomationProperties.Name` for every action used by QA.

- [x] **Step 5: Capture GREEN**

Run the two UI contract classes. Expected: pass.

### Task 7: Audit confirmation surface

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/AuditViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/AuditPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/AuditPage.xaml.cs`
- Modify: `native/src/EmsScout.Desktop/Services/INavigationService.cs`
- Test: `AreaManagementVisualContractTests.cs` plus repository decision tests.

- [x] **Step 1: Add failing audit contracts**

Assert group/action filters, `分组成员变更`, `确认加入`, `拒绝并屏蔽`, `确认移除`, `拒绝并保留`, note binding, refresh behavior and group deep link.

- [x] **Step 2: Capture RED**

Run audit/area contract filters. Expected: missing section/actions.

- [x] **Step 3: Implement actions and navigation**

Load pending requests, apply selected group/action filters, call `DecideChangeAsync`, keep the row on failure, refresh on success, and accept a group id navigation parameter.

- [x] **Step 4: Capture GREEN**

Run audit/area contract filters. Expected: pass.

### Task 8: Verification, real desktop QA, and review

**Files:**
- Evidence only under `%TEMP%/ems-scout-ulw`; no production DB changes.

- [x] **Step 1: Run targeted tests and build**

Run C1-C4 filters and Debug build; save complete outputs.

- [x] **Step 2: Create temporary QA database**

Import a sanitized CollectionSnapshot fixture with `EmsScout.DataTool --apply`, then use the new repository through a test/fixture helper to create one pending add and one pending remove. Point temporary settings at that data directory.

- [x] **Step 3: Drive the real desktop app**

Launch the built executable with Computer Use, navigate to Areas and Audit, exercise filters and one non-destructive note edit, and capture fresh screenshots/action log. Do not touch production data.

- [x] **Step 4: Run dual visual review**

Dispatch design-system/functional and CJK/layout reviewers with every changed page/state capture. Fix all product blockers and recapture until both pass.

- [x] **Step 5: Run full verification**

Run full native tests, `npm test`, native build and changed-file diagnostics. Then dispatch the HEAVY code reviewer with goal, diff, evidence and ultrawork notepad.

- [ ] **Step 6: Cleanup**

Close the QA app, verify its process is gone, delete only the temporary QA database/settings, and record the receipt. Do not commit unless the user separately authorizes it.
