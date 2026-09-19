# 交接：解开发布通道死锁（服务端）

写于 2026-09-19。这份文档是自包含的，不需要读之前的对话。

**任务**：让 1.1.7 能发布出去。目前服务端的两个版本都做不到，原因在下面。

**交接边界**：本文档负责的是服务端修复与部署。客户端 1.1.7 的包已构建并通过独立重验，
**不需要重新出包**。完成服务端修复并成功发布后，再回到 `docs/HANDOFF.md` 继续验收流程。

---

## 一、问题是什么

线上正在跑的 1.1.0 使用**旧可执行文件名** `BaiduBatchTransfer.App.exe`。
1.1.1 之后所有包改用**新名** `知道综合工具.exe`。

服务端在 2026-09-15（提交 `92e8d09a`）才加入对新布局的支持，而生产服务器上的二进制是
**2026-09-13 构建、09-14 部署**的。两边都发不出去：

| | 服务器当前的旧二进制 | 当前源码构建的新二进制 |
| --- | --- | --- |
| 接受 1.1.1+ 的新布局包 | **拒绝** `release_validation_failed` | 接受 |
| 启动时复验线上 1.1.0 代次 | 通过 | **拒绝**，发布器进入只读保护 |

命令行 `publish-bundle` 同样被挡：`cmd/ota-admin/publish_bundle.go` 开头也调用
`publication.Recover()`，失败即退出。所以**绕不过去**，必须改服务端代码。

### 已验证的事实（不是推测）

用服务端自己的校验器在本地跑真实归档：

```
1.1.7 包 -> PASS
1.1.0 包 -> FAIL: current Full package does not use the renamed layout
```

2026-09-19 曾把新二进制部署到服务器实测，日志出现
`publisher recovery requires read-only protection`，随即回滚，服务恢复正常。

复现命令（见第四节「如何用真实归档验证」）。

---

## 二、阻塞点在哪（代码位置）

仓库：`C:\Users\45203\Documents\dakazhandian\.worktrees\release-merge`
分支：`codex/release-1.1.2-merge`　提交：`a0376a70`

**第一处 —— 无条件要求当前版本必须是改名布局**

`services/ota_admin/internal/release/validator.go:174`

```go
currentLayout := fullLayouts[currentVersion]
if currentLayout != packageLayoutRenamed {
    return Report{}, fmt.Errorf("%w: current Full package does not use the renamed layout", ErrMetadata)
}
```

**第二处 —— Portable 校验把改名后的文件名写死**

`services/ota_admin/internal/release/package_structure.go:363`
`validatePortableStructure(file, expectedVersion)` 内部硬编码 `productMainDLLName`、
`productMainAssemblyName`、`productMainExecutableName`。即使放宽第一处，复验 1.1.0
代次仍会在这里失败（它在第一处之后执行，所以实测时还没跑到）。

**不需要改的**

- `validateSetupStructure`（`package_structure.go:55`）只查 PE 头，与命名无关，两种布局都过
- `classifyFullPackageStructure`（`package_structure.go:82`）本身能正确区分 legacy / renamed，
  返回值可直接用

**入口与调用链**

- `release.Validator.validate` — `validator.go:90`
- `deploy.Disk.RecoverGeneration` — `deploy/generation.go:98`（启动恢复走这条）
- `deploy.Disk.ValidateGeneration` — `deploy/generation.go:110`
- 接口 `deploy.Validator` / `openedDirectoryValidator` — `deploy/generation.go:18` 与 `:22`
- 调用点只有两处，都在 `deploy/recovery.go`（`:29` 与 `:106`），**都是在复验已发布代次**

---

## 三、建议的修复方向

**原则**：已发布的代次是历史，改不了。复验它的目的是确认**没被篡改**（签名、哈希、
清单闭包），而不是要求它符合今天的命名规范。

给"复验已发布代次"单独一条路径，只由 `ValidateGeneration` / `RecoverGeneration` 使用：

1. 不强制当前版本必须是改名布局，改为**接受它实际所用的布局**
2. Portable 按**该代次实际使用的布局**校验

**上传新包的路径保持严格不变**，仍要求改名布局。签名、哈希、清单闭包、版本下限等
核心校验一律不动。

---

## 四、部署前必须完成的验证

