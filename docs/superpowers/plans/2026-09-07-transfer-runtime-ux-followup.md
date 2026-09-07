# 批量转存运行体验补充实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**目标：** 修复自动分享链接不带提取码、日志不跟随、任务结束无提示、待处理状态不变化及批量粘贴入口过小的问题，并生成可供用户重新测试的 Windows x64 包。

**架构：** 在浏览器辅助程序的唯一分享提交边界前真实选择并验证“分享链接自动填充提取码”；在分享结果写入数据库前统一规范化分享 URL。任务生命周期由运行面板发出一次性终态事件，转存工作台负责提示、日志和待处理行的展示；逐条结果通过持久化的导入行 ID 与待处理行精确关联。

**技术栈：** .NET 8、WPF、CommunityToolkit.Mvvm、SQLite、xUnit、Python unittest、Node.js DOM 测试工具。

---

## 文件职责与改动边界

- `tools/managed_browser_helper/share_submission.py`：在唯一创建分享动作前选择并验证百度的自动填码选项。
- `tools/managed_browser_helper/tests/share_submission_harness.cjs`：用合成 DOM 验证复选框设置、事件和零误提交。
- `tools/managed_browser_helper/tests/test_share_submission.py`：验证 Python 提交边界和固定脚本协议。
- `src/BaiduBatchTransfer.Core/Domain/BaiduShareLink.cs`：唯一负责把已确认提取码安全写入百度分享 URL。
- `src/BaiduBatchTransfer.Application/Scheduling/ShareOperationExecutor.cs`、`src/BaiduBatchTransfer.Application/Scheduling/ShareOperationReconciliationService.cs`：在成功结果写入数据库前调用 URL 规范化规则。
- `tests/BaiduBatchTransfer.Application.Tests/Scheduling/ShareOperationExecutorTests.cs`、`tests/BaiduBatchTransfer.Application.Tests/Scheduling/ShareReconciliationTests.cs`：覆盖新建、复用和核对成功结果的完整链接。
- `src/BaiduBatchTransfer.App/Features/Common/WorkspaceRunPanelViewModel.cs`：检测活动批次进入终态并只发出一次通知事件。
- `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`：显示完成提示、写日志、切换结果页并维护待处理状态。
- `src/BaiduBatchTransfer.App/AppBootstrap.cs`：把现有 `IDialogService` 传入正式转存工作台组合根。
- `src/BaiduBatchTransfer.App/Features/Transfers/PendingRowRuntimeStatus.cs`：按导入行和多账号结果确定性汇总展示状态。
- `src/BaiduBatchTransfer.App/Features/Jobs/WizardContracts.cs`：给导入预览行增加独立的运行时展示状态，不改原始导入判断。
- `src/BaiduBatchTransfer.Core/Abstractions/PersistenceModels.cs`、`src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteJobRepository.cs`、`src/BaiduBatchTransfer.Application/Jobs/JobPageQuery.cs`、`src/BaiduBatchTransfer.App/Features/Jobs/JobResultsContracts.cs`、`src/BaiduBatchTransfer.App/Features/Jobs/LocalJobResultsWorkflow.cs`：把 `StagedImportRowId` 和原始分享状态传到界面结果行。
- `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`：显示多行粘贴框并命名日志列表。
- `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml.cs`：加载时订阅日志集合、卸载时解除并滚动到最新一条。
- `tests/BaiduBatchTransfer.App.Tests/Features/WorkspaceRunPanelViewModelTests.cs`、`tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`：覆盖完成事件、提示、结果页切换、行状态聚合和界面结构。
- `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/RepositoryTests.cs`、`tests/BaiduBatchTransfer.Application.Tests/Jobs/PagingAndStatisticsTests.cs`：覆盖导入行 ID 与原始分享状态从数据库到界面的投影。

### 任务 0：保存本轮目标文件的脏工作树基线

**文件：**
- 新建：`.artifacts/runtime-ux-followup-20260907/baseline/**`

- [ ] **步骤 1：确认工作树和目标工作树路径**

