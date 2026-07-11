# EMS Scout

EMS Scout 是面向楼宇空调运维的 Windows 桌面工具。它通过 Edge CDP 和 Playwright
采集 6 栋楼的 EMS 空调卡片，经版本化契约导入 SQLite，在原生数据管理页完成筛选、
质量审计、实时对账和 Excel 导出。

当前唯一面向用户的导出入口：

`数据管理 -> 导出当前筛选 Excel`

## 当前状态

- 产品主干：C# / .NET 10 + WinUI 3。
- 浏览器采集：Node.js + Playwright + Edge CDP Sidecar。
- Windows 打包与现场验证：PowerShell。
- 跨平台逻辑重构已完成。
- Windows CI 已通过 XAML、干净克隆测试、Sidecar smoke 和 MSIX 构建。
- 安装后运行、升级/卸载、内置 Sidecar 实际采集和真实 EMS 仍需在 Windows 设备验收。

详见 [当前状态](docs/状态.md) 和 [架构说明](docs/architecture.md)。

## 快速开始

### Windows 干净克隆

```powershell
git clone git@github.com:osGex0o0II/ems-scout.git
Set-Location ems-scout
npm ci
dotnet restore native\EmsScout.Native.slnx -r win-x64
npm run native:test
npm run self-test
npm run native:run
```

完整环境要求、MSIX 和现场步骤见 [Windows 验证清单](docs/Windows验证清单.md)。

### Node/契约测试

```bash
node --test sidecar/test/*.test.js tests/architecture/*.test.js tests/contract-audit/*.test.js tests/enumeration/*.test.js tests/field-e2e/*.test.js tests/golden/*.test.js
npm run self-test
```

### Windows x64 打包

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\prepare-sidecar.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-native.ps1 -Configuration Release
```

输出只写入被 Git 忽略的 `artifacts/`。

### 真实 EMS 单栋验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\field-e2e.ps1 `
  -Building 1号 -LaunchEdge -RunSingleBuilding
```

现场验证只允许写唯一 `out\field-e2e-*` 目录，不得写生产 `out\ac.db`。

## 架构边界

```text
WinUI
  -> WorkflowEvent/WorkflowControl
  -> Node Sidecar -> Playwright/Edge CDP -> EMS
  -> CollectionSnapshot
  -> C# validate/migrate/import/quality
  -> SQLite
  -> data filters -> devices.xlsx
```

- C# 负责产品 UI、数据库生命周期、质量、对账、Excel、错误和日志。
- Node.js 只负责 EMS 浏览器采集和 Sidecar 协议适配。
- PowerShell 只负责 Windows 打包、隔离 Edge 和现场 E2E。
- 只有 `EmsScout.Infrastructure/Migrations` 可以修改 SQLite schema。

## 目录

| 路径 | 用途 |
|---|---|
| `native/` | WinUI 产品、C# 分层、原生工具和测试 |
| `sidecar/` | Node Sidecar 协议和进程适配 |
| `src/` | Playwright/Edge CDP 采集器及 legacy TUI |
| `contracts/` | CollectionSnapshot、WorkflowEvent、WorkflowControl v1 |
| `scripts/` | 打包、现场验证和受保护兼容工具 |
| `tests/` | 契约、架构、采集规则和黄金测试 |
| `docs/` | 规范、架构、状态、交接和迁移文档 |
| `data/` | 已归档的受保护迁移证据 |
| `out/` | 本机运行/生产证据，不进入 Git |

## 数据安全

- 测试不得通过 SQLite 打开生产 `out/ac.db`。
- 生产基线测试先按字节复制 DB/WAL 到系统临时目录。
- 不删除或提交 `data/1号楼`、`data/2号楼` 的 WAL/SHM 证据。
- 不提交 `out/`、`artifacts/`、`node_modules/`、`bin/`、`obj/` 或日志。
- 临时 DB/Excel smoke 不能描述成真实 EMS E2E 通过。

## 文档入口

- [项目规范](docs/项目规范.md)：命名、架构、代码、数据和 Git 规则。
- [Windows 验证清单](docs/Windows验证清单.md)：另一台 Windows 设备的执行顺序。
- [架构说明](docs/architecture.md)：语言、分层、数据与进程所有权。
- [当前状态](docs/状态.md)：当前基线和未完成外部门。
- [交接说明](docs/交接.md)：关键事实和常用命令。
- [兼容路径清单](docs/legacy-inventory.md)：protected legacy 删除条件。
- [Native README](native/README.md)：WinUI 和 C# 项目说明。
- [Sidecar README](sidecar/README.md)：Node Sidecar 协议说明。

## 命名

- 产品显示名：`EMS Scout`。
- C# 程序集、命名空间和技术标识：`EmsScout`。
- npm 包名：`emsscout`。
- `AC-Scout.bat` 仅保留为 legacy 兼容文件名。

开发和提交前请先阅读 [项目规范](docs/项目规范.md)。
