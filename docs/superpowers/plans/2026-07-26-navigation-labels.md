# Navigation Labels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Display the requested seven Chinese labels in the existing WinUI navigation shell without changing navigation behavior.

**Architecture:** The static `NavigationViewItem` labels live in `MainWindow.xaml`; their stable Tags drive routing and must remain unchanged. The existing XML-based navigation information-architecture test verifies visible labels, order, placement, and Tags without needing to start WinUI.

**Tech Stack:** C#/.NET 10, WinUI 3 XAML, xUnit.

> **完成记录（2026-07-26）：** 步骤 1/3 的改动在接手时已存在于工作树（步骤 2 的红灯运行未独立复现）。步骤 4 聚焦测试 3/3 通过；步骤 5 以 `-UiValidation` 隔离目录启动并截图确认 7 个标签全部正确；步骤 6 已提交 `f4d8e30`。同轮门禁：.NET 非生产 360/360、Node 121 pass/2 skip、self-test、`dotnet format`、`git diff --check` 全部通过。

## Global Constraints

- Keep all navigation item placement, icons, page routes, and Tags unchanged.
- The visible labels must be 概览, 采集, 数据, 区域, 审计, 设置, 诊断 in their current menu/footer order.
- Do not modify page titles, commands, or other references to 数据管理, 区域组, or 系统设置.
- Launch the app with `-UiValidation` so verification uses a unique temporary data and export directory.

---

### Task 1: Navigation Label Contract

**Files:**
- Modify: `native/tests/EmsScout.Tests/NavigationInformationArchitectureTests.cs:7-35`

**Interfaces:**
- Consumes: `MainWindow.xaml` XML `NavigationView.MenuItems` and `NavigationView.FooterMenuItems` hosts.
- Produces: `ShellUsesFiveWorkflowItemsAndTwoFooterTools`, which asserts the requested labels and unchanged route Tags.

- [x] **Step 1: Write the failing test**

Replace the expected arrays in `ShellUsesFiveWorkflowItemsAndTwoFooterTools` with:

```csharp
[
    ("概览", "workbench"),
    ("采集", "collection"),
    ("数据", "devices"),
    ("区域", "rules"),
    ("审计", "audit"),
]

Assert.Equal([("设置", "settings"), ("诊断", "diagnostics")], footer);
```

- [x] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false --filter FullyQualifiedName~NavigationInformationArchitectureTests
```

Expected: `ShellUsesFiveWorkflowItemsAndTwoFooterTools` fails because `MainWindow.xaml` still contains 工作台, 设备数据, 区域组, and 系统设置.

- [x] **Step 3: Write minimal implementation**

In `native/src/EmsScout.Desktop/MainWindow.xaml`, preserve all Tags and change only these attributes:

```xml
<NavigationViewItem Content="概览" IsSelected="True" Tag="workbench">
<NavigationViewItem Content="数据" Tag="devices">
<NavigationViewItem Content="区域" Tag="rules">
<NavigationViewItem Content="设置" Tag="settings">
```

- [x] **Step 4: Run test to verify it passes**

Run the same focused `dotnet test` command.

Expected: all three `NavigationInformationArchitectureTests` pass.

- [x] **Step 5: Verify application launch for visual inspection**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-native.ps1 -UiValidation
```

Expected: the native `EMS Scout` window opens with the requested labels, using a unique `UI_VALIDATION_DIRECTORY` rather than repository or production data.

- [x] **Step 6: Commit the scoped change**

Run:

```powershell
git add -- native/src/EmsScout.Desktop/MainWindow.xaml native/tests/EmsScout.Tests/NavigationInformationArchitectureTests.cs docs/superpowers/specs/2026-07-26-navigation-labels-design.md docs/superpowers/plans/2026-07-26-navigation-labels.md
git diff --cached --check
git commit -m "fix: simplify navigation labels"
```
