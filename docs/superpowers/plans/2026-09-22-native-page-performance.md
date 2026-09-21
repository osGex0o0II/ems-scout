# 原生页面性能改进实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 消除区域规则编辑阻塞、数据页重复准备和总览复访重算，保持数据与导出语义。

**Architecture:** 先让读取不再写库，控制规则事件与界面虚拟化；再建立有版本的后台只读快照，让列表、筛选与总览复用。基础汇总走专用 SQL，昂贵详情按需加载。

**Tech Stack:** .NET 10、WinUI 3、CommunityToolkit.Mvvm、Microsoft.Data.Sqlite、xUnit。

**Spec:** `docs/superpowers/specs/2026-09-22-native-page-performance-design.md`。

## Global Constraints

- 2026-09-22 用户已明确授权“按照你的建议执行修改”。实现位于隔离分支 `codex/native-page-performance`；不替换已安装 MSIX、不改生产数据。执行结果和取舍见 `docs/performance/2026-09-22-native-page-performance.md`。
- 保持当前逐栋来源绑定；缺失批次信息/未知原始枚举值仍显示需复核/未知；不借性能改动伪造有效锁状态。
- 完整性校验依据每个批次自身声明，不引入固定设备数。6471 仅为此次测试样本。
- Excel 保持 `全部设备`+按楼号子表、13列、页面 `第N页`、裙楼/塔楼写座号、无座号 `-`、末列采集时间；筛选与导出读取同一版本。
- UI/性能测试和导出烟测仅使用隔离库；不将本地性能测试称为真实 EMS 现场 E2E。

## Task 1 — P0：固定基线与整合现有草稿

**Files:** 参考 `out/perf-audit-20260922/*`；新增 `native/tools/EmsScout.PerfProbe/` 正式性能 CLI、`native/tests/EmsScout.Tests/DeviceReadPerformanceContractTests.cs`。复核工作树已有9个修改文件与 `DeviceReadOptimization.cs`。

- [ ] 记录待修复源码提交、已安装版本、数据目录、数据库来源身份、规则数量。采用 SQLite Backup 生成唯一测试库；所有仓储 PathResolver 指向该库。
- [ ] 将临时探针的 Stopwatch/累计分配测量移入 CLI，分段记录数据库读取、实时详情、规则匹配、facet、排序、UI 绑定；写明进程首次/热调用区别。
- [ ] 在 WinUI 测试运行增加 DispatcherQueue 心跳、页面首帧/首批数据时间、SQL 调用数与 ListView 实现行数计数。记录进入区域页时楼层读取的实际调用次数。
- [ ] 在改动前保存测试失败基线：区域初始化按行读楼层、一次关键词两次刷新、数据页两次准备、返回总览重新 SearchAsync。对操作次数做自动断言，墙钟阈值放性能 CLI，避免普通单测因机器负载波动。
- [ ] 审阅已有未提交优化，只选择已验证语义的实现继续；特别检查人工座号修正、LIKE 通配符与内存匹配、历史规则。保留来源，不整体覆盖工作树。

**验证：** 探针输出版本与实际数据源；已安装基线与新源码结果分开；生产库不进入写路径。

## Task 2 — P0：楼层目录和区域配置真正只读

**Modify:** `SqliteAreaGroupRepository.cs:23,639,855,927`、`SqliteSchemaMigrator.cs`、`Application/Groups/AreaGroups.cs`、`GroupsViewModel.cs:360`。复用 `AreaGroupRuleIntegrationTests.cs`，新增 `AreaGroupReadOnlyTests.cs`。

**Interfaces:** 在现有 IAreaGroupRepository 增加纯配置读取 `Task<AreaGroupSet> LoadConfigurationAsync(CancellationToken)`；既有 LoadAsync 的带统计行为先保持兼容。LoadFloorsAsync 签名不变，契约变为纯读。

- [ ] 用隔离库新增回归：楼层读取前后 floor_catalog.updated_at、SQL total_changes 不变；打开 ReadOnly 连接能完成查询。
- [ ] 把 EnsureSchema/迁移统一放启动迁移入口；楼层发现同步在设备数据版本变化后单独执行一次，使用一个事务、复用命令，字段未变不 UPSERT。兼容独立 CLI 调用须在其启动阶段迁移。
- [ ] LoadFloorsAsync 仅 SELECT，按楼栋/目录版本缓存或一次读取全目录。首次加载各规则行使用共享选项，不逐行查询。
- [ ] 由 ViewModel 的真实 Building 变化触发选项更新，移除/抑制 XAML 初始化 SelectionChanged 的 I/O；同一楼栋相同版本合并正在进行的请求。
- [ ] 新增 LoadConfigurationAsync，只取组定义/规则；数据页和总览拿组名时不再触发全设备统计。

