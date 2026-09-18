# 通用区域组规则 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将区域管理改造成通用的楼栋/座号/楼层/关键词规则组，并让总览、数据筛选、Excel、关注审计和导入导出统一使用新规则。

**Architecture:** `AreaGroupRuleMatcher` 负责纯匹配语义；SQLite repository 负责区域组、规则和一次性迁移；桌面 ViewModel 只编辑已规范化的规则模型。所有消费方通过 `AreaGroupSet` 和 matcher 获取结果，旧成员表不再参与业务计算。

**Tech Stack:** .NET 10, WinUI 3, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, xUnit, System.Text.Json。

**Spec:** `docs/superpowers/specs/2026-09-18-area-rule-groups-design.md`

## Global Constraints

- 生产环境不预置任何区域组或规则。
- 规则单条使用 AND，同组多条使用 OR；包含先取候选，不含做排除；只有不含时在其作用域取全集后排除。
- 1-4号座号只能为 `-`；5号为 A-F；6号为 A-C；历史批次使用保存的 X/座号结果。
- 现场批次、实时详情、日志、质量报告、SQLite 和 JSON/NDJSON 不得进入区域组导入导出文件或远端仓库。
- 每项行为先写失败测试并确认失败，再写最小实现；所有改动在隔离分支完成。

### Task 1: 规则值对象与匹配器

**Files:**
- Create: `native/src/EmsScout.Application/Groups/AreaGroupRules.cs`
- Create: `native/src/EmsScout.Application/Groups/AreaGroupRuleMatcher.cs`
- Modify: `native/src/EmsScout.Application/Groups/AreaGroups.cs`
- Test: `native/tests/EmsScout.Tests/AreaGroupRuleMatcherTests.cs`

**Interfaces:**
- Produces `AreaGroupRuleRecord`, `AreaGroupRuleEdit`, `AreaGroupRuleSet`, `AreaGroupRuleNormalizer.NormalizeKeywords`, `AreaGroupRuleMatcher.MatchesAny(DeviceRecord, rules)`.

- [ ] **Step 1: Write failing tests** for building/zuo/floor/keyword AND, same-group OR, include/exclude combination, exclude-only scope, normalization, invalid building/zuo/floor and historical `DeviceRecord.Zuo` usage.
- [ ] **Step 2: Run** `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter FullyQualifiedName~AreaGroupRuleMatcherTests -v normal`; expect failures because the new records and matcher do not exist.
- [ ] **Step 3: Implement** immutable records and the matcher. Normalize floor through `DeviceFloorLabelFormatter`; match keywords with ordinal-ignore-case `Contains`; treat `-` and empty optional fields as wildcards; use `DeviceRecord.Zuo` without recalculation.
- [ ] **Step 4: Run** the focused test and confirm all rule semantics pass.

### Task 2: SQLite schema, migration, and repository contract

**Files:**
- Modify: `native/src/EmsScout.Application/Groups/AreaGroups.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteAreaGroupRepository.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteSchemaMigrator.cs`
- Test: `native/tests/EmsScout.Tests/AreaGroupRepositoryTests.cs`
- Test: `native/tests/EmsScout.Tests/AreaGroupMigrationTests.cs`

**Interfaces:**
- `IAreaGroupRepository.LoadAsync` returns groups plus rules.
- Adds `SaveRuleAsync`, `DeleteRuleAsync`, `ExportAsync`, `ImportAsync`.
- Removes production use of `LoadTargetOptionsAsync`, `SaveItemAsync`, `DeleteItemAsync`, floor catalog APIs, and system-group creation.

- [ ] **Step 1: Add failing SQLite tests** for schema creation, one-time clearing of old groups/items, no system group insertion, CRUD, rule order, group-key uniqueness, enable/disable, delete cascade, and import transaction behavior.
- [ ] **Step 2: Run focused repository/migration tests and verify the expected failures.**
- [ ] **Step 3: Implement schema marker migration and `area_group_rules`; keep device/batch tables untouched; clear only old group rows and old member rows once; create no production rules.**
- [ ] **Step 4: Implement repository CRUD with validation and transactional rule replacement for edits/imports.**
- [ ] **Step 5: Run focused tests and then all infrastructure tests.**

### Task 3: Rule JSON import/export

**Files:**
- Create: `native/src/EmsScout.Application/Groups/AreaGroupTransfer.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteAreaGroupRepository.cs`
- Test: `native/tests/EmsScout.Tests/AreaGroupTransferTests.cs`

