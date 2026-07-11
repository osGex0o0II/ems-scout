================================================================================
修改记录 — 2026-07-11 16:04
===============================================================================

一、Excel 与生产数据隔离

- Excel 测试增加工作表名、12 列标题与顺序、筛选行数、中文、XML 转义、公式文本、空值、`2.5F`/`B1F`/`BM`、空结果和 50,000 行上限。
- 新增 `ProductionDataSnapshot`，仓储、实时、对账和 Excel 基线测试只通过 SQLite 打开生产库的字节副本。
- 临时 DB ExportSmoke 与 `xlsx` 内容读取通过；生产 DB/WAL/SHM 哈希未变化。

二、采集任务拆分

- `CollectionTaskViewModel` 从 2011 行降到 1703 行。
- Node/Playwright/CDP 环境探测移到 `CollectionEnvironmentProbe`。
- NDJSON 进度解析移到 `CollectionProgressPresenter`，实时对账展示行移到独立文件。
- 新增架构守卫，禁止环境探测和进度解析回流 ViewModel。

三、错误与日志

- 新增 9 类应用错误、稳定错误码、安全用户文案、建议操作和可重试标记。
- Desktop 异常边界不再直接展示 `ex.Message`；Sidecar 非成功终态使用专用 `WorkflowExecutionException`。
- 新增原生 NDJSON logger，记录时间、级别、类别、事件、工作流、阶段、错误码、重试性和异常。
- 日志支持用户目录、Bearer token、敏感查询参数/字段脱敏，超长截断和并发写入；诊断页可发现原生日志。
- 修复 Windows Sidecar payload 漏打包枚举拆分模块的问题，smoke 现在实际加载全部新模块，并由架构测试守卫。

四、文档与验证

- 重写架构、状态、交接和上下文快照，删除 2026-06-07 的 6685 卡旧口径及多数派通讯推断。
- 当前通讯状态文档与 `IND_MAP` 一致：绿色关机、红色开机、灰色离线，未知不猜测。
- .NET 208/208、Node 73/73、`npm run self-test` 通过；Infrastructure 0 warning/0 error。
- macOS 仍不能执行 Windows XamlCompiler，WinUI/MSIX/真实 EMS 验收保持未完成。

================================================================================
================================================================================
修改记录 — 2026-07-11 13:50
===============================================================================

一、数据库结构所有权收口

- 删除 `SqliteAreaGroupRepository`、`SqliteDeviceAnnotationService`、`SqliteDeviceWatchRepository` 的运行时建表，以及 `SqliteCollectionRunRepository` 的运行时加列。
- 新增 `SqliteSchemaGuard`：未迁移数据库会明确失败并提示先运行版本迁移，不再由业务操作静默改变 schema。
- 新增架构测试，禁止 `EmsScout.Infrastructure/Migrations` 之外的 C# 出现 `CREATE TABLE` / `ALTER TABLE`。
- 修正此前运行时 `floor_catalog.floor_value REAL NOT NULL` 与 v1 迁移 `REAL` 不一致导致的结构分叉。

二、路径和时间语义修正

- 总览数据时间优先取最新 completed 采集批次的 `imported_at`/`completed_at`，无批次时才回退主库 mtime，避免 WAL 下显示旧时间。
- 采集任务开始后冻结数据目录，枚举、CollectionSnapshot、原生导入、质量和实时脚本使用同一绝对路径，避免运行中设置变化造成跨目录读写。
- 原生质量请求支持显式数据库路径；设置切换到已有 `ac.db` 前先运行版本迁移，迁移失败时不保存新目录。

三、验证

- .NET：160/160 通过。
- Node Sidecar、契约、架构、field-E2E 静态测试：40/40 通过。
- DataTool、SchemaTool、ExportSmoke Release：0 warning、0 error。
- `dotnet format --verify-no-changes` 通过，仅保留既有 workspace-load warning。

================================================================================
================================================================================
修改记录 — 2026-07-11 11:35
===============================================================================

一、产品与数据主干重构

- 确立 C#/.NET 10 为 WinUI、SQLite、质量、对账和 Excel 产品主干；Node 仅保留 Playwright/Edge CDP Sidecar。
- 新增 CollectionSnapshot、WorkflowEvent、WorkflowControl 三份 v1 契约，Sidecar stdout 使用严格 NDJSON，取消协议拥有唯一 terminal event。
- 新增 C# v0/v1/v2 SQLite 迁移、fresh DB 创建、稳定 device UID/source key、alias 与 ambiguity ledger；导入支持 shadow、显式 apply、事务回滚和用户数据保全。
- 原生质量审计取代 Node 基础质量主路径；WinUI 直接读取/导入 CollectionSnapshot，质量已连接 `SqliteQualityAuditService`。
- 默认数据目录迁至 LocalAppData；首次启动可从 legacy `out` 使用 SQLite Backup API 做 WAL-safe 迁移，不删除旧数据；系统设置提供显式选择旧 `out` 目录的迁移入口。

二、现场与发行链路

- `field-e2e.ps1` 改为 runner NDJSON -> collect -> CollectionSnapshot -> DataTool -> 原生质量 -> 临时 DB Excel 烟测，现场路径不再运行 Node import/quality/validate。
- 现场始终使用唯一 runDir、随机 loopback CDP、独立 Edge profile 和默认清理；生产 DB/WAL/SHM 仅做 metadata/SHA-256 守护。
- 新增 Windows x64 CI、Node Sidecar payload 打包、MSIX package 脚本和 Schema/Data CLI。

三、验证与边界

- Node 测试 35/35、.NET 测试 159/159、DataTool/SchemaTool Release 0 warning/0 error、NuGet vulnerability audit 0。
- run17 临时库 parity：6568 unique cards、373 pages、3 blocking issues、7 nonblocking known findings。
- 尚未在真实已登录 EMS 或干净 Windows 安装机上跑完整现场/安装包烟测；legacy 代码继续保留，不能据此声称删除条件已满足。

================================================================================
================================================================================
修改记录 — 2026-07-10 15:05
===============================================================================

一、全量采集失败根因与质量门修正

- 核查 2026-07-10 最近失败：六栋 6568 张唯一卡均已采到，失败来自最终质量门，而非楼栋/页面/卡片缺失。
- 修正采集重试已接受 `offline_template_stable`、最终审计却再次拒绝的规则矛盾。
- 新增 `device_anomalies_preserved`：仅允许卡名、通讯、indicator、开关完整，且连续 3 次稳定、每页最多 2 台且不超过 10% 的设备字段异常；占位符、重复塌缩、通讯缺失仍阻断。
- 删除宽泛的 `stable_partial` 放行。该规则曾把 3 台瞬态 indicator 缺失误当稳定页，导致生产数据退化。
- 2号 2.5F 两台 `2M001-KT` 按已核实 EMS 源缺陷处理：不借用邻卡 indicator，不推断 comm，精确记录为 `known_source_indicator_missing`；任何第三台缺 indicator 仍阻断。

二、最终生产数据

- 完成真实 EMS 六栋全量采集，并纠正重采 2号/4号后通过独立 JSON 校验与最终质量门。
- 最终 `out/enum_full_v5.json`：6568 张、142 子区、373 页。
- 最终 SQLite 历史批次：`run 17`，状态为 completed/full。
- 楼栋数量：1号 1493、2号 107、3号 1106、4号 1096、5号 286、6号 2480。
- 通讯状态：开机 1843、关机 3196、离线 1527、未知 2；合计 6568。
- 生产库导入前一致性备份：`out/ac_before_full_import_20260710_144208.db`。

三、统计、防误导与验证

- 历史 run、监控和原生分组统计改为只按 `comm` 计算开机/关机/离线；`switch=OFF` 不再掩盖未知通讯。
- `quality_report_run17` 已生成，仍如实阻断 3 类现场风险：4 个未登记离线模板页、10 台持续异常字段设备及其 10 个页面汇总；不是结构采集失败。
- `npm run self-test` 通过。
- 原生测试 107/107 通过。
- 数据管理唯一 Excel 导出烟测通过：单一 `devices` 工作表、12 列、6570 行（6568 库内卡 + 2 个已纳管虚拟设备）。

================================================================================
修改记录 — 2026-07-07 09:05
===============================================================================

一、现场 E2E `-LaunchEdge` 加固

- `scripts/field-e2e.ps1` 新增 `-LaunchEdge`：
  - 自动打开隔离 Edge，不杀用户已有 Edge。
  - 使用运行期随机本机 CDP 端口，不再默认复用 9222，避免误连已有浏览器会话。
  - 显式添加 `--remote-debugging-address=127.0.0.1`。
  - 使用本次 `out/field-e2e-*\.edge_profile` 独立 Profile。
  - 默认结束后只关闭本次 Profile 对应的 Edge 进程，并删除 Profile；如需保留，显式使用 `-KeepBrowser` / `-KeepProfile`。
- `src/enumerate.js` 新增 `--fail-if-not-logged-in`：
  - 现场脚本非交互 verify/采集时若未登录，退出码 3 快速失败。
  - 避免 WinUI/脚本场景进入 stdin 帮助菜单后卡住。
- `scripts/field-e2e.ps1` 的 EMS 标签页识别改为按 `$EmsUrl` host/path 判断，不再硬编码 `172.29.248.4`。

二、验证结果

- 对抗审查 Agent D 发现的风险已处理：随机端口、loopback 绑定、profile 生命周期清理、非交互登录失败。
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\field-e2e.ps1 -Building 1号 -LaunchEdge -LoginWaitSeconds 5` 实测：
  - 启动隔离 Edge，随机端口 `56518`。
  - EMS HTTP 200，CDP 可达。
  - 未登录时 `enumerate.js --fail-if-not-logged-in` 以退出码 3 快速失败。
  - 本次 Edge 进程已关闭，`.edge_profile` 已删除。
- `node --check src\enumerate.js` 通过。
- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false` 通过。
- `npm run self-test` 通过。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：60/60 通过。
- 真实 EMS 单栋采集仍需在打开的隔离 Edge 中完成登录后运行 `-LaunchEdge -RunSingleBuilding`。

================================================================================
修改记录 — 2026-07-07 08:50
===============================================================================

一、真实 EMS 现场 E2E 安全入口

- 新增 `scripts/field-e2e.ps1`：
  - 默认只做 EMS HTTP、Edge CDP、当前 EMS 页面/登录验证，不采集、不导入。
  - `-RunSingleBuilding` 才执行单栋真实链路：采集 → 临时 JSON → 临时 SQLite → 质量报告 → 数据管理 Excel 导出烟测。
  - 所有输出写入唯一 `out/field-e2e-YYYYMMDD_HHMMSS/`。
  - 硬性拒绝把 `EMS_DB_PATH` 解析到生产 `out/ac.db`，避免现场验证误清生产库。
- 新增 `native/tools/EmsScout.ExportSmoke/`：
  - 复用 `SqliteDeviceExportService`，对临时 DB 生成 `数据管理筛选结果_yyyyMMdd_HHmmss.xlsx`。
  - 验证 workbook ZIP 结构和 `summary/devices/filters` 三张表。
  - 默认不加载 watch repository，避免导出烟测对 DB 做非必要写入。

