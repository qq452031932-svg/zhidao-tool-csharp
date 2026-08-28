# 本地浏览器基础：Task 8 交接（2026-08-28）

## 当前状态

- 工作分支：`codex/transfer-share-link-replacement`。
- Task 1–7 已在此前完成：引擎契约、持久化、浏览器目录验证、独立 profile 与账号生命周期、私有助手协议。
- Task 8 已通过本轮主代理验证、独立规范复审与独立质量终审。两位复审者本轮均只读审查源码，未重复运行测试或清理命令。整个本地浏览器功能尚未完成。
- 本轮接续基线：`1f9a1ab`；修复提交：`babb1ff`。最后修复涉及验证器与对应测试：关闭封存前预开 writer 的窗口、确定性释放初始化失败的 root 句柄。

## 最后修复的关键约束

文件复制时原 writer 拒绝外部 writer。内容写完后，在原 writer 仍打开时取得同一 file-id 的临时只读句柄；关闭原 writer 后再取得最终不共享 Write/Delete 的只读句柄。若外部 writer 抢入，最终打开必须失败且不重试，不返回可运行 bundle。最终哈希验证只在这个最终句柄已经建立后进行。

payload 的所有长期目录句柄同样只共享 Read。根句柄在任何类型检查、ACL 操作之前立即登记到拥有者，异常时无需依赖 GC 即可释放。

此前验证的约束保持：全部目标写入相对可信父句柄；ACL 按句柄修改；严格 manifest；Incomplete/Ready0/Ready1 状态；活动锁；按 identity 清理；延迟队列/句柄预算；有界轮转回收；容器层级和标记开销计入恢复限制。

## 本轮主代理新鲜验证

测试进程使用工作树 `.artifacts/task8-verify-20260828` 作为 `TEMP`/`TMP`，不修改系统级环境变量。

| 验证 | 结果 |
| --- | --- |
| Release `ManagedBrowserHelperIntegrityVerifierTests`，启用真实 helper 包并要求集成测试 | 76/76，0 跳过 |
| `py -3.13 -B -m unittest discover -s tools/managed_browser_helper/tests -v` | 28/28 |
| `dotnet build BaiduBatchTransfer.sln -c Release --no-restore -warnaserror` | 0 警告，0 错误 |
| Release helper manifest 门禁 | 通过，BuildIdentity `d63d4c3911dbe25f42ee41b107c90f8a087116d1eba79cdb68ef55bbf1ddaa8a` |
| `git diff --check` | 通过；本机提示后续 Git 操作会规范化工作副本行尾 |

本轮未重新运行整个解决方案测试。此前完整 App 测试曾报告 5 项 SQLite/AppBootstrap 失败，不能将本轮专项通过表述为全套测试通过。

## 测试清理事件

见 [权限意外只读排查记录](2026-08-28-test-cleanup-permission-incident.md)。历史 Temp ACL 影响无法完全确认；没有进行猜测性批量恢复。后续禁止递归 `icacls` 清理，测试临时文件仅使用专用目录，不需要清理的残留保留。

## 下一项：Task 9 私有助手客户端

按现有基础计划实现 `ManagedBrowserHelperClient.cs` 与对应测试：

- 私有重定向 stdin/stdout；256-bit nonce 仅放首条长度帧，不放命令行、环境变量或文件。
- 验证父进程 PID、助手 PID、nonce、协议、引擎与能力集；不接受握手前命令。
- 有界 correlation tracking；迟到/重复响应；安全 stderr 事件码，不输出网页内容或凭据。
- C# 负责硬超时和取消：助手同步调用不返回时也必须停止本次请求，只终止自己创建并持有的助手进程。
- 持有已验证 bundle lease，直到助手退出且 stdout/stderr 读取器结束，再释放快照。
- 先写 fake child process 测试；成功、错误 stdout、退出、超时、取消、重复响应、优雅关闭后有界终止均需覆盖。

其后顺序是 Task 10 浏览器进程/端口归属、Task 11 选择目录与登录 UI、Task 12 基础验收。转存/分享页面写操作仍是后续计划，不能为了演示提前切换生产适配器或执行真实网盘写入。
