# Collection Page Focus Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Simplify the EMS Scout collection page into a clear select-building, prepare-browser, start-collection workflow with concise preflight feedback and an owned Edge open/close lifecycle.

**Architecture:** Keep the existing collection execution plans and sidecar pipeline intact. Add a small application-layer preflight summary builder, expose only three user-facing collection presets from the desktop ViewModel, and make the existing owned Edge process a single explicit toggle state. Legacy `auto-launch` settings normalize to manual `edge-cdp` behavior.

**Tech Stack:** .NET 10, C#, WinUI 3 XAML, CommunityToolkit.Mvvm, xUnit.

## Global Constraints

- Never open or mutate `out/ac.db`, `data/ac.db`, `data/1号楼/ac.db`, `data/2号楼/ac.db`, or their WAL/SHM files.
- Do not change enumeration rules, communication-state mappings, database schema, import behavior, or quality algorithms.
- Preserve internal maintenance execution modes for automation and diagnostics; only hide them from the normal collection page.
- The browser close action may terminate only the Edge process and profile created by EMS Scout.
- During collection, browser open/close and task options remain disabled.
- Do not stage or commit in the current dirty worktree.

---

### Task 1: User collection presets and legacy setting normalization

**Files:**
- Modify: `native/src/EmsScout.Application/Collection/CollectionTaskModes.cs`
- Modify: `native/src/EmsScout.Application/Settings/AppSettingsService.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/SettingsViewModel.cs`
- Modify: `native/tests/EmsScout.Tests/CollectionTaskModeCatalogTests.cs`
- Modify: `native/tests/EmsScout.Tests/AppSettingsServiceTests.cs`

**Interfaces:**
- Consumes: `CollectionTaskModeValues`, `CollectionTaskModeCatalog.BuildPlan(...)`, legacy `AppSettings.DefaultCollectionMode`.
- Produces: `CollectionTaskModeValues.Recapture`, three desktop-visible presets, and normalized `DefaultCollectionMode == "edge-cdp"`.

- [ ] **Step 1: Write failing preset and normalization tests**

```csharp
[Fact]
public void RecaptureUsesTheVerifiedStandardPipeline()
{
    var plan = CollectionTaskModeCatalog.BuildPlan(
        CollectionTaskModeValues.Recapture,
        new CollectionCustomTaskOptions(false, false, false, false));
    Assert.True(plan.RunEnumeration);
    Assert.True(plan.RunValidation);
    Assert.True(plan.RunImport);
    Assert.True(plan.RunQuality);
    Assert.False(plan.RunRealtimeDetails);
}

[Fact]
public void LegacyAutoLaunchNormalizesToManualCdp()
{
    var tempDir = Path.Combine(Path.GetTempPath(), "ems-settings-tests", Guid.NewGuid().ToString("N"));
    var settingsPath = Path.Combine(tempDir, "settings.json");
    Directory.CreateDirectory(tempDir);
    try
    {
        File.WriteAllText(settingsPath, """{"DefaultCollectionMode":"auto-launch"}""");
        var loaded = new AppSettingsService(settingsPath).Load();
        Assert.Equal("edge-cdp", loaded.DefaultCollectionMode);
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}
```

- [ ] **Step 2: Run focused tests and confirm the new assertions fail**

Run:

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter "FullyQualifiedName~CollectionTaskModeCatalogTests|FullyQualifiedName~AppSettingsServiceTests"
```

Expected: failure because `Recapture` does not exist and `auto-launch` is still preserved.

- [ ] **Step 3: Add the recapture execution plan and user-facing labels**

```csharp
public const string Recapture = "recapture";