二、原生采集预检防误导

- `CollectionTaskViewModel` 预检拆分 Node 依赖、采集脚本、Edge CDP、EMS 登录态。
- Edge CDP 不再只凭 `/json/version` 暗示“可采集”，会读取 `/json/list` 查找 EMS 标签页。
- EMS 登录态显示为“未验证”，明确“CDP 可达不等于 EMS 已登录”。
- 启动枚举任务前，如果启用登录态检查但没有发现 EMS 标签页，直接阻止启动，避免 WinUI 进入无 stdin 的 Node 登录等待。
- `TasksPage` 文案调整为“打开 EMS 页面”“运行前检查”。

三、验证结果

- `dotnet restore native\EmsScout.Native.slnx` 通过。
- `dotnet build native\tools\EmsScout.ExportSmoke\EmsScout.ExportSmoke.csproj -c Debug --no-restore /p:UseSharedCompilation=false` 通过。
- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false` 通过。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：60/60 通过。
- `npm run self-test` 通过。
- `node scripts\validate-enum.js` 通过。
- `dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore` 通过，仍有既有工作区加载警告。
- `dotnet run --project native\tools\EmsScout.ExportSmoke\EmsScout.ExportSmoke.csproj ... --db=out\ac.db --building=1号 --area=公区` 通过，生成 201 行筛选 Excel。
- `scripts\field-e2e.ps1 -Building 1号` 在当前现场环境安全失败：EMS HTTP 200，CDP `127.0.0.1:9222` 连接被拒绝，脚本在采集/导入前退出。
- 离线后半链路验证通过：复制现有 JSON 到 `out/field-e2e-offline-*` → 临时 DB 导入 1号 → 临时质量报告 → 临时筛选 Excel 201 行。
- 真实 EMS 单栋采集仍未完成，阻塞点仍是缺少已登录且可通过 CDP 连接的 EMS 会话。

================================================================================
修改记录 — 2026-07-07 08:30
===============================================================================

一、真实 EMS 端到端稳定性对抗审查

- 开启 3 个只读对抗性审查 Agent，分别检查真实采集链路、原生 UI/Excel 导出、legacy 报表和旧 Web 入口。
- 结论：本地链路可验证，但真实 EMS 现场端到端仍被登录/CDP 前置阻断，不能声称已通过。

二、修复会误导进度的 P0 风险

- `src/tui/actions.js` 正确读取 `[ACTION]switch_to_cdp` / `[ACTION]return`，避免返回/切换模式后继续导入旧 JSON。
- `scripts/import.js` 把清理旧数据、插入新数据、更新时间戳、同步楼层目录、创建历史批次放入同一个 SQLite transaction，避免导入失败留下空库或半库。
- `src/enumerate.js` 的 CDP 模式现在也会检查 EMS 登录状态，避免 9222 可达但未登录时直接采集。
- `src/rules.js` 当前基线修正为：2号 107、5号 286、6号 2480。

三、收口旧导出和旧发行物误导入口

- `scripts/report.js`、`scripts/dump-aircons.js`、`scripts/dump-public.js` 默认禁用；应急 legacy 运行必须显式设置 `EMS_ENABLE_LEGACY_REPORTS=1`。
- `AC-Scout.bat` 改为采集 TUI 文案；`EMS-Panel.bat` 增加 legacy 警告。
- `package.json` 中 `panel` / `desktop` / Electron 打包命令全部改成 `legacy:*`；`enum:edge` 改为干净 CDP 全量枚举，追加模式另设 `enum:edge:append`。
- Electron legacy 产品名和快捷方式改为 `EMS Legacy Web Panel`。
- 旧报表输出已归档到 `out/legacy-report-archive/`。
- 旧 Electron 发行物已归档到 `out/legacy-electron-dist-archive/dist_20260707_082001/`，当前 `dist` 目录为空。

四、原生数据管理和诊断页补强

- 数据管理导出前自动同步当前筛选结果，避免屏幕结果和 Excel 导出条件不一致。
- 数据管理和诊断页最近 Excel 列表改用严格文件名 `数据管理筛选结果_yyyyMMdd_HHmmss.xlsx`。
- 诊断页日志尾部读取改为共享读取，运行中的日志被占用时也能预览。
- 诊断页 legacy 文案同步为旧 Web 和旧多格式报表默认禁用，不作为当前 UI 主入口。

五、真实 EMS 探测结果

- `http://172.29.248.4:8000/ui` 返回 HTTP 200。
- `http://127.0.0.1:9222/json/version` 连接被拒绝。
- `node src\enumerate.js --auto-launch --verify --log-level=DEBUG --log-category=ENUM,QUALITY,CRASH --log-file` 在 `out/verify-probe/` 下记录：自动启动 Edge 后 60 秒未检测到 EMS 登录状态，未进入真实页面卡片验证。
- 已清理本次自动启动的临时 Edge 进程；未清理用户已有 Edge。
- 因缺少已登录 EMS 会话，本轮不能标记“真实 EMS 端到端稳定性已通过”。

六、验证结果

- `node --check` 覆盖 TUI、导入、枚举、规则、legacy 报表脚本和 Electron legacy 入口脚本。
- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false`：0 警告，0 错误。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：60/60 通过。
- `npm run self-test` 通过。
- `node scripts\validate-enum.js` 通过。
- 临时库 `out/e2e_tmp_ac.db` 导入烟测通过：1号 1493、2号 107、3号 1106、4号 1096、5号 286、6号 2480，合计 6568。
- `dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore` 通过，仍有既有工作区加载警告。
- `node scripts\quality-report.js` 通过，剩余 2 个 P2 数据质量提示：未知通讯 3、非离线卡缺少 indicator 原图 3。
- 扫描确认默认入口不再包含 `npm run panel`、`npm run desktop`、旧 Electron 产品名字样、旧对账导出接口字样。
================================================================================
修改记录 — 2026-07-06 23:52
===============================================================================

一、当前原生程序可用性全面检查

- 已启动原生 WinUI 程序并通过 UI Automation 逐页导航，确认 7 个顶级页面可访问：
  - 总览：可读到优先处理、基础质量审计、实时点位审计、实时对账、当前口径、状态分布、楼栋状态。
  - 采集任务：可读到开始/停止、检查环境、打开 EMS、采集范围、高级参数、质量审计、实时审计、实时对账、历史批次。
  - 数据管理：可读到数据口径、楼栋/通讯/区域/区域组/快捷筛选、高级筛选、分页、最近 Excel、导出当前筛选 Excel、设备列表和详情。
  - 审计中心：可读到运行基础审计、运行实时审计、基础质量审计、实时点位审计、实时对账、历史批次恢复/删除。
  - 分组设置：可读到区域组、新建/保存/删除分组、楼层目录、保存/停用楼层、成员维护、关注设备、关注事件。
  - 系统设置：可读到连接/目录/导出目录、默认采集模式、最近 Excel 跟踪。
  - 诊断：可读到应用信息、路径检查、流程口径、日志查看、最近 Excel。
- UIA 快照输出：
  - `out/native-usability-总览.txt`：193 行。
  - `out/native-usability-采集任务.txt`：265 行。
  - `out/native-usability-数据管理.txt`：762 行。
  - `out/native-usability-审计中心.txt`：328 行。
  - `out/native-usability-分组设置.txt`：215 行。
  - `out/native-usability-系统设置.txt`：48 行。
  - `out/native-usability-诊断.txt`：231 行。
- `PrintWindow` 另存分组页截图：`out/native-usability-groups-printwindow.png`。截图捕获时分组列表仍处于加载态，最终可用性以加载完成后的 UIA 文本快照为准。

二、分组设置补齐“关注事件”集中视图

- 新增 `WatchIncidentRow`，把 `DeviceWatchIncident` 包装为列表可显示字段。
- 分组设置的关注设备区域新增：
  - `关注事件` 汇总。
  - 异常事件列表：设备、变化、时间、批次。
  - `查看异常` 按钮，跳转数据管理并带入设备名、区域组和 `关注异常` 筛选。
- 这不是恢复旧 Web 的楼层监控旧流程，而是把有价值的异常事件能力吸收到“自定义区域组 + 关注时间窗”的原生模型中。

三、旧 Web 面板功能对比结论

- 已覆盖并进入原生主流程：
  - 总览指标和风险提示。
  - 采集任务启动/停止、环境检查、日志、补采和实时详情高级参数。
  - 数据管理筛选、分页、设备详情、备注、标签、实时匹配覆盖。
  - 数据管理筛选后 Excel 导出，仍是唯一导出方式。
  - 区域组：按楼层、子区、设备添加/删除成员。
  - 楼层目录新增/停用。
  - 关注设备：按时间窗检测 ON/OFF 变化，数据管理筛选和 Excel 导出带证据。
  - 质量审计、实时点位审计、实时对账、历史批次恢复/删除。
  - 日志和路径诊断、最近 Excel 查看。
- 暂未原样恢复，但已有替代或建议吸收：
  - 旧 Web `monitored_floors` / `floor_monitor_snapshots` / `floor_monitor_events` 独立楼层监控流；当前建议继续用“分组设置 -> 关注设备 -> 关注事件”承接，不恢复旧表驱动的独立入口。
  - 旧 Web 顶部批次下拉的全局切换；原生已在数据管理提供历史批次只读预览，在审计中心提供恢复为当前数据，不建议做全局隐式切换。
  - 旧 Web 的“说明 / 关于”页面；原生已拆到诊断页和 README/CHANGELOG，不建议作为业务主入口。
- 明确不恢复到原生主流程：
  - `scripts/report.js` 一键多格式报表。
  - `scripts/dump-aircons.js` legacy Excel 明细脚本。
  - `scripts/dump-public.js` legacy TXT 公区清单脚本。
  - TXT / Markdown 导出。
  - 旧 Web 报表列表和文件下载入口。

四、修改文件

- `native/src/EmsScout.Desktop/ViewModels/WatchIncidentRow.cs`
- `native/src/EmsScout.Desktop/ViewModels/GroupsViewModel.cs`
- `native/src/EmsScout.Desktop/Pages/AreasPage.xaml`
- `native/src/EmsScout.Desktop/Pages/AreasPage.xaml.cs`
- `CHANGELOG.md`
- `.context-summary.md`

五、验证结果

- 联网核对 Microsoft Learn WinUI 控件建议：
  - NavigationView 适合作为多页顶层导航。
  - InfoBar 适合作为高可见但不打断流程的内联状态提示。
  - ListView/GridView 适合展示可绑定的数据集合。
  - DatePicker/TimePicker 适合关注时间窗的日期和时间选择。
- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false`：0 警告，0 错误。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：60/60 通过。
- `npm run self-test` 通过。
- `dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore` 通过，仍有既有工作区加载警告。
- 扫描和 UIA 均确认原生 UI 仍只有 `数据管理 -> 导出当前筛选 Excel` 作为用户导出路径。