运行 `git rev-parse --show-toplevel`、`git status --short`，确认只在 `transfer-share-link-replacement` 工作树操作。不得执行 reset、restore、clean 或覆盖已有工件。

- [ ] **步骤 2：复制全部既有目标文件作为只读基线**

在唯一的新目录 `.artifacts/runtime-ux-followup-20260907/baseline` 中按仓库相对路径复制本计划列出的每个既有目标文件，并记录各文件 SHA-256。新建文件单独列入清单。基线目录若已存在则停止并改用新的唯一后缀，不覆盖旧基线。

- [ ] **步骤 3：保存 Git 差异证据**

使用 `git diff --binary --output=.artifacts/runtime-ux-followup-20260907/preexisting.patch` 保存修改前差异，并把修改前 `git status --short` 输出记录到测试交接文档草稿。后续审查以“基线副本对当前文件”的差异为本轮权威，不把原有用户修改误算成本轮修改。

- [ ] **步骤 4：提交约束**

共享的脏文件不做整文件提交；只允许提交全新的计划、测试或源文件，或者能证明只包含本轮新增内容的精确暂存补丁。无法安全拆分时保持未提交，并在最终交接中明确列出。

### 任务 1：百度创建分享时真实启用自动填充提取码

**文件：**
- 修改：`tools/managed_browser_helper/tests/share_submission_harness.cjs`
- 修改：`tools/managed_browser_helper/tests/test_share_submission.py`
- 修改：`tools/managed_browser_helper/share_submission.py`
- 修改：`tools/managed_browser_helper/helper-manifest.json`

- [ ] **步骤 1：先写失败的 DOM 测试**

在合成分享窗口中加入文本为“分享链接自动填充提取码”的真实 `input[type=checkbox]`，断言：初始未选中时脚本先把它选中并派发 `input`/`change`，然后才允许创建分享按钮被点击一次；缺失、重复、禁用或设置后仍未选中时，结果必须为 `KnownNotDispatched` 且创建按钮点击次数为零。测试还要证明调用参数中不存在用于关闭该规则的布尔值或调用方选择器。

- [ ] **步骤 2：运行测试确认按预期失败**

运行：

```powershell
python -m unittest tools.managed_browser_helper.tests.test_share_submission -v
```

预期：新增断言失败，原因是当前脚本没有查找或选择自动填码复选框。

- [ ] **步骤 3：实现最小的固定复选框选择逻辑**

在 `SUBMIT_SHARE_ONCE_SCRIPT` 内使用固定中文标签和 `input[type=checkbox]` 语义查找，不接受调用方传入选择器或开关。使用原生 `checked` setter，派发同步 `input` 与 `change` 事件；第二次 `proof()` 必须证明同一个复选框仍为选中状态，然后才跨越唯一创建分享按钮边界。复选框验证失败仍返回提交前失败，不得点击创建按钮。

- [ ] **步骤 4：运行测试确认通过**

运行：

```powershell
python -m unittest tools.managed_browser_helper.tests.test_share_submission -v
python -m unittest discover -s tools/managed_browser_helper/tests -v
```

预期：全部通过，创建分享按钮在成功场景只点击一次，所有失败场景为零次。

- [ ] **步骤 5：记录本任务改动**

计算最终 `share_submission.py` 的 UTF-8 文件长度和 SHA-256，只更新清单中该文件的条目，然后按构建脚本的固定顺序重新计算 `buildIdentity`。清单中其他文件的长度和哈希不得变化。由于工作树已有用户改动，不得用整树 `git add`，不得覆盖或清理既有修改。

### 任务 2：在结果持久化边界生成自带提取码的完整分享链接

