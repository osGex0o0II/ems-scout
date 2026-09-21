# EMS Scout 缺陷核实与整改方案（2026-09-22）

## 本次发布与审查范围

性能修改提交为 `18cb047`，已合入并推送 `main`。本地主目录为 `D:/Code/Git/ems-scout`，安装版已由 1.0.74.0 更新为 **1.0.75.0**。主目录完整测试 460/460 通过；MSIX 打包 0 警告、0 错误，签名有效，6 个分发文件哈希验证通过，安装后的 Desktop/Application/Infrastructure 三个程序集与本次构建一致。

发布前使用 SQLite Backup 备份当前数据库、规则、实时文件与设置；继续使用原工作区 `D:/Code/Git/ems-scout/.worktrees/area-rule-groups` 的数据和设置，没有把主目录的另一份数据库覆盖到当前数据上。正式安装版已打开，核对总览 6471 台、区域统计及初始规则楼层显示正常。这是本机样本数量，不是固定完整性门禁。

以下保留初次审查确认的 **11 项缺陷**及整改依据；用户已授权执行，实施与验证结果见 [整改记录](2026-09-22-bug-remediation-results.md)。范围覆盖采集/导入/质量审计、原生历史与实时详情、设置与发布脚本；不声称穷尽整个项目。没有进行真实 EMS 采集，也没有对用户数据库执行缺陷复现。除特别注明外，原始代码位置以 `18cb047` 为准。

证据分为：生产方法的内存复现、真实导入/审计脚本的临时数据库复现、隔离文件系统/等价表达式复现，以及确定性调用链核实。后两类不冒充完整 WinUI 端到端验证。

## 优先级清单

| 编号 | 优先级 | 问题 | 证据 | 主要影响 |
|---|---|---|---|---|
| B01 | P1 | 同名兜底抢占其他设备的精确实时详情 | 生产匹配方法内存复现 | 集控锁错绑，表格和 Excel 都可能错误 |
| B02 | P1 | 日志清理穿过目录 junction | 隔离文件系统复现 | 删除配置目录之外的日志 |
| B03 | P1 | 残缺页面导入被重写为合法计数 | 真实导入和质量脚本复现 | 不完整数据替换当前数据，审计仍无问题 |
| B04 | P1 | 稳定离线例外接受占位卡名 | 真实函数内存复现和临时导入 | 未完成加载的设备名称进入结果 |
| B05 | P2 | 外部数据目录可保存，却阻止下次启动 | 确定性调用链核实 | 用户无法进入设置自行修复 |
| B06 | P2 | 恢复旧批次后，最新快照从下拉消失 | 生产 catalog 内存复现 | 无法选择已有最新历史快照 |
| B07 | P2 | 历史区域选项读取当前区域配置 | 确定性调用链核实 | 历史区域筛选缺项、标签和计数错误 |
| B08 | P2 | 非空未知通讯状态漏审 | 真实导入和质量脚本复现 | unknown_count 与质量报告矛盾 |
| B09 | P2 | 缺图设备例外豁免了同页普通设备缺字段 | 真实函数内存复现 | 本可等待恢复的数据提前结束采集 |
| B10 | P2 | 打包校验失败前先删除已有发布包 | 原脚本片段隔离复现 | 失败操作毁掉上一次成功产物 |
| B11 | P2 | 八位 ARGB 颜色解析偏移错误 | 等价表达式复现 | 颜色预设保存后回退或变色 |

本轮按 P1 优先、P2 随后实施。严重程度描述的是满足触发条件后的影响，不表示这些问题已经污染当前生产数据。

## B01：同名兜底导致集控锁错绑

**定位：** [SqliteDeviceReadRepository.cs:1429](D:/Code/Git/ems-scout/native/src/EmsScout.Infrastructure/Sqlite/SqliteDeviceReadRepository.cs:1429)、[RealtimeDetailSet.cs:72](D:/Code/Git/ems-scout/native/src/EmsScout.Application/Devices/RealtimeDetailSet.cs:72)。

