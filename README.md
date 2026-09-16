<p align="center">
  <img src="docs/images/ems-scout-logo.png" width="220" alt="EMS Scout 标志" />
</p>

<h1 align="center">EMS Scout</h1>

<p align="center">
  <em>现场空调状态，统一采集、实时审计、可追溯。</em>
</p>

<p align="center">
  <a href="package.json"><img src="https://img.shields.io/badge/License-ISC-4c566a.svg" alt="ISC License" /></a>
  <a href="https://nodejs.org/"><img src="https://img.shields.io/badge/Runtime-Node.js-339933?logo=node.js&logoColor=white" alt="Node.js" /></a>
  <a href="https://learn.microsoft.com/windows/apps/winui/winui3/"><img src="https://img.shields.io/badge/UI-WinUI%203-0078D4?logo=windows&logoColor=white" alt="WinUI 3" /></a>
  <a href="https://www.sqlite.org/"><img src="https://img.shields.io/badge/Storage-SQLite-003B57?logo=sqlite&logoColor=white" alt="SQLite" /></a>
</p>

<p align="center">
  <a href="README.en.md">English</a> ·
  <a href="README.ja.md">日本語</a>
</p>

---

## 核心能力

| 现场采集 | 实时审计 | SQLite 历史 | Windows Native | Excel 导出 |
| --- | --- | --- | --- | --- |
| 6 栋楼 | 46 个实时点位 | 批次快照与恢复 | 数据管理与诊断 | 13 列分楼导出 |

## 工作流

```text
EMS 页面 → 枚举 → 质量校验 → SQLite → 实时审计 → Excel
```

## 实际软件截图

### 总览

<p align="center">
  <img src="docs/images/ems-scout-dashboard.png" alt="EMS Scout 总览" width="100%" />
</p>

### 数据管理

<p align="center">
  <img src="docs/images/ems-scout-data-management.png" alt="EMS Scout 数据管理" width="100%" />
</p>

### 关于

<p align="center">
  <img src="docs/images/ems-scout-about.png" alt="EMS Scout 关于" width="100%" />
</p>

## 项目结构

```text
.
├── src/                              # Node.js 采集、规则与历史核心
│   ├── enumerate.js                  # Playwright + Edge CDP 主枚举器
│   ├── enum-validator.js             # 枚举结果与设备身份校验
│   ├── rules.js                      # 状态、区域与数据质量规则
│   ├── realtime-quality.js           # 实时字段与原始值审计
│   └── data-history.js               # SQLite 历史批次与恢复
├── scripts/                          # 导入、采集、审计与现场校验脚本
├── native/
│   ├── src/EmsScout.Desktop/         # WinUI 3 原生界面
│   ├── src/EmsScout.Application/     # 用例、查询与业务契约
│   ├── src/EmsScout.Domain/          # 设备、楼栋与状态模型
│   ├── src/EmsScout.Infrastructure/ # SQLite、文件源与 Excel 导出
│   └── tools/EmsScout.ExportSmoke/   # Excel 导出烟测工具
├── data/                             # 本地楼栋数据归档（不纳入远端）
├── docs/                             # 架构、数据模型与软件截图
├── config/                           # 质量审计配置
├── README.md                         # 中文说明
├── README.en.md                      # English documentation
├── README.ja.md                      # 日本語ドキュメント
├── package.json                      # Node.js 脚本与依赖
└── CHANGELOG.md                      # 修改记录
```

## 文件说明

| 文件 | 作用 |
| --- | --- |
| [`src/enumerate.js`](src/enumerate.js) | 通过 Playwright、Edge CDP、SVG 与 Vue 数据枚举设备卡片。 |
| [`src/verify-live.js`](src/verify-live.js) | 对照 SQLite 核验当前浏览器实时状态。 |
| [`src/rules.js`](src/rules.js) | 判定开机、关机、离线、区域、楼栋身份与卡片质量。 |
| [`src/enum-validator.js`](src/enum-validator.js) | 校验楼栋范围、卡数、页面、重复设备与导入条件。 |
| [`src/realtime-quality.js`](src/realtime-quality.js) | 审计实时点位完整性、枚举值、温度范围与集控锁原始值。 |
| [`src/data-history.js`](src/data-history.js) | 管理 SQLite 历史批次、元数据与当前数据恢复。 |
| [`scripts/import.js`](scripts/import.js) | 将枚举 JSON 导入 SQLite。 |
| [`scripts/collect-realtime-all-batch.js`](scripts/collect-realtime-all-batch.js) | 协调六栋楼实时采集、进度与审计。 |
| [`scripts/collect-building-realtime-details.js`](scripts/collect-building-realtime-details.js) | 采集单栋楼设备实时详情。 |
| [`scripts/collect-building-realtime-batch.js`](scripts/collect-building-realtime-batch.js) | 执行单栋楼实时批次采集。 |
| [`scripts/realtime-browser.js`](scripts/realtime-browser.js) | 管理实时采集使用的 Edge 与 CDP 会话。 |
| [`scripts/quality-report.js`](scripts/quality-report.js) | 生成枚举质量报告。 |
| [`scripts/audit-realtime-data.js`](scripts/audit-realtime-data.js) | 审计实时数据与异常原始值。 |
| [`scripts/field-e2e.ps1`](scripts/field-e2e.ps1) | 使用隔离目录和临时数据库执行现场端到端校验。 |
| `native/src/EmsScout.Desktop` | 总览、采集、数据管理、审计、设置与诊断页面。 |
| `native/src/EmsScout.Infrastructure` | SQLite、实时快照、质量审计与 Excel 导出实现。 |
| `native/tests/EmsScout.Tests` | 原生应用、历史批次与导出契约测试。 |
| [`docs/architecture.md`](docs/architecture.md) | 分层架构与数据流说明。 |
| [`docs/data-model.md`](docs/data-model.md) | 数据库与 Excel 导出模型说明。 |
| [`CHANGELOG.md`](CHANGELOG.md) | 版本修改与现场验证记录。 |

## 致谢

- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) 与 WinUI 3
- [Node.js](https://nodejs.org/) 与 [Playwright](https://playwright.dev/)
- [better-sqlite3](https://github.com/WiseLibs/better-sqlite3)
- [SheetJS](https://sheetjs.com/)
- SmartPiEMS 现场系统及其验证环境

## 许可证

项目当前由 `package.json` 声明为 **ISC License**。仓库暂未包含独立的 `LICENSE` 文件。