**文件：**
- 新建：`src/BaiduBatchTransfer.Core/Domain/BaiduShareLink.cs`
- 修改：`src/BaiduBatchTransfer.Application/Scheduling/ShareOperationExecutor.cs`
- 修改：`src/BaiduBatchTransfer.Application/Scheduling/ShareOperationReconciliationService.cs`
- 新建：`tests/BaiduBatchTransfer.Core.Tests/BaiduShareLinkTests.cs`
- 修改：`tests/BaiduBatchTransfer.Application.Tests/Scheduling/ShareOperationExecutorTests.cs`
- 修改：`tests/BaiduBatchTransfer.Application.Tests/Scheduling/ShareReconciliationTests.cs`
- 修改：`tests/BaiduBatchTransfer.Application.Tests/Jobs/ReplacementWorkbookExportTests.cs`

- [ ] **步骤 1：先写失败的核心领域测试**

覆盖以下行为：基础链接加上 `?pwd=a1B2`；已有其他查询参数时使用 `&pwd=`；保留 `#fragment`；已有相同 `pwd` 时不重复；已有不同 `pwd` 时拒绝；无效 HTTPS URL 或无效四位提取码时拒绝。异常消息不得泄露提取码。

- [ ] **步骤 2：运行核心测试确认失败**

运行：

```powershell
dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj --no-restore --filter FullyQualifiedName~BaiduShareLinkTests
```

预期：失败，原因是当前没有可信的分享 URL 规范化器。

- [ ] **步骤 3：实现 URL 规范化器并接入结果持久化**

`BaiduShareLink` 只接受 HTTPS 绝对 URL 和四位字母数字提取码；按查询参数而不是字符串拼接处理 `pwd`，保留其他参数和 fragment。`ShareOperationExecutor` 的新建结果与复用已有分享结果，以及 `ShareOperationReconciliationService` 的回执和候选接受路径，都必须在写入 `BaiduShareResultRecord` 前调用规范化器；独立 `ExtractionCode` 字段保持不变。适配器回执仍保留百度原始返回值，避免无关层的全局行为变化。

规范化失败必须走现有状态机，不能让异常逸出后把操作遗留在 `Submitting`：新建分享已经跨越提交边界，因此 URL/code 不一致时调用现有 `MarkPendingAsync`，进入 `ResultPending` 等待核对；核对流程中发现不一致时继续调用 `KeepPendingAsync`，不能误判未提交；纯粹复用已有分享尚未创建新分享，属于安全的提交前候选问题，调用现有失败路径直接标记 `Failed`，错误码使用固定非敏感值且不写入提取码。

- [ ] **步骤 4：先增加再运行持久化及导出回归测试**

执行器和核对服务测试断言新建、复用、核对成功结果写入的 URL 都包含匹配的 `pwd`。额外断言：新建分享的不一致结果最终为 `ResultPending`；核对中的不一致结果继续保持待核对；尚未提交的安全复用候选不一致时最终为 `Failed`；三者都不能留下 `Submitting`。`ReplacementWorkbookExportTests` 断言分享链接列为带 `pwd` 的可分发 URL，同时提取码列仍为独立四位值。

运行：

```powershell
dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj --no-restore --filter FullyQualifiedName~BaiduShareLinkTests
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~ShareOperationExecutorTests|FullyQualifiedName~ShareReconciliationTests|FullyQualifiedName~ReplacementWorkbookExportTests"
```

预期：全部通过。只有结果持久化边界及其直接测试发生行为变化，适配器回执不需要整体改写。

- [ ] **步骤 5：记录本任务改动**

只记录本任务列出的文件，保留工作树中原有未提交修改。

### 任务 3：批次结束后只提示一次并写入日志

