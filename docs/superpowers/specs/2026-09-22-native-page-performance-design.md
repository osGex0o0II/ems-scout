# 原生页面性能：定位证据与改进设计

日期：2026-09-22。状态：按用户要求完成定位与方案，不代表已经实施或完成界面验收。

## 1. 调查范围与环境

- 主工作区 HEAD：`973eb27`。本轮未修改产品代码、正式数据库或已安装程序。
- 正在运行的 MSIX 版本：`1.0.74.0`。
- `%LOCALAPPDATA%/EMS Scout/workspace-root.txt` 指向 `D:/Code/Git/ems-scout/.worktrees/area-rule-groups`。设置的 DataDirectory 为 `out`。
- 主目录的 `out/ac.db` 与程序实际工作树的库不是同一个文件；前者缺少新规则，不能用来代表用户场景。
- 实际库的 SQLite Backup 副本：`out/perf-audit-20260922/installed-probe.db`。备份源以 ReadOnly 打开，所有可能写库的现有仓储方法仅对副本调用。
- 实际数据：6471 台当前设备，19719 条历史设备，3 个历史批次；3 个区域组，规则数分别为 93、99、38，共 230；关注规则数为 0。
- 性能探针直接引用已安装 MSIX 内的 Infrastructure/Application/Domain DLL，Release 控制台运行。已安装仓储仅实现 `IDeviceReadRepository`，尚无工作树中新增的优化接口。
- 下文代码行号使用主工作区的源码。工作树存在未提交改动，单独列出，未覆盖、暂存或提交这些改动。

## 2. 可复核的测量

证据：`out/perf-audit-20260922/installed-console.log`、`installed-measurements.json`。可复跑的临时探针：同目录 `Program.cs`、`PerfProbe.csproj`。

| 调用 | 进程内首次调用 ms | 后两次 ms | 热调用累计托管分配量 |
|---|---:|---:|---:|
| 区域组 LoadAsync | 213.71 | 96.11–116.44 | 48.75–49.44 MiB |
| 单栋楼层选项 LoadFloorsAsync | 123.68 | 134.08–134.74 | 约 0.26 MiB |
| 完整设备查询，只取 50 条 | 248.99 | 110.29–137.40 | 约 70.20 MiB |
| 完整设备查询，取全部 | 239.95 | 133.12–150.94 | 约 70.25 MiB |
| 筛选项 LoadFilterOptionsAsync | 323.24 | 131.43–137.10 | 约 86 MiB |
| 总览服务 LoadAsync | 650.32 | 255.66–273.01 | 约 160.65 MiB |

这些数字是服务/仓储耗时，不包含 WinUI 控件创建、布局、首次绘制，也不是清空系统文件缓存后的冷启动。分配量是调用期间累计分配，不是驻留内存。

另外把首组 93 条规则逐行调用一次楼层读取进行了路径重放，累计 **9814.75ms**。这是“每行初始化回调都执行”的模拟放大证据，不是实际页面端到端计时；真实 SelectionChanged 次数、控件实现数和帧间隔须在实施阶段采样。

所有被测调用的 `completedOnReturn=true`，`returnMs≈totalMs`：调用方在拿到 Task 之前已承担全部工作。结合 Page → ViewModel → 仓储直接调用链，可确认这些路径会同步占用调用线程；在界面事件中就是 UI 线程。`ConfigureAwait(false)` 不会把已同步完成的 SQLite 调用搬到后台，`Task.Yield()` 也只推迟开始时间。

规则行事件单独重放得到：

```text
Keywords 修改 -> Keywords, Record             # 两次通知
Building 修改 -> Building, Record, Scope       # 至少三次；座号重置还可能增加
规则重新编号 -> RuleOrder, Record               # 每个被重新编号的行两次
```

## 3. 根因与代码依据

### A. 区域页：楼层选项查询是隐藏的批量写入

链路：`AreasPage.xaml:205` 楼栋 ComboBox SelectionChanged → `AreasPage.xaml.cs:99` → `GroupsViewModel.cs:360` → `SqliteAreaGroupRepository.cs:23`。

`LoadFloorsAsync` 每次用读写连接打开库，执行 EnsureSchema，再执行 `SyncFloorCatalogFromCurrentAsync`。后者在 `:855` 读取**所有楼栋**的楼层，并在 `:878` 起逐条 UPSERT，未在此循环外建立批量事务，还更新 updated_at。最后才按请求楼栋读取目录。

ViewModel 的 `RefreshRuleOptionsForRowsAsync` 已在一次调用内按楼栋去重，这是已有的局部优化；但 XAML 逐行初始化事件没有被同一机制拦住，也没有楼层目录缓存。93/99 行可放大成大量重复同步写入。这是最高优先级。

