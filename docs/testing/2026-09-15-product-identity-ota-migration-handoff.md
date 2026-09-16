# 2026-09-15 产品身份与 OTA 迁移本地候选交接

## 结论与边界

基于源码提交 `0bb6df0e13de0315c946dc144edba06b54111165` 已在全新、隔离的 `.artifacts/ota-production-p111-task11-final` 目录构建出版本严格为 `1.1.1` 的生产等价候选。Setup、Portable、Full、Delta 和签名 feed/manifest 均已生成，构建内置验证、一次独立客户端重验和一次 Go 服务端真实候选重验均通过。

本轮只在本地工作树和其 `.artifacts` 下读写；未连接、上传或修改生产服务器，未执行 SSH/SCP/rsync/发布切换，也未调用百度网盘写入接口。现有 1.1.0 归档只读使用，未覆盖或删除；用户其他 artifacts 未清理。

## Git 与实现修复

- 分支：`codex/license-production-deploy`
- 候选源码 HEAD：`0bb6df0e13de0315c946dc144edba06b54111165`
- 本轮关键提交：
  - `23eec58f`：生产源码快照使用 NUL 分隔、严格 UTF-8 枚举中文路径。
  - `ccab3e71`：修正中文程序集 WPF pack URI、动态资源装载及两组测试夹具竞态。
  - `f2d06496`：阻止异步复制返回值污染 Git 路径列表。
  - `0b4fe31b`：复核并锁定生产源码 override manifest。
  - `52f2fbe8`：严格解析 RFC3161 GeneralizedTime。
  - `8076c709`：历史产品身份兼容只能由已认证且期望版本精确为 1.1.0 的 previous archive 触发。
  - `ce508b57`：把生产启动探针放到短路径，避开 Win32 进程路径长度限制。
  - `95473330`：只发布当前版本 Delta，不携带历史 Delta。
  - `208ef1d2`：仅接受 Velopack 1.2 对未变化普通文件生成的成对 0-byte diff/shasum 占位；身份关键文件、单边占位和变化文件全部拒绝。
  - `0bb6df0e`：让 Go 服务端与客户端执行完全相同的空占位、品牌、架构和图标门禁；固定唯一生产 feed；保留旧启动器调用方工作目录；迁移门强制 Velopack `1.2.0`。

## 格式、构建与测试

- `git diff --check`：通过，无输出。
- `dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Debug --no-restore`：通过，0 warnings、0 errors。
- Debug 实际业务产物为 `知道综合工具.exe/.dll/.deps.json/.runtimeconfig.json`；不存在旧名业务 DLL。旧名 `BaiduBatchTransfer.App.exe` 仅作为受限兼容启动器存在。
- `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --no-build --blame-hang-timeout 60s --blame-hang-dump-type none --logger "console;verbosity=normal"`：3276 passed、9 skipped、0 failed，总计 3285，耗时 21 分 25 秒。9 项均为已有环境门禁项；所有测试完成，未生成 hang sequence。
- `OtaProductionReleaseScriptTests`：73 passed、0 failed。
- Velopack 0-byte 占位行为测试：5 passed；异常组合拒绝测试：21 passed。
- `ProductIconTests|ProductIdentityBuildTests|WindowBrandingTests`：15 passed、0 failed。
- `LicenseActivationViewModelTests|LicenseActivationViewStructureTests|SettingsViewModelTests` 加主窗授权摘要两项：52 passed、0 failed。
- 修复提交后的聚焦 .NET 复验（产品身份/图标、旧启动器单元与集成、生产构建脚本、构建配置、迁移脚本）：251 passed、0 failed。
- `go test ./... -count=1`：全部 Go 包通过；其中 `internal/release`、`internal/candidate`、`internal/deploy` 及 Windows CRLF 兼容门均通过。新候选通过 Go 服务端真实 ZIP 校验，耗时 7.912 秒。

## 1.1.0 previous archive

只读输入：

`.artifacts/ota-production-20260913-v1.1.0-license/SuperPan.BaiduBatchTransfer-1.1.0-win-x64-stable-upload.zip`

- SHA-256：`67bf5a1621554b4744d0509a182fd85fa9fc571a0cb70e4c3609f1e68b947ad7`
- 历史身份认证报告：`.artifacts/task10-previous-1.1.0-historical-verification.json`
- 结果：`verified=true`；只有这个已认证 1.1.0 输入可启用历史名称兼容。1.1.1 旧布局、mixed 布局和期望版本不匹配的输入均由负向测试拒绝。

