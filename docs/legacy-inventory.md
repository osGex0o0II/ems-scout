# Legacy Inventory And Removal Gates

Updated: 2026-07-13

This inventory prevents cleanup from deleting a field fallback or a file that is still bundled into the native product. `protected` means deletion is blocked until the stated external gate passes. `product` means the file is still part of the current native workflow even when its implementation originated in the Node application.

| Area | Files and entry points | Status | Current consumer | Native replacement | Removal gate |
|---|---|---|---|---|---|
| EMS enumeration engine | `src/enumerate.js`, `src/rules.js`, `src/logger.js` | product | `sidecar/collect.js` and packaged Sidecar | None; Node owns Playwright/Edge CDP collection | Replace only after a contract-compatible collector passes run17 and real-EMS parity |
| Collection protocol adapter | `sidecar/collect.js`, `snapshot-adapter.js`, `legacy-line-adapter.js`, `runner.js` | product | WinUI collection runner and field E2E | Versioned CollectionSnapshot/WorkflowEvent boundary | Remove legacy adapters only after the collector emits the versioned contracts directly |
| Realtime collection | `scripts/collect-realtime-all-batch.js`, `collect-building-realtime-batch.js`, `collect-building-realtime-details.js`, `realtime-browser.js`, `realtime-logger.js`, `audit-realtime-data.js` | product | `CollectionTaskViewModel` and packaged Sidecar | C# owns reconciliation and audit result consumption, not browser extraction | Keep until an equivalent versioned realtime Sidecar contract is implemented and field-tested |
| TUI collection fallback | `src/collect.js`, `src/tui/`, `AC-Scout.bat`, npm `collect` | protected | Manual emergency collection | Native Collection Tasks page | Windows installed-package collection and real-EMS single-building parity |
| Node SQLite import | `scripts/import.js`, `scripts/merge-legacy-databases.js`, `scripts/sqlite-snapshot.js`, `scripts/schema.sql`, npm self-test import path | protected | Legacy TUI and offline emergency recovery | `CollectionSnapshotImporter`, `EmsScout.DataTool`, versioned migrations | Native fresh/partial/full import parity on Windows and recovery procedure sign-off |
| Node quality report | `scripts/quality-report.js`, npm `legacy:quality` | protected, explicitly named | Emergency parity comparison and legacy self-test | `SqliteQualityAuditService`, DataTool `audit` | Native run17 golden parity plus real-EMS field parity |
| Legacy reports | `scripts/report.js`, `dump-aircons.js`, `dump-public.js`, `report-monitor.js`, `verify-reports.js` | protected, disabled by environment gate | Emergency historical output only | Data Management filtered Excel and native diagnostics | Stakeholder confirmation that TXT/Markdown/multi-format recovery is no longer required after installed-package acceptance |
| Web panel | `src/panel/`, `web/panel/`, `EMS-Panel.bat`, npm `legacy:panel` | protected, disabled by environment gate | Emergency diagnostics | Seven-page WinUI application | Clean Windows install acceptance and UI workflow parity |
| Electron shell and packaging | `electron/`, `scripts/electron-after-pack.js`, `restore-node-native.js`, npm `legacy:desktop`, `legacy:pack:*`, `legacy:dist:*` | protected, disabled by environment gate | Emergency legacy desktop packaging | WinUI MSIX package | Signed/test-signed Windows x64 MSIX install, launch, upgrade and uninstall acceptance |
| Legacy validators and diagnostics | `src/enum-validator.js`, `scripts/validate-enum.js`, `inspect-ems-source.js`, `reconcile.js`, `monitor-floors.js`, `dashboard.js`, `views.sql` | protected pending classification | Manual troubleshooting and old workflows | CollectionSnapshot validation, native audit/reconciliation/groups | Delete individually only after reference search, fixture coverage and troubleshooting replacement are documented |
| Archived schema fixtures | `data/1号楼`, `data/2号楼`, `tests/fixtures/schema-baselines` | protected evidence | Migration and WAL compatibility tests | No replacement; these are evidence | Retain while v0/v1 migration support exists |

Legacy Web panel 即使显式启用也只监听回环地址，并要求回环 `Host`、进程内随机会话令牌及同源写请求；JSON 请求体按原始字节限制为 1 MiB，静态文件解析后不得越出 `web/panel/`。这些是应急 fallback 的安全边界，不改变其默认禁用状态。

合并旧库时先运行 `node scripts/merge-legacy-databases.js --help`，并显式传入新的 `--target`；工具只审计稳定私有快照、按唯一卡片写历史摘要，且目标已存在或并发出现时拒绝替换。Legacy 自动浏览器的 `.edge_profile*` 会保留 EMS 登录态，仅在明确需要应急复用时使用；工作区移交或归档前应关闭对应 Edge 并删除这些 profile，不得随安装包或 CI artifact 分发。

## Deletion Rules

1. Product Sidecar files are not legacy merely because they are JavaScript.
2. No protected row may be deleted based only on macOS tests.
3. Before deletion, remove or replace every npm, batch, CI, packaging, documentation and runtime reference.
4. Add a regression test for the replacement before deleting the old path.
5. Production `out/ac.db`, WAL and SHM are never opened to prove a deletion safe.
