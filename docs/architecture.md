# 目标系统前端架构分析

## 概览

- **系统名**：鹏城实验室能源管理系统（SmartPiEMS）
- **后端**：.NET，端口 8000
- **前端**：Vue 2.x SPA
- **路由**：Hash 模式 `/#/...`
- **实时**：WebSocket（STOMP over SockJS over CDP）

## 页面布局

```
┌────────────────────────────────────────────────┐
│ Logo / 用户 / 主题切换                          │
├──────┬─────────────────────────────────────────┤
│ 侧边 │ 面包屑 > 空调系统 > 1号...               │
│ 菜单 ├─────────────────────────────────────────┤
│      │ 标题 + 楼层索引 (B1F~31F 圆形按钮)       │
│      ├─────────────────────────────────────────┤
│      │ SVG: AC 卡片网格                        │
│      │ ┌────────────┬────────────┬───          │
│      │ │XXFDT-KT    │DXFDT-KT    │...          │
│      │ │25.2℃ 24℃   │25.9℃ 24℃   │            │
│      │ └────────────┴────────────┴───          │
│      │ ... (每行 8-10 卡)                       │
├──────┴─────────────────────────────────────────┤
│ 底栏                                            │
└────────────────────────────────────────────────┘
```

## 单个 AC 卡片 DOM 布局（SVG）

```svg
<g>
  <image href="...card-bg.png"/>
  <image href="...indicator.png" width="29" height="27"/>  <!-- 通讯状态灯 -->
  <text>XXFDT-KT</text>                                     <!-- 设备名 -->
  <image href="...switch.png" width="43" height="18"/>      <!-- 开关状态 -->
  <text>室内温度: </text><text>25.2 ℃</text>
  <text>设定温度: </text><text>24 ℃</text>
  <text>系统模式: </text><image href="...cool.png"/>
  <text>设定风速: </text><text>自动</text>
</g>
```

## 枚举管线（v5）

```
enumerate.js (Playwright + Edge CDP) → enum_full_v5.json
                                            ↓
                                    import.js → ac.db
                                            ↓
                              原生数据管理 → 导出当前筛选 Excel
```

旧 `dump-aircons.js` / `dump-public.js` / `report.js` 属于 legacy 报表脚本，默认禁用，不进入当前主流程。

## 数据流向

```
HTTP GetRunConfData → runConfDataProp (测点配置)
                      ↓
                    SVG text 渲染（占位符）
                      ↓
WebSocket realDataMsg → websocketDataProp[String(i)]
                      ↓
                    SVG text 更新（实时值）
```

## 数据约定

### 通讯状态（comm）= SVG indicator 29×27 图片分组判定
详见 `data-model.md` 的 Comm 判定算法。

### 公区定义
`layout='group'` 或 命名含 GQ/WSJ/DTT/FDT/XFDT/CSJ/FWJ/ZBS/ZSG/MD/RDJHJF；`QL-<数字>` → 非公区

### 页序
BM 排最前（`pageOrder: BM=-1`）

## 已知限制
- CDP 下 WS 数据不稳定（某些页面仅 1 条数据）
- 1号 fan 覆盖率低（群控布局 fan 定位不精）
- 5号/6号 座号基于 x 坐标硬编码