================================================================================
修改记录 — 2026-07-06 23:36
===============================================================================

一、数据管理页面可用性整改

- 修复数据管理页布局错误：快捷统计 `GridView` 与主数据列表原本同在 `Grid.Row=3`，会发生视觉叠压；现主数据列表和详情面板移动到独立内容区。
- 筛选条件区从一条超宽固定列改为两行主筛选：
  - 第一行：数据口径、楼栋、通讯、区域、搜索。
  - 第二行：区域组、快捷筛选、结果摘要、分页。
- 高级筛选改为三行等分列，减少新增“数据口径”后横向挤压。
- 分页按钮加 `AutomationProperties.Name` 和 Tooltip，便于无障碍检查和运行态验证。
- 数据口径切换事件增加初始化保护，避免初次绑定时触发重复查询。

二、可用性与旧面板对比结论

- 当前原生主页面：总览、采集任务、数据管理、审计中心、分组设置、系统设置、诊断。
- 分组设置已具备：
  - 新建/删除自定义分组。
  - 保存/停用楼层目录。
  - 添加/删除楼层、子区、设备成员。
  - 保存/删除关注时间窗。
- 关注设备已具备：
  - 关注规则绑定自定义区域组。
  - 关注窗口内成员设备 ON/OFF 变化标记为异常。
  - 数据管理可按关注状态筛选，并在详情和 Excel 中输出关注证据。
- 旧 Web 面板中不建议恢复到原生 UI 的入口：
  - `scripts/report.js` 多格式报表。
  - `scripts/dump-aircons.js` legacy Excel 明细脚本。
  - `scripts/dump-public.js` legacy TXT 公区清单脚本。
  - Web 文件下载 / 旧报表列表入口。
- 旧 Web 面板中可后续吸收但不应原样迁移的能力：
  - 楼层监控事件流，可后续合并到分组设置/关注设备的事件视图。
  - 更深的实时采集参数，可继续放在采集任务高级参数，不设为默认主流程。

三、验证结果

- 联网核对 Microsoft Learn WinUI 控件建议：
  - NavigationView 继续作为多页顶层导航。
  - InfoBar 用作内联、不打断的状态提示。
  - ComboBox 用于数据口径和筛选项单选。
  - ListView/GridView 用于数据集合和指标集合。
- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false`：0 警告，0 错误。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：60/60 通过。
- `npm run self-test` 通过。
- `dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore` 通过，仍有既有工作区加载警告。
- 运行态 UI Automation 确认数据管理页可找到：数据口径、区域组、快捷筛选、高级筛选、导出当前筛选 Excel、上一页、下一页。
- `PrintWindow` 截图确认数据管理页主列表不再被快捷统计条遮挡：`out/native-data-page-window.png`。
- 扫描确认原生 UI 仍只有 `数据管理 -> 导出当前筛选 Excel`，未恢复 TXT、Markdown、一键三报表出口。

================================================================================
修改记录 — 2026-07-06 23:17
===============================================================================

一、数据管理新增历史批次只读预览

- 数据管理筛选区新增 `数据口径` 下拉：
  - `当前数据`：读取当前 SQLite 表，仍是唯一可导出口径。
  - `历史 #ID`：直接读取 `run_cards` / `run_pages` / `run_sub_areas` 历史快照，不需要先恢复历史批次。
- 历史口径下显示 InfoBar：明确当前为历史快照，只读，不修改当前 SQLite。
- 历史口径下禁用或阻止：
  - `导出当前筛选 Excel`
  - 保存备注
  - 添加/删除标签
  - 保存实时分类覆盖
  - 忽略实时重复行
- 历史口径仍复用数据管理筛选、分页、快捷指标和详情面板，避免通过“恢复为当前数据”才能查看历史造成误操作。

二、数据层

- `DeviceQuery` 新增 `RunId`。
- `SqliteDeviceReadRepository` 根据 `RunId` 自动切换当前表或历史 `run_*` 表：
  - 当前：`cards/pages/sub_areas`
  - 历史：`run_cards/run_pages/run_sub_areas`
- 历史快照不附加实时详情、不附加关注窗口状态、不读取当前备注/标签覆盖，保持只读历史事实。
- `LoadFilterOptionsAsync(DeviceQuery)` 支持历史口径筛选项。

三、验证结果

- 新增 `SqliteDeviceReadRepositoryTests.SearchesHistoricalRunSnapshotWithoutRestoringCurrentDatabase`。
- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false`：0 警告，0 错误。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：60/60 通过。
- `npm run self-test` 通过。
- `dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore` 通过，仍有既有工作区加载警告。
- 扫描确认原生 UI 仍只有 `数据管理 -> 导出当前筛选 Excel`，未恢复 TXT、Markdown、一键三报表出口。

================================================================================
修改记录 — 2026-07-06 22:55
===============================================================================

一、原生新增诊断页

- 左侧 NavigationView 新增 `诊断` 顶级页，补齐旧 Web 面板“说明 / 关于”和日志查看能力中仍有价值的部分。
- 诊断页包含：
  - 应用信息：应用名、版本、.NET runtime、当前进程。
  - 路径检查：工作区、数据目录、SQLite、`enum_full_v5.json`、`quality_report.json`、导出目录、设置文件。
  - 流程口径：明确当前主流程是 `采集任务 -> 数据管理 -> 导出当前筛选 Excel`。
  - 日志查看：列出最近枚举日志、任务日志、原生运行日志和桌面日志，并显示所选日志末尾内容。
  - 最近 Excel：只列出 `数据管理筛选结果_*.xlsx`，可打开所在位置。
- 诊断页不恢复旧多格式报表、TXT、Markdown、一键三报表或旧报表生成入口。

二、修改文件

- `native/src/EmsScout.Desktop/Pages/DiagnosticsPage.xaml`
- `native/src/EmsScout.Desktop/Pages/DiagnosticsPage.xaml.cs`
- `native/src/EmsScout.Desktop/ViewModels/DiagnosticsViewModel.cs`
- `native/src/EmsScout.Desktop/ViewModels/DiagnosticFileRow.cs`
- `native/src/EmsScout.Desktop/ViewModels/DiagnosticInfoRow.cs`
- `native/src/EmsScout.Desktop/MainWindow.xaml`
- `native/src/EmsScout.Desktop/MainWindow.xaml.cs`
- `native/src/EmsScout.Desktop/App.xaml.cs`
- `README.md`
- `native/README.md`

三、验证结果

- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false`：0 警告，0 错误。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：59/59 通过。
- `npm run self-test` 通过。

================================================================================
修改记录 — 2026-07-06 22:45
===============================================================================

一、原生 UI 可用性收口

- 采集任务页补充固定任务模式说明：固定模式会托管枚举、导入、审计和实时详情步骤，灰色 Toggle 只是流程预览；切换到“自定义流程”后才允许手动组合。
- 分组设置页新增加载态和空态，分组命中数量计算期间显示 ProgressRing 和“正在计算分组命中数量”，避免左侧列表短暂空白误导用户。
- 审计中心基础质量问题列表调整为“级别 / 编码”两行显示，避免 `unknown_comm`、`missing_indicator` 等编码在窄列里被硬拆行。
- 审计中心顶部指标卡高度从 96 提到 112，降低卡片内容下缘裁切概率。

二、运行复查

- 原生应用已重启并复查三处页面：
  - `out/native-fix2-tasks.png`
  - `out/native-fix2-audit.png`
  - `out/native-fix2-groups.png`
- UI Automation 文本确认：
  - 采集任务页新增“当前任务模式会自动决定枚举、导入、审计和实时详情步骤；灰色开关表示由模式托管。”
  - 审计中心出现“级别 / 编码”，并能读取 `unknown_comm`、`missing_indicator`。
  - 分组设置页仍可见保存楼层、添加成员、关注设备和关注窗口说明。
- 采集任务页任务模式/楼栋选择在自动环境检查期间会短暂禁用；检查完成后恢复可编辑，已用 Automation 复核。

三、验证结果

- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false`：0 警告，0 错误。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：59/59 通过。
- `npm run self-test` 通过。
- `dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore` 通过，仍有既有工作区加载警告。

================================================================================
修改记录 — 2026-07-06 22:25
===============================================================================

一、采集任务新增明确任务模式

- 原生采集任务页新增“任务模式”下拉，避免用户靠多个 Toggle 猜测实际会执行什么。
- 模式包含：
  - 完整采集：枚举、校验 JSON、导入 SQLite、基础审计、实时详情、实时点位审计。
  - 采集并导入：枚举、校验 JSON、导入 SQLite、基础审计。
  - 仅枚举 JSON。
  - 仅校验 JSON。
  - 仅导入 SQLite。
  - 仅基础审计。
  - 仅实时详情。
  - 仅实时审计。
  - 自定义流程。
- 非自定义模式下流程 Toggle 置灰，只作为执行链预览；自定义流程才允许手动组合。
- `CollectionTaskModeCatalog` 放到 Application 层，Desktop ViewModel 只消费执行计划。
- 采集链补回显式 `scripts/validate-enum.js` 步骤；导入脚本自身仍保留内置校验防线。

二、数据管理加载态可用性修正

- 数据管理页加载中新增覆盖层，显示“正在查询 SQLite 数据”，避免列表空白和详情“未选择”误导用户。
- 加载完成状态改为明确显示筛选结果数量，例如“已读取当前筛选结果：6,570 台设备”。
- 空态只在加载结束且结果为 0 时显示。

三、验证结果

- 新增 `CollectionTaskModeCatalogTests`，覆盖完整采集、仅校验、仅导入、仅实时详情和自定义流程执行计划。
- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false`：0 警告，0 错误。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：59/59 通过。
- `npm run self-test` 通过。
- `dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore` 通过，仍有既有工作区加载警告。
- 原生应用已启动并截图核验：
  - `out/native_tasks_mode_verified.png`
  - `out/native_data_loaded_after_modes.png`
- 搜索确认原生端仍未恢复 TXT、Markdown、一键三报表、`dump-public`、`dump-aircons` 等旧导出入口。

四、仍未宣称完成的事项

- 旧 Web 面板的“监控楼层事件流”尚未作为原生独立页面恢复；当前用“关注设备”覆盖主要异常关注场景。
- 还需要继续做实际窗口下各页面密度、按钮状态和空态细节审查。

================================================================================
修改记录 — 2026-07-06 22:35
===============================================================================

一、首页新增一屏风险汇总

- 原生总览页新增“优先处理”区域，合并显示当前数据、关注设备、基础质量审计、实时点位审计、实时对账和历史批次风险。
- 可直接定位的数据类风险会跳转到数据管理并套用筛选：未知/离线、需排查、缺实时、详情异常、点位异常、关注异常。
- `DashboardRiskBuilder` 放在 Application 层，避免把风险规则写死在 UI 后台代码中。

二、审计中心补直接运行审计入口