**上一次部署失败的教训是：只验证了期望发生的，没验证会实际发生的。** 当时验了
「新校验器能接受新包」，没验「新校验器能接受线上正在跑的旧包」——而后者是启动时
必然要做的事。

改完至少要做到：

- [ ] 用**真实的 1.1.0 归档**验证复验通过
- [ ] 用**真实的 1.1.7 包**验证上传校验仍通过
- [ ] 新增测试钉死「上传旧布局的新包仍被拒绝」（防止把门禁整个放掉）
- [ ] 跑完整 `services/ota_admin` 测试套
- [ ] 部署到服务器后，**确认启动日志里没有** `publisher recovery requires read-only protection`

### 如何用真实归档验证

仓库里已有现成入口 `TestOptionalLargeProductionCandidate`，通过环境变量接受任意包，
使用生产信任链：

```powershell
$env:PATH = 'C:\Program Files\Go\bin;' + $env:PATH
$env:SUPERPAN_OTA_GOLDEN_ZIP = '<包的绝对路径>'
Set-Location 'C:\Users\45203\Documents\dakazhandian\.worktrees\release-merge\services\ota_admin'
go test ./internal/release -run TestOptionalLargeProductionCandidate -v
```

注意：该入口传入的 `highestCommittedVersion` 是空串，走的是**上传**校验路径。
验证「复验已发布代次」需要另写一个用新路径的测试。

---

## 五、本地电脑上的文件

| 用途 | 路径 |
| --- | --- |
| 仓库工作树 | `C:\Users\45203\Documents\dakazhandian\.worktrees\release-merge` |
| **1.1.7 候选包**（504,345,526 字节） | `.artifacts\ota-production-p117-20260919-081850\SuperPan.BaiduBatchTransfer-1.1.7-win-x64-stable-upload.zip` |
| **1.1.0 历史归档**（383,930,311 字节） | `C:\Users\45203\Documents\dakazhandian\.worktrees\license-production-deploy\.artifacts\ota-production-20260913-v1.1.0-license\SuperPan.BaiduBatchTransfer-1.1.0-win-x64-stable-upload.zip` |
| 已编译的新服务端二进制（**含缺陷，勿部署**） | `.artifacts\ota-admin-deploy\ota-admin` |
| 1.1.7 独立重验报告 | `.artifacts\p117-independent-verification.json` |

1.1.7 包的 SHA-256：
`fc1de97218f07b867e2a63b4335153823d907c9a8cd6ad6816577553ec9304f2`

`.artifacts\ota-admin-deploy\ota-admin` 是 2026-09-19 那次失败部署用的二进制
（SHA-256 `843c2da9f1a704566f2d2f92b9ab5b09ded91c694608adba3228ca0d251dbe87`）。
**它就是会让发布器进只读的那个版本，修复完请重新构建，不要直接用它。**

### 服务端构建参数（必须一致）

```
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -buildvcs=false -trimpath -ldflags '-s -w' ./cmd/ota-admin
```

参数来源：`docs/superpowers/plans/2026-09-10-production-license-service-deployment.md:194`。
用错参数体积会差 48%（漏 `-ldflags '-s -w'` 会从 12.8 MB 变成 19.0 MB）。

### Go 工具链

脚本用的是仓库内固定版本，**不是系统 PATH 上的 Go**：

```
.tools\go-1.27.1  ->  ..\license-production-deploy\.tools\go-1.27.1   （符号链接）
```

删掉 `license-production-deploy` 工作树会让授权栈验收失效，且报错不会提到这一点。
系统另装有 Go 1.27.0（`C:\Program Files\Go`），`go.mod` 要求 1.27.1，工具链会自动补齐。

---

## 六、服务器上的文件与当前状态

**主机**：`ota.fanfh.cn`，CentOS，装有宝塔面板。
**这台机器同时跑着博客和其他网站，OTA 以外的任何内容都不要动。**

| 项 | 值 |
| --- | --- |
| 服务名 | `superpan-ota-admin.service`（另有独立的 `superpan-license.service`） |
| 正在运行的二进制 | `/opt/superpan-ota-admin/ota-admin` |
| 其 SHA-256 | `bb917d27e74e86c582393c6cbe0d744565252f5772a5682787708f01f3e67351` |
| 配置 | `/etc/superpan-ota-admin/config.json`（`root:superpan-ota-admin` `0640`） |
| 状态库 | `/var/lib/superpan-ota-admin/state.db` |
| 上传暂存 | `/var/lib/superpan-ota-admin/staging` |
| 发布归档（服务读） | `/var/lib/otaadmin/releases` |
| 发布归档（nginx 供外） | `/www/wwwroot/ota.fanfh.cn/releases/win-x64` |
| 监听 | `127.0.0.1:18091`（公网经 nginx 反代） |

