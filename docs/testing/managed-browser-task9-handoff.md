# 本地浏览器基础：Task 9 交接（2026-08-28）

## 当前状态

- 分支：`codex/transfer-share-link-replacement`。
- 实现提交：`ccb64e823275a367aee88f3f7c5e76e7ae2436ff`，只新增 `ManagedBrowserHelperClient.cs` 及其测试。
- 主代理专项验证、独立规范审查、独立质量审查均已通过，Task 9 完成。质量审查没有 P1/P2 阻断；整个本地浏览器功能仍未完成。
- 本步骤没有切换生产转存/分享适配器，没有启动浏览器、扫码登录或操作真实网盘文件。

## 客户端边界

公开启动入口先通过 Task 8 验证器取得私有助手快照，再创建并持有助手进程。内部进程/租约接口仅供测试，不向业务调用者开放任意可执行文件启动或任意 PID 接管。

- 仅重定向 stdin/stdout 传递长度帧；256-bit 随机 nonce 在进程创建后生成，仅放首条握手消息。
- 握手校验 nonce、父 PID、实际助手 PID、协议、引擎和固定能力集。stderr 仅记录固定枚举，诊断队列最多 64 项。
- 每次只允许一个在途请求；并发立即返回 `Busy`。每会话最多 1024 帧，含握手，最后一帧留给 Shutdown；达到上限需新建客户端。
- 超时/取消使当前会话失效，不自动重放；身份观察返回 typed `TimedOut`/`Cancelled`，即使底层读写不响应取消也有硬截止时间。
- Dispose 先尝试 Shutdown，经过有界等待后仅终止本客户端持有的助手进程，不终止浏览器进程树。
- 快照租约仅在助手退出、stdout/stderr 读取结束、所有写操作结束后释放。若终止或管道结束无法确认，Dispose 有界返回，后台继续持有租约并记录固定诊断。
- 助手退出与最后一条 stdout 回复竞争时，允许安全返回 `ChildExited`；不将退出后尚未读取的回复认定成功。

## 主代理本轮实际验证

测试进程 `TEMP` / `TMP` 指向工作树 `.artifacts/task9-main-verify`，未修改系统级环境变量，未手工清理测试残留或重置 ACL。

| 验证 | 结果 |
| --- | --- |
| Release 客户端 + 协议专项 | 77/77，0 跳过 |
| Release 客户端专项连续重复 3 轮 | 每轮 35/35，0 跳过 |
| Release 完整性验证器专项 | 76/76，0 跳过 |
| Python 助手 unittest | 28/28 |
| 整个解决方案 Release `--no-restore -warnaserror` | 0 警告、0 错误 |
| Release helper manifest 门禁 | 通过，BuildIdentity `d63d4c3911dbe25f42ee41b107c90f8a087116d1eba79cdb68ef55bbf1ddaa8a` |
| `git diff --check 01b66a3..ccb64e8` | 通过 |

客户端专项包含真实打包助手的验证快照 → 握手 → health → shutdown；不调用浏览器。启用 `BAIDU_BATCH_TRANSFER_TEST_HELPER_DIRECTORY=artifacts/managed-browser-helper` 的绝对路径及 `BAIDU_BATCH_REQUIRE_HELPER_INTEGRATION=1`，避免集成测试被跳过。

实现者报告的 TDD 历史：初始运行时 RED 28 项失败；响应已到而写入卡住的退出竞态 RED 2 项失败；启动前后异常所有权 RED 2 项失败；修复后 GREEN。主代理亲自重跑的是上表 GREEN，不把实现者的 RED 报告说成主代理执行记录。两阶段独立审查均采用只读源码方式，测试证据来自主代理。

未运行整个 App 测试套件。之前记录的 5 项 SQLite/AppBootstrap 失败没有在本步骤处理，不能把专项通过说成全套通过。历史 Temp ACL 不确定性仍见 `2026-08-28-test-cleanup-permission-incident.md`，本步骤没有进行猜测性恢复。

质量审查保留一项非阻断 P3：启动配置测试断言所有继承的环境变量名都不含 `NONCE`；若测试宿主本来就有无关的同名变量，可能误报失败。生产客户端并未把自己的 nonce 写入环境。后续可将断言改为比较启动配置与继承环境的差异。

## 下一项

Task 10：启动并管理用户指定安装目录中的可见 Chrome/Edge，核实专用 profile、浏览器进程和实际调试端口归属；关闭时保留用户原有浏览器窗口。随后 Task 11 接选择目录与登录 UI，Task 12 做基础验收。真实转存/分享网页写操作仍属后续计划，不提前接入。
