# 本地浏览器基础：Task 11 接线记录（2026-08-28）

## 当前边界

接续提交为 `bd9295f`，分支为 `codex/transfer-share-link-replacement`。Task 10 已完成用户指定 Chrome 目录后的可见窗口检查与手动关闭检查；该测试没有登录账号。

Task 11 按原设计拆为三个可独立审查的实现段：11A 目录/绑定持久化，11B 进程/助手生命周期，11C 登录 UI/DI。11A 实现已提交 `dffd5f9`，主代理专项验证及独立规范/质量审查已完成；11B/11C 尚未实施，不应据此宣称登录入口或真实转存/分享已可用。

用户已指定 `C:\Program Files\Google\Chrome\Application`，不要重新索要目录或扫描安装位置，也不要把该个人路径硬编码进产品。

## 接线检查发现的缺口

1. `BrowserSetupService` 已有选择/竞争处理逻辑及测试，但 `IBrowserDirectoryState` 尚无生产持久化实现，服务也尚未接入生产 DI。本步骤补现有业务数据库中的单行目录选择状态与正常前向迁移，保留精确 revision/intent 检查；不另建第二个业务数据库。
2. 原 `AddAccountService.AddAsync` 依赖旧适配器校验、Cookie 加密及 WebView2 profile 提交。新引擎应使用明确的无 Cookie 添加/绑定路径，并通过真实 SQLite 测试验证账号与会话的一致性；不能伪造 Cookie 复用旧入口。
3. `ManagedChromiumProcessManager` 在 `WaitingForPage` 时可持有浏览器但尚未返回目标句柄。退出/移除账号必须处理这种状态，只能针对已持有的精确 profile 身份停止所属浏览器，不得按浏览器名称或旧 PID 清理。
4. 新增仅本地浏览器登录的账号不能冒充已可用的 WebView2 账号。已有账号重新绑定时，保留旧字段和已开始任务的引擎快照；生产转存/分享适配器仍保持旧路径。

上述为落实原 Task 11 所需的有限接口/持久化补充，不新增业务功能或改变用户确认的操作流程。

## 真实账号识别尚未验证

`tools/managed_browser_helper/main.py` 的当前 `_fixed_dom_identity` 使用 `data-baidu-login-state` 和 `data-baidu-stable-uid`，对应 Task 7 的静态测试样例。现有记录没有证明真实百度页面提供这些属性。当前 route 策略也只接受精确的百度/账号根路径，真实登录跳转适配尚未验证。

因此，模拟身份结果通过测试，只能证明接线和绑定逻辑，不代表真实扫码后能识别账号。不能把本次用户确认的未登录首页截图当作账号识别验收。不得随意猜测选择器、放宽地址校验或用昵称代替稳定身份。后续真实页面适配和用户扫码检查必须单独记录；本步骤不自动登录、不读取默认 Chrome profile、不执行网盘写操作。

## 验证与审查

主代理亲自执行的最终验证如下，测试进程 TEMP/TMP 定向工作树 `.artifacts/task11-main-verify`。未启动 GUI、未修改用户账号数据，也没有启用真实浏览器测试开关。

| 范围 | 结果 | 证据 |
| --- | --- | --- |
| 整个解决方案 Release 构建，`--no-restore -warnaserror` | 0 警告、0 错误，helper manifest 门禁通过 | 本轮构建输出；helper BuildIdentity 不变 |
| Debug：新增存储、目录选择、旧登录/账号管理与 AppBootstrap 专项 | 129 通过、0 跳过、0 失败 | `results/task11a-app-debug-main.trx` |
| Release：同组应用专项 | 126 通过、0 跳过、1 失败；新增 17 项均通过 | `results/task11a-app-main.trx` |
| Release：MigrationTests 与 ManagedBrowserStateStoreTests | 74 通过、0 跳过、0 失败 | `results/task11a-infra-main.trx` |
| `git diff --check bd9295f..dffd5f9` | 通过 | 本轮命令输出 |

上述 TRX 均在 `.artifacts/task11-main-verify/` 下。Release 应用组唯一失败为未修改的 `AppBootstrapTests.ProductionGatePresentsExactWriteAndReachesInnerOnlyAfterExplicitConfirmation`（524 行）：该测试无条件期待 `--webview-acceptance` 打开验收门禁，但 `AcceptanceWriteLeaseOptions.FromStartup` 在 Release 明确将 `isDebugBuild` 设为 false，协调器因此拒绝创建临时写入绑定。Debug 同组通过。本轮没有为了测试放宽正式版门禁，也没有修改这项旧测试；应在后续测试维护中修正配置期待，不能声称整个 Release 测试套件通过。

实现者报告的最终 App 129/129、Infrastructure 74/74 **均为 Debug**，记录在 `.artifacts/task11-implement/task11a-app-final.trx` 和 `task11a-infrastructure-final.trx`。主代理不能把这些记录标成 Release 结果。实现者另保留接口缺失、目录重选枚举约束、部分 intent 身份的 SQLite NULL CHECK、已退休安装仍返回绑定成功等 RED→GREEN 记录；主代理亲自运行的是上表 GREEN/配置差异检查，未重放历史 RED。

规范审查明确 Ready: Yes，未发现 11A 范围内规格偏差；独立质量审查也明确 Ready: Yes，无 P1/P2 阻断。审查者只读代码，测试与真实页面验证不由审查结论代替。新增审查代理受数量限制，因此由两位未参与 11A 实现的已有代理分别承担规范/质量审查，仍保持独立且按先规范后质量顺序执行。

质量审查保留非阻断 P3 测试建议：补充目录 CAS 的 wrong-kind / wrong-intentId / wrong-revision 参数化用例，以及复用已有安装的成功/冲突用例。当前实现已有对应完整比较；后续可补充覆盖，不应误报为缺少身份检查。

主代理只读核对实现者的 `.artifacts/task11-implement/task11-bootstrap-baseline.trx`：本次 AppBootstrap 基线 25 通过、0 失败。历史完整 App 测试的 5 项 SQLite/AppBootstrap 失败没有在这组专项基线重现；不能把“这次专项通过”写成“历史 5 项已经修复”。

存储与服务尚未接入生产登录 DI。11A 新增账号记录用 `LoginMethod=ManagedChromium` 区分；11C 在开放入口之前，必须确保这类仅新浏览器登录的账号不会被旧 WebView2 转存/分享流程当作可执行账号。

## 下一步

1. 11B 接精确所属 profile 的进程生命周期，处理浏览器已开但目标尚未就绪、账号移除和浏览器家族切换；继续使用 Task 6 原子状态转换。
2. 11C 把添加账号、所选账号重新登录、选择/重选安装目录接到界面；注册实际目录状态和登录服务，加入旧引擎账号资格过滤及组合测试。保持当前两栏工作区，不新增创建任务步骤。
3. 真实百度登录后页面与稳定账号识别仍需单独适配和用户现场检查。当前只具备受控窗口启动与存储基础，不可承诺扫码后自动保存已可用。

## 测试安全

仅设置测试进程自己的 TEMP/TMP 到工作树普通专用目录，不修改系统环境。不运行递归 ACL/reset、无界目录清理或浏览器批量结束命令。历史 ACL 事件的不确定性仍保留，详见 `2026-08-28-test-cleanup-permission-incident.md`。
