> **⚠️ 本文件已作废，仅作历史归档。不要据此工作。**
>
> 作废原因：其中的分支名、HEAD、"关键文件"清单和"下一窗口"计划全部过时
> （它描述的 HEAD 是 `eb3a35f`，实际功能分支已推进到 `5494d16a`，相差数百个提交）。
> 它所列的关键文件在主工作树中并不存在。
>
> 当前唯一有效交接文件：`docs/HANDOFF.md`

# 交接文件：批量转存、批量分享与结果 Excel

更新时间：2026-08-24  
仓库：`C:\Users\45203\Documents\dakazhandian`

## 1. 产品目标

本项目只需要实现一条本地 Windows 桌面自动化流程：

```text
导入百度分享链接/Excel
    -> 选择百度账号和目标目录
    -> 批量转存到用户自己的网盘
    -> 对转存成功的实际资源批量创建自己的分享
    -> 把原始数据、新分享链接、提取码和状态写入另存 Excel
```

不需要对齐 PanTools 的外围能力，也不需要做独立的“原文内联替换”工具。当前产品定义中，“替换链接”就是在不修改源 Excel 的前提下，另存结果 Excel 并追加新分享结果列。

## 2. 产品边界

### 必须支持

- Windows 10/11 x64 本地桌面应用；
- 百度扫码登录和多账号隔离；
- 文本粘贴导入；
- Excel 流式导入、列映射确认、原始列保留；
- 单账号、多账号复制、多账号分流；
- 目标目录和创建外层目录；
- 转存成功后使用实际远端资源 ID 创建分享；
- 永久有效、无限访问、随机或统一提取码；
- 转存与分享分别持久化、失败隔离、崩溃恢复；
- 分享失败单独重试，不重复已成功转存；
- 结果 Excel 另存，不覆盖源文件；
- 原始标题、备注、链接和其他文本列保留；
- 每个参与账号输出新链接、提取码和分享状态。

### 明确非目标

- 其他网盘和跨网盘转存；
- 资源库、快速转存独立入口；
- 会员/SVIP、支付、云端账号体系；
- 通用批量分享管理、批量取消分享、重命名和内容清理；
- 复制源 Excel 的图片、样式、宏、复杂公式和版式。

## 3. 当前代码位置和分支状态

主分支：

- 分支：`master`
- HEAD：`6e84ba8 docs: clarify share result headers`

功能工作树：

- 路径：`C:\Users\45203\Documents\dakazhandian\.worktrees\transfer-share-link-replacement`
- 分支：`codex/transfer-share-link-replacement`
- HEAD：`d588758 perf: stream exact set reconciliation queries`
- 工作树有用户确认的未提交修改，**不要使用 `git reset --hard`、`git checkout --` 或清理未提交文件**。

当前功能分支最近提交已经包含：

- 精确转存结果证据；
- 转存和分享分阶段持久化；
- 永久分享和无限访问；
- 幂等分享执行；
- 已有分享检测、复用和重新生成决策；
- 分享结果待确认和最终一致性核对；
- 分享失败独立恢复；
- 大批量 keyset 分页和流式查询。

关键文件：