**文件：**
- 修改：`src/BaiduBatchTransfer.App/Features/Common/WorkspaceRunPanelViewModel.cs`
- 修改：`src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- 修改：`src/BaiduBatchTransfer.App/AppBootstrap.cs`
- 修改：`src/BaiduBatchTransfer.Core/Abstractions/PersistenceModels.cs`
- 修改：`src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteJobRepository.cs`
- 修改：`src/BaiduBatchTransfer.App/Features/Jobs/JobResultsContracts.cs`
- 修改：`src/BaiduBatchTransfer.App/Features/Jobs/JobResultsViewModel.cs`
- 修改：`src/BaiduBatchTransfer.App/Features/Jobs/LocalJobResultsWorkflow.cs`
- 修改：`tests/BaiduBatchTransfer.Infrastructure.Tests/Database/RepositoryTests.cs`
- 修改：`tests/BaiduBatchTransfer.Application.Tests/Jobs/PagingAndStatisticsTests.cs`
- 修改：`tests/BaiduBatchTransfer.App.Tests/Features/WorkspaceRunPanelViewModelTests.cs`
- 修改：`tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`
- 修改：`tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`

- [ ] **步骤 1：先写失败的准确完成统计测试**

扩展批次统计但保留现有转存 `FailedCount` 语义，新增“完整成功操作数”和“终态问题操作数”：转存成功且分享状态为 `Succeeded` 或 `ReusedExisting` 才算完整成功；转存失败/取消或分享失败/取消计入问题数。分享未启用时，转存成功即可算完整成功。分别覆盖分享失败和复用已有分享，防止完成弹窗把分享失败误报为成功。

- [ ] **步骤 2：运行统计测试确认失败**

运行：

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~RepositoryTests
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter FullyQualifiedName~PagingAndStatisticsTests
```

预期：新增统计断言失败，因为当前汇总只有转存失败数。

- [ ] **步骤 3：实现兼容的完成统计投影**

在 `AccountBatchStatisticsRecord` 和 `JobResultsSummary` 尾部增加可选的完整成功数、终态问题数；SQL 通过每个转存操作唯一关联的 `ShareOperations` 和 `BatchOutputAccounts.ShareEnabled` 计算。`JobResultsViewModel` 暴露只读统计，原有 `FailedCount`、`PausedCount` 和界面含义保持不变。

- [ ] **步骤 4：先写失败的运行面板测试**

新增 `WorkspaceBatchTerminalEvent`，测试活动批次从 `Queued`/`Active` 进入 `Completed`、`CompletedWithShortfall` 或 `Cancelled` 后发出一次；连续刷新同一终态不重复；初始化时已经是终态的历史批次不发出；`Paused`、`AwaitingCandidates`、`PausedReady` 不视为结束。

- [ ] **步骤 5：运行测试确认失败**

