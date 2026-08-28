# 本地浏览器基础：Task 10 交接（2026-08-28）

## 状态

代码、专项自动验证及独立规范/质量审查已完成；真实浏览器启动检查尚未执行，Task 10 整体仍未完成，不能把本文件视为现场验收通过。接续基线为 `a592f88`，实现提交为 `670b125`，终止失败重试修复为 `3142321`，分支为 `codex/transfer-share-link-replacement`。

本步骤实现本地浏览器受控启动、调试端口与进程归属验证，以及只关闭软件所属窗口的退出流程。不切换生产转存/分享适配器，不登录账号，不访问分享链接，不操作网盘文件。

首次浏览器启动使用浏览器安装身份和已验证的专用 profile 身份，不要求尚未登录时就提供百度 UID。扫码后的账号绑定和 `IManagedBrowserLifecycle` 真实接线留给 Task 11。

## 待完成验证

- [x] 主代理新鲜自动测试和 Release 构建。
- [x] 独立规范审查（修复一项 P2 后复审通过）。
- [x] 独立质量审查（无 P1/P2 阻断）。
- [ ] 用户指定浏览器安装目录后的真实无账号启动检查。

实际启动检查必须使用用户明确指定的、直接包含 `chrome.exe` 或 `msedge.exe` 的目录，不扫描磁盘或枚举系统安装位置。使用新建专用测试 profile；不使用日常浏览器的个人资料。确认窗口持续可见、原绑定重连和仅关闭所属进程后，才能记录现场通过。

真实启动用例为 `OptInRealBrowserRemainsVisibleForFifteenSecondsAndOnlyOwnedJobIsClosed`，默认跳过。只有获得用户指定目录及 GUI 启动批准后，才给该测试进程设置 `BAIDU_BATCH_REQUIRE_TASK10_BROWSER=1`、`BAIDU_BATCH_TASK10_BROWSER_DIRECTORY` 和 `BAIDU_BATCH_TASK10_BROWSER_FAMILY`（`Chrome` 或 `Edge`）。只运行该用例，并人工核对窗口可见、未登录和日常浏览器不受影响；不能仅凭进程仍存活就宣布界面验收通过。

普通专项回归不启用上述开关，也不启用浏览器安装验证器的真实 Chrome/Edge 目录用例。跳过的真实浏览器用例应明确计数，不得算作现场验收通过。

## 主代理实际验证

以下均为实现提交后主代理亲自执行；测试进程 `TEMP` / `TMP` 指向工作树 `.artifacts/task10-main-verify`。

| 范围 | Release 结果 |
| --- | --- |
| 端口读取、TCP 归属、进程管理与应用退出专项 | 最终 70 通过、1 跳过、0 失败 |
| 浏览器可执行文件验证器、专用目录策略回归 | 98 通过、2 跳过、0 失败 |
| Task 9 助手客户端与协议回归（启用真实打包助手） | 77 通过、0 跳过、0 失败 |
| 进程管理与应用退出专项连续重复 3 轮（`670b125`） | 每轮 50 通过、1 跳过、0 失败 |
| 整个解决方案 `dotnet build -c Release --no-restore -warnaserror` | 0 警告、0 错误 |
| Release helper manifest 门禁 | 通过；BuildIdentity 保持 `d63d4c3911dbe25f42ee41b107c90f8a087116d1eba79cdb68ef55bbf1ddaa8a` |
| `git diff --check a592f88..3142321` | 通过 |

跳过项共 3 个：Task 10 的真实浏览器启动，以及验证器真实 Chrome/Edge 安装目录检查。没有启动浏览器。实际 Windows TCP 表查询、空 Job 创建/查询测试已执行，不能替代真实浏览器现场检查。

最终合并 App 专项为 147 通过、1 跳过（Task 10 的 70 项加助手/协议 77 项），加 Infrastructure 98 项，共 245 项通过。TRX 位于 `.artifacts/task10-main-verify/results/`：最终结果为 `task10-app-final.trx` 和 `task10-infra-final.trx`；初次结果及重复测试也保留。修复后重新构建整个 Release 解决方案及门禁均通过。没有运行全套 App 测试；之前记录的 5 项 SQLite/AppBootstrap 失败不在本次范围，不能宣称全套测试通过。

实现者报告了端口解析、固定句柄租约、原生启动顺序、超时、退出确认、启动/关闭竞争和监听者变更的 RED→GREEN 过程；主代理未亲自重放历史 RED，以上表格仅记录新鲜 GREEN 结果。

## 审查修复

独立规范审查发现：原生终止调用失败被忽略，且已请求标记永久置位，后续“重试”只等待而不会再次终止。主代理核对后先补 3 个用例，亲自运行得到 3 失败，再做最小修复得到 3 通过（`task10-terminate-retry-red.trx` / `task10-terminate-retry-green.trx`）。

- 原生终止返回值向上传递，仅成功才记录已请求；失败仍可重试。
- 失败时不释放 profile / executable pins，不报告停止成功。
- 尚未加入 Job 的启动失败根进程也保留持有句柄，供后续重试。
- 成功后的重复退出仍然幂等。

规范复审明确 Code/spec Ready: Yes。随后独立质量审查核对 `a592f88..3142321` 全部变更及相关实现，明确代码 Ready: Yes，无 P1/P2 阻断。两位审查者均只读代码，未运行测试；测试证据来自主代理。真实启动仍 pending。

## 下次接续

先获取用户明确指定的浏览器安装目录，完成上面的无账号真实窗口检查，再继续 Task 11 的选目录/扫码登录入口与账号绑定。当前没有把新浏览器接入生产转存或分享流程，不能向用户表示已可用它执行真实网盘操作。

## 技术核对来源

- [Microsoft CreateProcessW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)：显式传入程序路径，参数数组在原生边界进行 Windows 命令行编码，不经 shell。
- [Microsoft AssignProcessToJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject)：在恢复新进程执行前完成 Job 归属；现代 Windows 支持嵌套 Job，不开放 breakaway。
- [Microsoft GetExtendedTcpTable](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable) 与 [IsProcessInJob](https://learn.microsoft.com/en-us/windows/win32/api/jobapi/nf-jobapi-isprocessinjob)：检查监听者 PID 与持有 Job 的关系，不能只凭端口开放判断归属。
- [Chrome DevTools Protocol](https://chromedevtools.github.io/devtools-protocol/)：随机端口与浏览器调试地址可从专用 profile 中的 `DevToolsActivePort` 取得；需与浏览器标识和目标标识共同验证。
- [Chrome 调试端口变更说明](https://developer.chrome.com/blog/remote-debugging-port)：Chrome 136 起调试默认个人资料目录受到限制；本项目一直使用独立 `user-data-dir`，不复用日常资料。

## 测试安全边界

测试进程 TEMP/TMP 仅定向工作树专用目录，不修改系统环境。禁止手工递归 ACL/reset、无界临时目录清理和按名称批量结束浏览器。历史清理事件见 `2026-08-28-test-cleanup-permission-incident.md`；不进行猜测性恢复。
