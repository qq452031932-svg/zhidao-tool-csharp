# 待办与已知风险

按严重程度排。每条都写清楚"不做会怎样"，而不是只写"应该做"。

最后更新：2026-09-19（1.1.10 出包后）

**已解决**：授权栈端到端验收此前一次都没在本机跑通过，曾被误记为"没装 Go"。真实原因是
上面第 1 条的脚本死锁，加上验收冒烟的存储隔离失效（已修，见提交 `5310b6cd`）。
现在 `pwsh scripts/test_local_license_stack.ps1` 可以完整通过。

---

## 0. 发布通道死锁（2026-09-19 已修复并部署）

此节以下保留为历史排障记录。修复提交 `ab9e0a3d`，生产 OTA 服务已恢复可用；
**1.1.7 尚未发布**。执行结果与 OTA 专项备份位置见 `docs/HANDOFF-SERVER-UNBLOCK.md`。

**这是当前最高优先级，且没有绕过办法。**

线上发布的 1.1.0 使用旧的可执行文件名（`BaiduBatchTransfer.App.exe`）。1.1.1 之后的
所有包改用新名（`知道综合工具.exe`）。服务端在 2026-09-15 才加入对新布局的支持
（提交 `92e8d09a`），而生产服务器上的二进制是 2026-09-13 构建、09-14 部署的。

于是两边都发不出去：

| | 服务器当前的旧二进制 | 当前源码构建的新二进制 |
| --- | --- | --- |
| 接受 1.1.1+ 的新布局包 | **拒绝** `release_validation_failed` | 接受 |
| 启动时复验线上 1.1.0 | 通过 | **拒绝**，发布器进入只读保护 |

命令行 `publish-bundle` 同样被挡：它开头也调用 `publication.Recover()`。

### 已验证的事实（不是推测）

用服务端自己的校验器在本地跑：

```
1.1.7 包 -> PASS
1.1.0 包 -> FAIL: current Full package does not use the renamed layout
```

2026-09-19 曾把新二进制部署到服务器验证，日志出现
`publisher recovery requires read-only protection`，随即回滚，服务恢复正常。

### 阻塞点在哪

`internal/release/validator.go` 里这段是无条件的：

```go
currentLayout := fullLayouts[currentVersion]
if currentLayout != packageLayoutRenamed {
    return Report{}, fmt.Errorf("%w: current Full package does not use the renamed layout", ErrMetadata)
}
```

另外 `validatePortableStructure` 把改名后的文件名写死了，即使放宽上面那条，复验
1.1.0 代次仍会在这里失败。

`validateSetupStructure` 与命名无关，两种布局都能过。

### 建议的修复方向（未实施）

给"复验已发布代次"单独一条路径（`ValidateGeneration` / `RecoverGeneration` 使用），
在该路径下：

1. 不强制当前版本必须是改名布局，改为接受它实际所用的布局
2. Portable 按该代次实际使用的布局校验

**上传新包的路径保持严格不变**，仍要求改名布局。签名、哈希、清单闭包等核心校验一律不动。

原则：已发布的代次是历史，改不了。复验它的目的是确认**没被篡改**，而不是要求它符合
今天的命名规范。

### 实施时必须做的验证

上次部署的教训是"只验证了期望发生的，没验证会实际发生的"。改完至少要：

- 用**真实的 1.1.0 归档**验证复验通过（本地路径见
  `.worktrees/license-production-deploy/.artifacts/ota-production-20260913-v1.1.0-license/`）
- 用**真实的 1.1.7 包**验证上传校验仍通过
- 新增测试钉死"上传旧布局的新包仍被拒绝"
- 跑完整 `services/ota_admin` 测试套

### 服务器当前状态（2026-09-19）

已回滚到部署前的二进制 `bb917d27e74e86c582393c6cbe0d744565252f5772a5682787708f01f3e67351`，
服务 active，发布器无只读保护。`/root/` 下留有 `ota-admin`（新二进制，未使用）与
`ota-admin.bak-20260919`（旧二进制备份）。