**触发与根因：** 同楼同名的 A、B 分处不同页面，实时文件只包含 B。单遍循环先处理 A，找不到精确匹配便调用 `UniqueByName`，它只验证实时侧唯一并立即消费该行；随后 B 的精确匹配失败。SQL 筛选先于绑定，选中 B 所在页面又会恢复正确归属。

**已复现：** 直接调用生产 `AttachRealtimeRows`，完整候选中 A 得到 B 的“集控锁开启”，B 无实时详情；只传 B 时 B 为 exact=开启。导出直接使用同一 `RealtimeLockText`，错误可进入 Excel。核对旧提交 `973eb27` 后确认这是既有缺陷，并非此次性能优化新增。

**整改：** 在同一批次、楼栋的完整身份候选集合中完成绑定，再应用页面/模式/状态等显示筛选。先人工修正，再为所有设备保留精确匹配，最后仅在设备侧与实时侧都唯一时使用同名兜底。仅拆成两遍循环不够：被筛选隐藏的精确 owner 仍须保留身份占用。沿用现有来源批次校验，不能让某楼缺来源扩大为其他楼的未知状态；完成后的不可变映射纳入版本缓存。

**验收：** A/B 反例在全部、一页、二页、排序、分页下身份和锁值一致；A 始终无详情、B 始终 exact；锁选项计数、行列表、Excel 一致；合法单卡页面名兼容仍能兜底；六栋来源不完整和人工修正场景不回归。改动后重跑现有 31 组差分并记录有意纠正的行为差异。

## B02：日志清理越过数据目录

**定位：** [LocalLogCleanupService.cs:72](D:/Code/Git/ems-scout/native/src/EmsScout.Application/Settings/LocalLogCleanupService.cs:72)、[SettingsPage.xaml.cs:89](D:/Code/Git/ems-scout/native/src/EmsScout.Desktop/Pages/SettingsPage.xaml.cs:89)。

**触发与根因：** 数据目录中有指向外部目录的 junction。`SearchOption.AllDirectories` 会沿链接递归；返回路径仍带数据目录前缀，而删除前只检查文件自身的 `ReparsePoint`。外部普通日志文件没有该属性，检查因此失效。

**已复现：** 在独立 fixture 中建立 `data/linked -> outside-data`，枚举和相同删除条件删除了外部 sentinel；未触及用户日志。

**整改：** 预览和删除共用逐层、不跟随链接的枚举器，跳过目录和文件 reparse points，逐项处理权限和 I/O 错误；执行阶段重新核实祖先路径，避免只做字符串前缀检查。错误应返回跳过/失败明细，不从 UI 事件逃逸。

**验收：** 普通嵌套日志可删；文件链接、目录 junction、循环链接均不越界；外部 sentinel 哈希不变；无权限子目录不会崩溃；预览与实际可删数量一致。正式回归必须调用生产服务，而不只复制条件表达式。

## B03：空页和数量矛盾被导入“修正”

**定位：** [enum-validator.js:149](D:/Code/Git/ems-scout/src/enum-validator.js:149)、[import.js:177](D:/Code/Git/ems-scout/scripts/import.js:177)、[quality-report.js:305](D:/Code/Git/ems-scout/scripts/quality-report.js:305)。

**触发与根因：** 同一子区一页有卡、另一已声明页面为空。校验仅以整个子区判断空数据；导入把 count/raw/unique 重写成实际数组长度；质量 SQL 只查没有页面的子区，卡片 inner join 又跳过了零卡页。

**已复现：** 输入第一页声明 10 张但实际 1 张，第二页声明 12 张但实际 0 张；真实导入成功，库中计数变为 1/0，质量 issue_count=0，历史 status=completed。直接导入入口可达；正常采集器对无例外空页还有独立检查，不能泛化为所有采集都会漏过。

**整改：** 在编号及计数重写前校验输入自身的声明与实际数量；区分 raw/unique/count 的原始语义，不直接覆盖矛盾证据。每个声明页面必须有卡或满足明确合法空页规则。质量检查从 pages 左联 cards，核对零卡页和计数差异。导入拒绝必须发生在替换当前数据之前，并验证事务原子性。