- 审计中心顶部新增 `运行基础审计`，执行 `scripts/quality-report.js`。
- 审计中心顶部新增 `运行实时审计`，执行 `scripts/audit-realtime-data.js`。
- 审计命令复用 `NodeCollectionTaskRunner` 和数据目录环境变量，完成后自动刷新审计中心。
- 脚本输出通过 UI 线程调度更新状态，避免后台进程输出触发绑定线程问题。

三、文档入口降噪

- README 快速开始改为原生应用 `npm run native:run`。
- 明确旧 Web 面板、`scripts/report.js`、`dump-aircons.js`、`dump-public.js` 只作为 legacy 工具保留，不作为当前 UI 主流程入口。
- 再次确认当前用户导出方式仍唯一：原生 `数据管理 -> 导出当前筛选 Excel`。

四、验证结果

- 新增 `DashboardRiskBuilderTests`，覆盖关注异常、质量问题、实时异常、对账差异、异常批次和无风险成功态。
- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false`：0 警告，0 错误。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：54/54 通过。
- `npm run self-test` 通过。
- `dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore` 通过，仍有既有工作区加载警告。
- 原生 `native/src` 扫描只发现 `数据管理筛选结果_*.xlsx` 和 `导出当前筛选 Excel`，未接回 TXT / Markdown / 一键三报表出口。

================================================================================
修改记录 — 2026-07-06 21:45
===============================================================================

一、分组设置新增楼层目录维护

- 原生分组设置页新增“楼层目录”区域：
  - 按楼栋查看楼层目录。
  - 手动新增楼层，例如 `1F` / `B1F` / `2.5F`。
  - 维护备注。
  - 停用楼层目录，停用前弹出 WinUI `ContentDialog` 确认。
- 成员维护里的“楼层”下拉改为读取 `floor_catalog`，不再只依赖当前已采集设备筛选项。
- 采集发现的楼层会自动同步到 `floor_catalog`；手动新增楼层可立即用于“整层”成员加入自定义分组。
- 楼层目录楼栋与成员维护楼栋保持联动，避免新增楼层后出现在另一个楼栋上下文。
- 停用楼层只影响后续下拉选择，不删除已有分组成员和设备数据。

二、SQLite 仓储补齐 floor_catalog

- `IAreaGroupRepository` 新增：
  - `LoadFloorsAsync`
  - `SaveFloorAsync`
  - `DeleteFloorAsync`
- `SqliteAreaGroupRepository` 新增 `floor_catalog` 建表、唯一索引、当前采集楼层同步、手动保存、软删除停用。
- 新增 `FloorCatalogRecord` / `FloorCatalogEdit` 应用模型和 `FloorCatalogRow` 桌面行模型。

三、验证结果

- 新增测试 `MaintainsFloorCatalogForManualGrouping`，覆盖采集楼层同步、手动楼层保存、停用后默认隐藏、`includeDisabled` 可见。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：52/52 通过。
- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false`：0 警告，0 错误。
- `npm run self-test` 通过。
- `dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore` 通过，仍有既有工作区加载警告。
- 搜索确认原生端仍未恢复 TXT、Markdown、一键三报表、`dump-public`、`dump-aircons` 等旧导出入口。

================================================================================
修改记录 — 2026-07-06 21:15
===============================================================================

一、数据管理快捷筛选可用化

- `DataFacetItem` 新增 `FilterKind` / `FilterValue`，顶部统计卡不再只是展示数字。
- 数据管理页统计卡启用 `ItemClick`：
  - 全部：清除结果类筛选，保留楼栋/搜索等上下文。
  - 开机 / 关机 / 离线：应用通讯状态筛选。
  - 需排查 / 温度异常 / 集控锁定：应用快捷筛选。
  - 公区 / 非公区：应用区域筛选。
  - 实时详情 / 缺实时 / 手工覆盖 / 虚拟纳管：应用实时匹配筛选。
  - 点位完整 / 点位异常：应用点位筛选。
  - 已关注 / 关注异常：应用关注设备筛选。
- 快捷筛选应用后刷新列表，并在状态栏显示“已应用快捷筛选”。

二、关注设备导出验证

- 新增测试 `WatchAbnormalRowsAreExportedWithEvidence`。
- 覆盖关注规则时间窗内设备开关变化：
  - `WatchState=abnormal` 查询只导出异常设备。
  - Excel `devices` sheet 包含 `watch_state`、`watch_window`、`watch_evidence`。
  - 导出证据包含变更前后批次编号。

三、验证结果

- 联网核实官方 WinUI `ListView/GridView` 交互：`IsItemClickEnabled=true` + `SelectionMode=None` + `ItemClick` 适合统计卡快捷动作。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：51/51 通过。
- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false`：0 警告，0 错误。
- `npm run self-test` 通过。
- `dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore` 通过，仍有既有工作区加载警告。
- 搜索确认原生端仍未恢复 TXT、Markdown、一键三报表、`dump-public`、`dump-aircons` 等旧导出入口。

================================================================================
修改记录 — 2026-07-06 20:45
===============================================================================

一、原生审计中心独立化

- 新增 `AuditPage.xaml` / `AuditPage.xaml.cs` / `AuditViewModel.cs`。
- 主导航新增“审计中心”，位于“数据管理”和“分组设置”之间。
- 审计中心集中展示：
  - 基础质量审计摘要与问题列表。
  - 实时点位审计摘要、异常分类、楼栋明细。
  - 实时源对账筛选、差异列表、规则证据与定位到数据管理。
  - 历史批次列表、异常隔离、取消异常、恢复为当前数据、删除批次。
- 历史批次“恢复为当前数据”和“删除批次”保留 WinUI `ContentDialog` 二次确认。
- 本次没有新增任何导出入口；Excel 仍只允许从数据管理页“导出当前筛选 Excel”触发。

二、工程审查修正

- `QualityAuditIssueRow` 与 `RealtimeQualityCategoryRow` 新增原始 `Count` 属性。
- `AuditViewModel` 指标汇总改用原始数值，不再从本地化格式字符串反解析数字。
- `App.xaml.cs` 注册 `AuditViewModel`，`MainWindow` 接入审计中心页面路由。

三、验证结果

- 联网核实官方 WinUI 建议：`NavigationView` 适合顶层导航；页面内确认操作使用设置 `XamlRoot` 的 `ContentDialog`。
- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false`：0 警告，0 错误。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：50/50 通过。
- `npm run self-test` 通过。
- `dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore` 通过，仍有既有工作区加载警告。
- `scripts\run-native.ps1 -NoBuild` 可拉起原生进程，窗口标题 `EMS 空调控制台`。
- 搜索确认原生端仍未恢复 TXT、Markdown、一键三报表、`dump-public`、`dump-aircons` 等旧导出入口。

================================================================================
修改记录 — 2026-07-06 11:20
===============================================================================

一、原生分组设置可编辑化

- `App.xaml.cs` 注册 `IAreaGroupRepository`，修复分组设置页依赖未注册导致的运行期失败。
- `AreasPage.xaml` 从只读说明页改为自定义区域组维护页：
  - 区域组列表。
  - 详情统计。
  - 保存 / 删除自定义区域组。
  - 按楼层、子区、设备添加成员。
  - 删除成员。
  - 跳转数据管理查看筛选结果。
- 删除“只展示已落地分组 / 自定义维护不作为当前进度展示”的误导文案。
- 系统组保持只读，自定义组可维护。

二、分组筛选接入数据管理导出

- `MonitorGroupIds` 已进入数据管理查询条件。
- 从分组设置跳转到数据管理后，`导出当前筛选 Excel` 使用同一分组筛选口径。
- Excel 的 `summary` / `filters` sheet 新增分组筛选来源字段，避免导出文件无法追溯筛选口径。
- 未恢复旧 `scripts/report.js` 多格式报表、TXT、Markdown、一键三报表等旧出口。

三、运行方式修正

- 直接运行 `bin\...\EmsScout.Desktop.exe` 会因为 Windows App SDK Runtime 缺少包身份触发 `REGDB_E_CLASSNOTREG`，不作为验证方式。
- 新增 `scripts/run-native.ps1` 与 `npm run native:run`，统一通过 MSIX package profile 启动原生应用。
- `launchSettings.json` 移除 Unpackaged profile，避免误用裸 exe。

四、验证结果

- `dotnet build native\EmsScout.Native.slnx -c Debug --no-restore /p:UseSharedCompilation=false`：0 警告，0 错误。
- `dotnet test native\tests\EmsScout.Tests\EmsScout.Tests.csproj -c Debug --no-restore /p:UseSharedCompilation=false`：43/43 通过。
- `dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore` 通过。
- `npm run self-test` 通过。
- `node --check src\enumerate.js` / `node --check src\panel\server.js` 通过。
- `npm run native:run` / `scripts\run-native.ps1 -NoBuild` 可拉起包身份原生进程，当前应用处于响应状态。
================================================================================
修改记录 — 2026-06-22 13:40
===============================================================================

一、等待策略收敛

- `src/enumerate.js` 新增 `stablePartialState()` 与 `buildPartialSignature()`。
- `adaptivePolling()` 和 `qualityCheckWithProgressiveRetry()` 现在会把“页面已稳定，但仅少量字段长期缺失”的情况
  视为可继续前进，而不是一律等满 45 秒。
- 目标是避免页面已经可见后仍被通用质量门卡住；不改报表口径，不改 DB 结构。

二、验证结果

- 全量重采完成：`6568 cards, 142 sub-areas across 6 buildings`。
- `npm run verify:reports -- --dir=out/report-full-p2` 通过。
- `npm run self-test` 通过。
- 质量报告恢复为 `issue_count=0`，`duplicate_rendered_pages=1`，`uniform_resolved_pages=5`。
- 真实页面串行核对通过：4号 1F、2号 2.5F、6号 7F 一页。

三、残留现象

- `2号 2.5F` 仍保留 3 组同页重复渲染，这是 EMS 页面自身现象，入库已按卡名去重并在报表中标注。
- 少量实时温度与 live 页面存在 0.1~0.2 的浮动差异，属于现场状态变化，不是采集结构错误。

================================================================================
修改记录 — 2026-06-22 12:50
===============================================================================

一、P2 后全流程审计

- 复跑全流程：`node src/enumerate.js --edge --log-file` → `node scripts/import.js` →
  `node scripts/quality-report.js` → `node scripts/report.js --type=all --type=on --type=off --format=md --format=xlsx --format=txt --out=out/report-full-p2`。
- 结果：`6568 cards, 142 sub-areas across 6 buildings`。
- `npm run verify:reports -- --dir=out/report-full-p2` 通过，`npm run self-test` 通过。

二、真实页面对比

- 对比改为串行执行，避免多个 `verify-live.js` 同时抢同一个 Edge CDP 页面造成串台。
- `4号 1F`：楼栋/楼层/卡名一致，存在 2 个室内温度的实时浮动差异。
- `1号 13F --page=三页`：页内卡名与 DB 一致，差异集中在少量实时温度浮动。
- `2号 2.5F`：页内卡名、重复渲染元数据、字段值一致。
- `6号 7F --page=一页`：页内卡名一致，存在 1 个室内温度实时浮动差异。

三、恢复加固