### B. 区域页：属性通知被当作业务变化反复执行

`GroupsViewModel.cs:721` 只排除 MatchCount/MatchCountText，其余通知均重新计算整份草稿并调用 `RefreshRuleMatchCountsAsync`。后者 `:607` 先执行 `SearchAsync(Limit:50000)`，然后逐条规则统计。

`AreaGroupRuleRow.cs:124` 的关键词 setter 会通知 Keywords 和 Record；XAML `:215` 又设置每次文本变化更新来源。因此一个字符会启动两次完整查询，之后还对整组规则重算。删除前面的规则触发 RenumberRules，后续每个改号行也会产生两次查询。版本号只避免过期结果覆盖，不会省掉已启动的工作。

### C. 区域页：列表没有受约束的视口

`AreasPage.xaml:79` 外层 ScrollViewer → StackPanel → `:184` 规则 ListView，纵向尺寸没有明确约束。结构会破坏虚拟化所需的有限测量空间，放大控件创建与初始化事件。每行有四个 ComboBox、一个 TextBox 及多组 ObservableCollection。实际 realized-container 数量尚未通过 WinUI 采样确认，按高置信度待测项处理。

### D. 数据页：一次展示重复准备同一批数据

`DataViewModel.cs:355` 初始化，或 `:563` 应用筛选，先调用 ReloadFilterOptionsAsync，再调用 LoadPageCoreAsync。

前者 `:521` 先加载区域组及全部统计，再执行 LoadFilterOptionsAsync；后者 `:678` 再执行 SearchAsync。两次设备链路都会加载设备、准备区域规则、附加实时详情/人工修正、匹配区域、可能计算关注状态。

`SqliteDeviceReadRepository.cs:177` 先用 WithoutFacets 去掉楼栋等筛选来计算联动选项；`:217` 起对 17 个 facet 多轮扫描。原生数据页只显示其中一部分。

`SearchAsync` 的 SQL `:580` 没有 LIMIT/OFFSET；`:118` 在完成读取、匹配与排序后才 Skip/Take。现有数据页 PageSize=500。探针刻意取 50 条仍分配约 70 MiB，与取全部几乎一致，证实“小页”没有减少准备成本。

### E. 总览：轻量数字使用了通用明细流水线

`DashboardOverviewService.cs:21` 调用 SearchAsync(Limit:50000)，然后重新汇总。区域组仓储 `:927` 又加载一次设备用于区域统计；`DashboardAreaGroupBuilder.cs:39` 再次匹配设备与区域规则。

总览并非所有内容都可用一条 COUNT 替代：它还有区域组异常、模式/温度及锁状态。但基础总数、通讯分布、逐栋统计无需为每次导航重新读取所有详情、标签和关注历史。

### F. 页面重建与同步执行叠加

`App.xaml.cs:172`、`:174`、`:176` 三个 ViewModel 均为 Transient。`MainWindow.xaml.cs:298` 调用 Frame.Navigate；页面未配置 NavigationCacheMode；各 Page_Loaded 都重新加载。仓储虽然为 Singleton，但没有可复用数据快照。

因此“返回页面”会重新建立 VM、读取数据、重算、清空并逐条填充绑定集合。仅把 VM 改 Singleton 不够：现有页面生命周期 CTS 离开即取消，直接缓存 Page 还需重建 Token、处理草稿及事件订阅。

### G. 次要问题与增长风险

- `LoadEnabledAreaGroupRulesAsync` 在未选组时重复读取同一组规则，且逐条 `Append(...).ToArray()` 增加分配；Prepare 后 MatchesScope 仍在内层重复规范化座号和楼层。
- `SqliteRealtimeSnapshotStore.cs:237` 按批次取全部 payload，再在 C# 按楼栋过滤：单栋筛选也可能反序列化整批。不同来源分组时还可能重复读取。
- `SqliteDeviceWatchRepository.cs:13` 通用查询会评估关注状态；有关注规则时每条规则遍历当前设备和历史。实际库目前 0 条，**不是本次主要耗时证据**。
- `SqliteCollectionRunRepository.cs:16` ListAsync(null) 全列批次并逐批获取楼栋计数；目前 3 批只耗约 6ms，属历史增长风险，不应先优化它。
- `AppDataPathService.DataDirectory` 每次解析调用 settings.Load() 读 settings.json，是小额重复 I/O，优先级低。

## 4. 已有未提交优化的核对

工作树 `.worktrees/area-rule-groups` HEAD 为 `81d4316`，已有以下本地改动：

- 150ms 防抖、取消上次规则匹配、离开页面时取消。
- `IAreaGroupRuleMatchRepository.CountAreaGroupRuleMatchesAsync` 轻量统计。
- `IDeviceReadRepositoryWithFilterOptions.SearchWithFilterOptionsAsync` 合并筛选项和列表准备。
- 规则 ListView 改为 MaxHeight=480 并移除外层 ScrollViewer。

