# EMS Scout Windows 验证清单

更新时间：2026-07-18

本清单用于在另一台 Windows x64 设备上从干净克隆继续验证。每一步通过后再进入下一步；
失败时保留命令、完整输出和 `artifacts/`，不要用后续成功掩盖前序失败。

## 1. 环境要求

- Windows 11 x64，系统版本至少 `10.0.26100`（Windows 11 24H2）。
- Git，且已配置访问 `git@github.com:osGex0o0II/ems-scout.git` 的 SSH key。
- Node.js `24.18.0` 和配套 npm。
- .NET SDK `10.0.x`。
- Microsoft Edge。
- Windows PowerShell 5.1；建议同时安装 PowerShell 7 和 Windows Terminal。
- 首次 restore、npm install 和 Sidecar 准备阶段需要访问 NuGet、npm、GitHub 和 nodejs.org。

检查版本：

```powershell
git --version
node --version
npm --version
dotnet --info
$PSVersionTable.PSVersion
(Get-Item "$env:ProgramFiles(x86)\Microsoft\Edge\Application\msedge.exe").VersionInfo.FileVersion
```

## 2. 克隆

```powershell
git clone git@github.com:osGex0o0II/ems-scout.git
Set-Location ems-scout
git branch --show-current
git status --short
git log -1 --oneline
```

预期：分支为 `main`，工作树为空。不要把旧机器的 `out/`、浏览器 profile、`bin/`、
`obj/` 或 `node_modules/` 复制进仓库后再做干净克隆门禁。

## 3. 恢复依赖

```powershell
npm ci
dotnet restore native\EmsScout.Native.slnx -r win-x64
```

失败时先记录错误；不要删除 lockfile、升级包或临时更换版本来绕过恢复问题。

## 4. 干净克隆自动化门禁

```powershell
npm test
npm run self-test
npm run native:test
dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore
```

说明：干净克隆没有 `out/` 生产证据。Node run17 黄金测试会明确 skip；
`npm run native:test` 会排除 `Fixture=ProductionEvidence`，其余源码、迁移、契约、Excel、
回滚和架构测试必须通过。

CI 和安装包不得复制或上传 `data/`、`out/`、`*.db`、`*-wal`、`*-shm` 等现场证据；迁移自动化使用 `tests/contract-audit/fixtures/schema-v0.sql` 创建合成数据库。

## 5. 原生应用构建与启动

```powershell
npm run native:build
npm run native:run
```

不要直接双击 `bin\...\EmsScout.Desktop.exe`。必须通过带包身份的 `native:run` 或安装后的
MSIX 启动。

人工检查：

- 窗口、开始菜单和诊断页显示 `EMS Scout`。
- 七个页面可导航：总览、采集任务、数据管理、审计中心、分组设置、系统设置、诊断。
- 首次启动能创建默认数据目录和数据库，迁移失败时显示安全错误而不是原始异常。
- 设置页可保存 EMS 地址、数据目录、导出目录和日志级别。
- 没有现场数据时显示空状态，不崩溃、不伪造统计。

## 6. Sidecar 打包验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\prepare-sidecar.ps1
Get-Content artifacts\sidecar\win-x64\payload-manifest.json -Raw
```

预期：

- 下载并校验固定版本 Node `v24.18.0`。
- payload smoke 成功加载 Playwright、snapshot adapter 和全部枚举拆分模块。
- `runtime\node.exe`、`app\sidecar`、`app\src`、契约和 manifest 都存在。
- 不依赖全局 Node 执行打包后的 Sidecar。

## 7. Windows x64 MSIX

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-native.ps1 -Configuration Release
Get-ChildItem artifacts\packages\win-x64 -Recurse
```

记录实际生成的 MSIX 路径、文件大小和 SHA-256：

```powershell
Get-ChildItem artifacts\packages\win-x64 -Recurse -File |
  Select-Object FullName, Length, @{Name='SHA256';Expression={(Get-FileHash $_.FullName -Algorithm SHA256).Hash}}
```

安装包验收必须覆盖：

1. 当前测试机安装并启动。
2. 干净 Windows 用户配置下首次启动。
3. 安装后不依赖源码目录或全局 Node。
4. 同版本重装和后续版本升级。
5. 卸载后应用包被移除，用户数据是否保留符合预期并有记录。

如果包未签名或证书不受信任，记录具体错误和使用的测试证书流程；不得关闭系统安全策略后
把结果描述成正式安装通过。

GitHub Actions 使用 `scripts\new-test-signing-certificate.ps1` 创建不可导出的短期测试证书，
由 `scripts\package-native.ps1 -PackageCertificateThumbprint <thumbprint>` 签名 Release MSIX，
再用 `scripts\test-msix-install.ps1` 在干净 runner 上完成两轮安装、AUMID 启动和卸载。
安装烟测发现同身份应用已存在时必须直接失败，不得自动删除开发机上的现有包。

该自动化只证明测试签名包的干净用户安装生命周期。正式生产证书、跨版本升级、安装包内置
Sidecar 实采和真实 EMS 仍按本清单后续步骤独立验收。

