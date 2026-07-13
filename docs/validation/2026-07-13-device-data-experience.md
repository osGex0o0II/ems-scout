# 设备数据体验与导出一致性验证

日期：2026-07-13

范围：P0-D 设备快捷筛选、稳定刷新、详情面板、历史只读和 Excel 行数一致性。

## 安全边界

- 所有运行态验证只使用 `%TEMP%\ems-scout-ui-validation-*` 隔离目录。
- 合成验证目录：`C:\Users\Administrator\AppData\Local\Temp\ems-scout-ui-validation-8e73fd4e131048198b39b55083b2e6ee`。
- 未通过 SQLite 打开或修改 `out/ac.db`、`data/ac.db`、`data/1号楼/ac.db`、`data/2号楼/ac.db` 及其 WAL/SHM。
- 本记录是本地合成数据验收，不是已登录真实 EMS 的现场端到端证据。

## 自动化证据

```text
npm run native:test
229 passed, 0 failed, 0 skipped
```

新增测试覆盖：

- 查询版本单调递增，过期响应不能成为最后成功结果。
- 五个快捷筛选的稳定 key、标签、数量和选中态。
- 刷新失败不清空已有 `Devices`，页面具有安全错误条和重试入口。
- 空态隐藏结果表头，筛选矩阵默认折叠，详情栏宽度固定为 360px。
- 导出复用最后成功查询快照，并在页面显示数量确认。
- `DeviceListResult.Total` 与实际加载行数不一致时，写文件前拒绝导出。

```text
npm run native:build
0 warnings, 0 errors
```

## 运行态证据

空数据库使用 `scripts/run-native.ps1 -NoBuild -UiValidation` 启动；合成数据库使用同一临时 `settings.json` 通过打包启动参数重新启动。

- 949x540 压力窗口：快捷筛选和主要命令常驻，“更多筛选”默认折叠，结果空态与表头不重叠，详情栏仍可达。
- 1280x768 最大化窗口：5 行设备表和详情栏同时可见，无按钮、表格或详情文字重叠。
- 合成 5 台设备：离线 1、未知 1、温度异常 2、无实时数据 5、需关注 4。
- 点击“离线 1”后按钮保持选中，结果数变为 1，摘要显示“已生效条件：快捷：离线”，导出预览同步为 1 台。
- 首行 `1-0101-KT` 的基础信息、采集值、实时值、质量与关注信息可在详情栏读取。
- 切换历史批次后持续显示“只读”，导出按钮禁用。
- 导出确认对话框显示“将导出 5 台设备”；确认后状态显示已导出 5 行。
- `xlsx` 只读检查：工作表 `devices`，数据行 5，列 12，首设备 `1-0101-KT`。

临时数据库审计：

```text
AUDIT_OK
user_version=5
latest_supported=5
journal_mode=wal
quick_check=ok
current=true
pending=0
identity_unresolved=0
```

打包用户设置未被隔离验收改写：

```text
SHA-256 E028D0D08F37EDBE80AE8CF497F7CEBFBF127D0B00094FF64464C22892746729
```

## 未覆盖外部门禁

- 1366x768、1920x1080、150% DPI、深色和高对比度仍需在对应显示环境复验。
- 正式 MSIX 安装、升级、卸载和重新安装尚未执行。
- 内置 Sidecar 实际采集和真实 EMS 单栋/六栋现场 E2E 尚未执行。
