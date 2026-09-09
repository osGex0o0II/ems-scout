# Secondary Worktree Merge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将另一台电脑目录中的有效 Native EMS 修改迁移到当前项目，同时保留当前项目已有的历史批次/对比改造，并排除已淘汰的旧架构和构建产物。

**Architecture:** 当前项目的 Native WinUI 3 + Application/Infrastructure + SQLite 主链路是唯一合并目标。源目录没有 Git 元数据，因此以两个目录的 SHA-256 文件清单和逐文件差异为依据；同名关键文件采用人工三方思路合并，当前工作区已有修改优先保留。源目录的 Electron、Web、TUI、Legacy、旧报表入口不迁移。

**Tech Stack:** C#/.NET 10、WinUI 3、Windows App SDK、WinUIEx、SQLite、Node.js/Playwright、PowerShell、xUnit。

**Spec:** 用户请求：将 `D:\System Files\Downloads\LocalSend Files\ems-tool-audit-fix\ems-tool-audit-fix` 的修改合并到 `D:\Code\Git\ems-scout`，深度核实后说明实际修改内容。

## Global Constraints

- 不覆盖或回滚当前工作区已有修改。
- 不重新引入 `electron/`、`web/`、`src/panel/`、`src/tui/` 或 `native/src/EmsScout.Legacy/`。
- 不迁移 `bin/`、`obj/`、`out/`、`logs/`、数据库和其他本地构建/运行产物。
- 不提交或推送远端；本轮只修改当前工作区并报告结果。
- Native 历史批次查询、对比、恢复和其现有回归测试必须继续存在。

---

### Task 1: 建立源目录差异基线

**Files:**
- Create: `docs/superpowers/plans/2026-09-09-merge-secondary-worktree.md`
- Inspect: `AGENTS.md`, 两个目录的 Git 状态、项目文件、Native 入口和源目录独有文件

- [ ] 记录当前工作区状态、源目录是否为 Git 工作树、共同文件哈希、源独有文件和当前独有文件。
- [ ] 对共同变更文件按 Native UI、业务层、采集脚本、配置/文档分类。
- [ ] 将旧架构和构建产物列为明确不迁移项。

### Task 2: 审核并合并兼容的 Native 修改

**Files:**
- Modify: `native/src/EmsScout.Desktop/Pages/*.xaml`, `native/src/EmsScout.Desktop/Pages/*.xaml.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/*.cs`
- Modify: `native/src/EmsScout.Application/**/*.cs`
- Modify: `native/src/EmsScout.Infrastructure/**/*.cs`
- Modify: `scripts/*.ps1`, `scripts/*.js`, `scripts/schema.sql` only when the change belongs to the current Native/采集主链路

- [ ] 对源目录与当前版本的每个共同 Native 差异做逐文件内容审查，区分源目录新增能力、源目录回退和仅格式/版本差异。
- [ ] 以当前历史批次模型和当前未提交修改为基线，将源目录中不冲突的 UI、窗口、设置、托盘、采集稳定性或安装更新修正用 `apply_patch` 合并。
- [ ] 对同一文件存在逻辑冲突时保留当前历史数据能力，并把源目录的独立逻辑改写为兼容当前接口；禁止直接复制整文件。
- [ ] 检查项目文件引用、解决方案项目、XAML x:Name/绑定、SQLite schema 和迁移兼容性。

### Task 3: 补充合并回归测试

**Files:**
- Modify: `native/tests/EmsScout.Tests/*.cs`
- Modify: `scripts/self-test.js` only if a retained采集规则变更需要断言

- [ ] 为每个实际迁移的行为增加最小回归测试，覆盖历史批次选择/对比不回退、窗口/托盘设置、导出或采集规则的关键边界。
- [ ] 运行定向测试，确认失败来自缺失实现后再完成实现调整。

### Task 4: 全链路验证和差异报告

**Files:**
- Inspect: `git diff --check`, 构建/测试输出和最终 `git status`

- [ ] 运行 `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --configuration Release`。
- [ ] 运行 `dotnet build native/src/EmsScout.Desktop/EmsScout.Desktop.csproj --configuration Release`。
- [ ] 运行 `npm run validate` 和 `npm run self-test`。
- [ ] 运行 `git diff --check`，检查是否误加入旧架构、数据库、构建产物。
- [ ] 按“实际合并、明确不合并、冲突取舍、验证结果、剩余风险”输出报告。