- `src/enumerate.js` 新增 `clickMenuReady(page, menuMatch, opts)`，菜单点击后自动补注入并等待 `window.__ems` 就绪。
- `waitForReady()` 在检测到 `window.__ems` 缺失时自动重注入。
- 原始子区扫描和重扫路径增加空 helper 容错，修复楼栋切换后 helper 丢失导致的中途崩溃。

四、审计结论

- P0/P1/P2 报表修改全部通过验证。
- 真实页面对比确认采集结构正确，2号 2.5F 的同页重复卡已被准确标注。
- 多页楼层若不指定 `--page`，`verify-live.js` 会按整层口径展示 `dbNotLive`，这是预期行为，不是漏采。

================================================================================
修改记录 — 2026-06-22 12:25
===============================================================================

一、报表 P2 优化

- `scripts/report.js` 新增 `风险明细` 和 `异常明细` 两个 XLSX sheet，均带筛选和列宽，便于直接筛查处置对象。
- MD/TXT/XLSX 摘要新增风险分布和异常分布，补足 P1 只有 Top 列表、缺少全局分布入口的问题。
- `风险明细` 输出风险等级、风险分、风险原因、楼栋/楼层/子区/设备名、通讯、模式、温度、风速和备注。
- `异常明细` 输出异常标签、设备定位、开关/通讯、风险等级/风险分和重复渲染备注。
- `scripts/verify-reports.js` 增强：校验 `风险明细` / `异常明细` sheet、关键列、筛选、列宽，以及 `报表说明` 中的风险/异常分布段。

二、验证结果

- 新报表路径：`out/report-p2`，共 9 个文件。
- `npm run verify:reports -- --dir=out/report-p2` 通过，DB total=6568，ON=1878，notON=4690，duplicate rendered pages=1。
- `npm run self-test` 通过。
- `node --check scripts/report.js scripts/verify-reports.js` 通过。
- 抽样确认：未关闭 XLSX 包含 `汇总 | 报表说明 | 风险明细 | 异常明细 | 1号...6号`；`风险明细` 1042 行，`异常明细` 7 行。

备注：本轮仍只改报表层展示和验证，不改变采集逻辑、SQLite 结构和统计口径。

================================================================================
修改记录 — 2026-06-22 12:08
===============================================================================

一、报表 P1 优化

- `scripts/report.js` 增加开机风险评分：公区开机、低设定温度、高风速、开机但离线、重复渲染等因素累加，仅用于处置优先级排序。
- MD/TXT/XLSX 的摘要区新增“开机风险 Top / 楼层开机 Top / 座区统计 / 异常标签说明”。
- 未关闭空调 XLSX 明细新增 `风险等级`、`风险分`、`风险原因` 三列，并在公区/非公区分组内按风险优先排序。
- 异常标签统一补齐：通讯未知、开关未知、缺 indicator、通讯离线、开机但离线、模式未知、温度异常。
- `scripts/verify-reports.js` 增强：校验 P1 摘要段、状态类 XLSX 风险列、原有统计口径和重复渲染标注。

二、验证结果

- 新报表路径：`out/report-p1`，共 9 个文件。
- `npm run verify:reports -- --dir=out/report-p1` 通过，DB total=6568，ON=1878，notON=4690，duplicate rendered pages=1。
- `npm run self-test` 通过。
- `node --check scripts/report.js scripts/verify-reports.js` 通过。
- 抽样确认：未关闭 TXT 首页已展示风险 Top、楼层开机 Top、座区统计；未关闭 XLSX 已包含风险列。

备注：本轮只改报表层展示和验证，不改变采集逻辑、SQLite 结构和统计口径。

================================================================================
修改记录 — 2026-06-22 11:45
===============================================================================

一、报表 P0 优化

- 设备名排序改为自然排序，修复 `DXBCGQ-1, DXBCGQ-10, DXBCGQ-2` 这类字典序问题。
- MD 报表首页新增“报表摘要 / 楼栋汇总 / 数据质量说明 / 口径说明”。
- TXT 报表头部新增状态汇总、楼栋汇总、重复渲染页集中说明和口径说明。
- XLSX 报表新增 `报表说明` sheet，集中展示生成时间、状态汇总、楼栋基准、重复渲染页和口径说明。
- XLSX 总清单明细 sheet 增加筛选和列宽；状态报表和汇总 sheet 同步校验筛选/列宽。
- `scripts/verify-reports.js` 增强：校验 `报表说明`、筛选、列宽和重复页说明。

二、验证结果

- 新报表路径：`out/report-p0`，共 9 个文件。
- `npm run verify:reports -- --dir=out/report-p0` 通过。
- `npm run self-test` 通过。
- `node --check scripts/report.js scripts/verify-reports.js` 通过。
- 抽样确认：总清单 MD/TXT 均含摘要和重复页说明；XLSX 设备名自然排序生效。

备注：当前 `xlsx` 依赖能稳定写入筛选和列宽，但不会写出 Excel 冻结窗格 XML；本轮不引入新依赖，冻结窗格暂不纳入 P0 落地。

================================================================================
修改记录 — 2026-06-22 11:30
===============================================================================

一、第二阶段：采集稳定性加固

- 根因：质量判定只看温度/开关等局部字段，`comm`/`indicator` 晚到时可能被提前接受。
- 实测问题：4号 1F `4-1F-KT1-104` 曾写入 `comm='-'`、`indicator=''`；真实页面复核为
  `ON/开机/56f45bb314d74cc8da6c6c8e5942d08d.png`。
- 修正：`checkCardQuality(cards, meta)` 要求所有卡片通讯状态解析为 `开机/关机/离线`，
  同时在详情中输出 `comm=x/n`、`ind=x/n`。
- 修正：等待链增加通讯完整性检查，`WAIT_CARDS` 在 `comm < count` 时继续等待。
- 修正：`adaptivePolling`/深等待 fallback 不再因“有真实温度”单独放行，必须通过完整质量规则。
- 修正：新增重复塌缩检测，`rawCount >= 3` 且 `uniqueCount <= rawCount/2` 时标记
  `duplicate-collapse` 并拒绝该页，避免页面 stale 时 `raw=20 unique=1` 被当作有效采集。
- 自测：补充真实混合页、缺通讯/indicator、重复塌缩页、轻微重复渲染页用例。

二、验证结果

- 4号重采：1096/1096 卡，无重复塌缩页；导入后 `4-1F-KT1-104` 为 `ON/开机/红色 indicator`。
- 2号重采：107/107 卡；2.5F 保持真实重复渲染元数据 `raw=10 unique=7`。
- 质量报告：总卡数 6568，问题项 0，未知通讯 0，缺失 indicator 0。
- 报表验证：`out/report-stage2-final` 共 9 个文件，`verify:reports` 通过；重复渲染页标注命中 11 处。
- 真实页面核验：4号 1F live/DB 均为 13 张，live-only=0、DB-only=0；2号 2.5F live/DB 均为
  `raw=10 unique=7`，重复设备为 `2-2BC-GQ-KT-1/2/3`。

================================================================================
修改记录 — 2026-06-17 18:10
===============================================================================

一、分页漏采修复

- 根因：`collectPage()` 进入“无分页”分支后，等待稳定再重扫按钮时只检查编号页
  `一页/二页/...`，没有检查动态分页按钮 `下页`。
- 影响：3号/4号动态分页楼层若 `下页` 延迟渲染，会被写成 `default:20`，
  少采第二页 20 张。
- 修正：无分页重扫后同时检查 `reBtns['下页']` 和编号页；任一出现则重新进入
  `collectPage(prefix)`，走动态或标准分页路径。
- 诊断：新增 `PAGE_BTNS initial/rescan` DEBUG 日志，便于定位按钮是否迟到。

二、同页重复卡修复

- 根因：`extractCards()` 的 group layout 有同名去重，grid layout 没有；2号 2.5F
  `2-2BC-GQ-KT-1/2/3` 各重复一次。
- 修正：grid layout 同页按卡名去重；`scripts/import.js` 入库前再按 page 内卡名兜底去重。
- 基准：2号楼基准从 110 调整为 107，表示唯一卡数；旧 DB 若仍含重复会显示 `+3`
  并由质量报告列出重复样例。

三、质量审计增强

- 新增 `duplicate_cards_same_page`：同一 building/sub_area/page/card.name 重复。
- 新增 `empty_sub_areas`：非 inline 的空子区。
- 新增 `inline_sub_area` INFO：6号 BM 通过 A座 1F 的 BM page 采集，空 BM 子区不再被当作漏采。
- TUI 质量摘要同步显示重复卡/空子区。

================================================================================
修改记录 — 2026-06-17 17:50
===============================================================================

一、根因分析：对全量数据做重复检测 → 发现 11 页共 117 卡数据完全重复

通过 SQLite 查询确认：
- 6号 7F B座 一页/二页（40卡）为 uniformTemplate 默认值 `0/0/中/制冷`
- 3号/4号 2F 各 2 卡 DTT 为 uniformTemplate 默认值 `26/25/中/制冷`
- 5号 1F/2F 各 2 卡 WSJ 为 uniformTemplate 默认值 `0/0/中/制冷`
- 1号 8F 一页、6号 2F/3F 离线页面为真正的全离线数据（正常）

二、行业方案联网核查

对比 BACnet/BMS 标准和 Playwright 数据采集最佳实践：

| 问题 | 行业方案 | 来源 |
|------|---------|------|
| 模板数据误判为真实加载 | BACnet "Reliability Property" + "Stale Timer" — 每个点标记可靠性，永不混同模板与健康值 | ASHRAE 135、Johnson Controls、BTL |
| 等数据而非等网/等时间 | "Needle Test" — 等具体字段非空（如温度>0），不等网络空闲或固定延时 | Playwright 官方、Brightdata、webscraper.cloud |
| page.evaluate 抛异常无保护 | "两层 try-catch" — 内层 catch JS 异常，外层 catch navigation 导致的 context destroyed | Playwright 官方文档、GitHub #27374 |
| 全离线数据的合理放行 | BACnet "stale is a valid state" — Stale Timer 内值不变即记为不可靠 | Johnson Controls FX Workbench、Beckhoff |
| 2卡页面模板漏检 | BACnet 模板建模 — 模板可指向任意数量设备，不依赖数量 | RFC 3512 BLDG-HVAC-MIB |

三、修正方案

本次修改对标行业标准：

| 修正 | 文件 | 行 | 行业对标 |
|------|------|-----|---------|
| `uniformValues` 从 `n >= 3` 改为 `n >= 2` | `src/rules.js:82` | BACnet: 模板检测不依赖数量 |
| 稳定模板提前退出 → `hasRealTemp` 内容级等待 | `src/enumerate.js:adaptivePolling` | Needle Test: 等真实温度 >0 出现 |
| 全离线卡快速放行（comm='离线'） | `src/enumerate.js:adaptivePolling` | BACnet: stale is a valid state |
| `qualityCheckWithProgressiveRetry` 失败后追加 10 轮 WS 等待循环（5 处） | `src/enumerate.js` | 指数退避替代固定重试 |
| 5处 fallback 循环补 `__ems` crash recovery（4处新增） | `src/enumerate.js` | Playwright: 两层 try-catch |
| 3 处 `adaptivePolling` 的 `extractCards` 加 `.catch()` | `src/enumerate.js` | Playwright: always catch evaluate |

