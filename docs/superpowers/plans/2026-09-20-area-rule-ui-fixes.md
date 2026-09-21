# Area Rule UI Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复区域规则删除后的序号断号、匹配列裁切与未居中，以及总览区域组错误显示优先级和待确认状态的问题。

**Architecture:** 以 1 基序号作为唯一规则序号契约。SQLite 在迁移、删除和新增时保证每个区域组的规则始终按当前顺序连续编号，ViewModel 在内存列表变化后立即重编号；页面只调整列布局和绑定，不改变规则匹配语义。总览保留后台 `Priority`、`StateText` 和统计字段，仅将区域组卡片原位置改为显示 `Description` 备注。

**Tech Stack:** C# / .NET 10, WinUI 3 XAML, SQLite, xUnit, `dotnet test`, MSIX 本地构建。

**Spec:** 本次用户需求：规则删除后序号自动回位；包含/不含完整显示；楼栋、座号、楼层等列居中；总览区域组位置只显示与设置页联动的备注，不显示“重点”和“待确认”。

## Global Constraints

- 现场日志、质量报告、批次 JSON、NDJSON、SQLite 和其他现场数据不得提交或上传远端。
- 规则序号对用户和数据库统一为 `1..N`，同一区域组内不得出现重复、跳号或 0 基序号。
- 规则顺序按当前 `rule_order`、再按 `id` 稳定排序；历史序号不整体加一，必须重排为连续序列。
- 不删除后台 `priority`、实时状态或统计字段；本次只改变区域页面和总览卡片展示。
- 生产数据不通过测试夹具修改；数据库迁移只在应用启动时对规则表执行事务性整理。

---

### Task 1: 建立 1 基规则序号契约

**Files:**
- Modify: `native/src/EmsScout.Application/Groups/AreaGroupRules.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteAreaGroupRepository.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteSchemaMigrator.cs`
- Test: `native/tests/EmsScout.Tests/AreaGroupMigrationTests.cs`
- Test: `native/tests/EmsScout.Tests/AreaGroupRuleIntegrationTests.cs`

**Interfaces:**
- `IAreaGroupRepository.SaveRuleAsync` continues accepting `AreaGroupRuleEdit`, but persisted `RuleOrder` must be 1-based.
- Add one internal repository operation or private helper that renumbers one group atomically by `(rule_order, id)`.

- [ ] **Step 1: Write failing persistence tests.**

  Update existing assertions that currently expect `0,1` to expect `1,2`. Add cases for:

  ```csharp
  [Fact]
  public async Task DeleteRuleRenumbersRemainingRulesFromOne()
  {
      // Insert four rules, delete the second, reload, and assert rule_order is [1, 2, 3].
  }

  [Fact]
  public async Task ExistingGappedOrZeroBasedOrdersAreNormalizedOnMigration()
  {
      // Seed orders [0, 2, 4], run migration, and assert [1, 2, 3] in old-order/id order.
  }
  ```

  The tests must assert both order values and rule IDs so a sort-only change cannot pass.

- [ ] **Step 2: Run the focused tests and verify the expected failure.**

  Run:

  ```powershell
  dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter "FullyQualifiedName~AreaGroupMigrationTests|FullyQualifiedName~AreaGroupRuleIntegrationTests"
  ```

  Expected result: the old implementation fails because the UI/database contract is still 0-based or retains gaps.

- [ ] **Step 3: Implement canonical numbering.**

  Implement a transaction-safe renumber helper in `SqliteAreaGroupRepository`:

  1. Read the group rows ordered by `rule_order, id`.
  2. Temporarily assign non-conflicting negative values based on row identity.
  3. Assign final values `1..N` in the captured order.
  4. Commit only after all rows are updated.

  Use this helper after `DeleteRuleAsync`, before/after group rule persistence, and for the one-time schema migration. Change `NextRuleOrderAsync` to return `COUNT(*) + 1` or the canonical maximum plus one only after normalization.