new(CollectionTaskModeValues.CollectImport, "标准采集", "采集所选楼栋并更新当前数据。", "开始采集"),
new(CollectionTaskModeValues.Full, "完整采集", "标准采集后继续更新实时详情。", "开始完整采集"),
new(CollectionTaskModeValues.Recapture, "补采指定页面", "补采指定页面并更新当前数据。", "开始补采"),
```

Map `Recapture` to the same verified stages as `CollectImport`. Keep existing internal values and plans unchanged.

- [ ] **Step 4: Normalize old collection mode settings and remove the Settings ViewModel selector**

```csharp
output.DefaultCollectionMode = "edge-cdp";
```

Remove `DefaultCollectionModeIndex`; make `ToSettings()` write `DefaultCollectionMode = "edge-cdp"` so a subsequent save cannot restore legacy automatic launch.

- [ ] **Step 5: Run the focused tests and confirm they pass**

Run the command from Step 2. Expected: all selected tests pass.

---

### Task 2: Concise preflight summary and grouped detail contract

**Files:**
- Create: `native/src/EmsScout.Application/Collection/CollectionPreflightSummary.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/PreflightCheckRow.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs`
- Create: `native/tests/EmsScout.Tests/CollectionPreflightSummaryTests.cs`

**Interfaces:**
- Consumes: ordered `IReadOnlyList<CollectionPreflightRequirement>` values.
- Produces: `CollectionPreflightSummaryBuilder.Build(...)`, `CollectionPreflightSummary.Title`, `Detail`, `PassedCount`, `TotalCount`, and `DetailsHeader`.

- [ ] **Step 1: Write failing summary tests**

```csharp
[Fact]
public void FirstFailedRequirementBecomesTheVisibleBlocker()
{
    var summary = CollectionPreflightSummaryBuilder.Build([
        new("本地采集组件", "重新安装应用", true),
        new("采集浏览器", "请先打开采集浏览器", false),
        new("EMS 登录", "请完成登录", false),
    ]);
    Assert.False(summary.IsReady);
    Assert.Equal("运行前检查未通过", summary.Title);
    Assert.Equal("卡在采集浏览器：请先打开采集浏览器", summary.Detail);
    Assert.Equal("检查详情（1/3 通过）", summary.DetailsHeader);
}

[Fact]
public void AllRequirementsProduceAnExplicitPassResult()
{
    var summary = CollectionPreflightSummaryBuilder.Build([
        new("本地采集组件", "", true),
        new("采集浏览器", "", true),
        new("EMS 页面", "", true),
    ]);
    Assert.True(summary.IsReady);
    Assert.Equal("运行前检查通过", summary.Title);
    Assert.Equal("检查详情（3/3 通过）", summary.DetailsHeader);
}
```

- [ ] **Step 2: Run the summary tests and confirm missing types fail compilation**

Run:

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter "FullyQualifiedName~CollectionPreflightSummaryTests"
```

Expected: compile failure for missing preflight summary types.

- [ ] **Step 3: Implement the pure summary builder**

```csharp
public sealed record CollectionPreflightRequirement(string Name, string Resolution, bool IsSatisfied);

public sealed record CollectionPreflightSummary(
    bool IsReady,
    string Title,
    string Detail,
    int PassedCount,
    int TotalCount)
{
    public string DetailsHeader => $"检查详情（{PassedCount}/{TotalCount} 通过）";
}

public static class CollectionPreflightSummaryBuilder
{
    public static CollectionPreflightSummary Build(IReadOnlyList<CollectionPreflightRequirement> requirements)
    {
        var firstFailure = requirements.FirstOrDefault(item => !item.IsSatisfied);
        var passed = requirements.Count(item => item.IsSatisfied);
        return firstFailure is null
            ? new(true, "运行前检查通过", "采集浏览器、EMS 页面和本地采集组件均已就绪", passed, requirements.Count)
            : new(false, "运行前检查未通过", $"卡在{firstFailure.Name}：{firstFailure.Resolution}", passed, requirements.Count);
    }
}
```

- [ ] **Step 4: Group the ViewModel checks into four user concepts**

Build ordered requirements for:

```csharp
[
    new("本地采集组件", localResolution, localReady),
    new("当前数据", dataResolution, dataReadyForSelectedPlan),
    new("采集浏览器", "请先打开采集浏览器", browserReadyForSelectedPlan),
    new("EMS 登录", emsResolution, emsReadyForSelectedPlan),
]
```

Populate four `PreflightCheckRow` values, apply the summary to `IsEnvironmentReady`, `ReadinessTitle`, `ReadinessDetail`, `ReadinessGlyph`, and add `PreflightDetailsHeader` for XAML binding. For a standard enumeration run, a missing database is ready because the import stage creates it; for maintenance plans, preserve the existing snapshot/database requirements.

- [ ] **Step 5: Run summary and task mode tests**