## 1.1.1 候选资产

候选目录：`.artifacts/ota-production-p111-task11-final`

| 资产 | 字节 | SHA-256 |
| --- | ---: | --- |
| `SuperPan.BaiduBatchTransfer-1.1.1-win-x64-stable-upload.zip` | 504259441 | `8bb9131033d393510466edfab30462129f987a1b4f5eba9571f1b99a2ea5099e` |
| `SuperPan.BaiduBatchTransfer-1.1.1-win-x64-stable-verification.json` | 1000231 | `661e4b90adf16399601387f949e0631df6975836afb578d4e624048c3fabaa4b` |
| `SuperPan.BaiduBatchTransfer-Setup.exe` | 106539328 | `41eed43fa1630df325519cc5d0d099cf89cf26742535f139fb496c6fdfb14aa0` |
| `SuperPan.BaiduBatchTransfer-Portable.zip` | 101886270 | `d90d0a2893b2253d8b6f178ecdf6d6d4cca3326b013c8f3985deac2dd72b6111` |
| `SuperPan.BaiduBatchTransfer-1.1.1-full.nupkg` | 101978330 | `9c43b52c3f3c3e930e057b1ed60299206f7e03c3bc40013fe54c81d73528a519` |
| `SuperPan.BaiduBatchTransfer-1.1.1-delta.nupkg` | 8565632 | `7591fc31e97cdcd3df7b602e48979edcdc507ca010f15df011c63e5736f5a224` |

构建命令的逐进程审计记录位于 `.artifacts/ota-production-p111-task11-final/build-logs/commands.jsonl`，源码提交记录位于相邻的 `source-commit.txt`，内容为 `0bb6df0e13de0315c946dc144edba06b54111165`。生产构建内置的目录验证、Delta 重建和重新打开 ZIP 验证均退出 0。

独立重验使用 `scripts/Test-OtaUploadBundle.ps1`，带 `ExpectedVersion=1.1.1`、固定发布者证书/SPKI 和 `RequireTimestamp`。报告 `.artifacts/task11-p111-final-independent-verification.json` 的结果为：`verified=true`、11 个顶层资产、2212 条签名证据、bundle SHA-256 与上表一致；manifest SHA-256 为 `eab5b3458f950065521428dabdbcbeae9b4554ef257abbdfd1501c8cafe6e004`。

## PE 元数据、图标与签名

Portable `current/知道综合工具.exe`：

- `ProductName=知道综合工具`
- `FileDescription=知道综合工具`
- `ProductVersion=1.1.1`
- `FileVersion=1.1.1.0`
- `OriginalFilename/InternalName=知道综合工具.dll`

规范 ICO 自检通过，包含 16/24/32/48/64/128/256 像素帧。使用 PE `RT_GROUP_ICON/RT_ICON` 资源直接提取并与规范 ICO 的原始帧逐字节 SHA-256 比较，16、32、48、256 四帧均 `match=True`；证据脚本位于 `.artifacts/task10-icon-evidence.ps1`。

主 EXE、旧名兼容启动器和 Setup 的 Authenticode 状态均为 `Valid`，签名者均为 `CN=SuperPan OTA Publisher`，证书指纹 `5AE52053A2D7E548E1FA747DF3C6313167668F40`，SPKI SHA-256 `f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff`，并具有 DigiCert RFC3161 时间戳。

## 旧业务产物引用审计

`rg -n "BaiduBatchTransfer\.App\.(exe|dll|deps\.json|runtimeconfig\.json)" src scripts services tests` 的命中已分类：

- `ProductIdentity.Generated.cs`、Go 生成契约、`BaiduBatchTransfer.LegacyLauncher` 和相应 Branding 测试：明确的旧名兼容启动器契约。
- `Test-VelopackExecutableRenameSpike.ps1`、`Test-VelopackExecutableRenameMigration.ps1` 和相应测试：1.1.0 历史升级夹具。
- `Test-OtaUploadBundle.ps1` 的旧 DLL/deps/runtimeconfig 列表及 Updates 测试：拒绝 stale/mixed 布局的负向校验，或验证兼容 EXE 必须存在。
- `DoesNotContain` / `File.Exists(...)=false` 断言：证明生产/卡密/主程序路径不再消费旧业务产物。

未发现主程序构建、WPF pack URI、卡密构建脚本、生产/acceptance 主入口、客户端生产身份验证器或 Go 服务端验证器仍把旧 DLL/deps/runtimeconfig 当作当前业务产物。