四、修正详情

### Fix 1：`checkCardQuality` `uniformValues` 覆盖 2 卡页面

- 文件：`src/rules.js:82`
- 改前：`const uniformValues = n >= 3 && ...`
- 改后：`const uniformValues = n >= 2 && ...`
- 原因：2 卡页面（如 3号/4号/5号 公共区 2 卡）统不会触发模板检测 → `ok=true` 直接放行
- 安全：`knownDefaultValues` 仅匹配两个精确模式（`0/0/中/制冷` 和 `26/25/中/制冷`），真实数据巧合匹配概率极低

### Fix 2：`adaptivePolling` 稳定模板提前退出 → `hasRealTemp` 内容级等待

- 文件：`src/enumerate.js:adaptivePolling()`
- 改前：检测到 `allLoaded && template` → 3 轮稳定（~1.5s）后接受为真实数据
- 改后：删除稳定模板计数逻辑，改为 `hasRealTemp` = `data.cards.some(c => parseFloat(c.indoor) > 0)`；有真实温度才放行
- 原因：模板默认值（`OFF/0/0/中/制冷`）满足 `allLoaded` 条件（comm='关机'、switch='OFF'），被错误提前接受
- 行业依据：Needle Test — "if you know the value you're after, test for that value in the response"

### Fix 3：全离线快速放行

- 文件：`src/enumerate.js:adaptivePolling()`，在 `hasRealTemp` 后插入
- 逻辑：`phCount === 0 && data.cards.every(c => c.comm === '离线')` → 立即接受为 `all_offline`
- 原因：全离线卡（indoor=0, setT=0）不满足 `hasRealTemp`，若不处理会等到 45s 超时
- 行业依据：BACnet "stale is a valid state" — 通讯状态 '离线' 本身就是真实数据

### Fix 4：`qualityCheckWithProgressiveRetry` 失败后追加 WS 等待循环

- 文件：`src/enumerate.js`，5 处调用点统一改造
- 改前：重试 3 次（+1.7s）失败后直接保留模板数据
- 改后：追加 10 轮 `waitForDataReady(8,200)` + `waitForSvgStable(8,200)` → ~28s 深等待
- 原因：后续页的 WS 数据可能比 retry 时限（~1.7s）更晚到达
- 每轮检查 `hasRealTemp` 或 `qcN.ok`，任一满足即接受
- 稳定模板检测相关的字段 `STABLE_TEMPLATE_ROUNDS`、`stableTemplateRounds` 已全部删除

### Fix 5：fallback 循环补 `__ems` crash recovery（4 处）

- 文件：`src/enumerate.js`，fallback 循环内 `extractCards` 返回空时补 `injectHelpers` 重试
- 覆盖：recapture 动态分页、recapture 编号分页、BM page、collectPage 动态分页
- 原因：navigation/崩溃使 `__ems` 丢失 → `page.evaluate` 返回空 → 跳过一轮循环（~2.8s 浪费）
- 第 5 处（collectPage 编号分页）此前已有 crash recovery，保持不变

### Fix 6：3 处 `adaptivePolling` 的 `extractCards` 加 `.catch()`

- 文件：`src/enumerate.js`，覆盖 3 处 `adaptivePolling` 调用（动态/无分页/编号首页）
- 改前：`() => page.evaluate(() => window.__ems.extractCards()),`
- 改后：`() => page.evaluate(() => window.__ems.extractCards()).catch(() => ({ cards: [], count: 0 })),`
- 原因：polling 运行时若页面导航，`page.evaluate` 抛出 `Execution context was destroyed` → 整个枚举崩溃
- Fix 1+4 使 polling 运行时间更长（不再 1.5s 提前退出），崩溃概率增加，此修复尤为重要

五、文件变更

| 文件 | 行数 | 变更说明 |
|------|------|---------|
| src/rules.js | 109 | uniformValues n>=3→n>=2 |
| src/enumerate.js | 2193→2297 | adaptivePolling 删除稳定模板、改为 hasRealTemp + allOffline 双条件；fallback 5 处加 WS 深等待 + 4 处 injectHelpers 恢复；3 处 extractCards +.catch() |
| AGENTS.md | 308→322 | 新增本次修正文档 |
| CHANGELOG.md | 571→654 | 新增本次修改记录 |

六、修正对照表

| 场景 | Fix | 之前 | 之后 |
|------|-----|------|------|
| 6号 7F B座 一页（首页） | Fix 2+3 | 1.5s 后被"稳定模板"接受 | 45s 内等待真实温度，全离线快速放行 |
| 6号 7F B座 二页（后续页） | Fix 4 | 3 次重试（1.7s）后保留模板 | 重试失败后追加 10 轮 WS 深等待（~28s） |
| 3号/4号/5号 2卡模板页面 | Fix 1 | `checkCardQuality` 直接放行 | 被检测为模板，进入 adaptivePolling |
| polling 期间 `__ems` 丢失 | Fix 6 | 枚举崩溃 | 返回空数据继续轮询（.catch 兜底） |
| fallback 循环 `__ems` 丢失 | Fix 5 | 跳过一轮（~2.8s 浪费） | 重注入 helpers 后重试 |

===============================================================================
修改记录 — 2026-06-17 02:59
================================================================================

一、uniformTemplate 简化 + 稳定模板提前退出

1. 根因
   - 文件: src/rules.js:90
   - `uniformTemplate` 包含例外条件 `!(allOn || allOff)` 和 `!(uniformComm && !allOffline)`
   - 3号/4号全部关机的楼层（26/25/中/制冷）被误判为"非模板"，跳过自适应轮询直接下页
   - 这些楼层的第一页数据实际是模板默认值，但质量检查认为数据已完整

2. 修正
   - 删除所有例外条件，简化为 `const uniformTemplate = uniformValues && knownDefaultValues`
   - 模板数据始终被标记，触发自适应轮询

3. 自适应轮询增加稳定模板提前退出
   - 文件: src/enumerate.js adaptivePolling()
   - 连续 3 轮全部卡片有 comm/switch + 模板检测 → 约 1.5s 后接受为真实数据
   - 避免稳定模板数据空等 45 秒

二、翻页路径补充 indicator 图片等待

1. 根因
   - 翻页路径（页码/下页/补采）缺少 `waitForLoadedCards` → indicator 图片未加载
   - 4号 12 个子区（4F, 13F-29F）第二页 20/20 全部显示离线
   - 1号 8F、3号 1F、6号 3F 也有部分翻页全离线问题

2. 修正
   - 4 条翻页路径统一追加 `await waitForLoadedCards(page, { maxRetries: 3, waitMs: 250 })`
   - 覆盖：capturePages 动态分页/标准分页、collectPage 动态分页/标准分页
   - 代码：enumerate.js L1350, L1417, L1702, L1897

三、`checkCardQuality` ok 公式优先级修复

1. 根因
   - 文件: src/rules.js:93
   - `!uniformTemplate` 放在最外层作为「必须条件」——非模板页面即便数据已完整也需过 `switchLoaded≥50%` 检查
   - 混合页面（12离线+8关机）因离线卡 switch='-', switchLoaded < 50% 被误判为未加载，空等 45s

2. 修正
   - `!uniformTemplate` 从「必须条件」降为「优先级条件」：非模板直接放行 `ok=true`，模板数据才走开关/温度检查
   - 离线卡多的页面（如4号许多楼层）立即放行，不再触发自适应轮询

四、补采模式变量作用域修复

1. 3 处 const 重赋值
   - 根因: capturePages 函数用 `const data`/`const dataBM` 后尝试重赋值
   - 修正: L1350/L1416/L1487 `const` → `let`

2. 2 处变量未定义
   - 根因: capturePages 动态分页路径用 `plabel`（不存在），标准分页路径用 `pageNum`（不存在）
   - 修正: L1358 `plabel` → `pageNum`，L1424 `pageNum` → `plabel`

五、`knownDefaultValues` 补全模板模式

1. 根因
   - 文件: src/rules.js:84
   - 1号/2号/5号/6号的默认模板是 `0/0/中/制冷`，但代码只检查了 `fan='0'`（实际 fan='中'），完全漏检
   - 56 页共 1496 张模板卡未被标记，直接通过质量检查放行

2. 修正
   - `fanVal === '0'` → `fanVal === '中' && modeVal === '制冷'`
   - 1号/2号/5号/6号统一值 `0/0/中/制冷` 现被正确标记为模板

3. 自测更新
   - 文件: scripts/self-test.js:44
   - `loaded34`（全 26/25/中/制冷）预期从 `ok=true` 改为 `ok=false`
   - 模板数据应触发自适应轮询，而非直接放行

六、`collectPage`/`capturePages` 无分页路径加二次扫描

1. 根因
   - catch-22: `findPageBtns()` 初扫描发生在任何 waits 之前，SVG 尚未完全渲染
   - 对于按钮渲染较晚的子区（如 30F），初始 `uniquePages.length === 0` → 进入"无分页"路径
   - 该路径 waits 后**不重新扫描按钮**，直接抓一页了事
   - 对比"有分页"路径（line 1831）waits 后做 `finalBtns` 二次扫描确认所有按钮

2. 修正
   - 文件: src/enumerate.js
   - `collectPage` line 1793 无分页分支: waits 后加 `findPageBtns()` 重扫，发现按钮则 `return collectPage(prefix)` 递归重试
   - `capturePages` line 1394 无分页分支: 同上递归修复
   - 递归安全: 二次进入时 SVG 已稳定，`findPageBtns()` 正常检测到按钮，走标准分页路径，无无限循环

3. 6号 BM "not found" 化妆品修复
   - 文件: src/enumerate.js line 1601
   - BM（floor === -2）并非独立子区，其卡片通过 A座 1F 特殊路径采集
   - 主循环直接跳过（`continue`），不再尝试导航导致 "not found"
   - JSON 中标记 `err: 'bm inline'`，不影响下游处理

八、稳定模板提前退出加 `phCount === 0` 检查

1. 根因
   - 文件: src/enumerate.js:142
   - B1F 的 19 张卡全部为占位符名（`0-0001-KT`），但 `allLoaded=true`（comm/switch有值）
   - 稳定模板退出条件 `allLoaded && template` 未检查卡名是否真实
   - 3 轮后（~400ms）接受占位符数据作为"真实数据"

2. 修正
   - 新增 `phCount = data.cards.filter(c => !c.name || c.name === '0-0001-KT').length`
   - 条件改为 `allLoaded && phCount === 0 && template`
   - 占位符卡名的楼层不再走稳定模板退出，继续轮询直至真实数据到达或超时

3. 影响
   - B1F 原来 400ms 接受占位符数据 → 现在继续轮询直至真实数据到达或 45s 超时
       - 全关机楼层（卡名正常）不受影响，仍走稳定模板退出

