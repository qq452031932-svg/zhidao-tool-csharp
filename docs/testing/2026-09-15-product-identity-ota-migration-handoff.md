# 2026-09-15 产品身份与 OTA 迁移本地候选交接

## 结论与边界

基于源码提交 `208ef1d223698da092b9f9472902a4e4089500c3` 已在全新、隔离的 `.artifacts/ota-production-p111` 目录构建出版本严格为 `1.1.1` 的生产等价候选。Setup、Portable、Full、Delta 和签名 feed/manifest 均已生成，构建内置验证和一次独立重验均通过。

本轮只在本地工作树和其 `.artifacts` 下读写；未连接、上传或修改生产服务器，未执行 SSH/SCP/rsync/发布切换，也未调用百度网盘写入接口。现有 1.1.0 归档只读使用，未覆盖或删除；用户其他 artifacts 未清理。

## Git 与实现修复

- 分支：`codex/license-production-deploy`
- 候选源码 HEAD：`208ef1d223698da092b9f9472902a4e4089500c3`
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

## 格式、构建与测试

- `git diff --check`：通过，无输出。
- `dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Debug --no-restore`：通过，0 warnings、0 errors。
- Debug 实际业务产物为 `知道综合工具.exe/.dll/.deps.json/.runtimeconfig.json`；不存在旧名业务 DLL。旧名 `BaiduBatchTransfer.App.exe` 仅作为受限兼容启动器存在。
- `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --no-build --blame-hang-timeout 60s --blame-hang-dump-type none --logger "console;verbosity=normal"`：3276 passed、9 skipped、0 failed，总计 3285，耗时 21 分 25 秒。9 项均为已有环境门禁项；所有测试完成，未生成 hang sequence。
- `OtaProductionReleaseScriptTests`：73 passed、0 failed。
- Velopack 0-byte 占位行为测试：5 passed；异常组合拒绝测试：21 passed。
- `ProductIconTests|ProductIdentityBuildTests|WindowBrandingTests`：15 passed、0 failed。
- `LicenseActivationViewModelTests|LicenseActivationViewStructureTests|SettingsViewModelTests` 加主窗授权摘要两项：52 passed、0 failed。

## 1.1.0 previous archive

只读输入：

`.artifacts/ota-production-20260913-v1.1.0-license/SuperPan.BaiduBatchTransfer-1.1.0-win-x64-stable-upload.zip`

- SHA-256：`67bf5a1621554b4744d0509a182fd85fa9fc571a0cb70e4c3609f1e68b947ad7`
- 历史身份认证报告：`.artifacts/task10-previous-1.1.0-historical-verification.json`
- 结果：`verified=true`；只有这个已认证 1.1.0 输入可启用历史名称兼容。1.1.1 旧布局、mixed 布局和期望版本不匹配的输入均由负向测试拒绝。

## 1.1.1 候选资产

候选目录：`.artifacts/ota-production-p111`

| 资产 | 字节 | SHA-256 |
| --- | ---: | --- |
| `SuperPan.BaiduBatchTransfer-1.1.1-win-x64-stable-upload.zip` | 504260744 | `b8d8f49d634a602d0ad315fedf266f2ae35590a9b33bae7cee8fbaea817eefeb` |
| `SuperPan.BaiduBatchTransfer-1.1.1-win-x64-stable-verification.json` | 1000231 | `5cace853b03a3f4b59a70ff4f48dfd8475b60b2f7701caee24692c7183e1be2e` |
| `SuperPan.BaiduBatchTransfer-Setup.exe` | 106539352 | `04aea3d221c38d0779d2dba24b59998b103e23117aa9c919824af017135badf9` |
| `SuperPan.BaiduBatchTransfer-Portable.zip` | 101886300 | `72767680a6d517d4eff6dc15cb5f90682e61ab022caa1b909837235123f6b967` |
| `SuperPan.BaiduBatchTransfer-1.1.1-full.nupkg` | 101978360 | `b63aa5e3ad4dbcd94adc3ebe947741998aa0b49c6e0a43200077336221debde6` |
| `SuperPan.BaiduBatchTransfer-1.1.1-delta.nupkg` | 8566570 | `18e7e27e1dc360862a3cdb2c1dd9e80f5df971c7814e0bdbefe95f105da9c9ab` |

构建命令的逐进程审计记录位于 `.artifacts/ota-production-p111/build-logs/commands.jsonl`，源码提交记录位于相邻的 `source-commit.txt`。生产构建内置的目录验证、Delta 重建和重新打开 ZIP 验证均退出 0。

独立重验使用 `scripts/Test-OtaUploadBundle.ps1`，带 `ExpectedVersion=1.1.1`、固定发布者证书/SPKI 和 `RequireTimestamp`。报告 `.artifacts/task10-p111-independent-verification.json` 的结果为：`verified=true`、11 个顶层资产、2212 条签名证据、bundle SHA-256 与上表一致；manifest SHA-256 为 `88b89cb61215a08666b1797a0614ff26b94bdf00e02e0e21cba2308e3f899c82`。

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

## 明确待办/阻断

1. 在全新隔离 VM/Windows Sandbox 中完成 Task 9 六路径：全新 Setup、全新 Portable、旧名 1.1.0 的 Full/Delta 更新、固定 Setup 链接覆盖安装、回退/失败路径。
2. 提供生产信任链签发的专用测试卡，并记录创建与消耗审计；完成未激活、有限期、永久及升级后不重新激活四项。
3. 上述本地验收完成且用户再次明确授权上线后，才可准备生产 staging、上传和原子切换。本交接不构成上线授权；当前状态是“尚未上传生产”。

历史失败构建日志均保留在相应 `.artifacts/ota-production-*.failure-logs-*` 目录，未删除，以便追溯。