运行：

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter FullyQualifiedName~WorkspaceRunPanelViewModelTests
```

预期：新增测试失败，原因是当前自动刷新只替换状态，没有生命周期事件。

- [ ] **步骤 6：实现一次性终态事件，即使统计读取失败也必须通知**

运行面板保留会话内已通知批次 ID，并在覆盖快照前判断是否发生了“非终态 → 终态”的真实跃迁。必须先应用最新终态快照和工作台状态，再尝试刷新结果统计，不能让统计读取挡住终态落地。

为 `JobResultsViewModel` 增加供生命周期刷新调用、返回成功/失败结果的 `RefreshForLifecycleAsync`（现有刷新命令行为保持不变）。`WorkspaceBatchTerminalEvent` 中的完整成功数和终态问题数使用可空值：统计刷新成功时带上数值；统计读取异常或失败时仍然在 `finally`/等价的无条件路径发出一次终态事件，只把两个统计值留空。历史终态与重复刷新均不触发，同一批次也不能因为先无统计、后有统计而再次提示。

- [ ] **步骤 7：先写失败的工作台提示测试**

给测试替身 `FakeDialog` 记录 `ShowMessageAsync` 调用。断言事件到达后：只显示一次“转存任务已完成”提示，统计可用时内容含完成数和失败数；统计读取失败时终态仍已应用、提示与日志仍各出现一次，文案明确写“统计暂未读取”；同一批次后续刷新不得补弹第二次。关闭工作台后不再提示；弹窗异常不能影响状态和结果。

- [ ] **步骤 8：实现提示和安全订阅**

通过现有 `IDialogService` 注入转存工作台；构造时订阅终态事件，关闭时解除。`AppBootstrap.cs` 的正式组合根必须传入已经注册的同一个 `IDialogService`，并增加组合测试证明正式应用可构造。事件处理先写安全日志，再异步显示一次信息弹窗，并吞掉纯界面提示异常，不能影响后台执行。

- [ ] **步骤 9：运行测试确认通过**

运行：

```powershell
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkspaceRunPanelViewModelTests|FullyQualifiedName~TransferWorkspaceViewModelTests"
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter FullyQualifiedName~AppBootstrapTests
```

预期：全部通过。

### 任务 4：让待处理行准确显示批次阶段和多账号汇总结果

**文件：**
- 修改：`src/BaiduBatchTransfer.Core/Abstractions/PersistenceModels.cs`
- 修改：`src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteJobRepository.cs`
- 修改：`src/BaiduBatchTransfer.Application/Jobs/JobPageQuery.cs`
- 修改：`src/BaiduBatchTransfer.App/Features/Jobs/JobResultsContracts.cs`
- 修改：`src/BaiduBatchTransfer.App/Features/Jobs/LocalJobResultsWorkflow.cs`
- 修改：`src/BaiduBatchTransfer.App/Features/Jobs/WizardContracts.cs`
- 新建：`src/BaiduBatchTransfer.App/Features/Transfers/PendingRowRuntimeStatus.cs`
- 修改：`src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- 修改：`src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- 修改：`tests/BaiduBatchTransfer.Infrastructure.Tests/Database/RepositoryTests.cs`
- 修改：`tests/BaiduBatchTransfer.Application.Tests/Jobs/PagingAndStatisticsTests.cs`
- 修改：`tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`

- [ ] **步骤 1：先写失败的结果投影测试**

创建带 `StagedImportRowId` 的资源、转存和分享记录，断言仓储 `OperationResultRecord`、应用层 `OperationPageItem` 及界面 `JobResultRow` 均保留同一个导入行 ID，并保留原始 `ShareStatus`，不能只留下中文显示文字。导入预览来源同时把原始 `Disposition` 传给 `ImportPreviewRow`，界面不能通过“就绪”等中文文案猜测一行是否参与任务。

- [ ] **步骤 2：运行投影测试确认失败**

运行：

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~RepositoryTests
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter FullyQualifiedName~PagingAndStatisticsTests
```

预期：新增断言失败，原因是当前操作结果没有把 `r.StagedImportRowId` 和原始分享状态传到界面记录。

- [ ] **步骤 3：实现最小的逐层投影**

SQL 从 `ResourceItems` 读取 `StagedImportRowId`；在记录类型尾部增加可空字段以避免破坏现有调用；`LocalJobResultsWorkflow` 原样映射。`ImportPreviewPageSource` 把数据库原始 `Disposition` 写入预览行新增的只读字段。不得更改数据库结构或原始导入数据。

- [ ] **步骤 4：先写失败的纯状态汇总测试**

按同一导入行 ID 和 `CandidateSequence` 聚合，覆盖：

- 任务刚创建：`已进入任务`；
- 任一操作仍排队、准备或等待分享：`处理中`；
- 复制模式下所有参与账号的转存均成功，且分享状态均为 `Succeeded` 或 `ReusedExisting`：`已完成`；
- 成功加失败、成功加取消、成功加暂停、成功加 `ResultPending`/需核对：`有问题，请查看结果`；
- 已结束但明细数量不足：`任务已结束，请查看结果`。

- [ ] **步骤 5：实现 `PendingRowRuntimeStatus`**

该类型只接收结果快照、每条资源预期操作数和批次是否终态，返回固定中文展示状态。判断使用原始状态字段，不解析中文文案。`ShareOperationStatus.Succeeded` 与 `ShareOperationStatus.ReusedExisting` 都属于分享完成。失败或不确定优先于运行，运行优先于成功；只有明细数量达到预期且全部转存、分享完成时才返回“已完成”。

- [ ] **步骤 6：把展示状态接入待处理表格**

`ImportPreviewRow` 增加原始 `Disposition`、实现 `INotifyPropertyChanged` 的 `RuntimeStatusText` 与只读 `DisplayDispositionText`；原 `DispositionText` 保持不变。任务创建后只给 `Disposition == "Ready"` 且被本批次纳入的当前可见记录设置“已进入任务”，明确把 `SelectedDataTabIndex` 设为 `1`；结果集合刷新时按导入行 ID 更新；批次终态时将缺少精确明细的参与行设为“任务已结束，请查看结果”。XAML 的“处理状态”绑定改为 `DisplayDispositionText`。