**核心查询形态：**

```sql
SELECT id, building, floor_label, floor_value, source, enabled, note
FROM floor_catalog
WHERE building = $building AND ($include_disabled = 1 OR enabled = 1)
ORDER BY floor_value, floor_label;
```

**验证：** 93/99/500 行初始化对每个楼栋最多读取一次，重复楼栋零额外查询；保留 BM 追加、历史已选楼层、禁用目录及错误提示行为。楼层发现只在数据变更后运行，不在列表滚动时运行。

## Task 3 — P0：规则变更合并、增量匹配、有限视口

**Modify:** `GroupsViewModel.cs:567,598,721`、`AreaGroupRuleRow.cs`、`AreasPage.xaml:79,184`、`AreasPage.xaml.cs:99`；完善已有 `DeviceReadOptimization.cs`。新增 `AreaRuleRefreshCoordinator.cs` 于 Application/Groups 与对应测试，让事件调度可脱离 WinUI 测试。

**Interfaces:** `IAreaGroupRuleMatchRepository.CountAreaGroupRuleMatchesAsync(IReadOnlyList<AreaGroupRuleRecord>, CancellationToken)` 沿用工作树草稿接口；返回与请求规则等长、同顺序。输入为已复制的规则值，后台不读取 ObservableCollection。

- [ ] 回归用例：连续输入10个字符只产生一次最终匹配；Record/Scope/RuleOrder 通知不单独启动匹配；删除首行只更新序号，不重算每行；切组后旧结果不能覆盖。
- [ ] 仅 Building/Zuo/Floor/MatchMode/Keywords 影响命中统计；用200ms防抖和页生命周期取消。行状态变化一次性合并；过期查询在开始前、读循环与发布前检查取消。
- [ ] 用同一数据版本的轻量设备快照；只改一条规则时只重新统计该条，不通过通用 SearchAsync。必要的人工座号覆盖应保留，实时详情/关注历史不为命中计数加载。
- [ ] 草稿脏状态也按一次业务变更合并，批量初始化/删除/重新编号中抑制重复快照构建。RefreshRules 清理旧事件订阅与 `_ruleOptionsVersions`，避免保留旧行。
- [ ] 改右侧编辑区为受约束 Grid，表头 Auto、列表 `*`；ListView 使用内部滚动/虚拟化。避免依赖固定480px布局，覆盖小窗口与200% DPI。

**过滤通知示意：**

```csharp
if (e.PropertyName is not (nameof(AreaGroupRuleRow.Building)
    or nameof(AreaGroupRuleRow.Zuo) or nameof(AreaGroupRuleRow.Floor)
    or nameof(AreaGroupRuleRow.MatchMode) or nameof(AreaGroupRuleRow.Keywords)))
    return;
// 对该行的值快照排队；由协调器防抖与取消，而非立即读库。
```

**验证：** 100/500/1000 条规则滚动、输入、删除、切组；未保存提示/取消恢复/新建/保存保持；仅可视行及缓冲区被实现，滚动不写库。

## Task 4 — P0/P1：建立后台执行和版本化快照

**Create:** `Application/Devices/DeviceReadSnapshot.cs`、`IDeviceSnapshotProvider.cs`、`Infrastructure/Sqlite/SqliteDeviceSnapshotProvider.cs`、`DeviceDataRevisionMonitor.cs`。**Modify:** `SqliteDeviceReadRepository.cs`、`SqliteRealtimeSnapshotStore.cs`、`App.xaml.cs` 及写入/恢复完成事件。**Tests:** `DeviceSnapshotProviderTests.cs`。

**建议接口：**

```csharp
public sealed record DeviceSnapshotKey(
    string DatabaseIdentity, string SourceIdentity,
    long DeviceRevision, long RuleRevision, long AnnotationRevision,
    long RealtimeRevision, long WatchRevision);
public sealed record DeviceReadSnapshot(
    DeviceSnapshotKey Key,
    System.Collections.Immutable.ImmutableArray<DeviceRecord> Rows,
    string DataStatusText);
public interface IDeviceSnapshotProvider
{
    Task<DeviceReadSnapshot> GetAsync(long? runId, CancellationToken cancellationToken);
    void Invalidate();
}
```

