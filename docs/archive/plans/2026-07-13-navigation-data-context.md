# Navigation And Data Context Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace eight peer navigation items with five workflow items plus footer tools while preserving shared batch context and date-management deep links.

**Architecture:** Keep all existing WinUI pages and view models. `MainWindow` becomes the only navigation-information-architecture change: workbench, collection, device data, rules/plans, and audit are primary; settings and diagnostics are footer tools; date management is a child destination selected under rules/plans. Existing `DataContextService` remains the single batch source for workbench, device data, and audit.

**Tech Stack:** .NET 10, WinUI 3 `NavigationView`, xUnit source-contract tests, Windows Computer Use.

## Global Constraints

- Display brand remains `EMS Scout`; .NET identifiers remain `EmsScout`.
- Do not change collection rules, communication-state mapping, SQLite schema, or Excel export behavior.
- Do not open protected repository databases during runtime validation; use `run-native.ps1 -UiValidation`.
- Preserve current page implementations and existing user worktree changes.
- Historical data remains read-only and is still controlled by `DataContextService`.

---

### Task 1: Lock The Navigation Contract

**Files:**
- Create: `native/tests/EmsScout.Tests/NavigationInformationArchitectureTests.cs`
- Modify: `native/tests/EmsScout.Tests/DateManagementUiContractTests.cs`
- Test: `native/tests/EmsScout.Tests/NavigationInformationArchitectureTests.cs`

**Interfaces:**
- Consumes: `MainWindow.xaml` navigation item `Tag` values and `MainWindow.xaml.cs` page mapping.
- Produces: source contracts for five primary items, two footer tools, and the rules/plans date deep link.

- [ ] **Step 1: Write failing navigation tests**

Assert that `MainWindow.xaml` contains primary tags `workbench`, `collection`, `devices`, `rules`, and `audit`; contains `settings` and `diagnostics` only inside `NavigationView.FooterMenuItems`; and no longer exposes `dates` as a top-level item. Update the date contract to require `Content="规则与计划" Tag="rules"` and the existing `打开日期管理` button.

- [ ] **Step 2: Run the focused tests and verify failure**

```powershell
dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false --filter "FullyQualifiedName~NavigationInformationArchitectureTests|FullyQualifiedName~DateManagementUiContractTests"
```

Expected: navigation assertions fail against the eight-item shell.

- [ ] **Step 3: Do not change production code in this task**

The failed tests are the review checkpoint for Task 2.

### Task 2: Implement The Five-Item Shell

**Files:**
- Modify: `native/src/EmsScout.Desktop/MainWindow.xaml`
- Modify: `native/src/EmsScout.Desktop/MainWindow.xaml.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/HomePage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/TasksPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/DataPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/AreasPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/DateManagementPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/AuditPage.xaml`
- Test: `native/tests/EmsScout.Tests/NavigationInformationArchitectureTests.cs`

**Interfaces:**
- Consumes: `NavigationService.NavigateToData`, `NavigateToAudit`, and `NavigateToDates`.
- Produces: primary tags `workbench`, `collection`, `devices`, `rules`, `audit`; footer tags `settings`, `diagnostics`.

- [ ] **Step 1: Replace the primary navigation items**

Use these labels and mappings:

```text
工作台 -> HomePage
采集 -> TasksPage
设备数据 -> DataPage
规则与计划 -> AreasPage
审计 -> AuditPage
```

Move settings and diagnostics to `NavigationView.FooterMenuItems` without changing their pages.

- [ ] **Step 2: Preserve deep-link selection**

Map `NavigateToData` to tag `devices`, `NavigateToAudit` to `audit`, and `NavigateToDates` to tag `rules` while navigating to `DateManagementPage`. Update `SelectNavigationItem` to inspect both `MenuItems` and `FooterMenuItems`.

- [ ] **Step 3: Align visible page titles**

Change only the top page title text: `EMS Scout 运维总览` to `工作台`, `采集任务` to `采集`, `数据管理` to `设备数据`, `分组设置` to `规则与计划`, `日期管理` to `规则与计划 · 日期`, and `审计中心` to `审计`. Keep operational copy and command labels that name existing workflows where changing them would alter meaning.

- [ ] **Step 4: Run focused tests**

Run the Task 1 command. Expected: all navigation and date-management contract tests pass.

### Task 3: Verify Shared Context And Runtime Layout

**Files:**
- Modify: `native/README.md`
- Test: `native/tests/EmsScout.Tests/DataManagementUiContractTests.cs`

**Interfaces:**
- Consumes: existing singleton `DataContextService` bindings in workbench, device data, and audit.
- Produces: verified navigation/runtime evidence without database-path drift.

- [ ] **Step 1: Run the complete native test suite**

```powershell
npm run native:test
```

Expected: zero failures.

- [ ] **Step 2: Build the Debug AppX**

```powershell
npm run native:build
```

Expected: zero XAML compiler errors.

- [ ] **Step 3: Inspect the isolated packaged window**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-native.ps1 -NoBuild -UiValidation
```

Verify five primary items, two footer tools, date deep-link selection under rules/plans, no runtime dialog, no process crash, and no navigation/content overlap at the current desktop size.

- [ ] **Step 4: Update native product-shape documentation**

Document the five workflow destinations and footer tools. Do not claim the later P0-C/P0-D queue/detail features are complete.
