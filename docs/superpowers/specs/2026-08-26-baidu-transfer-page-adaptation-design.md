# 百度网页转存页面适配设计

## 目标

根据用户提供的三个真实页面阶段，补齐可见 WebView2 中的转存候选协议：提取分享内容、识别外链资源、自动进入预先选择的网盘目录、自动创建缺失目录并提交转存。页面证据不足时必须停止，不猜测成功，也不重复提交。

本设计只适配转存流程。自动分享继续使用现有安全骨架，待转存现场验收完成后单独适配。

## 已确认的页面阶段

截图只作为视觉事实，不保存其中的账号、提取码、文件名或其他用户数据。

1. **提取码页**：页面同时存在唯一提取码输入框和唯一“提取文件”按钮。
2. **外链资源页**：页面显示分享资源预览、当前保存位置以及唯一“保存到网盘”按钮。
3. **保存目录弹窗**：页面显示“保存到”弹窗、当前账号、文件夹列表，以及“新建文件夹”“取消”“确定”三个动作。

## 选择的识别方式

采用“语义文字 + 控件状态 + 精确路径”的联合识别，不使用屏幕坐标。

- 语义文字用于确定页面阶段与按钮用途。
- 可见性、启用状态、唯一候选数量用于避免点到隐藏控件、广告或重复按钮。
- 百度官方 HTTPS 域名、当前路径和弹窗层级必须同时匹配。
- 资源与目录必须具备可验证的稳定身份；若页面没有暴露唯一稳定身份，返回 `NotValidated`，不得退化成仅按文件名猜测。
- 正式 `BaiduSelectorCatalog.Production` 仍保持关闭；新规则属于版本化 candidate pack，只有单笔 Debug 验收租约或完全匹配的正式验收记录才能执行。

每个页面状态都有唯一 trusted root：官方精确 route、预期的 dialog/region 角色、标题和祖先链必须同时匹配。候选控件必须位于该 root 内，并且具备正确的 native/ARIA role、可见、启用、仍连接到文档、未被遮罩且通过 hit-test。隐藏模板、广告、客户端下载、顶部导航、未验收的 shadow root 或 trusted root 外的同名文字一律忽略；若无法证明唯一性则拒绝。

固定坐标方案和只依赖 CSS 类名的方案不采用：前者会受窗口大小影响，后者会因页面换肤或类名变化误点。

## 数据流

### 1. 提取分享内容

软件打开用户导入的官方分享链接，只在识别到唯一提取码页时执行：

1. 将该任务自带的提取码写入唯一输入框；提取码只在内存和页面输入中使用，不写日志、证据、截图或测试数据，也不进入 observation token/hash、raw script telemetry、异常 `Data`。
2. 同一次 DOM 命令再次核对官方域名、页面状态、输入框和按钮唯一性。
3. 点击“提取文件”，等待页面进入外链资源状态。

0 个或多个候选、验证码、登录失效、页面跳转或控件禁用均停止操作。

### 2. 确认外链资源

进入资源页后必须证明列表完整，而不只是当前 viewport 连续两次相同。协议仅接受页面可验证的 `totalCount` 或 end-marker，并逐页/逐 cursor/逐虚拟窗口滚动到末尾；遍历过程必须单调覆盖、无 gap、无 cursor cycle、总数不变化且全局稳定 ID 去重。无法证明完整时，在判断 0/1 候选或执行任何 mutation 前返回 `NotValidated`。

每个顶层资源必须得到唯一稳定 ID；完整 ID 集合严格排序、去重，并形成 generation token 和 source-set fingerprint。typed contract 从仅保存数量升级为持久化完整 `RemoteItemIdentitySet`、完整性证据和 generation token，Prepare、Submit 与 Recovery 都必须使用同一集合。

- 一个 APK、普通文件或文件夹：保存该精确资源。
- 一个链接包含多个顶层资源：作为一个精确集合整体保存。
- 只看到文件名、图标或大小，但没有稳定 ID：停止并返回 `NotValidated`。
- Submit 的同一次 DOM turn 必须再次证明页面当前选中 IDs 与持久化 source IDs 完全一致，禁止仅凭数量相同继续。