**验收：** 空二页、count=10/cards=1 都拒绝且当前库/历史/来源绑定不变；合法不同规模历史批次接受；6号 BM 内联占位按明确规则豁免；质量报告独立识别手工构造的零卡页。禁止引入固定 6471 卡数门槛。

## B04：稳定离线例外放行占位名

**定位：** [enumerate.js:179](D:/Code/Git/ems-scout/src/enumerate.js:179)、[enumerate.js:1163](D:/Code/Git/ems-scout/src/enumerate.js:1163)、[quality-report.js:269](D:/Code/Git/ems-scout/scripts/quality-report.js:269)。

**触发与根因：** 两张名称尚未加载的卡被编号成 `0-0001-KT#1/#2`，但已经显示离线模板。稳定离线分支不检查 placeholderNames，最终门禁只信原因白名单；审计又只识别没有编号后缀的精确占位名。

**已复现：** qc.ok=false、placeholderNames=2，在 0/400/800ms 三轮后仍 accept=true，最终 audit 返回空问题列表。另用正常字段单独证明编号占位名经真实导入后 placeholder_cards=0、issue_count=0；这不表示离线模板本身一定逃过全部后续复核。

**整改：** 所有例外共用不可豁免条件：真实名称、非占位、身份/重复结构有效。例外只能放宽其对应字段。最终 audit 重新计算条件，不能只信 qualityReason；采集、enum-validator、quality-report 复用同一个占位名分类函数。

**验收：** 原占位名、#后缀、空/空白名均拒绝，有 devId 也不绕过；合法重名卡的编号保留；真实名称的稳定离线页维持既有复核策略。

## B05：保存数据目录后无法再次启动

**定位：** [SettingsViewModel.cs:154](D:/Code/Git/ems-scout/native/src/EmsScout.Desktop/ViewModels/SettingsViewModel.cs:154)、[AppSettingsValidator.cs:22](D:/Code/Git/ems-scout/native/src/EmsScout.Application/Settings/AppSettingsValidator.cs:22)、[PathSafety.cs:15](D:/Code/Git/ems-scout/native/src/EmsScout.Application/Settings/PathSafety.cs:15)。

**代码确认：** FolderPicker 接受任意目录，保存校验只检查非空。工作区外目录会成功写入 settings.json；重启时 migrator 解析路径，PathSafety 的 allowExternal 默认 false，抛异常后只能进入“关闭”式启动失败窗口。无需该目录存在 DB 即可触发；未改动真实用户设置来复现。

**整改：** 保存与读取使用同一个路径校验规则，数据/导出目录全部预检；非法路径拒绝保存并保留原配置。如果产品确需支持外部目录，显式授权及读取规则统一设计。已有坏配置提供“修复目录/恢复安全设置”入口，不能跳过真正的迁移失败检查。

**验收：** 外部目录、工作区内文件、系统目录均按约定拒绝或明确授权；合法目录可保存；保存失败文件哈希不变；注入坏配置后可从 UI 修复启动，无需手工编辑 JSON。

## B06：恢复旧批次后无法选择最新历史快照

**定位：** [CollectionDataSourceCatalog.cs:19](D:/Code/Git/ems-scout/native/src/EmsScout.Application/Collection/CollectionDataSourceCatalog.cs:19)、[DataViewModel.cs:1099](D:/Code/Git/ems-scout/native/src/EmsScout.Desktop/ViewModels/DataViewModel.cs:1099)、[HomeViewModel.cs:275](D:/Code/Git/ems-scout/native/src/EmsScout.Desktop/ViewModels/HomeViewModel.cs:275)。

**已复现：** R2 比 R1 新，恢复 R1 后当前项依据真实绑定指向 R1，但 catalog 仍无条件从历史集合排除 R2。调用生产 catalog 的内存探针得到可选 RunId=[null,1]，R2 无处可选。指定不存在选项的 RunId 导航还会静默保留当前选择。