十、`page.evaluate` 缺少 `__ems` 的安全保护

1. 根因
   - 5号采集时 `window.__ems` 因页面崩溃/导航而丢失
   - `page.evaluate(() => window.__ems.extractCards())` 直接报错
   - 仅子区间有 healthCheck，子区内的页面迭代循环无保护

2. 修正
   - 文件: src/enumerate.js
   - 3 处主 `extractCards` 调用 + quality retry callback: 加 `if (!window.__ems) return { cards: [], count: 0 }` + `.catch()` 兜底
   - 3 处 stale retry `clickById`/`extractCards`: 同上守卫
     - `stillStale` 加 `dataRetry.cards.length > 0` 前置条件避免空数组误判

十一、`collectPage` 内 `__ems` 丢失后 injectHelpers 重试恢复

1. 根因
   - 文件: src/enumerate.js
   - 安全守卫 `if (!window.__ems) return { cards: [], count: 0 }` 防住了程序退出，但崩溃子区内所有剩余页面被空数据填充
   - healthCheck 在下一个子区边界才触发，来不及挽回当前子区数据

2. 修正
   - 6 处 `collectPage` 内的 `!window.__ems` 守卫后追加 `injectHelpers` + 重试
   - 覆盖：动态分页初始提取、quality retry callback、stale retry 提取（3 处）
   - 覆盖：编号分页初始提取、quality retry callback、stale retry 提取（3 处）
   - 空数据时重注入 helpers，再试一次提取；仍失败则保留空数据（与之前行为一致）
   - qualityCheckWithProgressiveRetry 的回调改为 `async`，内部重试注入

十二、Vue 富集条件 `svgVal < 0` → `svgVal <= 0`

1. 根因
   - 文件: src/enumerate.js:617
   - 模板默认值 `indoor=0` 的 SVG 值在 Vue 富集时未被替换
   - 条件 `svgVal < 0` 不捕捉 `0`，因为 `0 < 0 = false`
   - 导致 shutdown 卡片的 indoor 值停留在 `0`（模板默认），而非真实温度
   - 例: 2号 2F 32 张卡全部 `indoor=0`，应为 `~24-27°C`
   - `uniformValues = true, uniformTemplate = true` → 触发稳定模板提前退出 → 数据被错误接受

2. 修正
   - `svgVal < 0` → `svgVal <= 0`
   - Vue 富集现正确替换 `indoor=0` 为真实温度
   - `uniformValues = false`（indoor 不再统一）→ `uniformTemplate = false` → 质量检查立即通过

十三、5号 BM 子区被 `floor === -2` 全局 skip 误跳过

1. 根因
   - 文件: src/enumerate.js:1603
   - `if (target.floor === -2) { skip BM }` 本意跳过 6号 C座 BM（x=1725，inline 视图破坏 SVG 引用）
   - 但 5号 7 个 BM 子区位于各座之间的合法位置（x < 1500），不应跳过
   - 6号仅 1 个 BM 子区被跳过（由第 2099 行特殊处理接管）
   - 旧基线 286 正确；floorskip 引入后降为 201

2. 修正
   - `if (target.floor === -2)` → `if (bldg.building === '6号' && target.floor === -2)`
   - 6号 BM 不变（仍跳过分派给 line 2099 特例处理）
    - 5号 BM 按普通子区采集

十五、新增 `src/logger.js` 日志系统

   自定义轻量日志模块（~50 行）：
   - 6 级别：ERROR/WARN/INFO/DEBUG（CLI 控制 `--log-level`）
   - 6 类别：ENUM/QUALITY/RULE/VUE/CRASH/NET（颜色区分，可过滤）
   - 双输出：终端彩色格式化 + 文件 NDJSON（`out/enum_*.log`）
   - 规则追踪：`checkCardQuality`、Vue 富集、`__ems` 恢复等关键分支加 DEBUG 日志
   - 查询示例：`grep '"cat":"RULE"' out/enum_2026-06-17.log`

十六、文件变更

| 文件 | 行数 | 变更说明 |
|------|------|---------|
| src/rules.js | 112→109 | uniformTemplate 简化 + knownDefaultValues 补全模板模式 |
| src/enumerate.js | 1886→2193 | +adaptivePolling 稳定模板退出 + 翻页 4 路 waitForLoadedCards + const→let 3 处 + 变量修复 2 处 + collectPage/capturePages 二次扫描 + BM 跳过限于 6号 + 稳定模板 phCount 检查 + __ems 安全守卫 + Vue indoor svgVal <= 0 + collectPage injectHelpers 6 处重试 + 规则日志 RULE/VUE/CRASH/QUALITY 追踪 |
| src/logger.js | — | **新增** 日志系统（级别/类别/颜色/文件输出） |
| src/verify-live.js | 14→556 | Vue 富集条件 `svgVal<0`→`svgVal<=0` + VUE 日志追踪 |
| AGENTS.md | 229→273 | 日志系统文档 + 规则追踪示例
| scripts/self-test.js | 104→104 | loaded34 断言同步（模板应触发轮询） |

================================================================================
修改记录 — 2026-06-16 18:35
================================================================================

一、BAT 启动脚本修复

1. AC-Scout.bat 支持异地运行
   - 文件: AC-Scout.bat
   - 不再依赖命令行当前目录
   - 优先使用脚本所在目录，其次搜索相邻 ems-tool 目录和常见安装路径
   - 新增当前项目路径 D:\Code\Git\ems-tool 作为固定兜底

2. 修复批处理跳转风险
   - 原写法中 `cd ... & goto run` 容易让搜索流程提前跳转
   - 改为先设置 PROJECT_DIR，确认找到 src\collect.js 后再统一 cd /d 并启动

3. 验证
   - 项目目录内运行 AC-Scout.bat 通过
   - 复制到临时目录后运行 AC-Scout.bat 通过

================================================================================
修改记录 — 2026-06-16 18:25
================================================================================

一、TUI 显示优化

1. 主菜单信息分区
   - 文件: src/tui/menus.js
   - 调整为“数据状态 / 楼栋状态 / 操作”三段式
   - 第一屏直接显示最近采集时间、全量/单栋状态、耗时、质量摘要

2. 质量摘要更醒目
   - 文件: src/tui/actions.js
   - 新增 qualityBadge()
   - 显示 P1/P2 风险，例如“P1 占位20 / P2 未知通讯3/疑似默认页1”
   - 未生成质量报告时显示“质量 未生成”，无风险时显示“质量 OK”

3. 楼栋基准差异更直观
   - 文件: src/tui/actions.js, src/tui/menus.js
   - 新增 deltaLabel()
   - 主菜单和楼栋选择页统一显示“当前/基准”和差异
   - 少卡楼栋显示复采提示，例如 6号 “2473/2480  -7 复采”

4. 概览页表格化
   - 文件: src/tui/flows.js
   - 保留总体统计
   - 新增按楼栋展示总数、开机、关机、离线、基准差异

