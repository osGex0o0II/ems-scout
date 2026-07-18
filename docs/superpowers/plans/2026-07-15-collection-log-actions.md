# 采集页运行记录操作 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在采集页运行记录标题右侧增加“清空”和“展开/还原”图标按钮，清空当前界面日志并可将日志区域扩展到右侧任务区。

**Architecture:** 清空操作由 `CollectionTaskViewModel` 直接清理内存中的 `Logs` 集合，不触碰磁盘 NDJSON 诊断文件。展开状态由 ViewModel 的 `IsLogsExpanded` 驱动，XAML 在同一个执行区域内切换任务进度卡片与跨两行的日志卡片；任务运行状态和日志流保持不变。

**Tech Stack:** C#/.NET 10, WinUI 3 XAML, CommunityToolkit.Mvvm RelayCommand, xUnit contract tests.

## Global Constraints

- 复用现有 `ToolbarButtonStyle`、`FontIcon` 和主题资源，不引入新图标库或新视觉体系。
- “清空”只清空页面当前显示的最多 300 条日志，不删除磁盘上的诊断日志文件。
- 展开只影响右侧执行区域；窄窗口不得覆盖左侧采集范围和运行前检查区域。
- 不修改生产数据库，不提交或暂存 git 变更。

---

### Task 1: 锁定日志操作行为的回归契约

**Files:**
- Modify: `native/tests/EmsScout.Tests/CollectionPageUiContractTests.cs`
- Test: `native/tests/EmsScout.Tests/CollectionPageUiContractTests.cs`

**Interfaces:**
- Consumes: `CollectionTaskViewModel` command/property names and `TasksPage.xaml` bindings.
- Produces: contract assertions for `ClearLogsCommand`, `ToggleLogsExpandedCommand`, `IsLogsExpanded`, icon-only button accessibility labels, and expanded row-span bindings.

- [ ] **Step 1: Write the failing assertions**

  在 `CollectionBrowserIsAnExplicitToggleLockedDuringCollection` 后增加断言，要求 XAML/VM 中存在：

  ```csharp
  Assert.Contains("ClearLogsCommand", xaml);
  Assert.Contains("ToggleLogsExpandedCommand", xaml);
  Assert.Contains("IsLogsExpanded", xaml);
  Assert.Contains("AutomationProperties.Name=\"清空运行记录\"", xaml);
  Assert.Contains("AutomationProperties.Name=\"展开运行记录\"", xaml);
  Assert.Contains("Logs.Clear()", viewModel);
  Assert.Contains("public bool IsLogsExpanded", viewModel);
  Assert.Contains("Grid.RowSpan=\"2\"", xaml);
  ```

- [ ] **Step 2: Run the focused test and verify it fails**

  Run:

  ```powershell
  dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Debug --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~CollectionPageUiContractTests
  ```

  Expected: FAIL because the new commands, property, and bindings do not exist yet.

### Task 2: Add ViewModel commands and expanded-state notifications

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs`
- Test: `native/tests/EmsScout.Tests/CollectionPageUiContractTests.cs`

**Interfaces:**
- Consumes: `Logs` (`ObservableCollection<CollectionTaskLogRow>`), existing `ObservableObject`/`RelayCommand` patterns.
- Produces: `IsLogsExpanded`, `CanClearLogs`, `ClearLogsCommand`, `ToggleLogsExpandedCommand`.

- [ ] **Step 1: Add state and command implementation**

  Add the following members near the existing log collection:

  ```csharp
  public partial bool IsLogsExpanded { get; set; }

  public bool CanClearLogs => Logs.Count > 0;

  [RelayCommand(CanExecute = nameof(CanClearLogs))]
  private void ClearLogs()
  {
      Logs.Clear();
      ClearLogsCommand.NotifyCanExecuteChanged();
  }

  [RelayCommand]
  private void ToggleLogsExpanded() => IsLogsExpanded = !IsLogsExpanded;
  ```

  在 `AddLog` 的 UI 队列中，`Logs.Add(row)`/裁剪完成后调用 `ClearLogsCommand.NotifyCanExecuteChanged()`；在清空后由 `OnIsLogsExpandedChanged` 不需要额外副作用。若 Toolkit 生成的 partial 属性不适合当前文件写法，则改用普通属性 setter，并在 setter 中调用 `OnPropertyChanged(nameof(IsLogsExpanded))`。

- [ ] **Step 2: Run the focused test and verify it passes for ViewModel contracts**

  Run the same focused test command from Task 1. Expected: remaining failures only concern XAML bindings.

### Task 3: Add icon actions and WinUI layout states

**Files:**
- Modify: `native/src/EmsScout.Desktop/Pages/TasksPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/TasksPage.xaml.cs` only if `Logs_CollectionChanged` needs a safe expanded-state scroll target.
- Test: `native/tests/EmsScout.Tests/CollectionPageUiContractTests.cs`

**Interfaces:**
- Consumes: `ClearLogsCommand`, `ToggleLogsExpandedCommand`, `IsLogsExpanded`, `CanClearLogs`.
- Produces: two icon-only buttons in the green-box header position; normal/expanded visibility bindings for the two execution cards.

- [ ] **Step 1: Replace the log header StackPanel with a two-column Grid**

  Keep title and subtitle on the left. Add a right-aligned horizontal action group containing:

  ```xml
  <Button
      AutomationProperties.Name="清空运行记录"
      Command="{x:Bind ViewModel.ClearLogsCommand}"
      IsEnabled="{x:Bind ViewModel.CanClearLogs, Mode=OneWay}"
      Style="{StaticResource ToolbarButtonStyle}"
      ToolTipService.ToolTip="清空运行记录"
      Width="32"
      Height="32"
      Padding="0">
      <FontIcon FontSize="14" Glyph="&#xE74D;" />
  </Button>
  ```

  Add a second normal-state button bound to `ToggleLogsExpandedCommand` with `AutomationProperties.Name="展开运行记录"`, Tooltip `展开运行记录`, and the existing maximize glyph. Add a paired expanded-state button with Tooltip `还原任务进度布局` and the corresponding restore glyph; only one of the two expand buttons is visible at a time.

- [ ] **Step 2: Toggle execution cards without changing data state**

  Bind the task progress card visibility to `IsLogsExpanded` (visible when false) and the log card visibility to the inverse state. In expanded state set the log card `Grid.RowSpan="2"`; in normal state keep it at `Grid.Row="1"`. Keep `ExecutionPanel` as the outer boundary so the expanded log cannot cover setup/preflight content.

- [ ] **Step 3: Run focused tests and build**

  Run:

  ```powershell
  dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Debug --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~CollectionPageUiContractTests
  npm run native:build
  ```

  Expected: focused tests pass and native build exits 0.

### Task 4: Manual QA of clear and expand states

**Files:**
- No source changes unless QA exposes a regression.

- [ ] **Step 1: Launch the native app and open “采集”**
- [ ] **Step 2: Verify empty state**

  Confirm the clear icon is disabled when there are no visible logs; expand icon remains available.

- [ ] **Step 3: Verify populated state and clear behavior**

  Confirm clicking the trash icon empties only the visible list, new task messages continue to appear, and persistent diagnostic files remain untouched.

- [ ] **Step 4: Verify expanded and restored states**

  Confirm the log card fills the yellow-box execution area, the progress card is temporarily hidden, the icon/tooltip changes to restore, and restoring returns the original two-card layout.

- [ ] **Step 5: Verify narrow-window behavior**

  Resize below the existing 980px breakpoint and confirm expansion stays inside the execution section without covering setup/preflight content.