**整改：** “当前 cards”与“可用快照”分别建模，列出所有有效历史快照；若去重，必须依据当前真实绑定，混合/未知来源不得排除任意快照。找不到指定 RunId 要明确提示，不静默改读另一批。

**验收：** 恢复 R1、混合来源、未知来源下 R1/R2 都能查看；当前项始终 RunId=null；历史查询携正确 RunId；总览与数据页一致。

## B07：历史区域筛选使用当前配置

**定位：** [DataViewModel.cs:543](D:/Code/Git/ems-scout/native/src/EmsScout.Desktop/ViewModels/DataViewModel.cs:543)、[DataViewModel.cs:1221](D:/Code/Git/ems-scout/native/src/EmsScout.Desktop/ViewModels/DataViewModel.cs:1221)、[SqliteDeviceReadRepository.cs:837](D:/Code/Git/ems-scout/native/src/EmsScout.Infrastructure/Sqlite/SqliteDeviceReadRepository.cs:837)。

**代码确认：** 历史行归属来自 run_area_group_rules；区域下拉却无条件读取当前 monitor_groups 并只显示当前启用组。历史组在当前被删除/禁用后下拉消失；改名后历史行仍旧名，但下拉用新名，按名称关联的计数还可能退回当前统计。未在用户库执行删除来验证。

**整改：** 从所选快照统一生成 `{groupId,name,count}`，历史选项不依赖当前配置；用稳定 ID 关联统计，名称仅用于显示。当前模式保持当前配置语义。

**验收：** 保存批次后分别删除、禁用、改名当前组，历史名称/数量/选项/筛选结果全部保持快照；切回当前才反映新配置；当前数据 Excel 与当前筛选保持一致，历史批次继续禁止导出，不新增历史导出功能。

## B08：未知通讯状态被审计为正常

**定位：** [quality-report.js:275](D:/Code/Git/ems-scout/scripts/quality-report.js:275)。

**已复现：** `unknownComm` 只检查 `!r.comm`。输入非空未知状态，经真实导入/审计后 run.unknown_count=1，但报告 unknown_comm=0、issue_count=0，批次标为 completed。

**整改：** 用开机/关机/离线白名单及统一标准化函数判断未知，保留原始值用于诊断；历史计数与质量检查共用规则。不可把任意非空值降级为有效状态。

**验收：** null、空串、空白、未知文本/数字均进入 unknown 和需复核；三个合法状态不误报；审计 unknown 数与批次计数一致。

## B09：已知缺图例外吞掉同页普通设备的字段缺失

**定位：** [rules.js:378](D:/Code/Git/ems-scout/src/rules.js:378)、[enumerate.js:329](D:/Code/Git/ems-scout/src/enumerate.js:329)。

**已复现：** 两台已知 2M001 缺图设备与一台普通设备同页。普通设备仅有 indicator/comm，其温度/模式/风速缺失；qc.activeFieldOk=false，但 classifier.eligible=true，最终采集 audit 无问题。现场是否出现该组合仍需验证。后续 quality-report 仍可报 invalid_card_fields，因此不描述为全链路零问题。

**整改：** 豁免只作用于准确命中的缺图设备；其余设备继续接受逐卡状态、开关、温度、模式、风速检查，未就绪则继续轮询。不要把小样本模板启发式误当逐卡健康条件。

**验收：** 只有已知缺图且其他卡健康可接受；普通卡任一关键字段未加载都继续等；填齐后才通过；占位名和未知通讯不被豁免。

## B10：打包失败删除上次成功产物

**定位：** [native-package.ps1:22](D:/Code/Git/ems-scout/scripts/native-package.ps1:22)。

**已复现：** 提取原脚本 20–30 行在隔离目录运行。已有同版本模拟包时，脚本先删除目录，再因版本不高于安装版而抛错，原包已消失。此次发布使用新的 1.0.75.0 空目录，未触发该缺陷。

