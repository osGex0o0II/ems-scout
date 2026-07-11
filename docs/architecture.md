# EMS Scout 架构

更新时间：2026-07-11

## 产品目标

EMS Scout 从 EMS 页面采集 6 栋楼的空调卡片，经版本化契约导入 SQLite，
在 WinUI 数据管理页筛选并导出当前筛选 Excel。当前唯一面向用户的导出入口是：

`数据管理 -> 导出当前筛选 Excel`

## 语言边界

| 技术 | 负责 | 不负责 |
|---|---|---|
| C# / .NET 10 | WinUI、契约校验、SQLite 迁移与导入、质量、对账、历史批次、筛选、Excel、错误分类、结构化日志 | EMS DOM/Shadow DOM 抓取 |
| Node.js | Playwright + Edge CDP 枚举、实时详情抓取、版本化 Sidecar 协议适配 | 产品 UI、数据库迁移、主质量规则、用户导出 |
| PowerShell | Windows x64 打包、隔离 Edge、现场 E2E 和生产文件守护 | 产品业务逻辑 |
| SQL | 版本化 schema 资源和查询 | 业务仓储运行时自建表 |

JavaScript 并不等同于 legacy。`src/enumerate.js` 和 Sidecar 仍是产品采集组件；
Node 导入、旧质量报告、Web/Electron/TUI 和多格式报表才属于受保护兼容路径。

## 主链路

```text
WinUI Collection Tasks
  -> NodeCollectionTaskRunner
  -> sidecar/runner.js (WorkflowEvent v1 NDJSON)
  -> sidecar/collect.js
  -> src/enumerate.js (Playwright + Edge CDP)
  -> CollectionSnapshot v1
  -> C# validate / migrate / transactional import
  -> SQLite ac.db
  -> native quality / realtime reconciliation
  -> Data Management filters
  -> devices worksheet (12 columns)
```

进程边界由 `contracts/` 中三份 v1 契约控制：

- `CollectionSnapshot v1`：采集结果和来源证据。
- `WorkflowEvent v1`：started/progress/action/terminal 事件。
- `WorkflowControl v1`：桌面应用向 Sidecar 发送取消命令。

Sidecar stdout 只能输出 WorkflowEvent NDJSON；人类日志写 stderr。一个工作流只能有一个
terminal 事件，取消不能被“缺 terminal”协议错误覆盖。

## 分层

```text
EmsScout.Desktop
  -> EmsScout.Application
  -> EmsScout.Domain
  -> EmsScout.Infrastructure

EmsScout.Infrastructure
  -> SQLite / migrations / import / export
  -> Sidecar process and environment probes
  -> errors and NDJSON logging
```

- `Domain`：设备与通讯状态等稳定业务模型。
- `Application`：用例契约、工作流、设置、错误和日志接口。
- `Infrastructure`：SQLite、文件、Excel、Sidecar 和操作系统适配。
- `Desktop`：WinUI 页面、绑定和用户交互，不直接实现采集协议或数据库结构。

## 数据库所有权

- 只有 `EmsScout.Infrastructure/Migrations` 可以执行 `CREATE TABLE` 或 `ALTER TABLE`。
- 仓储通过 `SqliteSchemaGuard` 拒绝不兼容 schema，不在用户操作中静默修补。
- 导入按所选楼栋事务替换；取消、校验失败或故障注入必须完整回滚。
- 相同 workflow/run key、artifact SHA 和当前数据内容重复导入时返回原批次，不新增数据。
- 采集任务开始时冻结数据目录快照，所有阶段使用同一组绝对路径。
- 测试不得通过 SQLite 打开生产 `out/ac.db`；需要 run17 基线时先按字节复制到临时目录。

## 质量与身份

- Node 与 C# 消费同一份 `tests/fixtures/quality/page-quality-v1.json` 质量契约。
- `qualityReason` 只是证据，原生审计必须根据卡片内容重新计算，不能盲目信任标签。
- `source_key` 表示来源位置，`device_uid` 表示稳定设备身份；歧义写入 ledger，不猜测绑定。
- 当前 run17 黄金清单位于 `tests/fixtures/run17/golden-v1.json`。

## 错误与日志

错误统一分为配置、环境、认证、契约、数据库、质量、采集、取消和内部错误。
用户界面显示稳定错误码、安全文案和建议操作；原始异常写入结构化日志。

原生日志写入：

`%LOCALAPPDATA%\EMS Scout\logs\native-YYYY-MM-DD.ndjson`

每条记录包含时间、级别、类别、事件、工作流、阶段、错误码、可重试性和异常信息。
用户目录、Bearer token、敏感查询参数和敏感数据键会被脱敏，超长字段会截断。

## 现场安全边界

- `scripts/field-e2e.ps1` 只能写唯一 `out/field-e2e-*` 目录。
- `-LaunchEdge` 使用随机 loopback CDP 端口和本次 runDir 独立 profile。
- 临时导入必须显式传 snapshot/DB 路径，且不得解析到生产 `out/ac.db`。
- 默认清理本次 Edge 和 profile；只有显式参数才能保留。
- 本地逻辑测试、临时 DB 烟测和静态脚本检查不能描述成真实 EMS 端到端通过。

## 外部验收门

以下工作必须在 Windows 或真实 EMS 环境完成：

1. WinUI XAML 编译、启动和绑定验证。
2. Windows x64 MSIX 构建、安装、升级、卸载和干净机启动。
3. 安装包内置 Node runtime 与 Sidecar payload 验证。
4. 已登录 EMS 的单栋现场 E2E。
5. 六栋全量 shadow parity。
6. 上述验收通过后，才可按 `docs/legacy-inventory.md` 删除受保护兼容路径。
