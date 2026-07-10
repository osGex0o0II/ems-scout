# EMS Dashboard 自动化工具包

通过 **PowerShell + Chrome DevTools Protocol (CDP)** 直接控制本机 Edge 浏览器，从 SmartPiEMS 智能能源管理系统页面抓取空调（Panel AC）的实时数据。

> 目标系统：<http://172.29.248.4:8000/ui/#/home/27161>（1号科研综合楼空调群控）
>
> 适用场景：用户电脑已登录该系统，需要批量/定时抓取面板空调状态，在数据管理页筛选并导出 Excel，未来实现远程控制。

---

## 目录

- [快速开始](#快速开始)
- [目录结构](#目录结构)
- [核心原理](#核心原理)
- [目标系统结构](#目标系统结构)
- [数据提取方法](#数据提取方法)
- [关键发现（坑与解）](#关键发现坑与解)
- [常用脚本](#常用脚本)
- [扩展开发](#扩展开发)
- [故障排查](#故障排查)

---

## 快速开始

### 原生桌面应用

当前主入口是 WinUI 3 / Windows App SDK 原生桌面应用。它不是旧 Web 面板的逐项迁移，而是按运维工作台重构：`总览`、`采集任务`、`数据管理`、`审计中心`、`分组设置`、`系统设置`、`诊断`。主流程固定为采集任务 → 首页风险概览 → 数据管理筛选 → 导出当前筛选 Excel。

```powershell
cd D:\Code\ems-tool-audit-fix
npm run native:run
```

如需跳过构建直接打开已有 Debug 包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native.ps1 -NoBuild
```

旧 Web 面板、Electron 面板、`scripts/report.js`、`scripts/dump-aircons.js`、`scripts/dump-public.js`、`scripts/report-monitor.js` 只作为 legacy 工具保留，不作为当前 UI 主流程入口。当前唯一面向用户的导出方式是原生应用 `数据管理 -> 导出当前筛选 Excel`；`诊断`页只用于查看路径、日志和最近 Excel，不提供旧报表生成入口。旧多格式/监控报表脚本默认拒绝运行，如确需应急追溯，必须显式设置 `EMS_ENABLE_LEGACY_REPORTS=1`；旧 Web/Electron 面板必须显式设置 `EMS_ENABLE_LEGACY_PANEL=1`。

### 现场端到端验证

真实 EMS 验证使用隔离脚本，不直接写 `out/ac.db`。默认只检查 EMS HTTP、CDP 和当前登录页面；加 `-RunSingleBuilding` 后才会采集单栋，并把 JSON、SQLite、质量报告、Excel 烟测全部写入 `out/field-e2e-*` 临时目录。

```powershell
# 推荐：自动打开隔离的可采集 Edge，使用随机本机 CDP 端口
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/field-e2e.ps1 -Building 1号 -LaunchEdge

# 单栋真实链路：在打开的 Edge 中登录 EMS 后继续
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/field-e2e.ps1 -Building 1号 -LaunchEdge -RunSingleBuilding
```

`-LaunchEdge` 只绑定 `127.0.0.1`，使用本次运行目录下的独立 Edge Profile，默认结束后关闭本次启动的 Edge 并删除 Profile。若未登录，脚本会快速失败，不进入采集/导入。Excel 后半链路也可单独用 `native/tools/EmsScout.ExportSmoke` 对临时 DB 做烟测。

### 1. 启动带调试端口的 Edge

```powershell
cd C:\Users\Administrator\Desktop\Code\ems-tool\scripts
powershell -ExecutionPolicy Bypass -File start_edge.ps1
```

- 自动结束已运行的 Edge 进程
- 用保留的 Edge Profile（保持登录态）重新启动
- 监听 CDP 端口 `9222`
- 默认打开目标页面 `http://172.29.248.4:8000/ui/#/home/27161`

如果浏览器已经打开且你不想结束它，可以手动用命令行启动：

```cmd
"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --remote-debugging-port=9222 --user-data-dir="%LOCALAPPDATA%\Microsoft\Edge\User Data" "http://172.29.248.4:8000/ui/#/home/27161"
```

### 2. 验证 CDP 可达

```powershell
powershell -ExecutionPolicy Bypass -File list_targets.ps1
```

预期输出：

```
type title                 url                                   webSocketDebuggerUrl
---- -----                 ---                                   -------------------
page 鹏城实验室能源管理系统 http://172.29.248.4:8000/ui/#/home/27161 ws://127.0.0.1:9222/devtools/page/XXXXXXXXXXXXXXXX
```

### 3. 一键抓取全部数据

```powershell
powershell -ExecutionPolicy Bypass -File run_all.ps1 -OutDir C:\ac-logs\2026-06-04
```

> 每个脚本（`shot.ps1`/`state_now.ps1`/`ac_dashboard.ps1`/`dump_all.ps1`）也接受独立 `-Path`/`-JsonPath` 参数自定义输出位置；不传则默认写到 `$PSScriptRoot` 旁边。

会在 `C:\ac-logs\2026-06-04\` 下生成：

- `screenshot.png` — 当前页面截图
- `rt_state.json` — Vue 组件的实时状态摘要
- `ac_dashboard.json` — **19 台空调的完整实时数据**（含开关/模式/温度/风速）

### 4. 定时抓取（趋势分析）

```powershell
powershell -ExecutionPolicy Bypass -File watch_dashboard.ps1 -IntervalSec 60 -OutputDir C:\ac-logs\2026-06-04
```

每 60 秒抓一次快照，文件名形如 `ac_20260604_103015.json`。

---

## 目录结构

```
ems-tool/
├── README.md                ← 本文档
├── AGENTS.md                ← 项目上下文 & 架构速记
├── CHANGELOG.md             ← 修改记录
├── AC-Scout.bat             ← 入口（双击运行采集）
├── package.json             ← 依赖配置
├── src/
│   ├── collect.js           ← 一键编排器 + TUI 菜单
│   ├── enumerate.js         ← 主枚举器（Playwright + Edge CDP）
│   └── verify-live.js       ← 实时浏览器状态验证
├── scripts/
│   ├── import.js            ← JSON→SQLite 导入
│   ├── report.js            ← legacy 多格式报表脚本，默认禁用
│   ├── dump-aircons.js      ← legacy Excel 空调明细脚本，默认禁用
│   ├── dump-public.js       ← legacy TXT 公区 ON 清单脚本，默认禁用
│   ├── dashboard.js         ← 浏览器端 AC 提取脚本
│   ├── schema.sql           ← SQLite 建表语句
│   └── views.sql            ← SQLite 视图
├── docs/
│   ├── architecture.md      ← 目标系统前端架构分析
│   ├── data-model.md        ← Vue 数据模型详解
│   └── (项目移交与状态文档)
├── data/
│   ├── 1号楼/               ← 1号历史数据
│   └── 2号楼/               ← 2号历史数据
└── out/
    ├── enum_full_v5.json    ← 全量枚举结果
    ├── ac.db                ← SQLite 数据库
    └── .edge_profile/       ← Edge CDP 配置
```

---

## 核心原理

### 为什么用 CDP 而不是直接 HTTP？

页面是 Vue 2 SPA，**所有数据从 WebSocket 推送**到 Vuex store，HTTP API 只在初始加载时调用一次。直接抓 HTTP 只能拿到空壳（只有 SVG 模板，没有实时值）。

所以必须在浏览器里跑 JS 去读 Vue 实例，CDP 是唯一可行的路。

### 为什么用 PowerShell？

用户环境是 Windows + PS 5.1，**没有 Python 也没有 Node**。PowerShell + .NET Framework 4.x 是唯一原生可用的脚本环境。CDP 用 .NET 的 `System.Net.WebSockets.ClientWebSocket` 实现即可，无需任何额外依赖。

### 整体链路

```
[PS 脚本] --(WebSocket)--> [Edge --remote-debugging-port=9222]
                                  |
                                  +--> [目标页面 Vue 实例]
                                            |
                                            +--> [SVG 渲染的 19 个 AC 卡片]
```

**数据来源**：
1. Vue 实例 `w.__vue__.$data.runConfDataProp` — 测点配置
2. Vue 实例 `w.__vue__.$data.websocketDataProp` — 当前实时值（部分）
3. **Shadow DOM 内的 SVG `<text>` / `<image>` 元素** — 屏幕上实际显示的最终值（最可靠）

---

## 目标系统结构

### 前端技术栈

- **Vue 2.x** SPA
- **Hash 路由**（`/#/home/27161` 中的 27161 是建筑 ID）
- API 风格：`/api/...`，**PascalCase** 端点（如 `AccountToken/Login`）
- 通用响应包装：`{"status":200,"success":true,"data":...}`
- 登录端点：`POST /api/AccountToken/Login`
- 实时数据：WebSocket（推测 STOMP over SockJS），Vuex 状态管理

### 关键 DOM 节点

```
<html>
  <body>
    <div id="app">
      ...
      <div class="pi-graphics-configuration-svg-new">  ← 目标组件
        #shadow-root
          <style>...</style>
          <style>...</style>
          <style>...</style>
          <div>                                          ← 实际内容
            <svg width="1709" height="852">
              <!-- 楼层索引 + 19 个 AC 卡片 + 图标资源 -->
              <image href="/SvgResource/resource/fa70e30...png"/>  ← 开关 OFF 图标
              <image href="/SvgResource/resource/eaaccd7...png"/>  ← 开关 ON 图标
              <image href="/SvgResource/resource/de4ea65...png"/>  ← 制冷模式图标
              <text>XXFDT-KT</text>                      ← AC 名
              <text>27.2 ℃</text>                        ← 室内温度
              <text>24 ℃</text>                          ← 设定温度
              <text>自动</text>                          ← 风速
              <text>制冷</text> / <text>通风</text>      ← 模式
              ...
            </svg>
          </div>
      </div>
    </div>
  </body>
</html>
```

**重点**：

- 整个 AC 仪表盘在 **Shadow DOM** 里，普通 `querySelector('text')` 找不到任何东西
- 必须用 `document.querySelector('.pi-svg-container').shadowRoot.querySelector('svg')`
- 文本是真正的 `<text>` SVG 元素（不是 HTML `<div>`）
- 开关是 `<image>` 元素，**用图片资源区分 ON/OFF**
- 温度单位是 `℃`（U+2103），不是 `°C`（U+00B0 + C）
- 中文在 console 输出会乱码（GBK 控制台），**统一写到 UTF-8 文件**再读

### Vue 数据结构

```javascript
// 在目标组件 .__vue__.$data 上
runConfDataProp        : Array<Point>        // 209 个测点配置
websocketDataProp      : { [idx: string]: WSEntry }  // 当前实时值
chartDataRelationProp  : { [svgId: string]: any[] }  // 195 个图表数据（空数组）
svgContentProp         : { svgContent, jsonContent } // 190K 字符的 SVG 模板
svgListDraw            : Array<DrawElement>  // 195 个绘制元素
```

每个 `Point`：

```typescript
{
  ptPathKey: string,      // GUID，用于内部索引
  ptPathConf: {
    ptId: number,         // 测点全局 ID
    devId: number,        // 设备 ID（如 20008448）
    name: string,         // "当前开关机模式" / "室内温度" 等
    unit: string,         // "℃" / "" / null
    dynType: number,      // 1=开关, 4=模式, 9=温度, 22=通信, 23=风速, 11=名称
    valueType: number,    // 0=数值, 1=枚举
    enumDefine: [{Key,Value}]  // 枚举映射，如 {0:"关机",1:"开机"}
  }
}
```

`WSEntry`：

```typescript
{
  tag: {
    value: string,        // 当前值（**总是字符串**）
    valid: boolean,
    alarm: boolean,
    changeTime: number | null
  }
}
```

**关键映射**：`websocketDataProp[String(i)]` 对应 `runConfDataProp[i]`。

---

## 数据提取方法

### 路线 A：从 Vue 实例读（最完整但最脆弱）

```javascript
const w = document.querySelector('.pi-graphics-configuration-svg-new');
const d = w.__vue__.$data;
for (let i = 0; i < d.runConfDataProp.length; i++) {
  const conf = d.runConfDataProp[i].ptPathConf;
  const ws = d.websocketDataProp[String(i)];
  const value = ws ? ws.tag.value : null;
  // ...
}
```

**问题**：

- WebSocket 增量推送，**初始页面加载后只能拿到部分点的值**
- 调用 `w.__vue__.subscribe()` 只能重连 WS，不会重新拉全量
- 重连后需要等几分钟才能累积到所有点
- `runConfDataProp` 会被切楼层时整体替换（不是增量更新）

### 路线 B：从 Shadow DOM 读 SVG 文本（最可靠，✅ 推荐）

```javascript
const container = document.querySelector('.pi-svg-container');
const svg = container.shadowRoot.querySelector('svg');
const texts = Array.from(svg.querySelectorAll('text'));
const images = Array.from(svg.querySelectorAll('image'));

// 1. 提取所有 text 元素（按 y 分组）
const items = texts.map(t => {
  const r = t.getBoundingClientRect();
  return {
    x: Math.round(r.left + r.width/2),
    y: Math.round(r.top + r.height/2),
    txt: t.textContent.trim()
  };
});

// 2. 按行（y 坐标）找设备名行
const byY = groupBy(items, 'y');
const nameRows = Object.entries(byY)
  .filter(([y, arr]) => arr.length >= 8
    && arr.every(i => /^[A-Z0-9\-]+$/i.test(i.txt))
    && !arr.every(i => /^[A-Z]?\d+F$/i.test(i.txt)))  // 排除楼层按钮
  .map(([y, items]) => ({ y: +y, items }));

// 3. 对每个名字，按位置找最近的开关图/模式/温度/风速
for (const nameIt of nameRows.flatMap(r => r.items)) {
  const switch = nearest(images.filter(i => 38 <= i.w && i.w <= 46 && 17 <= i.h && i.h <= 20),
                         nameIt.x, nameIt.y + 100, 80, 50);
  const mode = nearest(items.filter(i => /^(制冷|通风|制热|送暖)$/.test(i.txt)),
                       nameIt.x, nameIt.y + 140, 80, 30);
  const indoor = nearest(items.filter(i => /\d+(\.\d+)?\s*℃/.test(i.txt)),
                         nameIt.x, nameIt.y + 170, 80, 40);
  // ...
}
```

### 开关状态识别（关键）

**`<image>` 元素**的资源 URL 决定状态：

| href 结尾 | 含义 |
|-----------|------|
| `fa70e30e908a89b17a400b3841cfd410.png` | OFF（绝大多数情况） |
| `eaaccd77903bc70772c518a56a4f5b9f.png` | ON（少数情况） |

用 `imgs.filter(i => 38<=i.w && i.w<=46 && 17<=i.h && i.h<=20)` 选出开关图标，按 `href` 分组，数量最多的就是 OFF，少量是 ON。

### 模式识别

**渲染方式不一致**：
- `制冷` → `<image>` 资源（图标 `de4ea659d70c571778343aa0c8434efa.png`）
- `通风` / `制热` / `送暖` → `<text>` 元素（直接显示文字）

所以要同时检查 image 和 text。

### 温度格式

- 显示文本是 `25.2 ℃`（数字 + 空格 + ℃）
- `℃` 是 Unicode U+2103，不是 `°C`（U+00B0 + C）
- 正则要用 `/\d+(\.\d+)?\s*\u2103/`

---

## 关键发现（坑与解）

### 1. PowerShell 5.1 不支持 `??`

```powershell
$x ?? $y   # ❌ SyntaxError
$x -?? $y  # ❌
```

替代：

```powershell
if ($null -ne $x) { $x } else { $y }
# 或
[??]::coalesce($x, $y)  # 也行
```

### 2. `ConvertFrom-Json` 在 `Task.Run` 脚本块里失效

PowerShell cmdlet 在 .NET 后台线程里上下文丢失。**改用 `JavaScriptSerializer`**：

```powershell
Add-Type -AssemblyName System.Web.Extensions
$js = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$obj = $js.Deserialize($msg, [hashtable])
```

### 3. 中文字符串在 here-string 里被截断

```powershell
$js = @'
const re = /^(ON|OFF|开|关)$/;   # ❌ "开|关" 被 PS 截断
'@
```

**解法**：JS 写到独立 `.js` 文件，用 `Get-Content -Raw -Encoding UTF8` 读。

### 4. WebSocket 重连后只推增量

调用 `v.subscribe()` 不会触发全量重发。需要等几分钟累积到所有点，或者保持原连接不重连。

### 5. Vue 实例有 200+ 个方法

`v.$options.methods` 包含 `initWebSocket`、`refreshSvgContent`、`subscribe`、`getSvgRunConfData` 等，但大部分是绑定值（`length=0`），直接调用很多会抛错。需要用 `try/catch` 逐个试。

### 6. DOM 元素在 Shadow DOM 里

页面把整个仪表盘封装在 `pi-svg-container` 的 shadow root 中。普通 `document.querySelector` 看不到内部元素，必须 `element.shadowRoot.querySelector(...)`。

### 7. 楼层按钮 B1F, 1F, 2F, ... 和 AC 名 XXFDT-KT 同形状

按 Y 分组找"名字行"时，必须排除 `^[A-Z]?\d+F$` 的纯楼层名。

### 8. Vuex 模块在生产环境被压缩

模块名是 `a/b/c/d`，看不出含义。直接通过 `__vue__.$store.state` 查到的状态有限。

### 9. 完整数据流（页面加载过程）

1. HTTP 加载 `index.html`（Vue 壳）
2. JS bundle 初始化路由，命中 `/home/27161`
3. 调 HTTP API 加载建筑信息、设备列表、SVG 模板
4. 渲染 Vue 组件，SVG 模板注入 shadow DOM
5. WebSocket 连接（推测 STOMP），订阅 `/user/realDataMsg`（user queue）
6. 收到首次推送，更新 `runConfDataProp` 关联的实时值到 SVG
7. 后续只推变化的点（增量）

---

## 常用脚本

### `cdp_lib.ps1` — CDP 客户端库

```powershell
. .\cdp_lib.ps1

Connect-Cdp                      # 默认连 127.0.0.1:9222，自动选第一个 page target
Send-Cdp "Page.reload" @{}       # 通用 CDP 调用
Invoke-PageJs "1+1"              # 跑 JS，返回值
Invoke-PageJs -AwaitPromise "(async()=>{ await fetch('...') })()"  # 异步
Save-Screenshot "out.png"        # 截图
Get-Events Network.webSocketFrame  # 拿之前错过的 WebSocket 帧
Disconnect-Cdp
```

### `EmsToolkit.psm1` — 高级 API

```powershell
Import-Module .\EmsToolkit.psm1 -Force

Start-EmsEdge -Url "http://172.29.248.4:8000/ui/#/home/27161"
Get-EmsTarget -Match "172.29.248.4"
Watch-EmsDashboard -IntervalSec 60 -Count 30 -OutputDir C:\ac-logs
```

### `dashboard.js` — 核心提取脚本（早期原型）

```javascript
// 返回 19 个 AC 的 {name, switch, mode, indoor, setTemp, fan}
// switch: "ON" / "OFF"
// mode: "制冷" / "通风" / "制热" / "送暖"
// indoor/setTemp: 字符串数字 ("25.2")
// fan: "自动" / "高" / "中" / "低" / "0"
```

详细见 `scripts/dashboard.js` 源码。

---

## 扩展开发

### 添加新设备类别（电表、照明、插座）

1. 在浏览器里打开对应页面（如 `#/meter/27161`）
2. 用 `find_svg.ps1` 找新的 shadow DOM 容器 class 名
3. 改 `dashboard.js` 适配新布局
4. 包装为新 PS 脚本

### 远程开关控制

尚未实现，但路径清晰：

1. 在 Vue 组件上找 `onClickOptype` / `ProcessGroupControl` / `postYkYt2DB` 方法
2. 调 `v.showControlModal(...)` 打开控制弹窗
3. 或直接通过 `axios` 调 `/api/Control/SendCommand`（需要先抓包找具体端点）

### 设备名称到 devId 反查

devId（如 `20008448`）和显示名（`XXFDT-KT`）的映射**不在前端**，需要：

- 抓 `/api/Device/GetById?devId=...` 的响应
- 或维护一个设备名 → devId 的本地映射文件

---

## 故障排查

### `list_targets.ps1` 返回空

**症状**：没有列出 `172.29.248.4` 的 target。
**解决**：
1. 确认 Edge 已启动：`Get-Process msedge`
2. 手动验证端口：`Invoke-RestMethod http://127.0.0.1:9222/json`
3. 防火墙可能拦截了 9222 端口
4. 多个 Edge 进程冲突，结束所有再重启

### `ac_dashboard.ps1` 输出 `no shadow`

**症状**：Shadow DOM 找不到。
**解决**：
1. 确认页面已登录（不是登录页）
2. 切到目标建筑：`#/home/27161`
3. 等页面完全加载（2-3 秒）再跑脚本
4. 可能在其他页面（综合告警、仪表管理），需要先导航回主页

### `ac_dashboard.ps1` 输出卡片数为 0

**症状**：count=0 或 name 全是 `?`。
**解决**：
1. 截图看页面是否真的渲染了卡片（可能 WebSocket 没数据）
2. 调 `state_now.ps1` 看 `wsCount` 是否大于 0
3. 切楼层（点 1F → 2F → 1F）会触发 Vue 重新加载

### 中文乱码

`Write-Host` 输出中文在 GBK 控制台会乱码。**统一写 UTF-8 文件再读**：

```powershell
$data | ConvertTo-Json -Depth 5 | Out-File "out.json" -Encoding UTF8
```

或者在 PowerShell ISE / VSCode / Windows Terminal (UTF-8) 里跑。

### Vue 实例找不到 `__vue__`

新版本 Vue 不会自动挂 `__vue__` 到 DOM。**回退方案**：

```javascript
const root = document.querySelector('#app');
// 递归找
function findVue(node) {
  if (node.__vue__) return node.__vue__;
  for (const c of node.children || []) {
    const v = findVue(c);
    if (v) return v;
  }
  return null;
}
```

---

## 参考资料

- [Chrome DevTools Protocol 文档](https://chromedevtools.github.io/devtools-protocol/)
- [CDP 域列表](https://chromedevtools.github.io/devtools-protocol/tot/)
- [System.Net.WebSockets 文档](https://docs.microsoft.com/en-us/dotnet/api/system.net.websockets)
- [Vue 2 响应式原理](https://v2.vuejs.org/v2/guide/reactivity.html)
- [JavaScriptSerializer (legacy Web Extensions)](https://docs.microsoft.com/en-us/dotnet/api/system.web.script.serialization.javascriptserializer)

---

---

## Node.js 自动化工具（2026-06 新增）

上述 PS 脚本为初期原型。项目现已迁移到 **Node.js + Playwright**，支持全量 6 栋楼枚举、数据质量校验和数据管理筛选导出：

### 主要脚本

| 文件 | 功能 |
|------|------|
| `src/enumerate.js` | Playwright 枚举器，通过 Edge CDP 遍历每栋楼的每层每子区卡片（1887 行） |
| `src/collect.js` | 一键编排器，TUI 菜单，按序执行枚举→导入→质量审计 |
| `src/verify-live.js` | 实时浏览器对比工具，与 DB 交叉验证 |
| `scripts/import.js` | JSON 枚举结果 → SQLite 数据库 |
| `scripts/report.js` | legacy 多格式报表脚本，默认禁用，当前不作为主流程入口 |
| `scripts/dump-aircons.js` | legacy Excel 明细脚本，默认禁用，当前不作为主流程入口 |
| `scripts/dump-public.js` | legacy TXT 清单脚本，默认禁用，当前不作为主流程入口 |

### 快速运行

```bash
# 一键采集（枚举 → 导入 → 质量审计）
node src/collect.js

# 或分步执行
node src/enumerate.js --edge                    # 全量枚举
node scripts/import.js                          # 导入 SQLite

# 筛选后 Excel 导出
npm run native:run                             # 原生数据管理页，点击“导出当前筛选 Excel”
```

### 关键架构

```
collect.js
  └── enumerate.js (Edge CDP, 2-10min/栋)
       → enum_full_v5.json (6571 卡 / 6 栋)
   └── import.js → ac.db (SQLite)
  └── quality-report.js → 质量审计
原生数据管理页
  └── 导出当前筛选 Excel → out/data-management-export/数据管理筛选结果_*.xlsx
```

### 最新优化

- **数据就绪保护**：4 项修复确保开关/温度/模式为真实负载数据而非 SVG 模板默认值
- **等待链优化**：`Promise.all` 并行化，全量耗时从 456s 降至 360s（+21%）
- **数据质量验证**：`checkCardQuality` 实时检测模板默认值（0℃/0℃/中），低质量时自动重试
- 详见 `AGENTS.md` 和 `CHANGELOG.md`

---

**最后更新**：2026-06-11