- `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- `src/BaiduBatchTransfer.App/Features/Imports/ImportView.xaml`
- `src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardView.xaml`
- `src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardViewModel.cs`
- `src/BaiduBatchTransfer.Application/Imports/TextImportParser.cs`
- `src/BaiduBatchTransfer.Application/Jobs/CreateBatchService.cs`
- `src/BaiduBatchTransfer.Application/Jobs/ResultColumnLayoutBuilder.cs`
- `src/BaiduBatchTransfer.Application/Jobs/ExportBatchResultsService.cs`
- `src/BaiduBatchTransfer.Application/Scheduling/ShareOperationExecutor.cs`
- `src/BaiduBatchTransfer.Application/Scheduling/ShareOperationReconciliationService.cs`
- `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlResultWriter.cs`
- `src/BaiduBatchTransfer.Infrastructure/Database/SqliteShareSchedulingStore.cs`

## 4. 当前已验证状态

来自 `docs/superpowers/execution-log.md`：

- Core.Tests：203 通过；
- BaiduAdapter.Tests：33 通过；
- Application.Tests：409 通过；
- App.Tests：157 通过；
- Infrastructure.Tests：290 通过；
- 600,000 操作分享 benchmark：通过；
- Release 构建：0 警告、0 错误；
- `git diff --check`：通过；
- 200,000 资源批量写入：约 26.10 秒，固定阈值 20 秒，仍是性能门槛，不要把它描述成完全通过。

这些是本地自动化结果，尚未替代真实百度账号验收。

## 5. 当前真实阻塞点

`AppBootstrap.cs` 仍然注册：

```csharp
services.AddSingleton<IAdapterAvailability, UnavailableAdapterAvailability>();
```

这会让应用禁止正式开始转存。这个门禁是有意保留的，不能在没有受控验收的情况下直接全局解除。

下一窗口应先做“现场验收模式”，而不是直接放开所有任务：

1. 只允许一个明确选择的测试账号；
2. 限制 1～3 条测试链接；
3. 要求用户二次确认；
4. 依次验证真实转存、实际资源 ID 落库、真实分享和结果导出；
5. 验收通过后，再决定是否切换正式 `AvailableAdapterAvailability`。

## 6. 下一窗口执行顺序

### 阶段 A：先验证最小真实闭环

```text
测试账号
 -> 1～3 条无敏感内容链接
 -> 单账号转存
 -> 保存精确远端资源 ID
 -> 永久分享
 -> 结果 Excel
