# 1.1.3 生产候选交接（2026-09-18）

## 为什么是 1.1.3 而不是 1.1.2

1.1.2 候选已于本日早些时候构建（`ota-production-p113-...` 之前的 `p112` 目录，
上传包 SHA-256 `9caf4640…`），但**从未发布**。此后又合入了账号栏横向折叠、导航
加粗与排序、`分享数据`→`监控反馈` 改名等改动。

继续沿用 1.1.2 会让归档里出现两个**内容不同、版本号相同**的包。Velopack 客户端
按版本号缓存，一旦其中任何一份流出，升级链会进入"同版本不同二进制"的脏状态。
因此按既定的"每次调整顺延版本号"策略出 1.1.3。**1.1.2 候选作废，不得发布。**

## 候选资产

目录：`.artifacts/ota-production-p113-20260918-151346`
源码提交：`34f25b30`

| 资产 | 字节 | SHA-256 |
| --- | ---: | --- |
| `SuperPan.BaiduBatchTransfer-1.1.3-win-x64-stable-upload.zip` | 504314717 | `215b834851dac0f9f2fa515eeb56b42cf741334d478630b8499668cdcbd25a2b` |

上传包顶层 10 项资产：

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `RELEASES-win-x64-stable` | 286 | — |
| `SuperPan.BaiduBatchTransfer-1.0.6-full.nupkg` | 95613046 | `d7911cc0a7af42a3…` |
| `SuperPan.BaiduBatchTransfer-1.1.0-full.nupkg` | 95738048 | `898afdc6308d715d…` |
| `SuperPan.BaiduBatchTransfer-1.1.3-delta.nupkg` | 8593841 | `72dbf3ed8ca18119…` |
| `SuperPan.BaiduBatchTransfer-1.1.3-full.nupkg` | 101986751 | `90a05dc4181c0592…` |
| `SuperPan.BaiduBatchTransfer-Portable.zip` | 101894692 | `d10d78f0a956cd7d…` |
| `SuperPan.BaiduBatchTransfer-Setup.exe` | 106547744 | `df8b6074cd6e6aad…` |
| `assets.win-x64-stable.json` | 329 | — |
| `releases.win-x64-stable.json` | 2420 | — |
| `upload-manifest.sha256(.sig)` | 934 / 384 | — |

1.0.6 与 1.1.0 的哈希与 1.1.2 轮次逐字节一致，历史包未被重新打包。

## 版本链与 delta 基准

feed 携带 `1.0.6` / `1.1.0` / `1.1.3`。生产当前最新为 **1.1.0**（发布前只读采集已确认），
delta 因此以 1.1.0 为基准，正确。1.1.1 与 1.1.2 均已构建但从未发布，不入 feed。

## 签名

- 证书 `CN=SuperPan OTA Publisher`，指纹 `5AE52053A2D7E548E1FA747DF3C6313167668F40`
- SPKI SHA-256 `f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff`
- **带 DigiCert RFC3161 时间戳**，时间戳 UTC `2026-09-18T07:14:4x`

## 独立重验

`scripts/Test-OtaUploadBundle.ps1 -ExpectedVersion 1.1.3 -RequireTimestamp` 加固定发布者
SPKI：`verified=true`。报告位于 `.artifacts/p113-independent-verification.json`。

注意该脚本的报告参数名是 `-OutputReportPath`，不是 `-ReportPath`。

## 测试状态

`BaiduBatchTransfer.App.Tests`：3216 过 / 9 跳过。

初次运行有 77 个失败，**全部**是 `pwsh` 不在 PATH 导致的进程启动失败
（`Win32Exception: 系统找不到指定的文件`）。把 Codex 运行时目录挂上 PATH 后
（`~/.cache/codex-runtimes/codex-primary-runtime/dependencies/native/powershell`），
`Tests.Updates` 542 过 / 2 红。

剩余 2 个红项**均为断言脆弱，非产品缺陷**，两者都正确地失败关闭（退出码非 0）：

1. `OtaReleaseScriptTests.RejectsNonCanonicalVersionsBeforeCreatingOutput(version: "")`
   —— 空串被 PowerShell 的参数绑定先拦下（`Cannot bind argument to parameter 'Version'
   because it is an empty string.`），根本没进脚本自己的版本校验，输出里因此没有断言
   期待的字样。脚本行为正确：拒绝且不创建输出目录。
2. `VelopackExecutableRenameMigrationScriptTests.UnsignedExecutableCannotMasqueradeAsTrustedStateDriver`
   —— 该测试把测试宿主自身当作"未签名程序"，但本机 `dotnet` 由微软 Authenticode
   签名且 `Status=Valid`，脚本因此越过"无签名"分支、改从 SPKI 不匹配处拒绝。
   安全结论不变。

**刻意未修**：在出生产包前放宽安全门禁的判定条件属本末倒置。发布后再修断言。

## 源码覆盖清单

出包前全量比对 158 条，发现 **4 条**失配（不止本次改动的那一条）：

- `src/BaiduBatchTransfer.App/Styles/SuperPanTheme.xaml`
- `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`
- `src/BaiduBatchTransfer.App/MainWindow.xaml`

其中只有最后一条来自折叠改动，另外三条自 `eb091e75` 起就一直失配。
**教训：每次出包前必须全量比对 158 条，只更新"本次改了的文件"会在出包中途撞上
逐字节门禁。**（提交 `34f25b30`）

## 尚未完成 / 发布前必做

- [ ] **1.1.0 → 1.1.3 实机升级验证**：签名迁移 fixture（`migration-gate-fixture.json`）
      在仓库、归档、服务器上均不存在，六路径自动化验收**做不了**。必须在一台装有
      1.1.0 的机器或虚拟机上手动点一次"检查更新"，确认能升上去。这是整个发布里
      唯一能证明升级链没断的环节，不可跳过。
- [ ] **作废并重发测试卡**：月卡 `5MU6RWVG-…`、年卡 `SEP56BJE-…` 已出现在协作记录中，
      等同公开。
- [ ] 新包完整验收：卡密激活与设置中心同步、机器码显示、检查更新显示"已是最新版本"、
      百度账号登录。
- [ ] 上传发布（由用户在 `https://ota.fanfh.cn/admin/` 执行）

## 结构性隐患（不阻塞本次发布）

- **换行符**：源码覆盖清单存的是工作树**磁盘字节**，而 `.gitattributes` 未给
  `.xaml` / `.cs` 钉 `eol=lf`，本机 `core.autocrlf=true`。同一个 commit 换台机器
  克隆出来哈希即对不上，包根本出不了。这是此前四次门禁失败的同一根因。
  建议发布后专做一次归一化（需重算全部 158 条，勿在发版前动）。
- Release 构建硬依赖 gitignored 的 `.artifacts`（helper 包），新克隆无法构建。
- 生产出包依赖的 pwsh 位于 Codex 缓存目录，随时可能被清理。

## 服务器边界

本轮未进行任何服务器操作：未连接、未上传、未修改生产服务器，未执行 SSH/SCP/rsync
或发布切换，未调用百度网盘写入接口。上传发布必须走后台的候选上传与明确发布确认，
禁止 SCP 或面板直接覆盖 `stable`。