DeviceRecord 的嵌套集合也必须不可变；若不能保证，应在快照 DTO 中复制冻结，不能只冻结最外层数组。

- [ ] 回归：同版本多调用共建一次；来源/规则/修正变化后重建；同runId但batchUid变化不复用；并发查询无实时匹配游标污染；失败可重试；取消一个等待者不破坏其他等待者。
- [ ] 把同步 SQLite 与 CPU 准备放到有并发上限的后台任务，在任务内创建/释放独立连接。UI 线程只捕获筛选/规则值并应用结果；不把整个 ViewModel.LoadAsync 放 Task.Run。
- [ ] 以持久只读连接观察外部提交 data_version；处理 WAL、数据库替换和目录切换。应用保存/导入/恢复成功时发布失效事件；后台构建前后版本不一致则丢弃结果。
- [ ] 当前数据用逐楼来源组合；历史用历史规则、批次UID和持久身份。缓存保留当前和有限历史 LRU，建议历史上限2份，并记录内存占用以调整。
- [ ] 原始实时详情按实际楼栋 SQL 过滤后再反序列化；缓存不可变内容，匹配时创建新 RealtimeDetailSet 或缓存已完成匹配的只读结果。
- [ ] 关注状态单独按版本按需评估；不让规则编辑和基础总览带上关注历史计算。

**验证：** 人工制造慢读取时 Dispatcher 仍响应；数据在构建期间被新批次替换不出现混合版本；未知锁状态/缺失快照保持。性能目标见 Spec，不以调用方法带 Async 作为通过依据。

## Task 5 — P1：数据页一次查询得到列表和联动选项

**Modify:** `DataViewModel.cs:355,505,563,674`、`SqliteDeviceReadRepository.cs:28,146`、`DeviceQuerySpecification.cs`。沿用草稿 `DevicePageAndFilterResult`/`IDeviceReadRepositoryWithFilterOptions`。**Tests:** `SqliteDeviceReadRepositoryTests.cs`、`DataManagementUiContractTests.cs`，增加数据快照行为测试。

- [ ] 以旧 SearchAsync + LoadFilterOptionsAsync 为参照，覆盖楼栋、楼层、座号、通讯状态、名称、模式、风速、温度、锁、区域/未匹配、历史批次与排序；比较总数、行身份/顺序和每项 facet 计数。
- [ ] `SearchWithFilterOptionsAsync(DeviceQuery, CancellationToken)` 获取一次 DeviceReadSnapshot，然后同时生成结果与联动选项；纯配置读取补全组定义，不再取组统计。
- [ ] 保持联动筛选“排除自身条件”的语义；不直接把所有 facet 都改成同一过滤集合。只为当前数据页面实际使用的 facet 计算选项，旧接口兼容其他调用者。
- [ ] 翻页复用已过滤/排序的设备身份列表，避免重新加载/匹配；绑定500行的实际成本单独记录，使用虚拟化和批量集合发布。
- [ ] 后台读取期间保留已显示结果并标记刷新中；快速改筛选以最后请求为准，成功结果只应用一次。
- [ ] Excel 导出捕获同一版本及筛选值；处理构建后到导出前的数据变化，禁止行列表和统计来自不同批次。

**验证：** 相同版本页面复访不读完整数据；单次筛选只准备一次快照；翻页不反序列化实时 JSON。LIKE 的 `%`、`_`、中文和大小写与原行为逐一核对。

## Task 6 — P1：总览专用汇总与复访复用

**Create:** `Application/IDashboardSummaryRepository.cs`、`Infrastructure/Sqlite/SqliteDashboardSummaryRepository.cs`。**Modify:** `DashboardOverviewService.cs:17`、`DashboardAreaGroupBuilder.cs:13`、`HomeViewModel.cs:172,297`、`App.xaml.cs`。**Tests:** `DashboardOverviewServiceTests.cs`、`DashboardAreaGroupBuilderTests.cs`、新增 SQL 汇总一致性测试。

- [ ] 回归：总览基础计数与旧实现一致，虚拟卡不混入；历史/当前/混合来源绑定、未知状态、完成时间保持；复访同版本不调用明细 SearchAsync。
- [ ] 基础总数/通讯/逐栋分布用专用 GROUP BY 聚合，复用 Domain 状态解析归一化，防止空白或未映射值被误计为关机。
- [ ] 区域、模式温度异常和锁统计复用相同版本快照、编译规则和区域成员索引；配置读取不重复计算一套区域统计。
- [ ] 总览缓存以数据来源、规则、实时和异常阈值版本为键。阈值变化仅重算受影响异常，不全量重读库。
- [ ] 基础指标优先显示；区域/锁统计可异步完成且标记加载状态。页面复访直接绑定完成的结果，不清空后重复读取。
- [ ] 完成时间从当前来源对应批次读取；保留当前主分支最新的时间一致性修复，不能用旧工作树版本覆盖。

