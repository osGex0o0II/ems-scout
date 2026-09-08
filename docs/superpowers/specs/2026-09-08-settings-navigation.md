# 设置与导航体验规格

## 目标

在现有 WinUI 3 原生应用中补齐用户可见的外观、动效、窗口启动和系统集成设置，并让页面切换动画由统一策略控制。

## 约束

- 保留当前 Native WinUI 3 架构，不恢复旧 Web、Electron 或 TUI 入口。
- 保留已有托盘关闭行为、主题、数据表密度和颜色预设。
- 默认动效为短时淡入；减少动效或显式关闭动效时不得产生位移动画。
- “登录后自动启动”使用 MSIX StartupTask；未打包调试环境不可导致程序崩溃。
- “发送到”使用当前 MSIX AUMID 创建用户 SendTo 快捷方式，关闭选项时只删除 EMS Scout 自己的快捷方式。
- 窗口位置只在用户开启保存时写入设置，并在恢复时限制尺寸和位置在当前显示器工作区内。
- 语言选项先提供系统默认和简体中文，当前界面无完整多语言资源时保持简体中文内容，不伪装成已完成翻译。

## 配置契约

新增 `AppSettings` 字段：

- `PageTransitionStyle`: `none`、`fade`、`slide`，默认 `fade`。
- `Language`: `system`、`zh-CN`，默认 `system`。
- `SaveWindowPlacement`: 默认 `true`。
- `StartMinimized`: 默认 `false`。
- `LaunchAtLogin`: 默认 `false`。
- `ShowInSendTo`: 默认 `false`。
- `WindowPlacement`: 可选的左上角坐标和窗口尺寸。

## 设置页结构

设置页保留左侧二级导航，右侧使用紧凑分组：连接、目录、数据表、外观、启动与窗口、系统集成、高级。主题、动效风格和减少动效位于外观；托盘、启动、窗口位置位于启动与窗口；语言和 SendTo 位于系统集成。

