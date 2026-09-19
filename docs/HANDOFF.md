# 交接：从这里开始

最后更新：2026-09-19

## 现在是什么状态

- **生产线上版本：1.1.0**。此后的 1.1.1 ~ 1.1.6 都构建过，**都没有发布**。
- **当前候选：1.1.7**，见 `docs/testing/2026-09-19-p117-release-candidate.md`。
- **⚠️ 发布通道当前是死锁的，1.1.7 发不出去。** 原因不在包，在服务器上的后台程序版本。
  **要动手解这个问题，读 `docs/HANDOFF-SERVER-UNBLOCK.md`**（自包含，含服务器状态、
  代码位置、构建参数、部署与回滚步骤）。背景概述见 `docs/OPEN-ISSUES.md` 第 0 条。
- **开发分支**：`codex/release-1.1.2-merge`（分支名是历史遗留，实际已推进到 1.1.6）。

## 先读哪几份

1. `docs/OPEN-ISSUES.md` —— **未决问题与已知风险**，按严重程度排，每条写了"不做会怎样"。
2. `docs/testing/2026-09-19-p117-release-candidate.md` —— 当前候选包的资产、校验与验收清单。
3. 本文档剩余部分 —— 出包与发布的操作要点。

`docs/testing/` 下其余文件是历史记录，其中 `2026-09-18-p112-*` 已明确标注作废。

## 出包

```
pwsh .tmp/build-<版本>.ps1
```

该脚本不在版本管理内（`.tmp/` 被忽略），从上一版复制改版本号即可。**四个必须知道的点**，
注释里也写了：

1. **生产模式要求 PowerShell 7**。本机 pwsh 7.6.5 不在 PATH，位于 Codex 运行时缓存目录，
   脚本按绝对路径调用。
2. `OutputDirectory` 必须是绝对本地路径。
3. `PreviousReleaseDirectory` **专供 1.1.0 历史归档**：`build_ota_release.ps1` 第 662 行把
   期望版本硬编码为 `1.1.0`。传别的版本会在校验处直接失败。
4. **`-TimestampUrl` 不传就会生成"显式无时间戳"的签名**，证书一旦过期，已签名的程序会
   集体失去信任。只接受 `http://timestamp.digicert.com/`。

### 出包前必做

- **全量比对 158 条源码覆盖清单**，不能只更新"本次改了的文件"。失配会静默累积，
  只在出包中途的逐字节门禁处爆出来。
- **检查 `packages.lock.json` 是否干净**。手工编 Release 验证时若传了
  `-p:VersionPrefix=<新版本>`，6 个 lock 文件的项目引用约束会被改写，必须
  `git checkout -- '*packages.lock.json'` 还原。生产出包脚本走 git 快照，不受影响。
- **版本号只能往前**。同一个版本号下不能有两个不同的二进制——Velopack 按版本号缓存。
  包一旦构建完又改了源码，版本顺延，不要原地重出。

## 跑测试

**必须逐项目串行跑。** 并行运行整个解决方案会产生约 15 个虚假失败（见 OPEN-ISSUES 第 7 条）。

```
$env:PATH = '<codex 缓存>/dependencies/native/powershell;' + $env:PATH
dotnet test tests/<项目>/<项目>.csproj -c Debug --nologo
```

**pwsh 必须在 PATH 上**，否则所有调 PowerShell 脚本的门禁测试会以"系统找不到指定的文件"
整片失败——那不是产品问题。

已知长期失败：`ShareSchedulingStoreTests.NewerActiveLogin…` 1 个（合并遗留，未修）。
详见 OPEN-ISSUES。

## 授权栈验收

```
pwsh scripts/test_local_license_stack.ps1
```

会在本机起一个只监听 `127.0.0.1` 的临时授权服务，用真实客户端跑完激活、多软件授权、
续期、解绑重绑、**后台删卡后拒绝**、离线边界，跑完自动清理。**不碰生产服务器。**

用的是仓库内固定版 Go（`.tools/go-1.27.1`，符号链接到另一个工作树），不是系统 PATH 上的 Go。

中途失败过一次的话，启动与停止脚本会互相锁死，需要手工删 `.tmp/` 下的残留——
见 OPEN-ISSUES 第 1 条。

## 发布

**只能由用户在 `https://ota.fanfh.cn/admin/` 执行候选上传与发布确认。**

禁止 SCP 或面板直接覆盖 `stable`。发布前必须在装有旧版的机器上手动验证一次升级——
六路径自动化验收做不了（OPEN-ISSUES 第 5 条）。

## 服务端

授权服务与更新 feed 同在 `ota.fanfh.cn`。服务端租约常量在
`services/ota_admin/internal/licensing/service/service.go`。

**改服务端租约前务必确认客户端普及度**：客户端对"服务端签发租约"有上限校验，
1.1.6 起放宽到 7 天，此前是 30 分钟——对旧客户端签发超过 30 分钟的租约会让它们把
合法回复判为非法信封，当场集体锁死。
