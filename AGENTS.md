# AGENTS.md — EMS 空调枚举项目上下文

## 项目目标
遍历 6 栋楼全部空调卡片 → SQLite → 数据管理筛选 Excel 导出，区分 开机/关机/离线

## 关键文件

| 文件 | 用途 |
|------|------|
| `src/enumerate.js` | 主枚举器（Playwright + Edge CDP），2083 行 |
| `src/collect.js` | 一键编排器 + TUI 菜单 |
| `src/verify-live.js` | 实时浏览器状态验证工具 |
| `scripts/report.js` | legacy 多格式报表脚本，默认禁用，当前不作为主流程入口 |
| `scripts/import.js` | JSON→SQLite 导入（Node.js + better-sqlite3） |
| `scripts/field-e2e.ps1` | 真实 EMS 现场端到端验证脚本；只写 `out/field-e2e-*` 临时目录 |
| `scripts/dump-aircons.js` | legacy Excel 明细脚本，默认禁用，当前不作为主流程入口 |
| `scripts/dump-public.js` | legacy TXT 公区 ON 清单脚本，默认禁用，当前不作为主流程入口 |
| `native/tools/EmsScout.ExportSmoke/` | 数据管理 Excel 导出烟测 CLI |
| `scripts/schema.sql` | SQLite 建表语句 |
| `out/ac.db` | SQLite 数据库 |
| `out/enum_full_v5.json` | 全量枚举结果 |
| `data/1号楼/` | 1号楼已验证数据归档 |
| `data/2号楼/` | 2号楼当前数据归档 |
| `CHANGELOG.md` | 修改记录 |
| `.context-summary.md` | 上下文快照（每次会话更新） |

## 运行命令

```bash
# 全量
node src/enumerate.js --edge
node scripts/import.js
npm run native:run   # 原生数据管理页，筛选后导出当前筛选 Excel

# 单栋验证
node src/enumerate.js --edge --bldg=1号

# 实时验证
node src/verify-live.js [--building=1号] [--floor=1]

# 自检模式
node src/enumerate.js --edge --verify

# 真实 EMS 现场验证（推荐自动打开隔离可采集 Edge；不写生产库）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/field-e2e.ps1 -Building 1号 -LaunchEdge

# 单栋真实端到端：采集 → 临时 SQLite → 质量报告 → 筛选 Excel 烟测
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/field-e2e.ps1 -Building 1号 -LaunchEdge -RunSingleBuilding
```

## 架构速记

```
collect.js
  └── enumerate.js → enum_full_v5.json → import.js → ac.db → 数据管理页 → Excel
                      (2-10 min per bldg)            (5s)      筛选后手动导出
  └── quality-report.js → 质量审计
```

当前唯一面向用户的导出方式：原生应用 `数据管理 -> 导出当前筛选 Excel`。
旧 `report.js` / `dump-aircons.js` / `dump-public.js` 默认禁用，仅允许显式设置 `EMS_ENABLE_LEGACY_REPORTS=1` 的应急 legacy 运行。

现场 E2E 不变量：
- `scripts/field-e2e.ps1` 必须使用唯一 `out/field-e2e-*` 目录。
- `-LaunchEdge` 必须使用随机本机 CDP 端口、`--remote-debugging-address=127.0.0.1`、本次 runDir 独立 profile。
- 默认必须清理本次启动的 Edge 和 profile；只允许显式 `-KeepBrowser` / `-KeepProfile` 保留。
- 导入必须显式传 `EMS_JSON_PATH` 和 `EMS_DB_PATH`，且 `EMS_DB_PATH` 不能解析到 `out/ac.db`。
- `quality-report.js` 必须使用临时 `EMS_DB_PATH` 和 `EMS_QUALITY_OUT`。
- Excel 烟测必须对临时 DB 运行，不得把本地测试通过描述成真实 EMS 端到端通过。

## 日志系统（`src/logger.js`）

`--log-level=DEBUG --log-category=RULE` 查看规则判定细节。

### 日志级别
| 级别 | 用途 | 颜色 |
|------|------|------|
| `ERROR` | 不可恢复错误 | 红 |
| `WARN` | 重试成功、质量降级 | 黄 |
| `INFO` | 主流程进度（默认） | 白 |
| `DEBUG` | 规则分支、Vue 富集、页崩溃 | 灰 |