5. 验证
   - node --check: collect.js + src/tui/*.js 全部通过
   - TUI 模拟: 主菜单、楼栋选择、概览页显示检查通过
   - npm run self-test 通过

================================================================================
修改记录 — 2026-06-16 18:10
================================================================================

一、TUI 结构拆分

1. collect.js 入口瘦身
   - 文件: src/collect.js
   - 仅保留 runCollectTui() 入口和错误收尾

2. 新增 TUI 模块
   - src/tui/ui.js: prompt/clearScreen/SEP/checkbox/视觉宽度/确认页/结果页
   - src/tui/actions.js: 子进程、采集、导入、质量审计、报表、DB 状态读取
   - src/tui/menus.js: 主菜单、楼栋选择、报表类型、格式选择
   - src/tui/flows.js: 主状态机、采集流程、自定义报表流程、概览流程

3. 行为保持
   - 保留采集前确认
   - 保留报表无二次确认
   - 保留采集后默认报表 [Y/n/custom]
   - 保留质量审计摘要和基准差异显示

4. 验证
   - node --check: collect.js + src/tui/*.js 全部通过
   - TUI 模拟: 退出、楼栋全选、报表生成、概览均通过
   - npm run self-test 通过

================================================================================
修改记录 — 2026-06-16 17:55
================================================================================

一、TUI 确认流程精简

1. 报表生成取消二次确认
   - 文件: src/collect.js
   - 原流程: 选类型 → C → 选格式 → C → 再确认 Y
   - 新流程: 选类型 → C → 选格式 → C 直接生成
   - 保留结果页“按 Enter 返回”，用于查看生成文件名，不作为确认动作

2. 采集后默认报表快捷路径
   - 文件: src/collect.js
   - 采集完成后提示: 生成默认报表？[Y/n/custom]
   - Y 或直接回车: 生成 all/on/off + md/xlsx/txt
   - n: 回主菜单
   - custom/C: 进入自定义报表选择

3. 报表流程去重
   - 文件: src/collect.js
   - 新增 customReportFlow() 和 generateDefaultReports()
   - 采集后自定义报表与主菜单报表共用同一套选择逻辑

================================================================================
修改记录 — 2026-06-16 17:40
================================================================================

一、TUI/UI 修复

1. 主菜单显示质量状态
   - 文件: src/collect.js
   - 新增显示楼栋卡数与基准差异，6号少卡会直接显示为“基准 -7”
   - 新增质量审计摘要，直接显示占位符、未知通讯、疑似默认页数量

2. 概览页显示质量状态
   - 文件: src/collect.js
   - 数据概览新增质量审计摘要
   - 楼栋行显示基准差异

3. 楼栋选择多选交互修复
   - 文件: src/collect.js
   - 根因: 数字输入处理块被放到 while 循环外，导致选中态无法稳定刷新
   - 修正: 数字输入在楼栋选择循环内处理，支持 1-7 与连续输入

4. 报表类型“全部”显示修复
   - 文件: src/collect.js
   - 选择 [4] 后当前状态显示“全部”，不再显示三个报表名拼接

5. 采集流程文案同步
   - 文件: src/collect.js
   - “采集 → 导入”更新为“采集 → 导入 → 质量审计”
   - 采集完成后显示质量报告位置

================================================================================
修改记录 — 2026-06-16 17:25
================================================================================

一、质量审计体系

1. 新增质量审计报告
   - 文件: scripts/quality-report.js
   - 输出: out/quality_report.json, out/quality_report.txt
   - 检查项: 0-0001-KT/空卡名、comm/switch 不一致、未知通讯、未知开关、
     疑似默认页、楼栋卡数/子区数与基准差异。
   - collect.js 导入数据库后自动执行质量审计。

2. 当前数据审计结果
   - 总卡数: 6564
   - 6号楼: 2473/2480 卡，30/31 子区
   - 6号 F9 9F 三页仍有 20 条 0-0001-KT
   - 未知通讯 3 条

二、规则集中化

1. 新增 src/rules.js
   - 集中维护 BLDG_ORDER / BLDG_META / PUBLIC_KEYWORDS / IND_MAP
   - 集中维护 getZuo5/getZuo6/getZone、isPublic/classifyAreaType、checkCardQuality

2. 接入共享规则
   - src/enumerate.js: 使用共享 checkCardQuality/getZone
   - src/collect.js: 使用共享楼栋顺序、楼栋基准、公区规则
   - scripts/report.js / dump-aircons.js / dump-public.js: 使用共享公区与座号规则

三、回归自测

1. 新增 scripts/self-test.js
   - 验证 3/4号默认值质量判定
   - 验证 0-0001-KT 拦截
   - 验证公区/非公区规则
   - 验证 --bldg 部分导入保留未选楼栋

2. package.json 新增命令
   - npm run quality
   - npm run self-test

四、联网审计记录

1. xlsx@0.18.5
   - GitHub Advisory: GHSA-4r6h-8v6p-xvw6, GHSA-5pgg-2g8v-p4x9
   - npm audit 标记 high，且无 npm fixAvailable
   - 当前项目主要使用 xlsx 写报表，避免读取不可信 Excel 文件

================================================================================
修改记录 — 2026-06-16 17:10
================================================================================

一、等待/质量判定修复

1. 3/4号楼默认模板误判导致等待偏长
   - 文件: src/enumerate.js
   - 根因: 旧 checkCardQuality 只要 indoor/setTemp/fan/mode 全页一致，就判为模板默认值。
     3/4号楼真实页面常见 26℃/25℃/中/制冷 全一致，导致首页反复进入 45s 轮询。
   - 修正: 只有在已知默认值且 comm/switch 未完整加载时才判模板；通讯灯已覆盖则放行。

2. 0-0001-KT 占位符保护
   - 文件: src/enumerate.js
   - 修正: checkCardQuality 新增 placeholderNames 检测，日志输出 ph=x/n。
   - 效果: 混有 0-0001-KT 的页面不再因“至少一个真实卡名”而被误放行。

二、导入器修复

1. --bldg 部分导入语义修正
   - 文件: scripts/import.js
   - 根因: 旧逻辑清空 cards/pages/sub_areas 后仍导入 JSON 全量；--bldg 只影响 updated_at。
   - 修正: --bldg 仅删除并重导入选中楼栋，未选楼栋数据保留。
   - 验证: 临时 DB 测试通过，重导 2号后 1号数据仍保留。

2. 空库初始化
   - 文件: scripts/import.js
   - 修正: 内置 CREATE TABLE IF NOT EXISTS / CREATE INDEX IF NOT EXISTS，fresh DB 可直接导入。
   - 兼容: 支持 EMS_JSON_PATH / EMS_DB_PATH 环境变量用于非破坏性测试。

三、状态误判修复

1. 单一 switch href 不再默认 OFF
   - 文件: src/enumerate.js, src/verify-live.js, scripts/dashboard.js
   - 根因: 页面只有一种开关图片 href 时旧逻辑直接当 offHref；indicator 缺失时可能误判全开页面为关机。
   - 修正: 只有出现两种 href 时才按频率推断 ON/OFF；单 href 保持未知，等待 indicator/Vue 覆盖。

2. verify-live 楼层过滤参数化
   - 文件: src/verify-live.js
   - 修正: --floor 使用 Number 校验和 SQL 参数绑定，避免异常输入拼进 SQL。

================================================================================
修改记录 — 2026-06-07 23:26
================================================================================

一、核心 Bug 修复

1. comm 字段区分 开机/关机/离线（原全部标记为"在线"）
   - 文件: src/enumerate.js:540-542
   - 根因: IND_MAP 映射后，ON 和 OFF 都赋值为 c.comm = '在线'，丢失开关机区分
   - 修正: c.comm = mapped （直接取 IND_MAP 值：'开机'/'关机'/'离线'）
    - domino update: scripts/dump-aircons.js, scripts/dump-public.js, src/verify-live.js

2. Network monitor requestfailed 崩溃
   - 文件: src/enumerate.js:800
   - 根因: Playwright requestfailed 事件对象用 .failure() 而非 .error()
   - 修正: fail = req.failure(); error = fail ? fail.errorText : 'unknown'

二、性能优化（实测 3.7-3.9x 提速）

1. Wait 常量大幅缩减（基于实时页面响应测量）
   - W.MENU_CLICK:  1500ms → 200ms  （实际 SVG 就绪 200ms）
   - W.FLOOR_CLICK: 2000ms → 100ms  （实际页面就绪 200ms）
   - W.PAGE_CLICK:   800ms → 100ms  （实际翻页就绪 <200ms）
   - W.BM_CLICK:    1000ms → 500ms

2. waitForDataReady 稳定阈值 6→2
   - 实测 WS 数据就绪仅需 ~450ms，原需要 3.5s（6×500ms）稳定判定
   - 改为 2 次稳定（2×200ms=400ms），核心提速 ~3s/子区

3. waitForSvgStable 轮询粒度 500ms→200ms
   - 实测 SVG indicator 稳定需 ~4.4s（EMS 服务端渲染耗时）
   - 细粒度 200ms 轮询可更快检测到稳定状态

4. waitForLoadedCards 间隔 2000ms→300ms
   - 占位卡检测无需等 2s，300ms 足够

三、新增功能

1. 网络监控（enumerate.js）
   - setupNetworkMonitor(page): 拦截所有请求/响应/WS 事件
   - getNetworkSummary(netLog): 汇总失败请求数、WS 断开次数
   - 默认启用，--no-net-monitor 关闭

2. 实时页面诊断（enumerate.js）
   - verifyPageState(page): 检测 shadow DOM/indicator 数/WS 数据量
   - verifyCardIntegrity(page): 校验占位卡/缺失 indicator/重复卡
   - captureEnhancedSnapshot(page): 增强快照含 indicator 频率分布
   - diagnosePage(page, netLog): 综合健康检查

3. 独立验证工具（src/verify-live.js）
   - 通过 CDP 连实时 Edge 页面，提取卡片并与 ac.db 对比
   - node src/verify-live.js [--building=1号] [--floor=1]

4. --verify 模式（enumerate.js 内置）
   - 枚举前诊断页面健康状态，与 DB 对比
   - node src/enumerate.js --edge --verify

5. --self-diagnose 模式
   - 枚举中每 5s 自动执行健康检查

四、数据归档

data/1号楼/   - 1号楼原始验证数据
data/2号楼/   - 2号楼当前数据（含本次枚举结果）
out/          - 最新全量数据（enum_full_v5.json, ac.db）

=================================================================================
修改记录 — 2026-06-11 08:55
===============================================================================

一、checkCardQuality 升级：模板默认值检测

原来 checkCardQuality 只检查字段非空（c.indoor !== '-'），会把 SVG 模板默认值
（0℃ | 0℃ | 中）当作有效数据放过，导致分页第一页数据全为模板默认值但未被识别。

现在检测真实载荷数据：
- withRealIndoor = parseFloat(c.indoor) > 0
- withRealSetTemp = parseFloat(c.setTemp) > 0
- withRealFan = c.fan !== '中' && c.fan !== '0'
- 统一值检测：所有卡 indoor/setTemp/fan/mode 完全一致视为模板默认（不同建筑模板值不同，1号=0℃/0℃/0，3/4号=26℃/25℃/中）
- 全离线页面自动豁免（allOffline → ok=true），统一值例外
- 代码：enumerate.js:707-728

二、首页提取加 retry（3 处路径）

动态/无分页/编号三类第一页在质量不通过时自动重试：
- waitForDataReady(maxRetries:15, waitMs:200) — 最长 3s
- waitForSvgStable(maxRetries:25, waitMs:200) — 最长 5s
- 重试后数据更优时替换为 retry 结果

三、实测效果

全量耗时 360s（6栋/6571卡），对比上次 456s（7.6min）提速 21%。
数据质量 0 缺漏卡。模板默认值问题已修复。

四、文件变更

| 文件 | 行数 | 变更说明 |
|------|------|---------|
| src/enumerate.js | 1845→1886 | checkCardQuality 模板默认值检测 + 统一值模式 + 首页重试 + 全离线豁免 |
| out/enum_full_v5.json | — | 全量采集（6571卡/6栋） |
| .context-summary.md | 39→45 | 更新至 2026-06-11 上下文 |

================================================================================
当前 DB 状态（导入时间：2026-06-07 23:26）
================================================================================

1号: 1493 卡 开机=287 关机=1134 离线=72
2号:  110 卡 开机=8   关机=99   离线=1 空=2
合计: 1603 卡

switch=ON 设备: 295 台（公区 94 + 非公区 201）
  - 1号: 287 台
  - 2号:   8 台

================================================================================
修改记录 — 2026-06-10 06:48
================================================================================

一、数据就绪保护（4 项修复）

1. waitForLoadedCards 区分「开关图未加载」vs「全部真关机」
   - 新增 switchLoaded 检查：d.cards.some(c => c.switch !== '-')
   - 开关图全空时继续等待（之前与全部关机混为一谈只多等 200ms）
   - 代码：enumerate.js:603-634

2. 翻页 SVG 稳定超时 800ms→1.2s
   - maxRetries: 4→6（4 处 page-turn 路径统一更新）
   - 慢服务器翻页多 400ms 余量

3. 翻页路径加 waitForDataReady
   - 翻页后先等 WS 数据稳定（maxRetries:5, waitMs:200），再等 SVG_STABLE
   - 确保 Vue WS 数据在 extractCards 前已完整
   - 代码：enumerate.js:1300,1353,1574,1690

4. checkCardQuality 提取后质量验证 + 自动重试
   - switch/mode 填充率 <75% 时自动重试（全等待 waitForDataReady + waitForSvgStable）
   - 全量提取点（首页/翻页/补采/BM/子标签）均加质量日志
   - 实测捕获：5号 F1/17 sw=4/14（仅 4/14 卡有开关数据），重试后全部填满
   - 代码：enumerate.js:707-720 + 12 处调用

二、等待链优化

- 全局并行化：首栋 Promise.all([waitForLoadedCards, waitForDataReady, waitForSvgStable])
- SVG_STABLE 阈值 3→2，间隔 200→150ms（上限 5s→3.75s）
- 翻页 SVG 超时 1.6s→0.8s→1.2s（maxRetries 8→4→6）
- waitForReady 首栋 40×300ms→10×200ms，后续 20→6
- waitForDataReady 30×300ms→10×200ms，稳定 2→1
- waitForLoadedCards 12×500ms→6×200ms
- waitForPageSwitch 12×300ms→6×200ms

三、实测效果

- 全量 6 栋耗时：456s（7.6min）→ 360s（6.0min），提速 **21%**
- 数据质量无缺漏（6571 卡全量，0 placeholder）
- SVG_STABLE 实际达成时间：450ms（首页）/ 600ms（翻页），远低于超时上限
- 卡号与上次全量完全一致

四、文件变更

| 文件 | 行数 | 变更说明 |
|------|------|---------|
| src/enumerate.js | 1578→1880 | 等待链优化 + 数据质量验证 + 模板默认值检测 + 首页重试 |
| out/enum_full_v5.json | — | 最新全量采集（6561 卡/6 栋） |