- [ ] **步骤 7：运行状态相关测试确认通过**

运行：

```powershell
dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~RepositoryTests
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter FullyQualifiedName~PagingAndStatisticsTests
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter FullyQualifiedName~TransferWorkspaceViewModelTests
```

预期：全部通过，并且原始导入判断仍未被改写。

### 任务 5：显示真正的多行批量粘贴框并让日志自动滚动

**文件：**
- 修改：`src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- 修改：`src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml.cs`
- 修改：`tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`
- 修改：`tests/BaiduBatchTransfer.Application.Tests/Imports/TextImportParserTests.cs`

- [ ] **步骤 1：先写失败的界面结构测试**

断言粘贴输入框具有足够的多行高度、`AcceptsReturn=True`、自动换行、垂直滚动条和中文提示；日志列表有固定名称。代码侧测试或结构测试必须证明加载时只订阅一次、卸载时解除订阅，并在新增日志后对最后一项调用 `ScrollIntoView`。

- [ ] **步骤 2：增加批量文本解析回归测试并确认现状**

用多行“标题 + 百度链接 + 提取码”输入断言一次导入得到多条记录，单链接仍得到一条。先运行解析测试，若它已通过，则记录为既有能力回归保护，不重复实现解析器。

运行：

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter FullyQualifiedName~TextImportParserTests
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter FullyQualifiedName~TransferWorkspaceViewModelTests
```

预期：解析器测试通过，新增界面结构或滚动测试失败。

- [ ] **步骤 3：实现多行输入外观**

把工具栏首行扩展为适合多行输入的高度；输入框顶部对齐、自动换行并显示垂直滚动条，保留现有 `Import.TextContent` 和一次点击 `ImportTextCommand`，不新增逐条按钮。

- [ ] **步骤 4：实现日志跟随控制**

给日志 `ListBox` 命名。视图 `Loaded` 时订阅 `Logs.CollectionChanged`，使用布尔状态防止重复订阅；`Unloaded` 时解除。新增日志后通过 Dispatcher 异步调用 `ScrollIntoView(Logs[^1])`。空集合、重置、视图卸载或滚动异常均安全忽略。

- [ ] **步骤 5：运行界面和导入测试确认通过**

运行：

```powershell
dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter FullyQualifiedName~TextImportParserTests
dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter FullyQualifiedName~TransferWorkspaceViewModelTests
```

预期：全部通过。

### 任务 6：完整验证并生成新的测试包

**文件：**
- 修改：`tools/managed_browser_helper/helper-manifest.json`
- 新建：`.artifacts/runtime-ux-followup-20260907/helper-bundle-reviewed-r1/**`
- 修改：`docs/testing/2026-09-07-transfer-runtime-ux-followup.md`
- 新建：`.artifacts/distribution/SuperPan-1.0.9-runtime-ux-win-x64.zip`

- [ ] **步骤 1：生成唯一的新 helper bundle**

先确认任务 1 后所有 helper 源码均已冻结，`helper-manifest.json` 与最终源码长度、SHA-256、`buildIdentity` 一致。使用不存在的唯一输出目录；如果 `r1` 已存在，停止并改用 `r2`，不得覆盖。

```powershell
$worktree = 'C:\Users\45203\Documents\dakazhandian\.worktrees\transfer-share-link-replacement'
$bundle = Join-Path $worktree '.artifacts\runtime-ux-followup-20260907\helper-bundle-reviewed-r1'
$cache = Join-Path $worktree '.artifacts\managed-browser-helper-cache'
if (Test-Path -LiteralPath $bundle) { throw 'helper bundle 已存在，请改用新的唯一 r2 目录' }
& (Join-Path $worktree 'scripts\build_managed_browser_helper.ps1') -DistributionMode PersonalNonCommercial -Offline -OutputDirectory $bundle -PackageCacheDirectory $cache
```

