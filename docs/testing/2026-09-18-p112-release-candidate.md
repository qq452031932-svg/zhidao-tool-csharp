# 1.1.2 生产候选交接（2026-09-18）

## 结论与边界

基于分支 `codex/release-1.1.2-merge` 构建出版本严格为 `1.1.2` 的生产候选。Setup、Portable、Full、Delta 与签名 feed 均已生成，构建内置校验与一次独立重验均通过。

本轮只在本地工作树与其 `.artifacts` 下读写。**未连接、上传或修改生产服务器**，未执行 SSH/SCP/rsync/发布切换，也未调用百度网盘写入接口。

## 候选资产

目录：`.artifacts/ota-production-p112-20260918-084324`

| 资产 | 字节 | SHA-256 |
| --- | ---: | --- |
| `SuperPan.BaiduBatchTransfer-1.1.2-win-x64-stable-upload.zip` | 504310553 | `9caf4640bf67ef60bf3a2c80476ae4a26e191eac8e28d549d9a3789b85bc77b1` |
| `SuperPan.BaiduBatchTransfer-1.1.2-win-x64-stable-verification.json` | 1000231 | — |

上传包顶层 11 项资产：

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| `assets.win-x64-stable.json` | 329 | `c6af5fd697a4cc6e…` |
| `RELEASES-win-x64-stable` | 286 | `8be3557c1ae2397c…` |
| `releases.win-x64-stable.json` | 2420 | `8a7669e27e76a59e…` |
| `SuperPan.BaiduBatchTransfer-1.0.6-full.nupkg` | 95613046 | `d7911cc0a7af42a3…` |
| `SuperPan.BaiduBatchTransfer-1.1.0-full.nupkg` | 95738048 | `898afdc6308d715d…` |
| `SuperPan.BaiduBatchTransfer-1.1.2-delta.nupkg` | 8591965 | `459b0b434b4c6684…` |
| `SuperPan.BaiduBatchTransfer-1.1.2-full.nupkg` | 101985769 | `b2246268d6bbf4ad…` |
| `SuperPan.BaiduBatchTransfer-Portable.zip` | 101893710 | `1c3770089e182eb6…` |
| `SuperPan.BaiduBatchTransfer-Setup.exe` | 106546768 | `f5e8eef008ae6c68…` |
| `upload-manifest.sha256` | — | — |
| `upload-manifest.sha256.sig` | — | — |

## 版本链与 delta 基准（发布前必须核对）

feed 携带 `1.0.6` / `1.1.0` / `1.1.2`，**不含 1.1.1**。依据是 1.1.1 虽已构建但从未发布，生产最新仍应为 1.1.0；delta 因此以 1.1.0 为基准。

**如果生产当前实际是 1.1.1，这个 feed 就是错的，必须以 1.1.1 归档为 previous 重新构建。** 上传前的只读采集必须先确认这一点。

## 签名

- 证书：`CN=SuperPan OTA Publisher`，指纹 `5AE52053A2D7E548E1FA747DF3C6313167668F40`，私钥在 `Cert:\CurrentUser\My`，2041-09-05 到期。
- SPKI SHA-256：`f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff`
- **带 DigiCert RFC3161 时间戳**（`http://timestamp.digicert.com/`），与 1.1.1 一致。

首次构建漏传 `-TimestampUrl`，产出了"显式无时间戳"的包（`ota-production-p112-20260918-083829`，SHA-256 `926b90bf…`）。**那一份不得发布**，证书过期后已签名程序会集体失去信任。

## 独立重验

`scripts/Test-OtaUploadBundle.ps1`，参数 `-ExpectedVersion 1.1.2 -RequireTimestamp` 加固定发布者 SPKI：`verified=true`，11 项顶层资产，报告位于 `.artifacts/p112-independent-verification.json`。

## 许可证栈验收

`scripts/test_local_license_stack.ps1` 全量通过：

```text
software=2 package=1 cards=2 user_stores=4 wpf_smoke=1
policy_transitions=4 renewal_checks=1 unbind_checks=1
delete_checks=1 offline_checks=2
```

覆盖激活、多软件授权、移出/重加授权包、停用/启用软件、续期、管理员解绑后重绑、**后台删卡后在线校验拒绝（`license_not_found`）**、离线边界。

## 本轮修复的发布阻塞项

合并 `codex/transfer-share-link-replacement` 时，三处把功能分支的旧状态覆盖了发布分支的正确状态：

1. `helper-bundle-r3` 覆盖 `r2`——r3 由旧 helper 源码构建且不在本工作树（`f0467d5`）
2. 六个 `packages.lock.json` 丢失整个 `net8.0-windows7.0/win-x64` 段，项目约束从 `[1.1.0, )` 降回 `[1.0.0, )`，导致 `--locked-mode` 还原全线 `NU1004`（`94c9bc6`）
3. 源码覆盖清单 21 条失配，其中 15 条仅换行符差异（`53c36ae`）

另有构建环境问题：生产模式要求 PowerShell 7（本机 pwsh 7.6.5 不在 PATH，位于 Codex 运行时目录）；`OutputDirectory` 必须绝对路径；`PreviousReleaseDirectory` 专供 1.1.0 历史归档（第 662 行硬编码期望版本）。以上四点已写入 `.tmp/build-1.1.2.ps1` 注释。

## 尚未完成

- **真实六路径 VM 升级验收**（1.1.0 → 1.1.2 的签名包实机验证）
- **生产信任链专用测试卡验收**：未激活 / 有限期 / 升级后不重新激活。注：授权系统只有 `month`/`quarter`/`half_year`/`year` 四种卡型，**不存在永久卡**，旧交接要求的"永久"一项是不成立的。
- 上传发布（由用户在后台执行）

## 结构性隐患（不阻塞本次发布）

- Release 构建硬依赖 gitignored 的 `.artifacts` 目录（helper 包），新克隆无法构建。
- 生产出包依赖的 pwsh 位于 Codex 缓存目录，该目录随时可能被清理。
- 换行符问题已四次绊住逐字节门禁（helper manifest、两个生成身份文件、源码覆盖清单），根因是 `.gitattributes` 覆盖不全。

## 服务器边界

本轮未进行任何服务器操作。上传发布必须通过 `https://ota.fanfh.cn/admin/` 的候选上传与明确发布确认，禁止 SCP 或面板直接覆盖 `stable`。