```

必须记录：

- 百度登录是否成功；
- 转存接口返回是否包含可核对的实际资源 ID；
- 单文件、文件夹或多顶层资源是否都能正确分享；
- 分享链接和提取码是否可打开；
- 结果 Excel 是否保留原始字段并填写新结果。

### 阶段 B：接通结果 Excel

重点不是新增“文本替换”，而是确认现有动态列模型真正接入写入器：

- 原始列按原顺序输出；
- `转存结果_原分享链接` 保留原链接；
- `转存结果_解析后标题` 使用用户标题或安全备用标题；
- 每个账号输出新分享链接、提取码、分享状态；
- 分流模式下非负责账号留空；
- 分享失败不写入假链接；
- 源文件不覆盖，生成带时间戳的新 `.xlsx`。

注意：当前 `OpenXmlResultWriter` 仍是固定列写入器，表头为“源行号、分享链接、状态、账号、目标目录、备注、错误”。需要核对或补齐动态结果写入器，不能只看到 `ResultColumnLayoutBuilder` 就认为导出已经完成。

### 阶段 C：接通桌面操作闭环

任务中心需要让用户能够：

- 查看转存和分享分阶段状态；
- 查看账号级暂停和失败；
- 重试分享失败项；
- 查看结果待确认；
- 点击“另存结果 Excel”。

### 阶段 D：扩大范围

最小闭环通过后，再验证：

- 多账号复制；
- 多账号分流；
- 账号独立关闭分享；
- 已有分享复用/重新生成；
- 崩溃恢复；
- 大批量流式导出。

## 7. 下一窗口第一步命令

```powershell
Set-Location 'C:\Users\45203\Documents\dakazhandian\.worktrees\transfer-share-link-replacement'
git status --short --branch
git log -5 --oneline --decorate
dotnet build --configuration Release
```

然后只读检查：

```powershell
rg -n "UnavailableAdapterAvailability|IAdapterAvailability|ExportBatchResultsService|OpenXmlResultWriter|ResultColumnLayout" src tests
```

不要先合并到 `master`，不要先做 PanTools 外围功能，也不要先重构后端。先完成受控真实闭环和结果 Excel 验收。

## 8. 交接原则

- 以功能工作树为开发现场；
- 现有未提交修改属于用户开发内容，保留并在其上继续；
- 不泄露或写入日志：Cookie、提取码、分享链接、账号 ID、百度用户 ID、响应正文；
- 真实百度操作必须使用用户明确提供的测试账号和无敏感内容链接；
- 任何“已完成/已通过”的结论都必须附带实际命令或现场验收证据；
- 如果真实接口返回无法提供精确资源 ID，保持“结果待确认”，不得按文件名、目录或时间猜测。

## 9. 参考文档

- `docs/superpowers/execution-log.md`
- `docs/superpowers/specs/2026-08-19-baidu-batch-transfer-design.md`
- `docs/superpowers/specs/2026-08-20-transfer-share-link-replacement-design.md`
- `docs/superpowers/plans/2026-08-21-share-pipeline-regression-fixes.md`

## 10. 现场验收的浏览器交接说明

本次尝试连接浏览器时，浏览器运行时返回可用列表为空，因此没有执行任何百度登录、页面操作或凭据读取。

下一窗口继续现场验收时有两种浏览器用途：

### Chrome/Edge 外部浏览器

可用于观察百度官方页面和手工完成一次转存/分享行为，但必须先在 Codex 设置中启用 Computer use/浏览器扩展，并让 Chrome 或 Edge 扩展处于已连接状态。连接后再打开百度网盘页面并扫码。

### 桌面应用自己的 WebView2

这是产品最终真正使用的登录态。Chrome/Edge 的登录状态不会自动复制到桌面应用，不能把浏览器 Cookie 当成桌面应用会话。最终验收必须在桌面应用登录窗口中再扫码一次，确认应用自己的 WebView2 Profile、会话保存和百度请求链路可用。

### 安全边界

- 不要求用户手工复制 Cookie；
- 不读取浏览器 Cookie、密码、本地存储或 Profile 文件；
- 不把 Cookie、完整响应正文、二维码、完整分享链接或提取码写入日志；
- 浏览器连接成功后，先用 1 条无敏感内容链接观察转存和分享，再扩大到 1～3 条测试链接；
- 如果无法从官方行为确认精确远端资源 ID或分享接受状态，继续保持 `NotValidated` 和 `UnavailableAdapterAvailability`，不要猜测接口。

### 下一窗口操作提示

1. 先确认 Chrome/Edge 扩展已连接，或确认可以打开桌面应用的 WebView2 登录窗口；
2. 使用用户自己的测试账号扫码；
3. 准备 1～3 条无敏感内容的百度分享链接；
4. 记录脱敏后的请求形状、状态语义、精确资源 ID 证据、分享分页和最终一致性信息；
5. 先写离线适配器夹具测试，再实现真实适配器；
6. 只有现场验收和离线测试都通过后，才允许切换正式能力开关。

## 11. 2026-08-24 最新交接：Task 11 已完成

本节是最新状态，优先级高于上文第 3～7 节中已经过时的分支、导出器和“下一窗口”描述。

### 11.1 代码与提交状态

功能工作树：

- 路径：`C:\Users\45203\Documents\dakazhandian\.worktrees\transfer-share-link-replacement`
- 分支：`codex/transfer-share-link-replacement`
- HEAD：`eb3a35f feat: add transfer share pipeline workflow`
- `git status --short` 无代码变更；仅出现用户级 Git ignore 文件无读取权限的环境警告。

主工作树仍为：

- 路径：`C:\Users\45203\Documents\dakazhandian`
- 分支：`master`
- HEAD：`6e84ba8 docs: clarify share result headers`
- 本交接文件与 `docs/superpowers/execution-log.md` 当前是主工作树中的未跟踪文件；不要删除、重置或用旧版本覆盖。
- Task 11 尚未合并到 `master`，不要在没有用户新指示时自行合并、推送或创建 PR。

### 11.2 Task 11 已完成内容

- 中文创建向导已接通账号选择、复制/分流、目标目录、分享开关、随机/固定提取码和权威确认摘要。
- 多账号时必须明确选择参与账号；不会因为登录了 10 个账号就自动给全部账号生成结果。
- 创建入口分别校验转存能力和分享能力；分享尚未验收时，用户可关闭分享，仅创建转存任务。
- 已有分享策略在向导中只公开已确认的 `询问，默认复用`，不再展示尚无完整执行语义的其他预设策略。
- 结果页已接通有界分页、刷新、中文状态、分享单独重试、转存/分享恢复、结果对账和另存结果表格。
- 结果动作携带页面快照版本；分享使用 `Generation + UpdatedAt`，转存使用单调 `AttemptCount + UpdatedAt`，旧页面不能操作新代次。
- 已有分享确认支持批次汇总、默认复用、逐账号覆盖、原确认 token、陈旧证据重发现以及程序重启恢复。
- 程序运行时每 2 秒检查一次待确认批次；每个用户主动关闭的批次只自动提示一次，成功保存决定后允许同批次后续新证据再次提示。
- Cancel 决定会在同一 SQLite 事务中把暂停原因改为 `CancelledByUser`；所有新的 `AwaitingExistingShareDecision` 都可重新进入恢复查询。
- 百度转存、分享、发现和对账调用统一经过共享并发门：全局最多 2 个网络操作、同一账号最多 1 个。
- 结果 Excel 走动态替换结果工作簿：一条源资源一行、原链接独立保留、源标题不改、失败行仍保留原链接、新链接只取当前已接受的分享结果、另存一份且不覆盖源文件。
- 导出、分页和异步向导均有批次/代次隔离；旧任务的迟到完成不能覆盖新任务状态。

主要新增入口：

- `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsViewModel.cs`
- `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsView.xaml`
- `src/BaiduBatchTransfer.App/Features/Jobs/ExistingShareDecisionInteraction.cs`
- `src/BaiduBatchTransfer.App/Features/Jobs/ExistingShareDecisionViewModel.cs`
- `src/BaiduBatchTransfer.App/Features/Jobs/ExistingShareDecisionWindow.xaml`
- `src/BaiduBatchTransfer.Application/Jobs/JobResultActions.cs`
- `src/BaiduBatchTransfer.Application/Scheduling/CoordinatedBaiduAdapters.cs`
- `src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteJobRepository.cs`
- `src/BaiduBatchTransfer.Infrastructure/Database/SqliteShareSchedulingStore.cs`
- `src/BaiduBatchTransfer.Infrastructure/Database/SqliteTransferSchedulingStore.cs`

### 11.3 最新验证证据

本次 Task 11 收尾实际执行并通过：

- App.Tests：`187/187`；
- Application.Tests：`477/477`；
- Infrastructure.Tests（排除下述两个严格耗时阈值用例）：`339/339`；
- ShareSchedulingStoreTests：`56/56`；
- Core.Tests：同一轮全方案验证中 `212/212`；
- BaiduAdapter.Tests：同一轮全方案验证中 `33/33`；
- Debug 全方案构建：0 警告、0 错误；
- Release 全方案构建：0 警告、0 错误；
- `git diff --cached --check`：通过；
- 两个独立审阅模块最终均给出 `READY`。

两个既有的大规模耗时阈值仍未通过，不能描述为全套测试完全绿色：

- `PreparedStreamingWritesHandleTwoHundredThousandResourcesAndThreeReplicasWithinBoundedMemory`：无并发复跑约 `37.06s`，超过仓库固定耗时门槛；输出的托管内存增长约 `12.7 MB`。
- `ShareWritesStreamSixHundredThousandOperationsInBoundedChunksAndMemory`：无并发复跑约 `3m38.87s`，超过仓库固定耗时门槛；输出的托管内存增长约 `14.0 MB`。
- 两项失败均发生在 elapsed-time 断言；本轮没有修改它们的批量写入核心路径，但后续仍需单独做性能分析，不能直接忽略或提高阈值。

### 11.4 当前安全门禁与下一步边界

Task 12 尚未开始。真实百度适配器仍有意保持失败关闭：

- `IAdapterAvailability` 使用 `UnavailableAdapterAvailability`；
- 结果页转存/分享能力使用 `UnavailableJobResultCapabilityAvailability`；
- 应用层结果动作使用 `UnavailableJobResultActionCapability`；
- 未完成用户自有测试账号的现场验收前，不得把上述门禁切换为可用。

下一阶段是计划中的 Task 12：实现并现场验收官方百度转存/分享适配器。它需要用户明确指示，并准备用户自有测试账号与 1～3 条无敏感内容测试链接。现场验收前继续遵守：

1. 不读取、复制或记录 Cookie、二维码、密码、完整响应正文、完整分享链接或提取码；
2. 必须确认真实转存返回的精确远端资源 ID；
3. 必须验证永久有效、无限访问、随机/固定提取码及 eventual consistency；
4. 先把脱敏行为写成离线 HTTP 夹具测试，再实现和启用能力；
5. 任一证据不明确时继续保持门禁禁用，不按名称、路径、时间或 UI 成功文字猜测。

### 11.5 当前停止点

按用户指示，交接文件写完后停止。不要继续 Task 12、不要启动现场百度操作、不要合并分支、不要推送；等待用户下一条明确指示。