### 日志类别
| 类别 | 颜色 | 内容 |
|------|------|------|
| `ENUM` | 青 | 页切换、子区计数 |
| `QUALITY` | 黄 | `checkCardQuality` 判定、`adaptivePolling` 轮询 |
| `RULE` | 紫 | `uniformTemplate`、`knownDefaultValues` 匹配 |
| `VUE` | 绿 | indoor/setTemp/fan/mode 富集替换 |
| `CRASH` | 红 | `__ems` 丢失、`recoverFromCrash` |
| `NET` | 灰 | WebSocket 数据、网络请求 |

### 文件输出
```bash
node src/enumerate.js --edge --log-file
# → out/enum_2026-06-17.log (NDJSON, 可 grep)
```

### 规则追踪示例
```bash
# 只看规则和富集日志
node src/enumerate.js --edge --log-level=DEBUG --log-category=RULE,VUE --log-file
grep '"cat":"RULE"' out/enum_2026-06-17.log
```

## 关键参数
- `--edge`: 用 Edge CDP（必须；无 headless 模式）
- `--bldg=1号`: 限单栋
- `--append`: 追加（不覆盖已有结果）
- `--recapture=x:y:z`: 补采特定子区
- `--verify`: 实时浏览器验证模式（对比 DB）
- `--self-diagnose`: 枚举中每 5s 自动健康检查
- `--no-net-monitor`: 关闭网络监控
- `--log-level=DEBUG`: 日志级别（ERROR/WARN/INFO/DEBUG，默认 INFO）
- `--log-category=RULE,QUALITY`: 日志类别过滤（ENUM/QUALITY/RULE/VUE/CRASH/NET，默认全部）
- `--log-file`: 输出 NDJSON 日志到 `out/enum_YYYY-MM-DD.log`（与 stdout 同时）
- `--log-level=DEBUG --log-category=RULE`: 只看规则判定分支

## 通讯状态判定（核心逻辑）
IND_MAP 硬编码映射 3 种 indicator 图片 href → 开机/关机/离线：
- `3bdc38eda0ae77f26807b2b6cdde4456.png` = 绿色 = **关机**
- `56f45bb314d74cc8da6c6c8e5942d08d.png` = 红色 = **开机**
- `833bea6e66e7ab0e55704d655e135c7c.png` = 灰色 = **离线**

**IND_MAP 必须在 Vue try/catch 外部**，否则 Vue 不可用时映射被跳过。

## 公区规则
`layout='group'` 或命名含 GQ/WSJ/DTT/FDT/XFDT/CSJ/FWJ/ZBS/ZSG/MD/RDJHJF → 公区
`QL-NNN`（横线后跟数字）→ 非公区（裙楼具体房间）

## 速度优化（2026-06-07）
基于实测页面响应时间（200ms SVG 渲染 / 450ms WS 就绪 / 4.4s indicator 稳定）：
- 消除固定 pause：MENU_CLICK 200ms, FLOOR_CLICK 100ms, PAGE_CLICK 100ms
- waitForDataReady 稳定阈值从 6→2（3.5s→0.4s）
- waitForSvgStable 轮询 500ms→200ms
- waitForLoadedCards 间隔 2000ms→300ms
- 实测提速 **3.7-3.9x**（1号楼 8.5min→2.2min，2号楼 48s→13s）

## 常见陷阱
- 切楼层后 SVG 在 shadow DOM 渲染，`querySelector` 需进 `.pi-svg-container` 的 shadowRoot
- WS 数据通过 CDP 不稳定（某些页面只有 1 条数据）
- Indicator 图片尺寸 29×27，开关图片 43×18
- 0-0001-KT = 数据未加载完成的占位符
- `pwsh` 不在 PATH，用 `powershell -NoProfile -ExecutionPolicy Bypass -File`
- `ImportExcel` PS 模块因 NuGet 不可用；用 `npm xlsx` 代替
- `GetTempFileName` 在 Windows 上可能因临时文件满 65535 而失败
- 2.5F 2M001-KT 无 indicator 图片（EMS 固有缺陷）
- 某些子区（2F/3F 塔楼）默认激活，不重复点击

## 当前 DB 状态（2026-06-17 全量）

| 楼栋 | 子区 | 合计 | 开机 | 关机 | 离线 |
|------|------|------|------|------|------|
| 1号 | 30 | 1493 | 215 | 1206 | 72 |
| 2号 | 5 | 110 | 10 | 97 | 1 |
| 3号 | 30 | 1106 | 378 | 302 | 426 |
| 4号 | 30 | 1096 | 195 | 183 | 717 |
| 5号 | 17 | 286 | 71 | 191 | 24 |
| 6号 | 31 | 2480 | 631 | 1572 | 277 |
| **合计** | **143** | **6571** | **1500** | **3551** | **1517** |