预期：输出新的 BuildIdentity；若源文件后来再次改变，保留 r1 并重新生成唯一的 r2。实际选定 r1 或 r2 后，立即把该绝对路径记录到测试文档；下面所有门禁、测试、构建和发布命令统一复用这个已记录的确切路径，不能再硬编码回 r1，也不能自动挑选“最新”目录。

- [ ] **步骤 2：使用完整参数执行 helper 完整性门禁**

```powershell
$worktree = 'C:\Users\45203\Documents\dakazhandian\.worktrees\transfer-share-link-replacement'
$bundle = '<从测试文档复制实际选定的 helper bundle 绝对路径>'
& (Join-Path $worktree 'scripts\verify_managed_browser_helper_package.ps1') `
  -ManifestSource (Join-Path $worktree 'tools\managed_browser_helper\helper-manifest.json') `
  -PackageDirectory $bundle `
  -HelperSourceDirectory (Join-Path $worktree 'tools\managed_browser_helper')
```

预期：清单、源码和 bundle 的全部长度及哈希一致。

- [ ] **步骤 3：运行辅助程序完整测试**

```powershell
python -m unittest discover -s tools/managed_browser_helper/tests -v
```

预期：全部通过。

- [ ] **步骤 4：运行全部 .NET 测试**

```powershell
$bundle = '<从测试文档复制实际选定的 helper bundle 绝对路径>'
dotnet test BaiduBatchTransfer.sln --no-restore -c Release -p:ManagedBrowserHelperPackageDirectory=$bundle
```

预期：全部通过，零失败。

- [ ] **步骤 5：执行 Release 构建**

```powershell
$bundle = '<从测试文档复制实际选定的 helper bundle 绝对路径>'
dotnet build BaiduBatchTransfer.sln --no-restore -c Release -p:ManagedBrowserHelperPackageDirectory=$bundle
```

预期：构建零错误；构建目标会再次使用完整参数执行 helper 门禁。

- [ ] **步骤 6：生成不覆盖旧包的新版本便携包**

使用正式包 ID、`win-x64-stable` 渠道和版本 `1.0.9`，但不上传 OTA。先用锁定模式确认 self-contained `win-x64` 依赖，再发布到全新目录并压缩：

```powershell
$worktree = 'C:\Users\45203\Documents\dakazhandian\.worktrees\transfer-share-link-replacement'
$bundle = '<从测试文档复制实际选定的 helper bundle 绝对路径>'
$publish = Join-Path $worktree '.artifacts\runtime-ux-followup-20260907\publish-1.0.9'
$zip = Join-Path $worktree '.artifacts\distribution\SuperPan-1.0.9-runtime-ux-win-x64.zip'
if ((Test-Path -LiteralPath $publish) -or (Test-Path -LiteralPath $zip)) { throw '1.0.9 输出已存在，请使用新的版本或唯一目录' }
dotnet restore src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj --locked-mode -r win-x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true
dotnet publish src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o $publish -p:VersionPrefix=1.0.9 -p:PackageId=SuperPan.BaiduBatchTransfer -p:UpdateChannel=win-x64-stable -p:ManagedBrowserHelperPackageDirectory=$bundle
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
```

确认发布目录内的应用版本为 `1.0.9`、`managed-browser-helper/helper-manifest.json` 与源码清单字节一致。不得上传 OTA，不得覆盖 `1.0.8` 包。

- [ ] **步骤 7：记录验证证据**

文档写明测试数量、Release 构建结果、ZIP 绝对路径、大小和 SHA-256；明确真实百度账号的最终分享复选框与完整链接仍需用户在新包上做一次现场验收。

- [ ] **步骤 8：最终差异审查**

```powershell
git diff --check
git status --short
```

除 `git diff` 外，逐一用任务 0 的基线副本与当前目标文件比较，并核对新建文件清单。工作树中已有的其他修改必须原样保留，不得清理、还原或纳入无关提交。
