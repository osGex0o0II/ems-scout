# Vue 数据模型详解

## 数据总览

```javascript
const w = document.querySelector('.pi-graphics-configuration-svg-new');
const d = w.__vue__.$data;
```

| 字段 | 类型 | 用途 |
|------|------|------|
| `runConfDataProp` | `Array<Point>` | 测点配置（209 个） |
| `websocketDataProp` | `Object<idx, WSEntry>` | 实时值（部分；增量推送，断网不重发） |
| `hisDataProp` | `Object` | 历史数据（按需加载） |
| `chartDataRelationProp` | `Object<svgId, Array>` | 图表数据（195 个 SVG 元素对应） |
| `chartData` | `Object` | ECharts 图表数据 |
| `svgContentProp` | `Object` | SVG 模板（190K 字符） |
| `svgListDraw` | `Array<DrawElement>` | 195 个绘制元素定义 |
| `ptPathInfo` | `Object` | 测点路径信息（通常空） |
| `relatePtInfo` | `Object` | 关联点信息（通常空） |
| `ptPathList` | `Array` | 测点路径列表（通常空） |
| `userData` | `Object` | 用户数据（通常空） |
| `groupData` | `Object` | 分组数据（通常空） |
| `isShowSpin` | `boolean` | 是否显示加载动画 |
| `svgLoaded` | `boolean` | SVG 是否已加载 |
| `svgTypeId` | `number` | SVG 类型 ID |
| `widgetId` | `number` | 组件实例 ID |
| `svgId` | `string` | SVG ID |

## Point 结构

```typescript
interface Point {
  ptPathKey: string;          // 内部 GUID
  ptPathConf: {
    ptId: number;             // 全局测点 ID
    devId: number | null;     // 设备 ID（顶层为 null）
    name: string | null;      // "当前开关机模式" / "室内温度" 等
    unit: string | null;      // "℃" / "" / null
    valueType: number;        // 0=数值, 1=枚举
    enumDefine: Array<{       // 枚举映射（仅 valueType=1）
      Key: number;
      Value: string;
    }>;
    dynType: number;          // 1=开关, 4=模式, 9=温度, 11=名称, 23=风速（无 22）
    measurandName: string | null;
  };
}
```

**注意**：dynType=22 不存在于 ptPathConf 上。它仅在 `svgListDraw[].dyn.listDyn[].DynType` 中出现，用作 SVG 绘制元素类型（"通讯树"图标），不是 WS 数据字段。

## WSEntry 结构

```typescript
interface WSEntry {
  tag: {
    value: string;            // 当前值（始终是字符串！）
    valid: boolean;           // 是否有效
    alarm: boolean;           // 是否告警
    changeTime: number | null;
  };
}
```

## DrawElement 结构

```typescript
interface DrawElement {
  id: string;                 // "_12", "_13"
  drawShape: number;          // 12=文字标签, 13=值显示
  mtype: number;              // 1=设备名, 2=开关, 3=模式, 4=温度, 5=风速
  ptPathConf: { ptId, devId, name, unit, dynType };
  fill?: string;
  fontSize?: number;
}
```

## 索引关系

```
runConfDataProp[0]   →   websocketDataProp["0"]
runConfDataProp[1]   →   websocketDataProp["1"]
...
runConfDataProp[N]   →   websocketDataProp["N"]
```

**注意**：
- 索引是 `runConfDataProp` 数组下标
- `websocketDataProp` 的 key 是该下标的字符串形式
- 如果某点无实时值，对应 key 不存在

## SVG 图标状态识别

卡片状态通过 SVG `<image>` 元素的 `href` 区分，非 CSS filter/opacity。

### 开关机图标（43×18 px）
宽 43±5，高 18±3。每页统计所有开关图标 href 出现次数，最多的是 OFF，次多的是 ON。

### 通讯状态指示灯（29×27 px）
宽 29±3，高 27±3。用于判断通讯状态。

#### Comm 判定算法
1. 按 indicator href 分组
2. 最多卡片的组 = 多数派 = "在线"
3. 其他组（少数派）：
   a. 若该组的开关状态分布与多数派不同 → "在线"（真实开机/关机差异）
   b. 若该组的开关状态分布与多数派相同 → "离线"（数据陈旧）

示例（1号 1F，19 卡）：
| indicator hash | 卡数 | 开关状态 | 判定 | 说明 |
|---------------|------|---------|------|------|
| `3bdc38ed...` | 12 | 混合 ON/OFF | 在线 | 多数派 |
| `56f45bb3...` | 1 | `-`（不同于多数 ON）| 在线 | 真实开机 |
| `833bea6e...` | 6 | 全部 ON（同多数）| 离线 | 通讯中断 |

## 通信点说明
- `ptId=20007188` 通信点 `devId=null` —— 属于整台设备全局状态，不绑具体子点
- 枚举定义：Key 0=未知, 1=在线, 2=离线

## 切换楼层时的行为
1. `runConfDataProp` 替换为当前楼层 ~209 个点
2. `websocketDataProp` 清空（不重发）
3. SVG 按新点 ptId 重新绑定
4. 等待新 WS 推送（只推变化的点）

## 历史数据
`hisDataProp` 通过 `GetAIHistoryData({ ptId, startTime, endTime })` 按需加载。