---

## 1. 启动/停止脚本互相锁死（本地授权栈）

任何一次中途失败的 `run_local_license_stack.ps1` 都会留下编译产物，之后：

- **启动**拒绝：`Local acceptance artifacts already exist; no data was overwritten.`
- **停止**拒绝：`Validated local PID manifest not found; no process was stopped.`（清单只有
  正常启动才会生成）

只能手工删 `.tmp/license-service-local.exe` 之类的残留才能解开。

**不做会怎样**：授权验收在第一次意外中断后就跑不起来了，而错误消息还会误导人——
启动失败的真因被 `finally` 里的清理异常覆盖掉，显示的是
"private runtime ownership was not established"，与真实原因毫无关系。**今天就因为这条
误导排查了很久，还据此写错了交接文档。**

**怎么做**：停止脚本应支持"没有清单也能清理已知残留"的恢复模式；启动脚本的 `finally`
不应让清理异常吞掉原始异常。

---

## 2. 分享核对可能永远停在 ResultPending

`ShareSchedulingStoreTests.NewerActiveLoginIsNotInvalidatedByOlderObservedSessionFailure`
持续失败：

```
Expected: Accepted    Actual: ResultPending
```

场景：登录过期 → 刷新会话 → 再次核对分享结果，**应当被接受**。

测试与被测的 `ShareOperationReconciliationService` 都在合并基线提交 `5dba75c3` 里最后
改动，从合并起就是红的。

**未修的原因**：无法判断是"合并时有意改了语义而测试没跟上"还是"真 bug"，两者的修法
完全相反，盲改比留着更危险。

**不做会怎样**：如果是真 bug，用户的百度登录一旦过期，已提交的分享操作可能再也确认
不了，一直挂在"结果待定"。这在长批次里很常见。

**怎么做**：单独立项。先读 `ShareOperationReconciliationService` 的验收判定，对照合并
前后的行为差异，确定哪一侧是对的。

---

## 3. 服务器单点，且挂了你不会知道

`ota.fanfh.cn` 同时承担授权校验与更新分发。

1.1.6 起打开软件有 7 天离线宽限，所以短时间宕机用户无感——**但也意味着你更不会察觉**。
联网操作（批量转存、分享）仍需实时校验，服务器挂了这些立刻停。

**不做会怎样**：故障从"用户打电话告诉你"开始计时。证书自动续期失败、磁盘满、进程挂掉
都属于这一类。

**怎么做**：加一个外部可用性监控，探测授权与 feed 两个端点，异常时推送到你手机。
成本很低。

---

## 4. 没有公告通道 —— 唯一联系方式硬编码在程序里

全软件只有一个微信号 `swatfanhua`，写死在源码里（激活窗与首页各一处）。

**不做会怎样**：这个号一旦换号、被封或停用，**已经装出去的用户就彻底联系不到你了**。
他们不会知道去哪找新联系方式，只会认为软件没人维护了。这比推送营销信息重要得多。

**怎么做**：授权服务加一个 `GET /announcements` 端点（只读、可缓存、失败静默），客户端
在首页显示。客户端这一半可以先写好。

---

## 5. 六路径升级验收永久做不了

签名迁移 fixture `migration-gate-fixture.json` 在仓库、归档、生产服务器上均不存在，
自动化验收无法运行。

**不做会怎样**：每次发版的升级链路只能靠人工在装有旧版的机器上手点验证。漏一次，
就可能推一个装不上的包给全部用户。

---

## 6. 出包能力绑死在一台机器上

三个问题叠加，合起来是同一件事：

- **换行符**：源码覆盖清单存的是工作树磁盘字节，而 `.gitattributes` 未给 `.xaml` / `.cs`
  钉 `eol=lf`，本机 `core.autocrlf=true`。**换台机器克隆出来，158 条哈希对不上，包出不了。**
- **`.artifacts` 依赖**：Release 构建硬依赖 gitignored 的 helper 包目录，新克隆没有。
- **固定版 Go 是符号链接**：`.tools/go-1.27.1` 指向 `.worktrees/license-production-deploy`
  下的目录。删掉那个工作树，授权栈验收即刻失效，而报错不会提到这一点。