只有资源集合、观察指纹和唯一“保存到网盘”按钮都满足要求，才允许点击。

### 3. EnsureDirectory：选择并自动创建目标目录

目标目录由用户在软件界面提前选择并保存为规范化网盘绝对路径。目录准备与最终转存分开执行，避免新目录没有提交前 baseline。

`EnsureDirectory` 阶段打开保存弹窗，并绑定 `(start exact ID, start canonical absolute path, target canonical absolute path)`。只有 start path 是 target path 在完整目录段边界上的前缀时，才处理剩余 suffix；否则先安全回到已验证的网盘根目录，无法证明根目录身份时返回 `NotValidated`。每进入一级都重新核对 exact ancestor ID chain 与 breadcrumb，不能仅比较最后一个目录名。

1. 对当前层完成全量遍历，证明 total/end-marker、无 gap/cycle、全局 ID 唯一并稳定；只看当前 viewport 不算完整。
2. 若恰好一个文件夹与当前片段精确匹配，进入该文件夹。
3. 若不存在，先持久化 `parent exact ID + 规范目录段 + mutation intent + generation`，再获取 mutation permit，点击唯一“新建文件夹”、输入当前片段并确认创建。
4. 点击后若响应丢失、进程中断或租约过期，将该段标记为 `OutcomeUnknown`，禁止自动重发；恢复时只能只读完整枚举原 parent。只有账号、parent、路径均匹配且恰好一个 exact child 时才能确认，否则要求人工处理。新 generation 也必须先恢复核对，不能盲目再次创建。
5. 创建后重新完整扫描同一 parent，重新证明 total/end-marker、远端 UID、parent exact ID、ancestor chain 和 canonical path，并要求服务端返回的精确名称匹配、`kind=folder`、恰好一个 stable child exact ID。只有这些全部成立才持久化 `Confirmed` child ID 并进入；否则记录 `OutcomeUnknown`。若出现多个候选、同名歧义、服务端改名、同名非文件夹、非法名称、路径超限或结果无法确认，立即停止，并向用户显示已经成功创建的部分路径。
6. 到达完整目标路径后，精确复核面包屑/路径、target exact ID 和远端百度 UID，然后点击“取消”关闭弹窗，不点击最终“确定”。取消必须经过现场验证；若无法证明取消不会转存，则缺失目录场景保持 `NotValidated`。

创建目录的每一步都是网页写操作，必须获取当前 operation 的 mutation permit。授权过期或被释放后，旧会话不能再修改页面。

### 4. PrepareTransfer：读取提交前 baseline

目录准备完成后，`PrepareTransfer` 通过只读网盘页面重新打开 target exact ID，对目录列表执行同样的完整遍历，并持久化完整 baseline IDs、total/end-marker、generation token、target exact ID 与远端百度 UID。无法证明完整列表时不得进入提交。

### 5. SubmitTransfer：重新选择同一目录并唯一提交

`SubmitTransfer` 使用新的可见会话重新打开外链，复核完整 source IDs，并再次打开保存弹窗。此阶段不得创建目录，只能完整遍历并找到先前持久化的同一 target exact ID；路径、账号或 ID 任一变化均停止。

随后先持久化 durable `MarkSubmitting` 边界，再获取最终 `ClickConfirm` mutation permit，并在同一次 DOM turn 重新核对官方 URI、trusted root、完整 source IDs、远端 UID、target exact ID、按钮唯一性和 hit-test，唯一一次点击“确定”。durable boundary 之后任何异常都进入 `ResultPending`，恢复流程永远只读且绝不再次点击。

### 6. 确认转存结果

点击“确定”后只接受百度官方 result container 中的 typed 受理证据：非空官方 request ID、受理状态、target exact ID，以及可验证的 source→destination identity mapping。普通 toast、相似文字、按钮消失或目录数量变化都不能单独生成 `TransferOfficialAcceptance`。

随后重新打开目标目录并完整遍历列表，与提交前 baseline 比较：

