# 本地浏览器基础：Task 11 接线记录（2026-08-28）

## 当前边界

接续提交为 `bd9295f`，分支为 `codex/transfer-share-link-replacement`。Task 10 已完成用户指定 Chrome 目录后的可见窗口检查与手动关闭检查；该测试没有登录账号。

Task 11 按原设计拆为三个可独立审查的实现段：11A 目录/绑定持久化，11B 进程/助手生命周期，11C 登录 UI/DI。11A 实现已提交 `dffd5f9`，主代理专项验证及独立规范/质量审查已完成。11B 从交接提交 `016ab4a` 继续，首份实现为 `0ed1040`，规范审查发现的两项遗漏已在 `d6f62d8` 修复，主代理最终验证及独立规范/质量复查均通过。11C 尚未实施，不应据此宣称登录入口或真实转存/分享已可用。

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

1. 11B 已完成底层接线与专项审查，见下方最终证据；无需重新实现进程、绑定或 Task 6 隔离状态机。
2. 11C 把添加账号、所选账号重新登录、选择/重选安装目录接到界面；注册实际目录状态、退休槽位协调器与登录服务，补组合测试。旧引擎资格过滤已在 11B 前置，仍需验证生产组合。保持当前两栏工作区，不新增创建任务步骤。
3. 真实百度登录后页面与稳定账号识别仍需单独适配和用户现场检查。当前只具备受控窗口启动、存储与登录管理基础，不可承诺扫码后自动保存已可用。

## 11B 接续范围与检查要点

本段只连接已有的安装目录选择服务、受管进程、只读身份助手与 11A 原子账号绑定，不切换生产登录按钮或转存/分享适配器。浏览器及身份观察测试使用替身；SQLite 持久化使用真实临时数据库。本轮不启动浏览器、不登录账号、不打开导入链接，也不运行云端写操作。

- 普通登录等待、取消或超时保留已启动的专用浏览器，返回明确状态；身份未通过核对时不修改原有账号或会话。
- 浏览器已启动但 Target 尚未就绪时，仍必须记录所属关系；只允许停止管理器已持有的精确 Profile，不能按进程名称或历史 PID 结束浏览器。
- `IManagedBrowserLifecycle` 的加载/停止调用本身没有覆盖账号删除持久化准备的锁周期。因此登录、家族切换、删除之间需要共享的账号级协调，删除持有至 `PrepareRemovalAsync` 提交。它只协调同一应用进程；数据库的账号有效性、版本和原子状态检查仍不可省略，不得将进程内锁描述为跨进程保护。
- Chrome/Edge 切换继续使用 Task 6 的退休/新增事务，先停止旧所属进程、再启动新家族的独立 Profile；仅相同稳定 UID 才能切换，旧操作快照和旧 Profile 不删除。
- 切换前先只读检查原会话是否还有未完成/恢复中的操作，再停止旧浏览器；最终事务仍必须重复检查，不能用提前检查替代提交时的约束。
- 未绑定成功的 Profile 不能假造 UID 送入 Task 6 清理流程。账号移除应保留并明确阻断这种资料；软件重启后也不能绕过检查。检测只读、只针对软件管理的两个家族下的精确账号 GUID 路径，不创建目录、不读取 Cookie、不接管未知资料，不增加自动清理或孤立资料恢复界面。
- 检查生产依赖组合：实际账号移除生命周期可以接入，添加/重新登录入口仍由 11C 上线。仅新浏览器账号的旧引擎资格过滤可作为防护前置到 11B，仍需证明原 WebView2 账号及已开始任务不受影响。

11B 的完成状态和验证结果应以本节后续追加的实际命令、TRX 与独立审查结果为准；上述要求列表不是通过证明。

### 11B 首份实现的独立验证与审查

主代理对 `0ed1040` 独立运行了以下检查。测试进程 TEMP/TMP 指向 `.artifacts/task11b-main-verify/temp`，明确关闭两项真实浏览器开关；未操作真实账号。

| 配置与范围 | 实际结果 |
| --- | --- |
| 整个解决方案 Release，`--no-restore -warnaserror` | 0 警告、0 错误，helper 发布检查通过 |
| Debug 应用：登录/存储/进程/助手/协议/目录/旧登录与账号界面/AppBootstrap/退出专项 | 295 通过、3 跳过、0 失败 |
| Release 应用：新登录生命周期、进程及绑定存储 | 99 通过、2 跳过、0 失败 |
| Release Infrastructure：ManagedBrowserStateStore、AccountRemovalStore、MigrationTests | 196 通过、0 跳过、0 失败，约 3 分 37 秒 |
| Release Application：ManagedBrowserAccountRemoval、AccountRemovalCoordinator | 40 通过、0 跳过、0 失败 |
| `git diff --check 016ab4a..0ed1040` | 通过 |

