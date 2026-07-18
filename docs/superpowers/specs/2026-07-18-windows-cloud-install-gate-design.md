# Windows 云端安装门禁设计

## 目标

把现有 Windows GitHub Actions 从“测试并生成 MSIX”扩展为可审计的
“测试、生成临时测试证书、签名、安装、启动、同版本重装、卸载、清理”闭环。
门禁只使用合成/默认应用数据，不读取或上传 `data/`、`out/` 或任何 SQLite 现场证据。

## 方案选择

采用工作流内生成的临时自签代码签名证书。证书主题必须与
`Package.appxmanifest` 的 `Publisher="CN=EMS Scout"` 一致，私钥只存在于当次
GitHub-hosted Windows runner 的 `CurrentUser\My`，公钥临时加入
`CurrentUser\TrustedPeople`，作业结束时删除。

未采用以下方案：

- 允许未签名旁加载：绕过了实际 Windows 信任链，不能证明用户可安装。
- 仓库内保存 PFX：会泄露私钥，禁止使用。
- 立即接入正式企业证书：当前没有受控的证书 Secret；测试门禁应先独立闭环。

## 组件边界

### `scripts/new-test-signing-certificate.ps1`

只负责生成临时代码签名证书并返回证书指纹。证书必须可用于代码签名，主题固定为
`CN=EMS Scout`，不得导出私钥到工作区或 Artifact。

### `scripts/package-native.ps1`

新增可选 `PackageCertificateThumbprint`。传入时启用 MSIX 签名并把指纹传给
MSBuild；未传入时保留现有本地无签名构建能力。脚本在成功后验证主 EMS Scout
MSIX 的 Authenticode 状态，签名不合法立即失败。

### `scripts/test-msix-install.ps1`

只允许在没有已注册 EMS Scout 包的干净用户环境执行，避免覆盖开发机上的松散包。
脚本发现唯一主 MSIX 和当前架构依赖后，执行两轮：

1. 安装、按 AUMID 激活、确认 `EmsScout.Desktop` 进程保持运行、关闭、卸载。
2. 用同一包重新安装、再次激活验证、关闭、卸载。

`finally` 必须清理由脚本安装的应用包。启动使用 Windows
`IApplicationActivationManager`，不依赖直接运行无包身份的 EXE。

### `.github/workflows/windows-x64.yml`

原有测试全部通过后生成测试证书，使用证书指纹构建 Release x64 包，验证签名并运行
安装烟测。证书与残留应用包在 `if: always()` 步骤清理。Artifact 只上传测试结果、
Sidecar manifest 和包目录，不上传私钥。

## 失败与安全语义

- 任意测试、签名、安装、激活或卸载失败都使作业失败。
- 已存在同身份应用包时安装烟测直接失败，不自动删除来源不明的本机包。
- 签名证书主题、包 Publisher 或签名状态不一致时直接失败。
- 启动后进程未出现或提前退出时直接失败。
- 清理步骤始终执行，但不得掩盖此前失败。
- 本门禁证明测试签名包可在 GitHub-hosted Windows 用户环境完成安装生命周期；不把它
  描述为正式生产证书、升级包或真实 EMS 现场验收。

## 验收标准

- 当前非生产原生测试、Node 测试、self-test、格式、迁移和 Sidecar smoke 通过。
- 生成的 EMS Scout 主 MSIX `Get-AuthenticodeSignature` 为 `Valid`。
- GitHub Actions 日志包含两次安装/启动成功和最终卸载成功。
- 作业结束后 `Get-AppxPackage -Name 1FACE092-146B-4AE5-83DB-3990E6AE8371`
  无结果，临时证书不再位于 `CurrentUser\My` 或 `CurrentUser\TrustedPeople`。
- Artifact 包含签名 MSIX，且不包含 `.pfx`、`.pvk` 或其他私钥文件。