- 新增 destination IDs 必须通过官方 request/result mapping 对应完整 source IDs；若百度不提供映射，则必须使用协议明确、可验证且抗同名冲突的内容身份。仅凭相同数量、文件名、图标或大小不得宣称集合一致；无法证明时保持 `ResultPending`。
- 文件夹保存后记录该文件夹的精确远端身份；
- APK 或普通文件保存后记录对应文件的精确远端身份；
- 结果不明确、出现验证码、窗口关闭或程序中断时进入 `ResultPending`，只能走恢复核对，不能盲目再次点击“确定”。

这些精确 destination IDs 会传给后续自动分享，保证分享的是实际保存的文件或文件夹。

### 7. 只读恢复

替换现有恢复时重新打开外链并输入提取码的做法。提交前已经持久化完整 source IDs、source generation token、target exact ID、目标 baseline 和 durable submission descriptor；提交时若获得官方 request/result mapping，也立即持久化。

恢复流程使用独立的 typed read-only contract，只打开已知 target exact ID，完整遍历当前目录，并使用持久化 source IDs、baseline 与 official mapping 判断结果。它绝不重新打开受提取码保护的 source、绝不填写提取码、绝不点击任何 source-page 控件，也不获取 candidate write permit。恢复读取由 durable pending operation、AccountId 和稳定百度 UID 共同授权；UID 不匹配立即停止。这样临时 candidate 可以形成真实恢复证据，而不把临时写租约扩展到 recovery。

## 账号绑定

本地 `AccountId` 不能靠头像或显示名映射。candidate session payload 必须携带 `AccountSession.BaiduUserId`（或已验证的等价稳定远端 UID）。每次导航、每次弹窗重开、每次 observe 以及每次 mutation 的同一 JavaScript turn 都必须读取并精确复核该 UID；UID 缺失、变化或出现切号状态时立即撤销 binding，保持零 mutation。

## 组件变更

- 新增独立版本常量：`ProtocolVersion = baidu-visible-transfer-v2`、`SelectorVersion = baidu-transfer-pages-v2`。route、trusted root、语义文字、控件角色、action/payload 或身份映射任一规则变化都必须提升相应版本；BuildVersion 继续精确绑定，旧正式记录自动失效。转存与分享验收记录继续独立。
- 扩展 `BaiduCandidatePageState`，明确提取码页、外链资源页和目录弹窗状态。
- 扩展 candidate command/payload，支持提取码输入、提取按钮、资源保存、目录进入、新建目录和最终确认；所有 `ToString` 与诊断均脱敏。
- 将 DOM 查询封装为固定内置脚本。调用者只能传 typed 参数，不能传任意脚本或定位器。
- 扩展 `ITransferAutomationSession` typed contract，使 source listing 携带完整 `RemoteItemIdentitySet`、完整性证据与 generation token；`WebView2AutomationSession` 把新命令翻译为 listing、baseline、official acceptance 与确认结果。
- 新增只读 transfer recovery contract，并修改 `TransferAutomationFlow.ConfirmExactAsync`：只读取 target current listing 和持久化证据，不再调用 `OpenExternalShareAsync` 或 `EnterExtractionCodeAsync`。
- 为目录创建增加可恢复的逐段持久化记录，保存 parent/child exact ID、规范段名、generation、intent 和 `Confirmed/OutcomeUnknown`，不保存账号凭据或提取码。
- 继续使用现有 immutable binding、阶段白名单和 mutation permit；不放宽导航域名、正式能力记录或恢复规则。

## Candidate action 与阶段白名单

所有输入、选择和点击型 DOM mutation 都必须逐次取得当前 immutable binding 的 mutation permit；同一会话中的前一次 permit 不能复用。

| 阶段 | 允许的 candidate mutation | 明确禁止 |
| --- | --- | --- |
| `EnsureDirectory` | `SetExtractionCode`、`SubmitExtraction`、`OpenDestinationPicker`、`EnterDirectory`、`CreateDirectory`、`ConfirmDirectoryCreation`、`CancelDestinationPicker` | 最终 `ClickConfirmTransfer` |
| `PrepareTransfer` | 无；仅官方导航、完整枚举和 baseline 持久化 | 输入、选择、创建、最终确认 |
| `SubmitTransfer` | `SetExtractionCode`、`SubmitExtraction`、`SelectExactItems`、`OpenDestinationPicker`、`EnterDirectory`、最终 `ClickConfirmTransfer` | `CreateDirectory`、重复最终确认 |
| `Recovery` | 无；仅目标目录只读完整枚举 | 任何 input/click、打开或解锁 source、任何重提交流程 |