switch=ON 设备 1529 台（公区 121 + 非公区 1408）

> **2026-06-17 注**：5号 286 为旧基线恢复（6号 A座 BM 跳过写在 `bldg.building === '6号'` 条件内，不波及 5号）
> **2026-06-17 18:10 注**：2号 110 旧口径包含 2.5F 同页重复的 3 张 GQ 卡；代码基准改为唯一卡数 **107**，旧 DB 未重导入前会保留 110 并由质量报告标记重复。

## 实测页面响应时间（2026-06-10 基准）

| 阶段 | 典型的实际耗时 | 说明 |
|------|---------------|------|
| SVG 文本渲染 | ~200ms | `isReady` 检测到 >5 text 元素 |
| 开关图片加载 | ~450ms | `extractCards` 中 switch href 完成 |
| WS 数据就绪 | ~400ms | `waitForDataReady` 稳定检测 |
| 指示图稳定 | **450-600ms**（页面）/ **600ms**（翻页） | 3 种 indicator 全部加载完毕 |
| 页面切换 | ~200-800ms | WS 计数变化检测 |

**关键发现**（2026-06-10）：SVG_STABLE 实际在 450-600ms 达成，之前的基准测试（6.15s）是特定时段/子区的异常值。

## 代码审查修复（2026-06-08）
- `c.indicatorSrc` → `c.indicator`（5处）：`verifyCardIntegrity`、`captureEnhancedSnapshot`、`--verify` 模式
- 删除调试残留空循环（提取Vue后不做事）
- 删除未用常量 `W.TAB_CLICK`
- `verify-live.js` DB 查询修正：JOIN `sub_areas` 取 building/floor

## 5号座号边界修正（2026-06-08）
基于 SVG x 坐标数据驱动校正 `getZuo5`/`getZone` 边界：

| 区域 | 旧边界 | 新边界 | 原因 |
|------|--------|--------|------|
| A/B | 400 | 400 | 保持 |
| B/C | **720** | **616** | 原值偏右 104px，x=695 C座 BM 被误划入 B座 |
| C/D | **920** | **874** | 原值偏右 46px |
| D/E | 1120 | 1120 | 保持 |
| E/F | **1400** | **1424** | 原值偏左 24px |

修正文件：`enumerate.js`、`dump-aircons.js`、`dump-public.js`

## 卡片数据质量修正（2026-06-08）
问题：部分卡片 indoor/setTemp 值错误（indoor=小整数如1-4，与 setTemp 互换）
根因：
- **Grid layout** 硬编码 Y 偏移 (y+140/170/200/235) 与部分子区实际布局不符
- **Group layout** 使用全局距离排序，无 Y 范围过滤，误取邻卡文本
- **Vue 富集** 用 WebSocket 无效值覆盖了正确的 SVG 文本值

修复：
1. **Grid layout** `extractCards`：按 Y 排序取最近的 2 个 ℃ 文本（上=indoor，下=setTemp），x 搜索范围 80→100px
2. **Group layout** `extractCards`：改用 `nearest` 带 Y 范围限制，扩展 mode 正则包含 `地暖`/`制热+地暖`
3. **Vue 富集**：优先保留 SVG 值，Vue 仅在 SVG 值缺失或超出合理范围（indoor 0-60℃/setTemp 5-40℃）时覆盖
4. 所有子区 layout 统一用 `modeTexts` 正则：`制冷|通风|制热|送暖|地暖|制热+地暖`

修正文件：`enumerate.js`（`extractCards` → grid/group/Vue enrichment）

## 楼层辨认
1号/2号/6号 所有卡品的 DB 楼层号与卡名前缀完全一致，零错配。

## 最新修复（2026-06-09）

### 6号 A座 1F BM 采集修复
- `findSpecialPageBtns`：还原 `r.left > 1500`（BM 按钮在 C座列 x=1725 但切换的是 A座 BM inline 视图）
- **Stale reference 修复**：BM 点击后 SVG 重新渲染 → 元素 ID 改变。返回 1F 时不再用缓存的旧 ID `sBtns['1F']`，而是重新扫描 SVG 获取当前 1F 按钮 ID