## 卡密与迁移验收状态

授权 UI 与状态自动测试已通过 52 项，覆盖未激活、激活中/失败、错误码安全映射、有限期显示、无授权占位、设置页授权状态与主窗口套餐/到期摘要更新。

当前 artifacts 和仓库中未找到可用于生产信任链验收的专用测试卡或其独立创建/消耗审计记录，因此未伪造以下真实验收结论：未激活、有限期、永久、从 1.1.0 升级后不要求重新激活。这四项仍需在隔离 Release 用户环境拿到专用测试卡后执行。

已有 Velopack 1.2 独立低权限用户 spike 证据位于 `.artifacts/velopack-executable-rename-spike-1.2.0/isolated-evidence/final-evidence.txt`，证明旧名安装可升级并重启到中文主程序，桌面和开始菜单快捷方式均指向中文 EXE。但它是合成迁移 spike，不替代真实签名 1.1.0 → 1.1.1 候选的完整六路径验收。

## 生产发布清单（只生成，未执行）

### 本地来源与服务器写入边界

- 唯一允许上传的本地文件：`.artifacts/ota-production-p111-task11-final/SuperPan.BaiduBatchTransfer-1.1.1-win-x64-stable-upload.zip`，SHA-256 为 `8bb9131033d393510466edfab30462129f987a1b4f5eba9571f1b99a2ea5099e`。
- 为上线前逐项核对而展开的本地 staging：`.artifacts/task11-production-staging-1.1.1-final`。它不是新的构建输入；其 11 个文件来自上述 ZIP，不能单独拼包、改名或替换。
- 生产发布入口只能使用 `https://ota.fanfh.cn/admin/` 的候选上传、门禁和明确发布确认。不要用 SCP、宝塔文件管理器或 shell 直接覆盖 `stable` 中的文件。
- 服务端公开根保持 `/www/wwwroot/ota.fanfh.cn/releases/win-x64`，但发布器只允许在自己的 staging 和 `generations/release-<id>` 内建立新一代，并以相对 `stable` 链接原子切换。禁止递归删除 `/www/wwwroot/ota.fanfh.cn`、`releases`、`generations` 或任何服务器目录；禁止修改/清理同机其他站点、数据库、宝塔配置和其他项目内容。

服务器目标 generation 的精确资产 allowlist 与本地新 SHA-256 如下。除这 11 项外出现任何顶层文件都必须停止；`upload-manifest.sha256` 本身只引用其允许的发布资产，签名文件用于验证该清单。

| 服务器 generation 顶层资产 | 新 SHA-256 |
| --- | --- |
| `assets.win-x64-stable.json` | `86e46098791a75854eaa1fd1b30dd8a354e9dfa322fb99c4bcde9f9b1828e665` |
| `RELEASES-win-x64-stable` | `99b6b6cbf5f5f5aa9f1b47c4ecc87ed4743edfbdfddd170303f8e8f0b9950c36` |
| `releases.win-x64-stable.json` | `45a450eb995e23c4c66605da610c5eff3aaa4ab32137ffc42d5bcd8d4f2c6d0e` |
| `SuperPan.BaiduBatchTransfer-1.0.6-full.nupkg` | `d7911cc0a7af42a3d944006e0a7010927362f590684f85d6cc1f1f8811633fe1` |
| `SuperPan.BaiduBatchTransfer-1.1.0-full.nupkg` | `898afdc6308d715d8dca1102ba76a86646afe7cbded254efb76a22c5fac31eb1` |
| `SuperPan.BaiduBatchTransfer-1.1.1-delta.nupkg` | `7591fc31e97cdcd3df7b602e48979edcdc507ca010f15df011c63e5736f5a224` |
| `SuperPan.BaiduBatchTransfer-1.1.1-full.nupkg` | `9c43b52c3f3c3e930e057b1ed60299206f7e03c3bc40013fe54c81d73528a519` |
| `SuperPan.BaiduBatchTransfer-Portable.zip` | `d90d0a2893b2253d8b6f178ecdf6d6d4cca3326b013c8f3985deac2dd72b6111` |
| `SuperPan.BaiduBatchTransfer-Setup.exe` | `41eed43fa1630df325519cc5d0d099cf89cf26742535f139fb496c6fdfb14aa0` |
| `upload-manifest.sha256` | `eab5b3458f950065521428dabdbcbeae9b4554ef257abbdfd1501c8cafe6e004` |
| `upload-manifest.sha256.sig` | `95baaa23c12a32fc8e751bef9d92eb8436abc4b6a05dad7fe29a47bb61950ae8` |

