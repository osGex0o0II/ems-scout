# 设置与导航体验 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成 EMS Scout 设置页扩展、窗口状态持久化、StartupTask/SendTo 集成和统一页面切换动效。

**Architecture:** 配置字段和归一化继续由 `AppSettingsService` 负责；WinUI 运行时通过独立服务读取配置，`MainWindow` 只负责生命周期和导航触发。系统集成服务均采用失败可恢复策略，未打包环境不影响主界面启动。

**Tech Stack:** .NET 10、WinUI 3、WinUIEx、CommunityToolkit.Mvvm、xUnit、MSIX StartupTask。

**Spec:** `docs/superpowers/specs/2026-09-08-settings-navigation.md`

## Global Constraints

- 保留当前 Native WinUI 3 架构，不恢复旧 Web、Electron 或 TUI 入口。
- 默认页面动效为短时淡入；减少动效或显式关闭动效时不产生位移动画。
- StartupTask 和 SendTo 在调试/未打包环境失败时不可让应用崩溃。
- 不删除用户数据库，不回滚工作区已有改动。

---

### Task 1: 配置契约与持久化

**Files:**
- Modify: `native/src/EmsScout.Application/Settings/AppSettings.cs`
- Modify: `native/src/EmsScout.Application/Settings/AppSettingsService.cs`
- Test: `native/tests/EmsScout.Tests/AppSettingsServiceTests.cs`

- [x] 写出新字段的默认值、归一化和 Clone 持久化失败测试。
- [x] 运行定向测试确认它因字段缺失或归一化缺失失败。
- [x] 添加字段、窗口位置记录类型和允许值归一化。
- [x] 运行定向测试确认通过。

### Task 2: 设置 ViewModel 与页面

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/SettingsViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/SettingsPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/SettingsPage.xaml.cs`
- Test: `native/tests/EmsScout.Tests/SettingsUiContractTests.cs`

- [x] 先添加设置项和分组的 UI 合同失败测试。
- [x] 运行测试确认合同缺失。
- [x] 添加 ViewModel 映射和紧凑的二级分组页面。
- [x] 运行测试确认通过并编译 Desktop 项目。

### Task 3: 窗口位置、启动和 SendTo

**Files:**
- Create: `native/src/EmsScout.Desktop/Services/StartupTaskService.cs`
- Create: `native/src/EmsScout.Desktop/Services/SendToShortcutService.cs`
- Modify: `native/src/EmsScout.Desktop/MainWindow.xaml.cs`
- Modify: `native/src/EmsScout.Desktop/App.xaml.cs`
- Modify: `native/src/EmsScout.Desktop/Package.appxmanifest`
- Modify: `native/src/EmsScout.Desktop/Services/WindowSizeConstraint.cs`
- Test: `native/tests/EmsScout.Tests/SettingsUiContractTests.cs`

- [x] 先补 manifest、快捷方式和窗口保存行为合同失败测试。
- [x] 运行测试确认缺少实现。
- [x] 实现启动任务、SendTo 快捷方式、窗口位置保存/恢复和启动最小化。
- [x] 运行测试和 Desktop 构建。

### Task 4: 统一页面切换动效

**Files:**
- Modify: `native/src/EmsScout.Desktop/MainWindow.xaml.cs`
- Modify: `native/src/EmsScout.Desktop/AppUiSettingsService.cs`
- Test: `native/tests/EmsScout.Tests/SettingsUiContractTests.cs`

- [x] 先添加动效策略合同测试。
- [x] 运行测试确认当前导航没有统一策略。
- [x] 添加淡入、轻微滑入和无动画三种策略，并让减少动效覆盖位移动画。
- [x] 运行 Desktop 构建和全量测试。

### Task 5: 全链路验证

**Files:**
- No source changes unless a verification defect is found.

- [x] 运行 `dotnet test native/EmsScout.Native.slnx --configuration Release`，218 项通过。
- [x] 运行 `npm run validate` 和 `npm run self-test`。
- [x] 构建 x64 Native 应用并检查 MSIX manifest，`1.0.8.10` 包生成成功。
- [ ] 本机安装新 MSIX 并验证新 UI；当前终端缺少管理员权限，开发证书尚未受 `LocalMachine\\Root/TrustedPeople` 信任，安装脚本在安装前安全停止。