### findAllSubAreaGroups 动作按钮过滤
- `findAllSubAreaGroups` 新增 Y 坐标中位数 ±30px 过滤（`enumerate.js:149`），排除动作栏上的 "1F" 按钮（y≈226）被误当子区。BM（y≈244）保留不受影响
- 修复了 C座 1F 被枚举两次的重复问题（2480 卡而非 2504，去重 24 张）

## 数据就绪保护（2026-06-10）

### 问题：采集时开关/温度/模式数据未完全加载
根因：`waitForLoadedCards` 无法区分「开关图片未加载」和「全部真关机」——开关图 href 为空时所有卡 switch='-'，被误判为全部关机只多等一轮就放行。翻页路径只等 SVG_STABLE 而不等 WS 数据稳定，翻页超时仅 800ms 偏紧。

### 修复 1：`waitForLoadedCards` 区分开关图状态
- 新增 `switchLoaded` 检查：`d.cards.some(c => c.switch !== '-')`
- 开关图全空时继续等待（之前与全部关机混为一谈只多等 200ms）
- 代码：`enumerate.js:603-634`

### 修复 2：翻页 SVG 超时 4×200→6×200（800ms→1.2s）
- 4 处 page-turn 路径统一更新
- 代码：`enumerate.js:1301,1354,1576,1692`

### 修复 3：翻页路径加 `waitForDataReady`
- 翻页后先等 WS 数据稳定（`maxRetries:5, waitMs:200`），再等 SVG_STABLE
- 确保 Vue WebSocket 数据在 `extractCards` 前已完整
- 代码：`enumerate.js:1300,1353,1574,1690`

### 修复 4：`checkCardQuality` 模板默认值检测（含统一值检测）
不同建筑模板默认值不同：1号=0℃/0℃/0，3/4号=26℃/25℃/中。共同特征：所有卡 indoor/setTemp/fan/mode 完全一致。
```javascript
function checkCardQuality(cards) {
  const uniformValues = n >= 3 && uniqueIndoor.size <= 1 && uniqueSetTemp.size <= 1 && uniqueFan.size <= 1 && uniqueMode.size <= 1;
  const knownDefaultValues = (indoorVal === '0' && setTempVal === '0' && fanVal === '0') ||
    (indoorVal === '26' && setTempVal === '25' && fanVal === '中' && modeVal === '制冷');
  const uniformTemplate = uniformValues && knownDefaultValues;
  return { ok: placeholderNames === 0 && !uniformTemplate && ((allOffline && n >= 2) || (switchLoaded >= n*0.5 && (hasRealTemp || withRealFan > 0))), details };
}
```

**2026-06-17 简化**：`uniformTemplate` 删除 `!(allOn || allOff)` 和 `!(uniformComm && !allOffline)` 例外条件。
此前全部关机的楼层（26/25/中/制冷）被误判为"非模板"，导致跳过自适应轮询直接采集下页。
配合 `adaptivePolling` 新增的稳定模板提前退出（3 轮一致即接受），约 1.5s 即可放行模板数据。

### `checkCardQuality` ok 公式优先级修复（2026-06-17）
- `rules.js:93` `!uniformTemplate` 从「必须条件」改为「优先级条件」——非模板直接放行，模板数据才走开关/温度检查
- 此前：混合页面（12离线+8关机）因 `switchLoaded < 50%` 被误判为未加载，空等 45s
- 修复后：非模板直接 `ok=true`，离线卡多的页面立即放行；模板数据走稳定模板提前退出 ~1.5s
- switch/mode 填充率 <75% 时自动重试（全等待 waitForDataReady + waitForSvgStable）
- 重试成功用新数据，失败保留原有（至少卡名完整）
- 全量提取点（首页/翻页/补采/BM/子标签）均加质量日志
- **首页提取另加 retry**：动态/无分页/编号三类第一页在质量不通过时自动重试（maxRetries:15+25），新版 `checkCardQuality` 可识别模板默认值
- 代码：`enumerate.js:707-728` + 16 处调用（含 3 处首页提取重试 + 1 定义 + 12 页内质量检查）

### catch-22: 无分页路径二次扫描修复（2026-06-17）

1. **问题**：`collectPage`/`capturePages` 的初始 `findPageBtns()` 在 SVG 尚未渲染完成时扫描，对于分页按钮延迟渲染的子区（如 30F），`uniquePages.length === 0` → 进入"无分页"路径，该路径 waits 后不重扫按钮，只抓一页
2. **修复**：waits 后加 `finalBtns` 重扫，发现按钮则 `return collectPage(prefix)` 递归重试
3. **递归安全**：二次进入时 SVG 已稳定，走标准分页路径，无无限循环
4. **BM 化妆修复**：主循环跳过 `floor === -2`（BM），标记 `err: 'bm inline'`，不影响下游处理