### 上线前只读采集（必做，不猜旧值）

本轮没有连接生产服务器，因此没有新鲜的“当前生产旧 SHA”或当前 `stable` release ID。历史文档中的哈希不能冒充上线时基线。获得单独上线授权后，任何写操作之前必须只读采集：当前 `stable` 相对链接目标、该 generation 的精确普通文件清单与 SHA-256、后台当前版本/release ID、公开 URL 对应 SHA-256 和缓存头。结果固定记录到本地 `.artifacts/production-release-preflight-1.1.1/stable-target.txt`、`current-production-sha256.txt`、`admin-current-release.json`、`public-production-sha256.txt`；任一项无法采集、路径含绝对/反斜杠/`..`、存在符号链接成员、文件集超出已发布 generation，均停止发布。

### 原子上传、校验、切换顺序

1. 确认工作树提交、候选 ZIP SHA-256 和本页 11 项新 SHA 未变化；确认真实六路径 VM 与生产链测试卡验收已经补齐。未补齐时保持门禁关闭。
2. 完成上一节只读生产基线并记录“切换前 release ID/generation”。不要移动、覆盖或删除原 generation。
3. 在管理后台只上传上述唯一 ZIP；等待服务端把候选写入其专用 staging。上传或校验中断时不点发布，保留原 `stable`。
4. 核对后台所有验证门均通过，候选版本严格为 `1.1.1`、包 ID 为 `SuperPan.BaiduBatchTransfer`、渠道为 `win-x64-stable`，服务器显示的 11 项文件名/哈希与本页完全一致。任何差异立即停止。
5. 由用户在后台执行一次明确发布确认。发布器负责写入并同步临时 generation、将其原子改名为 `generations/release-<new-id>`、建立临时相对链接，再原子替换 `stable` 并同步父目录；不要另行手工切换别名。
6. 仅在后台操作到达成功终态后，只读核对：`stable` 指向新 generation；11 项哈希一致；release feed、asset catalog、`RELEASES` 引用闭包一致；固定 Setup/Portable 别名和版本化包的公网哈希/缓存头正确。
7. 最后才进行下一节的用户在线验收。验收未完成前不得称为生产上线完成。

### feed/固定别名回退

- 预先保留切换前 release ID、generation 和全量 SHA；旧 generation 不删除。
- 如新 feed、下载、应用或数据/授权验收失败，立即停止继续分发，在后台“发布历史”选择刚才记录的上一生产 release，输入当前管理员密码并再次确认回滚。后台会原子把 `stable` 相对链接切回该 generation；不要手工覆盖三个 feed 文件，也不要单独替换 Setup/Portable。
- 回退后重新核对 `stable` 目标、旧 SHA、三个元数据入口与固定 Setup/Portable 公网 URL。后台回退只改变服务端 feed/别名，不会让已经升级到 1.1.1 的客户端自动降级；已升级客户端应停止继续操作，等待高版本前向修复，不能强制覆盖用户数据。
- 如果后台回退失败、旧 generation/哈希与基线不一致、数据库或文件系统报告非终态，停止一切写操作并保留证据，不运行递归删除或自行修复命令。

## 用户真实在线验收路径（上线后执行）

1. 在专用隔离 Windows 用户/VM 安装并启动正式 `1.1.0`，先记录 `%LOCALAPPDATA%\BaiduBatchTransfer` 的账号、设置、任务唯一标记和卡密到期时间；确认程序当前确为旧版本。
2. 在 1.1.0 内点击“检查更新”，必须只发现正式 `1.1.1`；核对包 ID/渠道和签名链后下载并应用更新。
3. 应用完成后必须自动重启到 `知道综合工具.exe`。核对窗口标题、任务栏/桌面/开始菜单图标和快捷方式目标；同目录旧入口 `BaiduBatchTransfer.App.exe` 只能转发到中文主程序。
4. 核对升级前记录的百度账号、设置、当前任务/历史、卡密激活状态和精确到期时间全部保留，不要求重新激活；不要用“能打开窗口”替代数据核验。
5. 在另一全新隔离 Windows 用户/VM 打开固定链接 `https://ota.fanfh.cn/releases/win-x64/stable/SuperPan.BaiduBatchTransfer-Setup.exe`，下载后先核对公网 SHA 与当前生产清单，再完成全新安装；确认安装根、桌面/开始菜单入口和自动启动目标均为 `知道综合工具.exe`。
6. 可另外核对固定 Portable 链接 `https://ota.fanfh.cn/releases/win-x64/stable/SuperPan.BaiduBatchTransfer-Portable.zip`，但 Portable 不能替代 Setup 的快捷方式/卸载项证据。

