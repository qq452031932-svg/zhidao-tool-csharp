# WebView2 候选写协议设计

## 目标

在不改变正式 `BaiduSelectorCatalog` 的前提下，为 Debug + `--webview-acceptance` 的一次性、精确 operation 租约提供可见 WebView2 typed transfer/share 候选路径。未取得现场证据的页面字段和动作必须保持 `NotValidated`，且浏览器点击数为零。

## 边界

- 不访问百度开放 API，不执行 `fetch`/XHR，不读取 cookie、header、完整 DOM，不加载外部脚本。
- 不使用 remote debugging、复用 Chrome profile 或复制参考程序的账号、授权、许可逻辑。
- `BaiduSelectorCatalog.Production` 继续 `NotValidated`，所有 `ExecutableLocators` 继续为空。
- 候选脚本有且只有两条互斥授权来源：首次现场操作要求 Debug acceptance 模式、精确 operation scope、未过期且已消费的一次性租约；后续普通运行或安全恢复要求完全匹配且未过期的正式 capability record。没有正式记录时，Release、普通运行和恢复均拒绝；另一账号/操作/适配器/版本/代次/批次始终拒绝。
- 手工目录确认不等于 selector 证据，也不能正式接受 candidate protocol。

## 架构

`IBaiduCandidateDomExecutor` 是唯一可执行 DOM 的边界。它接受固定枚举 command 和无敏感日志的 typed 参数，执行应用内置脚本，返回白名单 DTO：页面状态、候选数、列表完整/稳定标志、exact IDs、官方结果标识和观察指纹。调用方不能传脚本文本或 locator。

`WebView2CandidateDomExecutor` 只对当前可见 `CoreWebView2` 调用 `ExecuteScriptAsync`。每个 command 分成 observe 与 mutate 两段：observe 必须先证明官方 URI、唯一页面状态、唯一候选、exact 集合；mutate 在点击前立即重新调用 `IAcceptanceCandidateActionAuthorizationSource`。失败、取消或过期不会执行 mutate。

`WebView2AutomationSession` 实现 `ITransferAutomationSession` 和 `IShareResultExportSession`，把 executor DTO 翻译成现有 flow 的 typed listing/acceptance。现有 `TransferAutomationFlow` 与 `ShareAutomationFlow` 继续负责 baseline、提交边界、官方 acceptance、重开后的 exact diff 和 share-list new-one 校验。

coordinated adapter 在调用 inner adapter / session factory 之前完成精确写授权，并把不可变 binding 注入 operation context；session 不接受未绑定的候选写请求。binding 固化 `ConsumedLease` 或 `ExactFormalRecord` 来源、exact identity、capability、原始到期时间和正式记录身份。临时来源只承认 acceptance 模式下已消费、未过期、非恢复的 logical operation；正式来源精确匹配 `AdapterKind + ProtocolVersion + SelectorVersion + BuildVersion` 的未过期 capability record，可用于 ordinary run 和按既有规则进入的 recovery。每个后续动作只复核 binding 中原来源；运行中新出现的 lease 或 formal record 不得替换来源、混合授权或续期。

静态 Production catalog 与可执行 candidate pack 分离：catalog 继续关闭且不保存 executable locators；candidate pack 自有固定版本和脚本集合，只有上述临时或正式来源动态授权。现场证据没有完成时无法写入正式 record，因此 candidate pack 在普通运行中仍不可达。

分享准备新增 `PrepareShare` 写闸门动作。临时路径由它唯一一次消费租约，正式 ordinary/recovery 路径由它精确读取并固化 formal record；两者都在 inner/session factory 前注入 binding。`SubmitShare` 不再消费租约或创建 scope，只复核同一 binding 的来源、identity 和原始到期时间。

## 候选脚本与现场证据

参考源码仅用于候选语义：网盘表格行、批量分享、永久期、批量创建链接、结果弹窗和导出按钮。以下字段尚须现场探测，缺失时对应 command 返回 `NotValidated`：

- 外部分享、提取码、保存目录、转存成功页面的唯一状态标志；源 top-level exact ID 与完整性；目的目录 path identity 和完整稳定 exact listing；官方转存 acceptance/request ID。
- own-drive 行稳定 exact ID、虚拟滚动/分页完整性、选择状态；永久/无限/固定码控件的官方语义和提交状态。
- 官方分享列表稳定 share ID、exact resource IDs、settings、complete/stable 标志，以及官方导出文件 schema。
- 验证码、登录过期、风控和未知页面的唯一标志。

## 失败语义

观察为 0 个或多个候选、页面状态冲突、exact 集合不等、列表不完整/不稳定、scope 不匹配、租约过期、恢复、取消或窗口关闭时，返回 `BaiduErrorKind.NotValidated` 或现有安全的 pending 结果；任何 mutation 之前拒绝，click count 保持零。提交后无法精确确认时只能进入 `ResultPending`，不得推断成功。

## 测试

- executor：unique、0、multiple、wrong-state、wrong exact set、结构化 DTO 非法、取消。
- authorization：Debug acceptance exact identity 允许且临时 recovery 拒绝；无正式记录的普通/Release/恢复、过期、跨 identity/capability 全拒绝；完全匹配且未过期的正式 record 允许 ordinary/recovery；每个 mutation 都重新授权。
- queue/session：scope 在导航前绑定；用户关闭和取消停止后续动作；不执行外部脚本/API。
- transfer/share：typed contract 可达；baseline、official acceptance、destination exact diff、official share-list new-one、export after exact success。
- scheduling：share `PrepareShare` 在 session 创建前建立 scope；SubmitShare 只能使用同一未过期 logical operation。