### `knownDefaultValues` 补全模板模式（2026-06-17）
- `rules.js:84` 1号/2号/5号/6号的默认模板是 `0/0/中/制冷`，但原代码只检查了 `fan='0'`（实际 fan='中'），56 页共 1496 张模板卡完全漏检
- 修正：`fanVal === '0'` → `fanVal === '中' && modeVal === '制冷'`
- 自测同步：`scripts/self-test.js:44` `loaded34` 断言 `ok=true` → `ok=false`
- `npm run self-test` 通过

### 稳定模板提前退出加 `phCount` 检查（2026-06-17）
- `enumerate.js:142` 稳定模板退出条件仅检查 `allLoaded && template`，忽略了占位符卡名（`0-0001-KT`）
- B1F 19 张卡全部为占位符名，但 comm/switch 有值 → `allLoaded=true` → 3 轮后（~400ms）接受
- 修正：新增 `phCount = c => !c.name || c.name === '0-0001-KT'`，条件改为 `allLoaded && phCount === 0 && template`
- 占位符卡名的楼层继续轮询直至真实数据到达或 45s 超时

### Vue 富集 `indoor=0` 未被替换（2026-06-17）
- `enumerate.js:617` Vue 富集条件 `svgVal < 0` 不捕捉 `0`（`0 < 0 = false`）
- 模板默认值 `indoor=0` 未被真实温度替换 → `uniformValues=true` → `uniformTemplate=true`
- 例：2号 2F 32 张卡全部 `indoor=0`，稳定模板提前退出后数据被错误接受
- 修正：`svgVal < 0` → `svgVal <= 0`，`indoor=0` 现被正确替换为真实温度

### 实测效果
- 5号测试：捕获到 1 次 `LOW QUALITY: sw=4/14`（仅 4/14 卡有开关数据），重试后全部填满
- 总耗时影响：翻页路径约 +400ms（waitForDataReady 稳定检测），质量重试仅低质量时触发

## 等待链优化汇总（2026-06-10）
对比 2026-06-09 全量（456.4s / 7.6min）：
- 全局并行化：首栋 `Promise.all([waitForLoadedCards, waitForDataReady, waitForSvgStable])`
- SVG_STABLE 阈值 3→2，间隔 200→150ms（3.75s 上限）
- 翻页 SVG 超时 1.6s→0.8s→1.2s（4→6×200ms）
- `waitForReady` 首栋 10×200ms，后续 6×200ms
- `waitForDataReady` 10×200ms，稳定 1
- 实测提速 **~21%**（456s→360s），数据质量提升 0 缺漏卡

## 最新修复（2026-06-17 v2）

### 根因分析 + 行业对标

本次修复基于全量数据重复检测 + 联网核查 BACnet/BMS 行业标准 + Playwright 数据采集最佳实践。

| 问题 | 行业方案 | 来源 | 实现 |
|------|---------|------|------|
| 模板默认值被接受为真实数据 | BACnet Reliability Property — 永不混同模板与健康值 | ASHRAE 135, JCI | `hasRealTemp` 内容级等待 |
| 等数据而非等时间 | Needle Test — 等具体字段非空 | Playwright, Brightdata | `parseFloat(c.indoor) > 0` |
| `page.evaluate` 抛异常 | 两层 try-catch | Playwright 官方 | 3 处 `.catch()` + injectHelpers |
| 全离线数据合理放行 | Stale 本身就是有效状态 | BACnet, Beckhoff | `allOffline` 快速通道 |
| 2卡模板漏检 | 模板不依赖设备数量 | RFC 3512 | `n >= 3` → `n >= 2` |

### 修正 1：`uniformValues` 覆盖 2 卡页面

- 文件：`rules.js:82`
- 改前：`const uniformValues = n >= 3 && ...`
- 改后：`const uniformValues = n >= 2 && ...`
- 原因：3号/4号/5号 2卡 DTT/WSJ 页面统一模板值不被检测 → 直接放行
- 安全：`knownDefaultValues` 仅匹配两个精确模式，真实数据误判概率极低

### 修正 2：`adaptivePolling` 稳定模板提前退出 → `hasRealTemp` 内容级等待