## 8. 正式更新发布与跨版本验收

正式更新依赖同一包身份、同一 Publisher 和连续可信的生产签名证书。现有云端安装门禁生成的
临时 `CN=EMS Scout` 证书只用于一次 runner 验证，不能用于正式发布，也不能证明跨版本升级。

仓库管理员必须在 GitHub Actions Secrets 中配置：

- `EMS_SIGNING_CERTIFICATE_BASE64`：生产 PFX 的 Base64 内容。
- `EMS_SIGNING_CERTIFICATE_PASSWORD`：PFX 密码。

生产证书必须包含私钥，Subject 必须精确等于 `CN=EMS Scout`，且发布时剩余有效期至少 30 天。
证书续期必须保持 Windows 包升级所需的 Publisher 信任连续性；不得为每个版本生成新自签证书。

正式版本使用四段递增标签，例如：

```powershell
git tag v1.0.1.0
git push origin v1.0.1.0
```

`.github/workflows/release-windows-x64.yml` 会重新运行测试和格式门禁，使用标签版本构建签名
MSIX，生成 `EmsScout.appinstaller`，复核签名后发布两个 GitHub Release 资产。Release MSIX
内嵌 Windows App SDK，不依赖单独的 `Microsoft.WindowsAppRuntime.2` 框架包；包内身份、
Publisher、四段版本和依赖由打包脚本直接检查。生产签名必须带 RFC3161 时间戳，确保签名
证书到期后历史安装包仍可验证。Secrets 缺失或
证书校验失败时必须停止，不得回退到临时证书或未签名包。

跨版本验收必须在隔离 Windows 用户和临时数据目录中执行：

1. 通过上一正式 Release 的 `EmsScout.appinstaller` 安装 N-1。
2. 启动应用，在临时数据目录写入合成数据库和设置，记录文件哈希及包版本。
3. 发布 N，并在 N-1 的“系统设置 -> 软件更新”中检查更新。
4. 确认发现 N；启动采集任务时“安装更新”必须禁用，结束采集后恢复。
5. 点击“安装更新”，由 Windows App Installer 完成覆盖安装，然后通过 AUMID 启动。
6. 核对 `Get-AppxPackage -Name 1FACE092-146B-4AE5-83DB-3990E6AE8371` 的 Version 和 Publisher。
7. 核对临时数据库、设置和导出结果保留；数据库迁移只执行一次且无损。

正式验收还要覆盖断网、Release 资产不存在、错误 Publisher、签名不可信和低版本包。不得在
采集运行中强制安装，不得用生产 `out\ac.db`、WAL/SHM 或备份作为升级测试输入。

## 9. 可选生产证据测试

`out/` 不在 Git 中。只有在确认文件来源、权限和哈希后，才把所需 run17 DB、JSON、
质量报告和实时文件放到本机仓库的 `out/`，然后运行：

```powershell
npm run native:test:evidence
```

该测试通过 `ProductionDataSnapshot` 复制数据库后查询，不直接通过 SQLite 打开源 DB。
不要从聊天、临时网盘或未知备份获取生产证据。

## 10. 真实 EMS 单栋 E2E

先关闭可能占用 Edge/CDP 的旧测试，再运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\field-e2e.ps1 `
  -Building 1号 -LaunchEdge -RunSingleBuilding
```

在脚本打开的隔离 Edge 中完成 EMS 登录。脚本必须使用：

- 唯一 `out\field-e2e-*` runDir。
- 随机 loopback CDP 端口。
- `--remote-debugging-address=127.0.0.1`。
- 本次 runDir 独立 profile。
- 临时 CollectionSnapshot、SQLite、质量输出和 Excel。
- 默认关闭本次 Edge 并清理 profile。

保留 runDir 内的 WorkflowEvent、snapshot、DB、质量、Excel 和 manifest 作为证据。
任何阶段触碰生产 `out\ac.db`、WAL 或 SHM 都视为失败。

## 11. 六栋 shadow parity

单栋通过后再运行全量采集。将安装产品产生的 CollectionSnapshot 与当前权威口径比较：

- 6 栋。
- 143 子区。
- 373 页面。
- 6571 raw cards。
- 6568 unique cards。
- 楼栋唯一卡片：1493、107、1106、1096、286、2480。

通讯状态会随现场变化，不能要求固定开关数量；必须核查卡片唯一性、占位符、indicator、
质量原因和已知来源缺陷。差异需按楼栋、子区、页面和设备名保留证据。

## 12. 结果记录

每次 Windows 验证至少记录：

- commit SHA、Windows build、Node/.NET/Edge 版本。
- 执行命令和退出码。
- 自动化测试通过/失败/skip 数。
- MSIX 路径、大小、SHA-256、安装方式和证书状态。
- 现场 runDir 名称、楼栋、耗时、卡片数和质量结论。
- 失败阶段、错误码、相关 NDJSON 日志和是否可复现。

验证结果应追加到 `CHANGELOG.md` 或独立验收记录后再提交，不提交 `artifacts/`、`out/`、
浏览器 profile、生产 DB 或日志原件。