这些方向部分正确，但不等于问题已解决：仍保留楼层读取中的逐次 UPSERT、缺少可靠后台执行边界、没有跨页面快照与总览缓存。固定 480 高度需验证短窗口/高 DPI。轻量规则统计只按 DB 坐标算座号，应验证人工座号修正与旧查询是否一致。新增合并查询需验证 SQL LIKE 与内存 Contains、联动选项、历史规则与排序语义一致。

本轮实测已安装 DLL 不含两个新增优化接口，不能将未提交改动的预期效果视为当前程序已发布能力。

## 5. 推荐设计

比较三种路线：

1. 只加 Task.Run/防抖：能缓和无响应，但重复读取/写入与高分配仍存在，不完整。
2. **推荐：轻量只读查询 + 后台构建不可变快照 + 按数据版本失效 + 受约束的列表虚拟化。** 适合当前数千台规模；初次按需构建一次，页面复访和编辑重用。
3. 立即把全部规则和实时富集物化入 SQLite：长期可扩展，但涉及历史、人工修正和采集导入的事务一致性；暂不作为首轮前置条件。

推荐数据流：

```text
采集导入/恢复/规则保存/人工修正/设置变化/外部库替换
    -> 发布对应版本变化
    -> 只使受影响的快照与汇总失效

页面导航 -> 读取已完成快照 -> UI 绑定
首次/失效 -> 一个后台任务构建快照 -> 发布不可变结果 -> UI 绑定

区域草稿编辑 -> 200ms 防抖 -> 只计算变更规则 -> 更新命中数
数据筛选 -> 同一快照计算结果页与选项 -> 翻页复用
总览 -> 基础 SQL 聚合 + 缓存的区域统计/实时锁状态
```

快照标识不能只有 RunId：包含规范化数据库身份、当前逐栋来源 `(building, runId, batchUid, runKey, state)` 或历史身份、设备/规则/标注修正/实时快照/关注版本。总览附加异常阈值设置版本。历史规则必须使用该批次的规则快照。

外部 Node 导入也会更新库：应用内事件之外，需要持久只读连接上的 PRAGMA data_version 检测，加数据库被替换/数据目录切换的身份检测。不能靠每次新开连接读取 data_version，也不能只比较 ac.db 时间戳（WAL 下不可靠）。首次构建前后校验版本，变化时丢弃并重建，禁止混合批次。

缓存必须有大小边界与失效策略：保留当前快照与有限历史 LRU；同一版本合并进行中的构建；失败/取消结果不长期缓存。取消某个页面的等待不能错误取消其他页面共享的构建。

`RealtimeDetailSet` 含 UsedRowIds、TakeExact 使用游标，是可变对象，不能跨请求直接共享。缓存不可变原始详情或最终只读结果，每次匹配拥有独立使用状态。

## 6. 不变量与验收目标

- 保持包含/排除、仅排除规则、空规则、BM/2.5F、5/6号座号、未保存草稿及历史快照语义。
- 保持当前逐栋来源绑定；缺失批次信息/未知原始枚举值仍显示需复核/未知；不借性能改动伪造有效锁状态。
- 完整性校验依据每个批次自身声明，不引入固定设备数。6471 仅为此次测试样本。
- Excel 保持 `全部设备`+按楼号子表、13列、页面 `第N页`、裙楼/塔楼写座号、无座号 `-`、末列采集时间；筛选与导出读取同一版本。
- UI/性能测试和导出烟测仅使用隔离库；不将本地性能测试称为真实 EMS 现场 E2E。

建议验收门槛，均为未来目标而非已达成结果：

| 场景 | 目标 |
|---|---|
| 三页复访，数据未变 | P95 ≤150ms，不全量重读或重匹配 |
| 区域页 100/500 条规则 | 界面先响应；列表只实现可视行及缓冲区；不随总规则数初始化全部控件 |
| 输入关键词 | 输入响应 ≤50ms；停止输入约200ms后合并计算；只改一行时不全组重算 |
| 数据筛选/翻页，快照已存在 | P95 ≤200ms；一轮筛选至多一次快照准备；翻页不重读实时详情 |
| 当前样本首开数据页 | P95 ≤1s，读取/匹配期间 UI 可交互 |
| 总览首屏基础统计 | P95 ≤300ms；区域/锁统计可分区完成 |
| UI 执行片段 | 不出现 >50ms 的查询或匹配工作直接占用 UI 线程 |

至少测 20 次热操作再报告 P50/P95；冷启动另列。额外使用 1万/5万设备、100/500/1000 条规则及增加历史批次的合成样本观察增长趋势，不把合成数据结果当作现场结果。