- [ ] **Step 4: Add an idempotent migration.**

  Add a migration after the existing area-rule migration, with a distinct name such as `area-group-rule-order-v2`. It must normalize every group, be safe to run once, and leave `cards`, `run_cards`, `run_realtime_details`, and other device tables untouched.

- [ ] **Step 5: Run persistence tests again.**

  Expected result: all area-group migration and integration tests pass, including repeated migration execution and deletion of first, middle, and last rules.

---

### Task 2: Make the ViewModel renumber immediately after edits

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/AreaGroupRuleRow.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/GroupsViewModel.cs`
- Test: `native/tests/EmsScout.Tests/GroupSettingsUiContractTests.cs`
- Test: `native/tests/EmsScout.Tests/AreaGroupDisplayTests.cs`

**Interfaces:**
- `AreaGroupRuleRow.RuleOrder` becomes a change-notifying settable property controlled by the ViewModel.
- Add `GroupsViewModel.RenumberRules()` as the single in-memory numbering path.

- [ ] **Step 1: Write a failing ViewModel/row test.**

  Cover this behavior: four rows show `1,2,3,4`; remove the second row; the remaining rows immediately expose `1,2,3`; add a row; it exposes `1,2,3,4` before saving.

- [ ] **Step 2: Run the test and confirm it fails.**

  The current `RuleOrder` is read-only and `DeleteRuleRowAsync` only removes the object, so the test must fail before production changes.

- [ ] **Step 3: Implement the minimal in-memory fix.**

  Add a row method such as `SetRuleOrder(int value)` that uses `SetProperty` and raises `PropertyChanged`. Implement:

  ```csharp
  private void RenumberRules()
  {
      for (var index = 0; index < Rules.Count; index++)
          Rules[index].SetRuleOrder(index + 1);
  }
  ```

  Call it after removing a row, after loading rows, and before `SaveGroup` serializes rules. Change new-row creation to use `Rules.Count + 1`, followed by `RenumberRules()`.

- [ ] **Step 4: Verify ViewModel and contract tests.**

  Run the focused group settings tests and confirm the UI order is continuous without requiring a reload.

---

### Task 3: Correct rule table width and alignment

**Files:**
- Modify: `native/src/EmsScout.Desktop/Pages/AreasPage.xaml`
- Modify: `native/tests/EmsScout.Tests/GroupSettingsUiContractTests.cs`

**Interfaces:**
- Header grid and rule-row grid must retain identical column definitions.
- Keyword input remains left aligned; all other rule columns and the delete action are centered.

- [ ] **Step 1: Add failing XAML contract assertions.**

  Assert that both grids contain a match column with a minimum width of at least `96`, and that building, zuo, floor, match, order, header, and delete controls use centered alignment. Assert that the keyword TextBox remains left aligned.

- [ ] **Step 2: Run the contract test and confirm it fails against the current `74`-pixel column and explicit left alignment.**

- [ ] **Step 3: Update both grids in `AreasPage.xaml`.**

  Use the same definitions in both places, with the match column widened, for example:

  ```xml
  <ColumnDefinition Width="104" MinWidth="96" />
  ```

  Apply `HorizontalAlignment="Center"` and `HorizontalContentAlignment="Center"` to the non-keyword ComboBoxes and their headers. Keep the keyword TextBox left aligned. Give the delete button a stable width and centered alignment so the action column does not shift.

- [ ] **Step 4: Run XAML contract tests and build the desktop project.**

  Confirm no fixed-width overflow regression is introduced and the layout still uses the existing disabled horizontal-scroll policy.

---

### Task 4: Replace total-area status text with the linked note

**Files:**
- Modify: `native/src/EmsScout.Desktop/Pages/HomePage.xaml`
- Modify: `native/src/EmsScout.Desktop/ViewModels/HomeViewModel.cs`
- Modify: `native/tests/EmsScout.Tests/DashboardUiContractTests.cs`
- Modify: `native/tests/EmsScout.Tests/DashboardAreaGroupBuilderTests.cs`

**Interfaces:**
- `DashboardAreaGroupSummary.Description` remains the source of truth.
- `DashboardAreaGroupRow.Description` returns the saved note, or `-` when the note is empty.
- `Priority`, `StateText`, `Glyph`, and realtime/statistics properties remain available for other logic, but the area-group card templates no longer render `Priority` or `StateText` in the highlighted position.

- [ ] **Step 1: Write failing dashboard contract tests.**

  Assert that both wide and compact area-group templates visibly bind `Description`, do not bind `Priority` or `StateText`, and that an empty saved note is represented as `-`.

- [ ] **Step 2: Run the tests and confirm they fail.**

  The current templates contain two `Priority` and two `StateText` bindings and no visible `Text="{x:Bind Description}"` binding.

- [ ] **Step 3: Implement the presentation-only change.**

  In both area-group templates, replace the priority/state stack at the highlighted location with the note binding. Keep the group name, scope line, device statistics, anomaly counts, and lock counts unchanged. Do not remove or rewrite the backend priority field.

- [ ] **Step 4: Verify note linkage.**

  Use a dashboard builder test with a group description such as `公共区域设备` and assert the summary carries that value. Add the empty-note case and assert the ViewModel exposes `-`.

---

### Task 5: Full verification and local packaging

**Files:**
- Modify only if required by test failures: the files from Tasks 1-4.
- Do not include: `out/*.db`, logs, JSON/NDJSON, quality reports, or generated field artifacts.

- [ ] **Step 1: Run focused tests.**

  ```powershell
  dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter "FullyQualifiedName~AreaGroupMigrationTests|FullyQualifiedName~AreaGroupRuleIntegrationTests|FullyQualifiedName~GroupSettingsUiContractTests|FullyQualifiedName~DashboardUiContractTests"
  ```

- [ ] **Step 2: Run the complete test suite and Release build.**

  ```powershell
  dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore
  dotnet build native/src/EmsScout.Desktop/EmsScout.Desktop.csproj -c Release --no-restore
  ```

  Record pre-existing unrelated failures separately; the new area-rule tests must be green.

- [ ] **Step 3: Inspect the diff and data boundary.**

  Confirm only source, tests, and plan/documentation files changed. Run `git status --short` and verify no local database or field output is staged.

- [ ] **Step 4: Build and install a version higher than `1.0.64.0` locally.**

  Package a new MSIX version, close the old installed process, install locally, and launch the new version. This is local installation only; do not push field data or generated outputs.

- [ ] **Step 5: Perform the acceptance scenario.**

  In the installed program:

  1. Open an existing editable group with four rules.
  2. Delete the second rule and verify the visible sequence becomes `1,2,3` immediately.
  3. Add a rule and verify it becomes `4`.
  4. Select `不含` and verify the full text and dropdown indicator are visible.
  5. Verify building, zuo, floor, match, sequence, and delete action are centered while keywords remain left aligned.
  6. Set the group note to `公共区域设备`, save, open 总览, and verify that position shows only `公共区域设备`; verify `重点` and `待确认` are absent.
  7. Reload the app and confirm the sequence and note persist.

- [ ] **Step 6: Report outcome without remote submission.**

  Report changed files, tests, local package version, and any remaining visual limitation. Do not commit or push unless separately requested.

---

## Self-Review

- The sequence defect is covered at both database and in-memory UI layers.
- Historical orders are normalized by stable ordering rather than blindly incremented.
- Match-mode width and alignment are covered by XAML contracts and an installed-app acceptance check.
- Both wide and compact overview templates are covered.
- The note source remains the existing SQLite `description` field, so no new data migration is needed for notes.
- Field data and generated output are explicitly excluded from packaging and version control.