以下任一情况出现都应立即停止，不继续下一步，并按上一节回退 feed/固定别名：检查不到 1.1.1 或发现其他版本；签名/哈希/包 ID/渠道校验失败；下载/应用失败；没有自动重启、重启进程不是中文主程序、仍生成旧名主快捷方式；账号/设置/任务丢失或改变；卡密失效、到期时间改变或要求重新激活；固定 Setup 链接 404、哈希不符或安装目标错误。若服务器稳定目录、数据库或同机其他项目出现任何意外变化，也必须停止且不得清理现场。

上述两个固定下载 URL 只有在 1.1.1 经后台成功原子发布并完成公网复核后，才代表本候选并可以发给用户。当前它们仍是生产 `stable` 的动态别名，本地候选存在不等于链接已经生效。

## 最终实现自审（2026-09-16）

最终审阅范围为基线 `4bccc2803839d3051177ab4e862e8f7cddbaf42d` 到本交接提交之前的完整产品身份/OTA 迁移差异。审阅清单覆盖：中文主产物和 PE/WPF 品牌；稳定 PackageId、数据目录、互斥体与卡密产品 ID；旧名兼容启动器边界；本地卡密构建；Setup/Portable/Full/Delta 生产构建；客户端及 Go 服务端上传验证；1.1.0 历史身份限定；迁移门的进程、离线、证据与回退边界；生产候选及本页发布边界。聚焦复验结果记录在最终提交前的新鲜命令输出中；若发现阻断项，必须先按回归测试修复并重新执行 Task 10 门禁，不能仅在文档中豁免。

当前停在生产变更边界：只新增本地候选、展开 staging 和发布清单，没有连接、上传、修改或切换生产服务器。本地 1.1.1 候选可供后续获授权上线，但真实六路径 VM 和生产链测试卡仍为 pending，固定下载链接也尚不能宣称已经指向 1.1.1。

## 明确待办/阻断

1. 在全新隔离 VM/Windows Sandbox 中完成 Task 9 六路径：全新 Setup、全新 Portable、旧名 1.1.0 的 Full/Delta 更新、固定 Setup 链接覆盖安装、回退/失败路径。
2. 提供生产信任链签发的专用测试卡，并记录创建与消耗审计；完成未激活、有限期、永久及升级后不重新激活四项。
3. 上述本地验收完成且用户再次明确授权上线后，才可准备生产 staging、上传和原子切换。本交接不构成上线授权；当前状态是“尚未上传生产”。

历史失败构建日志均保留在相应 `.artifacts/ota-production-*.failure-logs-*` 目录，未删除，以便追溯。

## 本次会话最终交接快照

- 本快照写入前的交接基线提交：`5dd0c995bb60d8bd79d6a9ecb50e63de4da02ca3`。
- 最终候选源码提交：`0bb6df0e13de0315c946dc144edba06b54111165`。
- 服务器唯一允许上传的候选：`.artifacts/ota-production-p111-task11-final/SuperPan.BaiduBatchTransfer-1.1.1-win-x64-stable-upload.zip`。
- 候选大小：`504259441` bytes；SHA-256：`8bb9131033d393510466edfab30462129f987a1b4f5eba9571f1b99a2ea5099e`。
- 本地首次安装程序：`.artifacts/task11-production-staging-1.1.1-final/SuperPan.BaiduBatchTransfer-Setup.exe`。
- 本地 Portable：`.artifacts/task11-production-staging-1.1.1-final/SuperPan.BaiduBatchTransfer-Portable.zip`。
- 最终验证：相关 .NET 门禁 `251/251` 通过；Go 全量测试通过；独立客户端验证 `verified=true`，包含 11 个顶层文件和 2212 条签名证据；真实候选 Go 服务端验证通过；最终代码审阅原 1 项 Critical 与 4 项 Important 均已关闭。
- 生产状态：未连接、未上传、未修改或切换生产服务器；未覆盖 1.1.0，未删除用户现有 artifacts，也未清理服务器上的其他项目。
- 上线前仍必须完成：真实六路径 VM/Windows Sandbox 验收、生产信任链测试卡四状态验收，以及用户再次明确给出的生产上线授权。
