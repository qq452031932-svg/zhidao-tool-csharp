# 1.1.4 生产候选交接（2026-09-18）

## 为什么版本号一路跳到 1.1.4

1.1.1、1.1.2、1.1.3 都构建过，**都没发布**。生产最新始终是 **1.1.0**。

每次顺延而不是复用版本号，是因为 Velopack 客户端按版本号缓存：同一个版本号对应
两份不同的二进制，会让升级链进入极难排查的脏状态。1.1.3 作废的直接原因是它把
占位文案 `SuperPan production release.` 烤进了包里，加上真正的更新说明之后就是
另一个二进制了。

**1.1.1 / 1.1.2 / 1.1.3 的候选包一律不得发布。**

## 候选资产

目录：`.artifacts/ota-production-p114-20260918-180732`
源码提交：`3c1f8bed`

| 资产 | 字节 | SHA-256 |
| --- | ---: | --- |
| `SuperPan.BaiduBatchTransfer-1.1.4-win-x64-stable-upload.zip` | 504317732 | `7a9352df137da75eba971e23f86ca55fe9e4da0432b818bfb2745edfec9117cb` |

上传包顶层资产：

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `SuperPan.BaiduBatchTransfer-1.0.6-full.nupkg` | 95613046 | `d7911cc0a7af42a3…` |
| `SuperPan.BaiduBatchTransfer-1.1.0-full.nupkg` | 95738048 | `898afdc6308d715d…` |
| `SuperPan.BaiduBatchTransfer-1.1.4-delta.nupkg` | — | `c173255610c76de0…` |
| `SuperPan.BaiduBatchTransfer-1.1.4-full.nupkg` | 101987554 | `dc4a92971cdf28a9…` |
| `SuperPan.BaiduBatchTransfer-Portable.zip` | — | `01e2a9a238 3e993b…` |
| `SuperPan.BaiduBatchTransfer-Setup.exe` | — | `a2ca104058283a96…` |

1.0.6 与 1.1.0 的哈希与前几轮逐字节一致，历史包未被重新打包。

## 更新说明（本轮新增的真实改动）

链路本来就是完整的：`build_ota_release.ps1 -ReleaseNotesPath` → nuspec 的
`<releaseNotes>` → Velopack → 设置中心"更新说明"框。**此前从未传过这个参数**，
所以每次发布都带着脚本的兜底文案 `SuperPan production release.`。

本轮传入 `docs/release-notes/1.1.4.md`，包内内容与源文件逐字符一致（167 字），
占位文案已消失。

文案覆盖 1.1.0 之后的全部改动——生产用户是从 1.1.0 升上来的，中间三个版本没发布过。
写成四段短句而非 changelog：那个框是纯 `TextBlock`（不渲染 markdown），
`MaxHeight="160"`，四段刚好不用滚动。

## 版本链与 delta 基准

feed 携带 `1.0.6` / `1.1.0` / `1.1.4`，delta 以 **1.1.0** 为基准，与生产实际版本一致。

## 签名

- 证书 `CN=SuperPan OTA Publisher`，指纹 `5AE52053A2D7E548E1FA747DF3C6313167668F40`
- SPKI `f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff`
- 带 DigiCert RFC3161 时间戳

独立重验 `verified=true`。按策略分组后：

| 策略 | 条目 | 结论 |
| --- | ---: | --- |
| `publisher-authenticode-spki` | 17 | SPKI 全部相符，平台校验全 Valid，映像摘要全部已验 |
| `python-psf-authenticode-and-helper-manifest` | 8 | Python 官方签名 + helper 清单 |
| `manifest-hash-package-data` | 2187 | 运行时与第三方库，按清单哈希校验 |

**看总数会吓一跳（2212 个已签文件里 2190 个"无时间戳"、2195 个"签名者不符"），
但那是把 .NET 运行时和第三方库也算进去了。只有 `publisher-authenticode-spki`
这 17 条要求由我方签名。**

那 17 条里有 3 条缺时间戳，全部位于历史 **1.0.6** 包内，是启用时间戳之前签的。
重签会改变哈希、破坏历史包不可变性，不能动。**1.1.4 自身的 11 个产物全部带时间戳。**

## 测试状态

初次全量运行 77 个失败，**全部**是 `pwsh` 不在 PATH 导致的进程启动失败，
与产品无关。把 Codex 运行时目录挂上 PATH 后只剩 2 个，均为断言脆弱，本轮已修。
修的是断言写法，不是安全判定，两处"必须拒绝"的核心结论都保留：

1. `RejectsNonCanonicalVersionsBeforeCreatingOutput(version: "")` —— 空串走不到脚本
   自己的版本校验，PowerShell 参数绑定先拒了；那条消息随系统语言本地化（本机中文），
   且控制台换行把消息里嵌的参数名 `Version` 劈成 `Ver`+`sion`，按英文字样断言必然误报。
   拆成独立用例 `RejectsAnEmptyVersionBeforeCreatingOutput`，改断言不随语言变的
   `FullyQualifiedErrorId`（`ParameterArgumentValidationErrorEmptyStringNotAllowed`），
   其余七个用例保留原有子串校验。
2. `UnsignedExecutableCannotMasqueradeAsTrustedStateDriver` —— 该测试把测试宿主自身
   当作"未签名程序"，但本机 `dotnet` 由微软签名且 `Status=Valid`，脚本因此越过
   "无签名"分支、改从 SPKI 不匹配处拒绝。更名为
   `ForeignExecutableCannotMasqueradeAsTrustedStateDriver`，断言"非发布者签名必须
   被拒，且拒绝理由与签名相关"。

（提交 `413b7ad7`）

### 跑测试时会弹出的报错窗口，是正常现象

`ContainedProcessKillsLogFloodingParentAndFileHoldingChild` 会**故意**造一个失控进程
（独占文件 + 刷爆日志），验证进程围栏能否把父子进程一起强杀。父进程正在
`Start-Process` 拉子进程时被 Job 强杀，管道断裂，Windows 弹出
`0x800700e8 (ERROR_BROKEN_PIPE)`。**这是测试通过的证据，不是故障。**

## 源码覆盖清单

出包前全量比对 158 条：0 条失配（本轮只改了测试与文档，均不在清单内）。

**规则：每次出包前必须全量比对 158 条。** 1.1.3 那轮发现 4 条失配，其中只有 1 条
来自当次改动，另外 3 条自 `eb091e75` 起就一直失配——只更新"本次改了的文件"会在
出包中途撞上逐字节门禁。

## 发布前必做

- [ ] **1.1.0 → 1.1.4 实机升级验证**：签名迁移 fixture（`migration-gate-fixture.json`）
      在仓库、归档、服务器上均不存在，六路径自动化验收**做不了**。必须在一台装有
      1.1.0 的机器或虚拟机上手动点一次"检查更新"，确认能升上去，并顺便确认
      更新说明是否正常显示。这是唯一能证明升级链没断的验证，不可跳过。
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
- `Test-OtaUploadBundle.ps1` 的报告参数名是 `-OutputReportPath`，不是 `-ReportPath`。

## 服务器边界

本轮未进行任何服务器操作：未连接、未上传、未修改生产服务器，未执行 SSH/SCP/rsync
或发布切换，未调用百度网盘写入接口。上传发布必须走后台的候选上传与明确发布确认，
禁止 SCP 或面板直接覆盖 `stable`。