Run:

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter "FullyQualifiedName~CollectionPreflightSummaryTests|FullyQualifiedName~CollectionTaskModeCatalogTests"
```

Expected: all selected tests pass.

---

### Task 3: Owned collection browser toggle and task interlock

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/TasksPage.xaml`
- Create: `native/tests/EmsScout.Tests/CollectionPageUiContractTests.cs`

**Interfaces:**
- Consumes: `_ownedEdgeProcess`, `_ownedEdgeSessionRoot`, `_ownedEdgeCdpPort`, `CheckEnvironmentAsync()`.
- Produces: `ToggleCollectionBrowserCommand`, `IsCollectionBrowserOpen`, `CollectionBrowserActionText`, `CollectionBrowserActionGlyph`, and `CollectionBrowserActionToolTip`.

- [ ] **Step 1: Write failing UI/source contract assertions**

```csharp
Assert.Contains("ToggleCollectionBrowserCommand", xaml);
Assert.Contains("关闭采集浏览器", viewModel);
Assert.Contains("采集期间不能关闭浏览器", viewModel);
Assert.Contains("IsRunning && IsCollectionBrowserOpen", viewModel);
Assert.DoesNotContain("--auto-launch", viewModel);
Assert.DoesNotContain("DefaultCollectionMode.Equals(\"auto-launch\"", viewModel);
Assert.Contains("!IsRunning && !IsCheckingEnvironment", viewModel);
```

- [ ] **Step 2: Run the contract test and confirm failure**

Run:

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter "FullyQualifiedName~CollectionPageUiContractTests"
```

Expected: failure because the page still binds `OpenEmsCommand` and contains auto-launch branches.

- [ ] **Step 3: Replace the open-only command with a stateful toggle**

```csharp
private bool CanToggleCollectionBrowser() => !IsRunning && !IsCheckingEnvironment;

[RelayCommand(CanExecute = nameof(CanToggleCollectionBrowser))]
private async Task ToggleCollectionBrowserAsync()
{
    RefreshCollectionBrowserState();
    if (IsCollectionBrowserOpen)
    {
        if (TryDisposeOwnedBrowser())
        {
            UpdateCollectionBrowserPresentation();
            await CheckEnvironmentAsync().ConfigureAwait(true);
        }
        return;
    }
    await OpenOwnedCollectionBrowserAsync().ConfigureAwait(true);
}
```

`TryDisposeOwnedBrowser()` must leave the process reference intact when the process is still alive after a failed close. It clears the random CDP port and deletes the profile only after confirmed exit.

- [ ] **Step 4: Remove implicit browser launch branches**

- Always invoke enumeration with `--edge`.
- Always run realtime details with `--browser-mode=cdp` and `REALTIME_BROWSER_MODE=cdp`.
- Always require reachable CDP and, when enabled, an EMS page before starting a browser-dependent task.
- Remove readiness copy that promises Edge will open after Start.
- Update the toggle presentation after open, close, external exit detection, task start, and task completion.

- [ ] **Step 5: Bind the page button to the toggle state**

```xml
<Button
    Command="{x:Bind ViewModel.ToggleCollectionBrowserCommand}"
    ToolTipService.ToolTip="{x:Bind ViewModel.CollectionBrowserActionToolTip, Mode=OneWay}">
    <StackPanel Orientation="Horizontal" Spacing="6">
        <FontIcon Glyph="{x:Bind ViewModel.CollectionBrowserActionGlyph, Mode=OneWay}" />
        <TextBlock Text="{x:Bind ViewModel.CollectionBrowserActionText, Mode=OneWay}" />
    </StackPanel>
</Button>
```

- [ ] **Step 6: Run the browser contract and existing owned endpoint tests**

Run:

```powershell
dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --filter "FullyQualifiedName~CollectionPageUiContractTests|FullyQualifiedName~OwnedEdgeCdpEndpointTests"
```

Expected: all selected tests pass.

---

### Task 4: Collection page information hierarchy and Settings cleanup

**Files:**
- Modify: `native/src/EmsScout.Desktop/Pages/TasksPage.xaml`
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/SettingsPage.xaml`
- Modify: `native/src/EmsScout.Desktop/ViewModels/DiagnosticsViewModel.cs`
- Modify: `native/tests/EmsScout.Tests/CollectionPageUiContractTests.cs`

**Interfaces:**
- Consumes: `TaskModes`, `IsRecaptureMode`, `PreflightDetailsHeader`, `PreflightChecks`.
- Produces: unified building labels, collapsed preflight detail, three visible collection presets, conditional recapture input, and no automatic Edge setting.