**整改：** 版本、路径、证书、运行时等预检放在最终输出变更之前；使用唯一 staging 目录构建和校验，成功后才发布。已有版本默认拒绝覆盖，或明确备份/替换策略；失败保留原包。

**验收：** 版本拒绝、签名失败、缺少 Node、构建失败时旧产物哈希均不变；只有完整成功包可替换最终目录，manifest 文件哈希验证一致。

## B11：颜色保存后不能准确还原

**定位：** [SettingsViewModel.cs:282](D:/Code/Git/ems-scout/native/src/EmsScout.Desktop/ViewModels/SettingsViewModel.cs:282)。

**已复现：** 八位 `#AARRGGBB` 路径 offset=1，RGB 从 2/4/6 读取，正确位置应为 3/5/7。`#FF9AA0A6` 变成 `#FFF9AA0A`，`#FFE9C46A` 变成 `#FFFE9C46`，六位格式正常。选项匹配失败后又通过双向回调回退到默认预设。

**整改：** 修正分段偏移，保留 alpha；预设匹配失败不能顺带篡改合法自定义色；提取无 UI 依赖的颜色编解码以便行为测试。

**验收：** 所有预设、六位 RGB、八位 ARGB（含非 FF alpha）经过选择→保存→加载→再次保存保持一致；无效字符串按约定回退。

## 实施顺序与完成门槛

1. **第一批：数据归属与操作边界。** B01、B02、B05。分别建立生产匹配、链接文件系统和设置恢复测试；B01 不得绕过来源批次校验，B02 不得触及真实日志。它们可以独立实施，最后统一回归。
2. **第二批：采集/导入门禁。** B03、B04、B08、B09。先统一名称、状态与页结构不变量，再收紧例外，最后补导入事务和质量 SQL。保持合法离线、小规模动态批次及 BM 内联规则。
3. **第三批：历史与发布体验。** B06、B07、B10、B11。统一当前与历史快照目录/区域选项，发布产物使用暂存完成后替换，颜色逻辑补纯函数往返测试。
4. 每批均跑相关 Node 测试、完整 .NET 测试、Release 构建及临时库 ExcelSmoke。并发、筛选、分页、历史来源变化须保留此次性能缓存的失效规则，复测冷/热耗时防止恢复全量重复加载。
5. 最后运行隔离 WinUI 验收：恢复旧批次后查看最新快照、历史组变更、锁值跨筛选一致性、坏设置恢复。采集时序需 `field-e2e.ps1` 的唯一 `out/field-e2e-*` 和随机本机 CDP/profile，不能把本地测试通过描述为真实 EMS 现场通过。

以上是待执行计划；本次安装包包含已验证的性能改进，没有把这 11 项尚未实施的修复宣称为已完成。

## 本机证据与回退材料

- 发布记录及原始审查输出：[release-20260922-1.0.75.0](D:/Code/Git/ems-scout/out/release-20260922-1.0.75.0)。
- 更新前 SQLite Backup、规则及设置：[before-update](D:/Code/Git/ems-scout/out/release-20260922-1.0.75.0/before-update)。
- 安装包：[1.0.75.0](D:/Code/Git/ems-scout/out/native-packages/1.0.75.0)。
- 原 1.0.74.0 包保留在原工作区的 `out/native-packages/1.0.74.0`，未删除；回退程序须单独核实版本和数据兼容，不能用卸载删除用户数据。
- Node 复现原工作树：[repro-collection.cjs](C:/Users/Administrator/.codex/worktrees/native-page-performance/ems-scout/.superpowers/sdd/release-audit-20260922/repro-collection.cjs)。
- Native 探针：[NativeAuditProbe.csproj](C:/Users/Administrator/.codex/worktrees/native-page-performance/ems-scout/.superpowers/sdd/release-audit-20260922/native-probe/NativeAuditProbe.csproj)，主代理已执行，两个断言场景均重现当前缺陷，退出 0。

原始数据库、设置备份和探针输出保留本机，不推送到远端。整改文档随主分支同步。