**当前状态（2026-09-19 13:14 已确认）**：服务 `active`，运行的是原始二进制，
发布器**无**只读保护，用户不受任何影响。

### 上次操作在服务器上留下的文件

```
/root/ota-admin                 新二进制，未使用，SHA-256 843c2da9...（含缺陷）
/root/ota-admin.bak-20260919    旧二进制备份，SHA-256 bb917d27...（与当前运行的一致）
```

两个都不会被自动执行。备份建议留着直到新版本稳定运行。

### 已确认的兼容性

- 配置与新二进制**完全兼容**，逐项比对无差异
- `store` 与 `config` 包自 2026-09-14 起零改动，**不需要数据库迁移**

### 自检配置的正确方式

必须以服务组身份运行，**用 root 直接跑会误报**
`invalid production configuration`——因为配置是 `0640` 且属组
`superpan-ota-admin`，而校验要求「组可读的配置其属组必须等于当前进程的有效组」：

```bash
sg superpan-ota-admin -c "/root/ota-admin check-config --config /etc/superpan-ota-admin/config.json"
```

通过时输出 `ota-admin: production configuration valid`。

### 部署与回滚

部署（先备份、先自检，逐条执行，每条确认输出）：

```bash
cp -p /opt/superpan-ota-admin/ota-admin /root/ota-admin.bak-$(date +%Y%m%d)
sg superpan-ota-admin -c "/root/ota-admin.new check-config --config /etc/superpan-ota-admin/config.json"
systemctl stop superpan-ota-admin
install -o root -g root -m 0755 /root/ota-admin.new /opt/superpan-ota-admin/ota-admin
systemctl start superpan-ota-admin
sleep 3 && systemctl is-active superpan-ota-admin
journalctl -u superpan-ota-admin --no-pager -n 5
```

**启动日志里出现 `publisher recovery requires read-only protection` 就说明修复不完整，
立即回滚。**

回滚：

```bash
systemctl stop superpan-ota-admin
install -o root -g root -m 0755 /root/ota-admin.bak-YYYYMMDD /opt/superpan-ota-admin/ota-admin
systemctl start superpan-ota-admin
systemctl is-active superpan-ota-admin
```

影响范围：中断只在 stop 到 start 之间的几秒，且**只影响后台管理界面**。
授权服务是独立进程，发布文件由 nginx 直接提供，**付费用户的激活、校验与下载更新都不受影响**。

---

## 七、发布 1.1.7 的两条路径

服务端修好之后：

**网页后台**（`https://ota.fanfh.cn/admin/`）——上传 481 MB 的包，有校验清单可以在
发布前复核包 ID、渠道、版本、文件数与签名，确认后才发布。

**命令行**——1.1.0 当初用的方式，适合大包：

```bash
ota-admin publish-bundle --config /etc/superpan-ota-admin/config.json --bundle /绝对路径/xxx-upload.zip
```

一步到位直接发布，**没有复核环节**。同样要求配置通过精确生产校验。

---

## 八、发布之后还没做的事

见 `docs/testing/2026-09-19-p117-release-candidate.md` 的「发布前必做」。其中两项与
服务端无关但不能漏：

- **1.1.0 → 1.1.7 实机升级验证**：签名迁移 fixture 不存在，六路径自动化验收做不了，
  必须在装有 1.1.0 的机器上手动点一次「检查更新」
- **作废并重发测试卡**：月卡 `5MU6RWVG-…`、年卡 `SEP56BJE-…` 已在协作记录中出现，
  等同公开

另外：**服务器 root 密码曾在协作记录中出现过，建议更换。**

---

## 九、其他未决问题

见 `docs/OPEN-ISSUES.md`。与本次任务相关度较高的：

- 第 1 条：本地授权栈的启动/停止脚本互相锁死，且启动失败的真因会被清理逻辑的异常覆盖
- 第 3 条：服务器单点且无可用性告警
- 第 6 条：出包能力目前绑死在这一台机器上