证据为 `.artifacts/task11b-main-verify/results/task11b-{app-debug,app-release,infra-release,application-release}-main.trx`。跳过项为真实浏览器或打包助手的 opt-in 检查。Infrastructure 命令中的 `ProbeExisting` 过滤词没有匹配新增测试 `ReadOnlyPresenceProbe`，因此本表不能替代那两项存在性检查的单独复跑。旧的 Release DEBUG-only 验收测试问题见 11A 记录，本次没有声称整个 Release 测试套件通过。

实现者还用真实 SQLite/资料目录隔离测试发现，新登录生成的 ProfileId/OwnershipId 使用 GUID `D` 格式，而既有 Task 6 标记与清理要求 `N` 格式。修正仅发生在新会话生成处；原始快照和严格清理校验没有放宽。独立验证的版本已包含该修正。

首轮规范审查结论 **No（11B）**，两项 P2：

1. 已关闭浏览器重新启动时，移除旧 owner 后局部引用未清空；新资料目录已固定、可执行文件核验期间取消，会跳过新租约释放。应区分已移除 owner 与本次实际启动的 owner，补取消回归测试。
2. Chrome → Edge → Chrome 会复用已退休 Chrome Profile、却创建新 SessionId，违反既有唯一约束。Task 6 原有往返测试要求新 Profile，不能把该问题混同于“旧目录阻断账号删除”的已记录限制。

第二项按已有 Task 6 隔离机制补接，不另建目录代次模型或数据库迁移：只定位有持久化身份且已退休的目标家族资料，复用确定性 manifest 与 Prepared/Claim/MoveIntent/Quarantined 恢复流程，保留旧资料而不删除，然后在原目标槽位创建新的 Profile。当前活动账号/浏览器在目标槽位安全准备前不得受影响；未知、未绑定、歧义或 CleanupPending 资料继续拒绝自动处理。最终应增加往返成功、新编号、提交前阻断与崩溃恢复测试，重新进行规范审查，通过后再进行独立质量审查。

### 11B 修复后的最终证据（`d6f62d8`）

两项 P2 已按上述方案修复。新增测试覆盖旧 owner 移除后取消、新句柄释放、Chrome → Edge → Chrome 新身份、UID 不匹配保留绑定、Prepared / MoveIntent / 已移动尚未提交完成三种恢复阶段、CleanupPending、退休目标的未完成历史阻断，以及唯一退休槽位歧义。没有新增 schema、目录布局或改写 Task 6 清理规则。

主代理亲自对最终提交重新执行：

| 配置与范围 | 实际结果 | TRX（同一 results 目录） |
| --- | --- | --- |
| 整个解决方案 Release，`--no-restore -warnaserror` | 0 警告、0 错误，helper 门禁通过 | 构建输出 |
| Debug 应用：与首份实现相同的完整专项过滤组 | 309 通过、3 跳过、0 失败 | `task11b-app-debug-final-main.trx` |
| Release 应用：登录生命周期、绑定存储、进程管理 | 113 通过、2 跳过、0 失败 | `task11b-app-release-final-main.trx` |
| Release Infrastructure：ManagedBrowserStateStoreTests、ReadOnlyPresenceProbe | 41 通过、0 跳过、0 失败 | `task11b-infra-release-final-main.trx` |
| Release Application：两组账号移除协调测试 | 40 通过、0 跳过、0 失败 | `task11b-application-release-final-main.trx` |
| `git diff --check 016ab4a..d6f62d8` | 通过 | 命令输出 |

TRX 均位于 `.artifacts/task11b-main-verify/results/`；TEMP/TMP 仍为该验证目录下的专用 `temp`，没有启动真实浏览器。最后一轮 Infrastructure 的过滤词已正确匹配两项 `ReadOnlyPresenceProbe`，弥补首轮过滤遗漏；首轮较广的 196 项存储/迁移结果仍单独保留，不能与最终 41 项混称同一轮。跳过项目仍为明确 opt-in 的真实浏览器/打包助手测试。helper BuildIdentity 保持 `d63d4c3911dbe25f42ee41b107c90f8a087116d1eba79cdb68ef55bbf1ddaa8a`。

独立规范复查 **Ready: Yes（11B）**，随后独立质量审查 **Ready: Yes（11B）**，没有剩余 P1/P2 阻断。两位审查者均未参与实现、只读检查；审查顺序为规范通过后再质量审查。它们不替代以上实际测试，也不代表真实页面已验收。

仍保留以下边界：生产转存/分享及登录按钮没有切换；真实稳定 UID 仍未验证；仅新引擎账号不会被旧 WebView2 流程选用。未绑定/未知目录，以及尚未纳入当前移除流程的其他家族资料，仍会保守阻止退出账号；不删除、不猜测归属，也不承诺重试登录即可消除该阻断。账号 lease 是进程内保护，既有单实例约束与 SQLite 最终事务检查仍必须保留。

## 测试安全

仅设置测试进程自己的 TEMP/TMP 到工作树普通专用目录，不修改系统环境。不运行递归 ACL/reset、无界目录清理或浏览器批量结束命令。历史 ACL 事件的不确定性仍保留，详见 `2026-08-28-test-cleanup-permission-incident.md`。