**Interfaces:**
- `AreaGroupTransferDocument` contains `schemaVersion` and `groups` only.
- `IAreaGroupRepository.ExportAsync` returns the document; `ImportAsync` merges by `groupKey`.

- [ ] **Step 1: Add failing tests** for JSON shape, no live/batch fields, key-based update, new-group insert, preservation of unrelated groups, invalid/duplicate keys, and rollback on invalid rule.
- [ ] **Step 2: Run focused tests and verify failures.**
- [ ] **Step 3: Implement DTO validation and deterministic serialization with rule order preserved.**
- [ ] **Step 4: Run focused transfer tests and repository tests.**

### Task 4: Dashboard, data query, Excel, and watch integration

**Files:**
- Modify: `native/src/EmsScout.Application/DashboardAreaGroupBuilder.cs`
- Modify: `native/src/EmsScout.Application/Devices/DeviceQuerySpecification.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteDeviceReadRepository.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteDeviceExportService.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteDeviceWatchRepository.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/DataViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/HomeViewModel.cs`
- Test: `native/tests/EmsScout.Tests/DashboardAreaGroupBuilderTests.cs`
- Test: `native/tests/EmsScout.Tests/SqliteDeviceReadRepositoryTests.cs`
- Test: `native/tests/EmsScout.Tests/DeviceExportTests.cs`

- [ ] **Step 1: Add failing integration tests** for generic group matching, overlap, disabled hiding, `-` for unmatched area, current/history queries, and export filtering.
- [ ] **Step 2: Run focused tests and verify failures.**
- [ ] **Step 3: Replace old `monitor_group_items` reads with rule loading and matcher calls; remove automatic public/private group creation and area-type fallback as a group source.**
- [ ] **Step 4: Update dashboard/data navigation and export to use `group:<id>` only; keep base device classification only as a displayed legacy field until the UI contract is removed.**
- [ ] **Step 5: Update watch membership/sample selection to the same matcher semantics; do not resurrect member-table SQL.**
- [ ] **Step 6: Run focused integration tests and all tests.**

### Task 5: Area management UI

**Files:**
- Modify: `native/src/EmsScout.Desktop/Pages/AreasPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/AreasPage.xaml.cs`
- Replace: `native/src/EmsScout.Desktop/ViewModels/GroupsViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/GroupSummaryRow.cs`
- Create: `native/src/EmsScout.Desktop/ViewModels/AreaGroupRuleRow.cs`
- Remove or stop using: `AreaGroupItemRow.cs`, `AreaGroupTargetOptionRow.cs`, `AreaGroupTargetTypeOption.cs`, `FloorCatalogRow.cs`
- Test: `native/tests/EmsScout.Tests/GroupSettingsUiContractTests.cs`

- [ ] **Step 1: Add failing UI contract tests** for 备注, generic group name/key, enable/disable, rule editor with building/zuo/floor/mode/keywords, rule table, add/edit/delete, and import/export actions; assert old “更多组设置”, “添加楼层或设备”, and system-area text are absent.
- [ ] **Step 2: Run the UI contract tests and verify failures.**
- [ ] **Step 3: Implement the new ViewModel with observable rule rows, building-dependent seat options, normalized keywords, validation messages, and navigation to data by group id.**
- [ ] **Step 4: Implement XAML layout and code-behind confirmation dialogs/file pickers; show only created groups, keep disabled groups in management.**
- [ ] **Step 5: Run UI contract tests and compile the desktop project.**

### Task 6: Development fixtures, regression cleanup, and verification

**Files:**
- Create or modify: `native/tests/EmsScout.Tests/AreaGroupTestFixtures.cs`
- Modify: `native/tests/EmsScout.Tests/*` tests that construct old system/member records
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteDeviceWatchRepository.cs` tests if needed
- Modify: `AGENTS.md` only if the final command or data-governance contract changes

- [ ] **Step 1: Add failing fixture test** proving production repository load creates zero groups while a test-only fixture can load public/non-public rules explicitly.
- [ ] **Step 2: Run the fixture test and verify failure.**
- [ ] **Step 3: Implement test-only fixture loading; do not add seed data to app startup or schema migration.**
- [ ] **Step 4: Run `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -v normal` and fix all regressions.
- [ ] **Step 5: Run `dotnet build native/src/EmsScout.Desktop/EmsScout.Desktop.csproj --no-restore -p:Platform=x64` and inspect the final diff for local-data paths or artifacts.
- [ ] **Step 6: Run `git diff --check`, `git status --short`, and a repository search proving no production `EnsureSystemGroupsAsync`/`monitor_group_items` read path remains.**