- 文件：`enumerate.js:adaptivePolling()`
- 改前：`allLoaded && template` + 3 轮稳定（~1.5s）→ 接受
- 改后：删除稳定模板逻辑，改为 `data.cards.some(c => parseFloat(c.indoor) > 0)` → 有真实温度才放行
- 原因：模板默认值 `OFF/0/0/中/制冷` 的 `comm='关机'` 和 `switch='OFF'` 满足 `allLoaded`，被错误提前接受
- 删除字段：`STABLE_TEMPLATE_ROUNDS`、`stableTemplateRounds`
- 行业对标：Needle Test — "test for the specific value, not structural heuristics"

### 修正 3：全离线快速放行

- 文件：`enumerate.js:adaptivePolling()` 在 `hasRealTemp` 后追加
- 条件：`phCount === 0 && data.cards.every(c => c.comm === '离线')`
- 原因：全离线卡 `indoor=0` 不满足 `hasRealTemp`，若无此条件会等 45s 超时
- 行业对标：BACnet "stale is a valid state"

### 修正 4：`qualityCheckWithProgressiveRetry` 失败后追加 WS 深等待

- 文件：`enumerate.js` 5 处调用点
- 改前：3 次重试（~1.7s）失败后保留模板数据
- 改后：追加 10 轮 `waitForDataReady(8,200)` + `waitForSvgStable(8,200)` → ~28s
- 每轮检查 `hasRealTemp` 或 `qcN.ok`，任一满足即接受
- 各点同时补 `injectHelpers` crash recovery（见 Fix 5）

### 修正 5：fallback 循环补 `__ems` crash recovery（4 处新增）

- 文件：`enumerate.js`，fallback 循环 `extractCards` 空时补 `injectHelpers` + 重试
- 覆盖：recapture 动态/编号、BM page、collectPage 动态
- 第 5 处（collectPage 编号）此前已有 crash recovery

### 修正 6：3 处 `adaptivePolling` 的 `extractCards` 加 `.catch()`

- 文件：`enumerate.js` 3 处 `adaptivePolling` 调用
- 改前：`() => page.evaluate(() => window.__ems.extractCards())`
- 改后：`() => page.evaluate(() => window.__ems.extractCards()).catch(() => ({ cards: [], count: 0 }))`
- 原因：polling 运行时页面导航抛出 context destroyed → 枚举崩溃；Fix 2 使 polling 时长从 1.5s 增加到 45s，崩溃概率上升
- 行业对标：Playwright "always wrap evaluate in try-catch"

### 效果对比

| 场景 | 之前 | 之后 |
|------|------|------|
| 6号 7F B座 一页（20卡模板） | 1.5s 被稳定模板接受 | 最多 45s 等真实温度；全离线快速放行 |
| 6号 7F B座 二页（20卡模板） | 1.7s 重试后保留模板 | 合计 ~30s 深等待 |
| 3号/4号/5号 2卡模板 | `checkCardQuality` 直接放行 | 进入 adaptivePolling 轮询 |
| polling 期间 __ems 丢失 | 枚举崩溃 | 返回空数据，继续轮询 |
| fallback __ems 丢失 | 跳过一轮（~2.8s浪费） | injectHelpers 重试 |


## 此前修复（2026-06-17 v1）

### uniformTemplate 简化
- `rules.js:90` 删除 `!(allOn || allOff)` 和 `!(uniformComm && !allOffline)` 例外条件
- 模板数据始终被标记（26/25/中/制冷 或 0/0/0），触发自适应轮询
- **此前 bug**：3号/4号全部关机的楼层因 uniformTemplate=false 跳过轮询，第一页模板数据直接下页

### adaptivePolling 稳定模板提前退出
- `enumerate.js:adaptivePolling()` 新增：连续 3 轮全部卡片 comm/switch 完整 + 模板检测 → 约 1.5s 接受为真实数据
- 避免稳定模板数据空等 45 秒

### 翻页路径加 `waitForLoadedCards`
- 4 条翻页路径统一追加 `await waitForLoadedCards(page, { maxRetries: 3, waitMs: 250 })`
- 覆盖：`capturePages` 动态/标准分页、`collectPage` 动态/标准分页
- **此前 bug**：缺失 indicator 图片等待 → 4号 12 子区翻页全离线、1号 8F/3号 1F/6号 3F 部分

### 补采模式作用域修复
- `capturePages` 函数：3 处 `const`→`let`（`data`/`dataBM` 重赋值崩溃）
- `capturePages` 函数：2 处变量未定义（动态分页用 `plabel`，标准分页用 `pageNum`）
