# UI Refactor Stage 1

## 范围

本阶段只改造原生 HTML/CSS/JS 面板的 UI 基建与低风险展示层，不更换技术栈，不引入 React/Vue/Vite/Tailwind，不改采集逻辑，不改数据库 schema，不改 API 返回结构。

## 修改文件

- `web/panel/index.html`
  - 接入 `web/panel/components/ui.js`。
  - 将总览页初始 KPI 占位改为 8 张控制台指标卡。
  - 将数据管理表格初始表头同步为新的信息层级。
- `web/panel/app.js`
  - 增加 UI helper 包装函数，统一调用 Badge、MetricCard 与状态语义映射。
  - 总览页改为健康态指标卡：总设备数、实时详情数、已匹配、未匹配、在线/运行中、离线/异常、质量问题数、最近采集时间。
  - 数据管理表格改为设备、位置、区域、通讯、开关、模式/风速、温度、匹配、异常、更新时间、操作的层级。
  - 设备状态、匹配状态、点位完整度、集控锁定、异常提示统一使用语义 Badge。
  - 采集任务日志区域根据任务状态增加 `idle/info/success/danger` 类。
  - 质量审计、批次状态、区域概览逐步切到新的 `success/info/warning/danger/muted/locked` 语义。
- `web/panel/styles.css`
  - 整理并扩展视觉 tokens：背景、面板、卡片、边框、文字、主色、正常、警告、故障、离线、锁定、控制台背景。
  - 统一 Badge 样式，并保留旧 `.p1/.p2/.ok` 兼容。
  - 优化总览 KPI 卡片、表格、数据行 hover、任务日志、质量卡片、提示框和导航样式。

## 新增文件

- `web/panel/components/ui.js`
  - `escapeHtml`
  - `badge`
  - `metricCard`
  - `panelCard`
  - `emptyState`
  - `statusType`
  - `powerType`
  - `taskStatusType`
  - `qualityType`

## 视觉变化

- 总览页更接近 HVAC 控制台首页，第一屏直接突出数据完整性、详情匹配、在线/离线、质量问题和最近采集时间。
- 数据管理页表格从普通字段堆叠改为设备排查视角，核心状态使用统一 Badge，异常提示更醒目。
- 采集任务日志变为深色控制台风格，保留原日志写入逻辑。
- 质量审计、导出记录、区域管理继续保留原功能，样式跟随统一 tokens 和 Badge。

## API / 数据库

- 未改数据库 schema。
- 未改后端采集逻辑。
- 未改 API 返回结构。
- 未新增远程控制、Energy、Alarm 或权限模型。

## 验证

已执行：

```bash
node --check web\panel\components\ui.js
node --check web\panel\app.js
node --check src\panel\server.js
npm run self-test
npm run panel
```

浏览器巡检：

- `http://127.0.0.1:17777/`
- 总览、采集任务、区域管理、数据管理、质量审计、导出记录均可打开。
- Playwright 巡检 console/page error 为 0。
- `/api/summary`、`/api/cards`、`/api/runs`、`/api/reports` 返回正常。

截图保存在：

- `out/ui-stage1-overview.png`
- `out/ui-stage1-tasks.png`
- `out/ui-stage1-monitor.png`
- `out/ui-stage1-data-final.png`
- `out/ui-stage1-quality.png`
- `out/ui-stage1-reports.png`

## 剩余问题

- `app.js` 仍然偏大，本阶段只抽出基础 UI helper，尚未进一步拆分页面级渲染模块。
- 数据管理表格仍使用字符串拼接渲染，后续可以继续拆成 `DataTable`/`Toolbar`/`Pager` 渲染函数。
- 日志仍是纯文本展示，尚未按每行日志级别解析着色；本阶段只做容器和状态视觉。
- 总览图表仍是轻量 CSS 图表，后续可以继续抽象成可复用图表组件。

## 下一阶段建议

1. 继续拆 `web/panel/app.js`：按 `overview/data/tasks/quality/areas/reports` 拆成页面渲染模块，保持原生 JS。
2. 抽出 `DataTable`、`Toolbar`、`Pager`，让数据管理表格和质量样例表复用同一套结构。
3. 把状态判断集中到一个前端状态映射文件，减少页面内重复判断。
4. 优化总览楼栋/区域分布图，增加“异常最多楼栋”和“未匹配最多楼栋”的排查视角。
5. 在不改 API 的前提下，为日志文本做前端行级解析，识别 `ERROR/WARN/INFO/DEBUG` 并弱着色。