**SQL 起点：**

```sql
SELECT s.building, c.comm, COUNT(*) AS device_count
FROM cards c
JOIN pages p ON p.id = c.page_id
JOIN sub_areas s ON s.id = p.sub_area_id
GROUP BY s.building, c.comm;
```

历史版本使用 run_cards/run_pages/run_sub_areas 并限制 run_id。SQL 输出仍需经过既有通讯状态解析器分类。

**验证：** 首屏基础汇总≤300ms目标、复访≤150ms目标；离线/未知/需复核不会因为分阶段显示变成有效锁状态。不要只把 HomeViewModel 改 Singleton 作为缓存方案。

## Task 7 — P2：按实测决定更深层优化并验收

**Modify:** 性能 CLI、已定位热点与必要测试；不预设全面改写。

- [ ] 对相同副本、相同规则重测20次热操作，报告P50/P95、累计分配、SQL次数、UI最长阻塞、实际实现行数。将冷启动另列。
- [ ] 若规则计算仍显著，预编译关键词/楼层/座号，按楼栋→楼层→座号缩小候选；共享区域成员索引。避免在设备×规则内循环反复 Normalize。
- [ ] 若50k设备仍不满足预算，再评估基础查询SQL分页/聚合、持久化区域成员表。规则匹配、人工修正、实时筛选必须在分页前处理，禁止为了速度先LIMIT再过滤。
- [ ] 数据库索引用 EXPLAIN QUERY PLAN 确认后增加；不把“加索引”当作事件风暴或UI阻塞的解决办法。
- [ ] 回归执行：`dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj -c Release`；若触及导入/迁移，再执行 `npm run self-test` 与相关 Node 测试。数据导出用 ExportSmoke 对临时库检查既有13列契约。
- [ ] 手工验收：93/99/38真实规则、100/500/1000合成规则；连续输入/删除/切组/返回三页、调整窗口和DPI、采集完成刷新、恢复不同数量历史、外部导入、未知锁状态、相同筛选导出。
- [ ] 整理差异和实测，再单独完成代码审查及发布验证；本计划不授权覆盖他人修改或将未验证构建替换正式程序。

## 推荐交付顺序

第一批完成任务1–3：先解除区域页最严重的重复写入和事件放大。第二批完成任务4–5：后台执行、快照、数据页一次准备。第三批完成任务6–7：总览汇总与整体性能验收。

每批都应可独立验证；P0完成后即复测，不等所有重构结束才观察收益。只要目标满足，不追加物化表或全面查询重写。


## 2026-09-22 执行结果与方案调整

实施分支：`codex/native-page-performance`。完整说明与实测以 `docs/performance/2026-09-22-native-page-performance.md` 为准；上方为执行前任务清单，不把其中所有候选优化勾为已实现。

- 任务1–3：安装版程序集基线、隔离副本探针、纯读楼层/配置、按楼栋共享请求、规则200ms防抖、脏行统计、虚拟化列表均已实现；实际窗口验证93/99/38规则与选中项恢复。
- 任务4–5：后台准备、不可变有界缓存、版本检测/并发共建/取消/失败重试、组合列表选项、分页复用、单快照导出均已实现。差分31组查询无差异；为保留旧版实时来源判定，快照按SQL候选范围缓存，不强制所有筛选共享全库候选。
- 任务6：SQL基础聚合和总览版本缓存已实现。首次完整总览后台约236ms，暂不做分阶段首屏；设置变更通过缓存键触发新的完整结果，未额外实现仅异常字段的局部重算。
- 任务7：20次热调用、500/1000合成规则、完整测试、Release构建、导出烟测与独立代码审查已执行。原始证据在工作树 `.superpowers/sdd/native-page-performance/` 和 `out/performance-verification/`。
- 验证边界：未替换安装版，未进行现场EMS采集；未完成200% DPI/1000规则完整UI压测、50k设备压测和正式MSIX首帧基准。本次未新增索引/物化表/公共snapshot-provider层，不将这些候选优化算作已实施。
