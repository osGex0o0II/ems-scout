# UI Refactor Stage 2

## 范围

第二阶段基于第一阶段继续改造“数据管理”页面，把普通数据表升级为 HVAC 设备排查工作台。未更换技术栈，未引入 React/Vue/Tailwind，未修改采集逻辑，未修改数据库 schema。

## 修改文件

- `web/panel/index.html`
  - 数据管理页新增 `deviceWorkbench` 排查概览条挂载点。
  - 数据表主列调整为设备、位置、健康态、通讯/匹配、运行状态、温度、异常提示、操作。
  - 新增设备详情抽屉 DOM。
  - 接入 `device-health.js`、`device-table.js`。
- `web/panel/app.js`
  - 新增快捷筛选配置和跳转逻辑。
  - 数据管理页接入排查概览条。
  - 数据表渲染切换到 `DeviceTable.renderRow`。
  - 增加设备详情打开/关闭逻辑。
  - 总览页关键卡片和楼栋行支持跳转数据管理筛选。
- `web/panel/styles.css`
  - 新增工作台概览卡片、快捷筛选按钮、健康态列、详情抽屉样式。
  - 优化数据管理表格宽度和排查状态视觉。
- `src/panel/server.js`
  - 兼容增加 `/api/cards` 的 `facets` 字段，用于当前筛选口径下的排查概览统计。
  - 兼容增加 `realtime_match=db_missing` 与 `realtime_match=ignored`。
  - 温度异常筛选口径扩展为缺失/不可解析/越界。

## 新增文件

- `web/panel/components/device-health.js`
  - `deriveDeviceHealth(row)`
  - `collectDeviceIssues(row)`
  - `matchesQuickFilter(row, key)`
  - 温度、点位、DB/实时一致性等前端派生工具。
- `web/panel/components/device-table.js`
  - 设备表格行渲染。
  - 设备详情抽屉渲染。
  - 状态 Badge、匹配、运行状态、温度和异常提示渲染。

## 数据管理页新增能力

- 排查概览条：全部、已匹配、未匹配、DB缺实时、实时未匹配、离线、开机、关机、集控锁定、需排查。
- 快捷筛选：全部、需排查、未匹配、离线、DB缺实时、实时未匹配、集控锁定、温度异常、已忽略。
- 设备健康态列：用统一 Badge 显示正常、离线、未匹配、DB缺实时、集控锁定、温度需复核、需排查、未知。
- 设备详情抽屉：区分基础信息、数据库采集字段、实时详情字段、匹配信息、质量/异常提示、实时字段预览、原始字段预览。
- 总览联动：未匹配卡跳转数据管理未匹配筛选；离线/异常卡跳转需排查；质量问题卡跳转质量审计；楼栋行跳转数据管理楼栋筛选。

## 健康态派生

`deriveDeviceHealth(row)` 只基于现有字段派生，不伪造数据：

- `unmatched`：实时详情未匹配数据库设备。
- `missing_realtime`：DB 有设备但缺实时详情。
- `offline`：DB 或实时通讯状态为离线，明确不等同于关机。
- `temperature_warning`：室内温度/设定温度缺失、不可解析或超出 5-40 范围。
- `attention`：点位不完整、详情错误、默认值疑似、DB/实时状态不一致、座号/区域未识别等。
- `locked`：实时详情显示集控锁定开启。
- `normal`：实时详情已匹配，通讯状态正常，且未触发上述问题。
- `unknown`：字段不足，无法判断。

## API / 数据库

- 未改数据库 schema。
- 未改采集逻辑。
- `/api/cards` 只增加兼容字段 `facets`，原 `total/rows/run_id` 保持不变。
- 新增兼容筛选值：`realtime_match=db_missing`、`realtime_match=ignored`。

## 验证结果

已执行：

```bash
node --check web\panel\components\ui.js
node --check web\panel\components\device-health.js
node --check web\panel\components\device-table.js
node --check web\panel\app.js
node --check src\panel\server.js
npm run self-test
npm run panel
```

结果：全部通过，面板运行在 `http://127.0.0.1:17777/`。

API 抽查：

- 全部设备：6575
- 已匹配：6568
- 实时未匹配：7
- DB缺实时：0
- 开机：2188
- 关机：4387
- 集控锁定：1481
- 温度异常/缺失：443
- 需排查：3694

Playwright 检查：

- 总览、采集任务、区域管理、数据管理、质量审计、导出记录均可打开。
- console error：0
- page error：0
- 快捷筛选“未匹配”筛出 7 条。
- 快捷筛选“温度异常”筛出 443 条。
- 设备详情抽屉可打开/关闭，且包含 DB 字段、实时详情字段和原始字段预览。
- 总览“未匹配”卡可跳转到数据管理未匹配筛选。
- 数据管理页导出按钮、标签控件、备注控件仍存在。

截图：

- `out/ui-stage2-data-workbench-final.png`
- `out/ui-stage2-device-detail-final.png`

## 剩余问题

- `app.js` 仍然较大；本阶段已抽出健康态和表格详情，但页面调度逻辑还在主文件。
- “需排查”数量较高，主要因为当前数据中集控锁定、DB/实时状态不一致、温度缺失/异常等规则都纳入排查；后续可按运维优先级拆成 P1/P2/P3。
- `DB缺实时` 当前为 0，是真实数据口径，不是功能缺失。
- 详情抽屉目前展示字段较全，后续可增加字段搜索或折叠分组。

## 下一阶段建议

1. 将 `app.js` 中数据管理页面调度继续拆到 `web/panel/components/filters.js` 或 `web/panel/pages/data.js`。
2. 为“需排查”增加优先级队列：严重异常、未匹配、温度异常、状态不一致、集控锁定。
3. 在数据管理中增加“只看当前楼栋/楼层问题排行”的侧栏或顶部排行。
4. 给详情抽屉增加字段搜索和一键复制设备定位信息。
5. 质量审计页与数据管理页进一步联动：点击质量问题样例直接跳到设备详情。
