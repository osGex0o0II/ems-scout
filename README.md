# EMS Scout

EMS Scout 是面向 Windows 的 WinUI 3 空调运维工作台：使用 Node.js、Playwright 和 Edge CDP 采集 EMS 数据，经 JSON 校验、SQLite 入库后，在 Native 数据管理页筛选并导出 Excel。

## 当前产品入口

唯一用户界面是 `native/src/EmsScout.Desktop`。工作流为：

```text
Native 采集任务
  -> scripts/enumerate.js / src/enumerate.js
  -> enum_full_v5.json
  -> scripts/import.js
  -> SQLite
  -> Native 数据管理
  -> 导出当前筛选 Excel
```

Native 页面包括总览、采集任务、数据管理、审计中心、分组设置、系统设置和诊断。历史批次选择、刷新最新采集数据、质量审计和 13 列 Excel 导出都由 Native 页面提供。

## 运行

```powershell
cd D:\Code\Git\ems-scout
npm install
npm run native:run
```

分步采集和导入：

```powershell
node src/enumerate.js --edge
node scripts/validate-enum.js
node scripts/import.js
node scripts/quality-report.js
```

单栋采集：

```powershell
node src/enumerate.js --edge --bldg=1号
```

真实 EMS 现场验证只写隔离的 `out/field-e2e-*` 目录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/field-e2e.ps1 -Building 1号 -LaunchEdge
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/field-e2e.ps1 -Building 1号 -LaunchEdge -RunSingleBuilding
```

采集器和现场脚本使用本机回环 CDP、独立端口和独立 Edge Profile；默认会清理本次启动的浏览器与 Profile。不要把现场临时数据库的验证结果当作生产数据库验证。

## 开发验证

```powershell
npm run self-test
npm run native:build
npm run native:test
dotnet list native/EmsScout.Native.slnx package --vulnerable --include-transitive
npm audit
```

推荐通过 `npm run native:run` 启动应用，不要直接运行未打包的 Native exe。

## 目录

```text
src/
  enumerate.js       EMS/Edge CDP 主枚举器
  enum-validator.js  枚举结果校验
  rules.js           采集质量和区域规则
  data-history.js    SQLite 历史批次读写
scripts/
  import.js          JSON -> SQLite
  quality-report.js  数据质量审计
  audit-realtime-data.js  实时点位审计
  field-e2e.ps1      隔离现场端到端验证
native/src/
  EmsScout.Desktop        WinUI 3 产品界面
  EmsScout.Application    应用用例和页面契约
  EmsScout.Collection     Native 采集编排契约
  EmsScout.Domain         领域模型
  EmsScout.Infrastructure SQLite、文件源和 Excel 导出
native/tools/EmsScout.ExportSmoke/
  Native Excel 导出烟测 CLI
```

## 数据契约

生产导出固定为 `全部设备` 和按楼号子表，13 列：楼栋、座号、楼层、页面、设备名、区域、开关机状态、模式、风速、设置温度、环境温度、集控锁定状态、采集时间。生产数据库、历史归档和 `data/**` 不属于源代码清理范围。

## 日志

```powershell
node src/enumerate.js --edge --log-level=DEBUG --log-category=RULE,VUE --log-file
```

日志类别包括 `ENUM`、`QUALITY`、`RULE`、`VUE`、`CRASH` 和 `NET`。