- **pwsh 位置**：生产出包要求 PowerShell 7，本机的 pwsh 7.6.5 不在 PATH，位于 Codex 运行时
  缓存目录，该目录随时可能被清理。

**不做会怎样**：这台机器换掉、重装或缓存被清，**出包能力即刻消失**，且恢复过程没有文档。

**怎么做**：换行符归一化需重算全部 158 条哈希，**不要在发版前动**；helper 包应纳入版本
管理或可重建；pwsh 应正式安装而非依赖缓存。

---

## 6b. 改这几个文件必须顺手重新固定源码清单

`eng/ota/production-source-overrides.sha256` 里的 158 条哈希是**逐字节**校验的。
`MainWindow.xaml`、`SuperPanTheme.xaml`、`TransferWorkspaceView.xaml`、`JobResultsView.xaml`、
`TransferWorkspaceViewModel.cs`、`JobResultsViewModel.cs`、`LocalJobResultsWorkflow.cs`、
`SqliteBatchResultProjectionRepository.cs` 都在这份清单里。

**不做会怎样**：代码能编译、测试能跑、提交能进去，但 HEAD 处于**出不了生产包**的
状态，而且只会在出包跑到一半时才报错。2026-09-19 这一天内撞了两次（`0da9e06f`、
`54a9d8e2`），两次都是靠出包前的全量比对才发现的。

**怎么做**：改完 `src/` 下的 `.cs`/`.xaml` 后，重算清单里对应条目的 SHA-256（文件本身保持
LF 结尾）。**不要只比这次改过的文件，要全量比 158 条**。

## 6c. 两个大批量写入测试的墙钟预算在本机必红

`RepositoryTests.PreparedStreamingWritesHandleTwoHundredThousand…` 预算 20 秒，本机 54.7 秒；
`RepositoryTests.ShareWritesStreamSixHundredThousand…` 预算 120 秒，本机 155 秒。

**两个测试名字里写的内存不变量都轻松通过**（预算 384 MB，实际 12.8 MB 与 13.1 MB，
用了 3%）。挂的只是顺手加的吞吐预算。

**不做会怎样**：全量跑永远至少三红（这两条 + 第 2 条的 `NewerActiveLogin…`），真正的回归
会被埋在噪声里。2026-09-19 就差点发生：同一轮里的 4 个失败有 3 个是噪声。

**怎么做**：去掉墙钟断言，或放宽到 3 倍，只保留内存断言。

## 6d. 「按账号拆分导出」没有入口

`ReplacementWorkbookExportService.ExportSplitToDirectoryAsync`（成功与失败分文件）在整个
`src/` 里**没有任何调用者**，只有测试在用。而 1.1.1 的更新日志写着“结果可按账号拆分
导出，成功与失败分开成文件”。

**不做会怎样**：更新日志向用户承诺了一个点不到的功能。这是第二个“有代码有测试
但没有按钮”的功能（第一个是重试失败项，已于 1.1.7 补上按钮）。

**怎么做**：要么给它一个入口，要么把它和对应的更新日志一起删。注意 1.1.10 起
「另存结果表格」已经是每个账号一个文件了，两者功能已大量重叠。
## 7. 全量测试不能并行跑

并行运行整个解决方案会产生约 15 个**虚假失败**——OTA 脚本测试要反复起 PowerShell 子进程，
而 `Infrastructure.Tests` 单项就占 55 分钟 CPU。同一批用例单独跑全绿。

**验收时必须逐项目串行跑，并行结果不可信。**

---

## 8. 跑测试时会弹出报错窗口（正常现象）

`ContainedProcessKillsLogFloodingParentAndFileHoldingChild` 会**故意**造一个失控进程，
验证进程围栏能否把父子进程一起强杀。Windows 会弹 `0x800700e8 (ERROR_BROKEN_PIPE)`。
**这是测试通过的证据，不是故障。**