列表滚动属于固定脚本内的只读枚举过程，不得触发网络写操作或点击资源动作。阶段白名单、action/payload 或页面语义变化都要求提升 candidate selector/protocol 版本。

## 失败与安全规则

- 页面状态与 trusted root 必须唯一；任何冲突都返回 `NotValidated`。
- Mutation 脚本必须在同一次 JavaScript 执行中重新获取节点，并核对 URL、远端 UID、状态、祖先链、角色、可见/启用/connected/topmost、控件数量、资源集合和观察指纹，再做一次逻辑动作。
- durable boundary 前的转存失败是 `DefinitelyNotAccepted`；目录创建已发生但结果未知时单独记录 `OutcomeUnknown`，不能笼统当成无副作用；boundary 后无法确认是 `ResultPending`。
- 不读取或记录 Cookie、请求头、账号凭据、完整 DOM、完整分享链接或提取码。提取码可以短暂存在于进程内 typed payload 和发往当前官方页面的内存 script 参数中，但不得持久化，也不得出现在针对 script args/response 的 telemetry、日志、异常输出或截图。
- 不使用开放平台 API、XHR/fetch、远程调试端口、外部脚本或百度客户端自动拉起协议。
- 广告按钮、客户端下载、顶部导航等不属于目标容器的同名文字不得成为候选。

## 测试与验收

### 自动化测试

- 三种页面状态和 trusted root 的唯一识别，以及 0 个/多个候选拒绝；覆盖同名广告、隐藏副本、遮罩、stale node 和未验收 shadow root。
- 提取码不出现在 observation token/hash、针对 script args/response 的 telemetry、日志、异常 `Data`、`ToString`、截图或快照中；内存 typed payload 不持久化，覆盖 serializer、host 和 dispatcher 抛错路径，测试只使用假码。
- 资源列表未完成、不稳定、重复 ID、无稳定 ID、集合变化时零 mutation。
- 虚拟滚动/分页覆盖同名目录位于未渲染页、懒加载插入/重排、cursor cycle/gap、total 变化和全局重复 ID。
- 目录已存在、非根目录起点、逐级进入、缺失一级自动创建、多级缺失依次创建。
- 非根起点只有在 segment-boundary 前缀成立时处理 suffix；错误前缀必须安全回根或 `NotValidated`，每级 ancestor exact ID chain 必须匹配。
- 新建结果缺失/重复/服务端改名、Unicode/空白、同名非文件夹、路径变化、账号变化和弹窗关闭时停止。
- 并发创建同名目录、部分创建后 crash/lease expiry/restart：只读恢复，不重复创建，并报告已创建部分。
- 每个写动作前必须有 mutation permit；过期、释放、跨账号/操作/代次均零 mutation。
- observe→mutate 间切换远端 UID 必须零 mutation并撤销 binding。
- Prepare 证明完整 baseline；Submit 不创建目录，durable boundary 后只允许一次最终确认。
- 提交响应丢失保持 `ResultPending`，恢复只读且永不再次提交。
- 带提取码的 temporary candidate 在 recovery 中只读取 target，source-page mutation 数为 0，且不需要 recovery write lease。
- 对阶段白名单逐项测试；`CreateDirectory` 只能 Ensure，`ClickConfirmTransfer` 只能 Submit，Recovery 的所有 mutation 均拒绝。
- 一个文件、一个文件夹和多个顶层资源均持久化完整 source IDs，并验证官方 source→destination 映射。

### 现场验收

首次只运行 Debug + `--webview-acceptance`，使用可丢弃资源：

1. 一个 APK 转存到已存在目录。
2. 一个文件夹转存到已存在目录。
3. 自动创建一层和两层缺失目录后转存。
4. 一个链接含多个顶层资源时整体转存。
5. 人工触发取消、验证码或关闭窗口，确认没有重复提交。

只有真实远端结果、恢复核对和本机导出证据全部通过，并由用户再次确认后，才写入正式版本记录。
