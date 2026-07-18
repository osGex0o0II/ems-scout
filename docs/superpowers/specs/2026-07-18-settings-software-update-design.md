# 设置页软件更新设计

## 目标

在原生 Windows 应用的“系统设置”中提供可信、可理解的软件更新入口。应用负责检查版本和呈现状态，Windows App Installer 负责下载、签名验证与覆盖安装。更新不得中断正在运行的采集任务，也不得修改或迁移生产数据库。

## 用户体验

设置页新增“软件更新”区域，显示当前版本、最近一次检查结果和一个上下文操作按钮。

- 初始状态：显示当前版本，按钮为“检查更新”。
- 检查中：显示进度，按钮禁用，避免重复请求。
- 已是最新版：显示最新状态和本次检查时间。
- 发现新版本：显示可用版本，按钮变为“安装更新”。
- 采集中发现更新：保留版本信息，但禁用安装并显示“采集结束后可安装”。采集结束后自动恢复按钮。
- 检查失败：使用中性错误文案并允许重试，不影响设置页其他功能。
- 启动安装器失败：保留可用版本，允许再次尝试。

更新区使用现有 `WorkbenchCardStyle`、主题中性色和工具栏按钮样式。只有可执行的主操作使用系统强调色，不新增高饱和状态背景。

## 架构

### Application 层

新增 `Updates` 模块：

- `AppInstallerManifestParser`：安全解析 `.appinstaller` XML，只读取 `MainPackage` 的 Name、Publisher、Version、Uri。
- `AppUpdateService`：通过固定 HTTPS 地址下载清单，限制响应大小，校验包身份、Publisher、下载主机和版本，再与当前版本比较。
- `IAppVersionProvider`：提供当前已安装版本，方便测试和平台适配。
- `IAppUpdateLauncher`：请求操作系统打开 App Installer，Application 层不直接依赖 WinRT。

检查结果是不可变数据，明确区分“已是最新版”和“发现更新”。网络、XML、身份或 URI 校验失败统一转换为用户可重试的检查失败，不把内部异常细节暴露到界面。

### Desktop 层

- `PackageAppVersionProvider` 优先读取 `Package.Current.Id.Version`，开发环境无包身份时回退到程序集版本。
- `WindowsAppUpdateLauncher` 通过 `ms-appinstaller:?source=...` 调用 Windows App Installer，成功后立即退出当前应用。
- `SettingsViewModel` 持有更新状态与命令，复用 `ApplicationOperationState` 禁止采集中安装。
- `SettingsPage` 加载时只加载当前版本，不自动弹出安装 UI；用户明确点击后才联网检查或安装。

## 更新源与安全边界

固定清单地址：

`https://github.com/osGex0o0II/ems-scout/releases/latest/download/EmsScout.appinstaller`

允许的包下载主机仅为 `github.com`，重定向后的下载由系统处理。清单必须满足：

- `MainPackage Name` 等于 `1FACE092-146B-4AE5-83DB-3990E6AE8371`。
- `MainPackage Publisher` 等于 `CN=EMS Scout`。
- `MainPackage Uri` 使用 HTTPS 且主机在允许列表内。
- XML 禁止 DTD，响应体设定大小上限。
- 可用版本必须严格高于当前版本才显示更新。

最终包签名与身份验证由 Windows App Installer 执行。应用不下载、解压或替换自身文件，不实现静默提权和降级。

## 发布流程

新增只由 `vA.B.C.D` 标签手动触发的正式发布工作流：

1. 运行 Node、原生测试、格式检查和发布契约测试。
2. 从 GitHub Secrets 导入生产 PFX，Secrets 名称为 `EMS_SIGNING_CERTIFICATE_BASE64` 与 `EMS_SIGNING_CERTIFICATE_PASSWORD`。
3. 在导入前以临时密钥模式校验证书有效期、私钥和 Subject=`CN=EMS Scout`；任何条件不满足则停止发布。
4. 使用标签版本生成临时包 manifest，构建自包含 Windows App SDK 的 MSIX，完成 RFC3161 时间戳签名并验证包内身份、版本、依赖、签名和时间戳。
5. 生成 `EmsScout.appinstaller`，主包 URL 指向本标签的版本化 MSIX。
6. 发布 MSIX 和 `.appinstaller` 到 GitHub Release。
7. 无论成功失败都从 runner 证书库和磁盘清理证书材料。

现有 `windows-x64.yml` 继续使用临时测试证书做构建与安装门禁，不把测试证书产物发布给用户。

## 数据与运行保护

- 检查更新不获取数据库路径，不接触 `ac.db`、WAL、SHM、备份和采集结果。
- 采集与更新安装使用同一个原子操作槽位双向互斥；采集中不可安装，安装器启动期间也不可开始采集。
- 更新完成后的数据库升级继续走现有 `StartupDatabaseInitializer` 和事务化迁移流程。
- 初版不实现强制更新、后台静默安装、自动回滚或多更新通道。

## 验收

- 清单解析覆盖合法清单、DTD、错误身份、错误 Publisher、HTTP URI、非允许主机和无效版本。
- 更新服务覆盖最新版、发现更新、响应过大、超时和清单校验失败。
- UI 契约覆盖当前版本、检查按钮、安装按钮、状态文案和采集锁绑定。
- 发布契约覆盖标签格式、生产 Secrets、证书主体校验、版本传递、签名验证、Release 资产和清理步骤。
- 原生单测、格式检查与 Release 构建通过。