- [ ] **Step 1: Extend the contract test with the final information architecture**

```csharp
Assert.Contains("new(\"1号\", \"1号楼\", true)", viewModel);
Assert.DoesNotContain("科研综合楼", viewModel);
Assert.Contains("Text=\"高级设置\"", xaml);
Assert.Contains("Text=\"采集方式、补采和诊断选项\"", xaml);
Assert.Contains("PreflightDetailsHeader", xaml);
Assert.Contains("x:Load=\"{x:Bind ViewModel.IsRecaptureMode", xaml);
Assert.DoesNotContain("默认采集模式", settingsXaml);
Assert.DoesNotContain("自动启动 Edge", settingsXaml);
```

Assert `TaskModes` filters to exactly `CollectImport`, `Full`, and `Recapture`, while `CollectionTaskModeCatalog.Options` still contains maintenance modes.

- [ ] **Step 2: Run the contract test and confirm the new assertions fail**

Run the Task 3 focused test command. Expected: assertions fail against the old labels and XAML.

- [ ] **Step 3: Implement the collection page XAML hierarchy**

- Change all building display labels to `N号楼` while retaining values `N号`.
- Replace the always-visible preflight card with a collapsed `Expander` whose header binds `PreflightDetailsHeader` and whose body lists the four grouped rows.
- Rename `高级任务` to `高级设置`.
- Keep the three-option `ComboBox`, its plain-language description, conditional recapture field, and `保存诊断日志` toggle.
- Remove log category, realtime batch/timeout/reopen/max-device controls, self-diagnose, disable-network-monitor, refresh-inventory, and skip-inventory controls from XAML. Keep their safe ViewModel defaults for execution compatibility.

- [ ] **Step 4: Remove the Settings page mode selector and update diagnostics copy**

The settings card retains log level and NDJSON controls. Replace the diagnostics row `默认采集模式` with user-facing copy such as `采集浏览器` / `由采集页手动管理`.

- [ ] **Step 5: Build the desktop project and fix XAML compiler errors**

Run:

```powershell
npm run native:build
```

Expected: exit code 0, 0 errors.

- [ ] **Step 6: Run the collection page contract test**

Run the Task 3 focused test command. Expected: all selected tests pass.

---

### Task 5: Full verification and isolated runtime evidence

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `.context-summary.md`
- Create: `docs/validation/2026-07-13-collection-page-focus.md`

**Interfaces:**
- Consumes: completed collection page implementation.
- Produces: automated and isolated visual verification evidence without touching production databases.

- [ ] **Step 1: Run the full native test suite**

Run:

```powershell
npm run native:test
```

Record the exact pass/fail count. Expected: zero failures.

- [ ] **Step 2: Run the full native build**

Run:

```powershell
npm run native:build
```

Record exact warning/error counts. Expected: zero errors.

- [ ] **Step 3: Run whitespace and scoped diff checks**

Run:

```powershell
git diff --check
git diff -- native/src/EmsScout.Application/Collection native/src/EmsScout.Application/Settings/AppSettingsService.cs native/src/EmsScout.Desktop/Pages/TasksPage.xaml native/src/EmsScout.Desktop/Pages/SettingsPage.xaml native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs native/src/EmsScout.Desktop/ViewModels/SettingsViewModel.cs native/src/EmsScout.Desktop/ViewModels/DiagnosticsViewModel.cs native/tests/EmsScout.Tests
```

Expected: no whitespace errors and no protected database paths in the scoped changes.

- [ ] **Step 4: Launch the existing isolated UI-validation instance**

Use `scripts/run-native.ps1 -NoBuild -UiValidation` or the existing temporary UI-validation settings path. Verify both the narrow stress window and a normal desktop window:

- all six building labels are consistent;
- the preflight banner says pass/fail and names the first blocker;
- details are collapsed by default and expand to four rows;
- only three collection presets are visible;
- recapture input appears only for the recapture preset;
- open changes to close, close returns to open;
- browser close is disabled while collection state is running;
- Settings contains no automatic Edge option.

- [ ] **Step 5: Record only observed evidence**

Update the validation document, changelog, and context summary. Explicitly label this as isolated local UI validation, not real EMS field E2E.
