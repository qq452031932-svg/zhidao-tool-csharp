# Two-Workspace WebView Automation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将现有百度批量转存程序改成只有“转存”和“分享”两个工作区，并用可见、可人工接管的 WebView2 完成真实转存、精确资源分享和本机结果导出。

**Architecture:** 保留现有导入、账号隔离、分配、调度、提交边界、幂等、崩溃恢复和动态结果列模型，在应用层增加两个面向用户的组合工作区。真实百度操作实现为现有 `IBaiduNetdiskAdapter` / `IBaiduShareAdapter` 的 WebView2 适配器；所有写操作由可见网页点击触发，稳定标识、提交基线和结果证据继续交给现有调度状态机持久化，无法唯一确认时暂停而不重试。

**Tech Stack:** .NET / C#、WPF、CommunityToolkit.Mvvm、Microsoft WebView2、SQLite、Open XML SDK、xUnit。

---

## 实施前边界

- 工作目录使用 `C:\Users\45203\Documents\dakazhandian\.worktrees\transfer-share-link-replacement`，分支为 `codex/transfer-share-link-replacement`。
- 先保留并接管当前未提交的验收探针文件；不要删除或覆盖用户已有改动。
- `D:\自动分享` 只作只读行为参考。不得复制其中的卡密、密钥、Cookie、配置、日志、外部 Chrome 调试端口或 Python 运行时。
- 真实账号、验证码和线上文件的现场验证只能在用户在场时执行；自动测试只使用脱敏 HTML/DOM 夹具和假 WebView 会话。
- 每项任务按 RED → GREEN → REFACTOR 执行。除计划明确列出的现场验收外，不以“点击没报错”作为成功证据。

## 文件职责映射

### 保留并复用

- `Features/Imports/*`：文本和 Excel 导入、字段映射、行修正。
- `Features/Jobs/CreateJobWizardViewModel.cs` 及 `Application/Jobs/*`：批次确认、账号分配和调度参数；界面隐藏但服务继续复用。
- `Application/Scheduling/OperationExecutor.cs`：转存提交边界、待确认和安全重试。
- `Application/Scheduling/ShareOperationExecutor.cs`：分享基线、提交代次、结果待确认和复用已有分享。
- `Infrastructure/Database/Migrations/008_TransferShareLinkReplacement.sql`：现有转存/分享精确关系和动态结果模型。
- `Infrastructure/Excel/*Replacement*`：转存并自动分享的动态账号列输出。
- `Infrastructure/Security/*` 与账号登录窗口：每账号独立 Profile 和本机加密会话。

### 新增或替换

- `Features/Transfers/*`：一个页面的转存工作区，组合现有导入和批次服务。
- `Features/Shares/*`：自己网盘选资源、逐项分享和汇总导出的分享工作区。
- `Features/BrowserAutomation/*`：全局可见浏览器队列、页面窗口、人工接管、下载协调。
- `BaiduAdapter/WebAutomation/*`：不依赖 WPF 的页面状态、候选定位器和动作协议。
- `Infrastructure/Database/Migrations/009_WebViewAutomationAndStandaloneShare.sql`：补充只分享、结果目的地、导出恢复和浏览器阶段/证据，不复制已有调度事实。
- `Infrastructure/Excel/OpenXmlStandaloneShareResultWriter.cs`：只分享批次的一行一资源汇总文件。

---

### Task 1: 固化并修正当前 Debug 验收探针

**Files:**
- Modify: `src/BaiduBatchTransfer.App/App.xaml.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Acceptance/BaiduAcceptanceProbeLauncher.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Acceptance/BaiduAcceptanceProbeSession.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Acceptance/BaiduAcceptanceProbeWindow.xaml.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/BaiduAcceptanceProbeAccountSelectorTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/BaiduAcceptanceProbeSessionTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/BaiduAcceptanceProbeLauncherTests.cs`

- [ ] **Step 1: 先记录并保护工作树现状**

Run: `git status --short`

Expected: 只把当前 `Features/Acceptance/*`、`App.xaml.cs`、`AppBootstrap.cs` 和三个验收探针测试视为本任务已有改动；不要清理其他文件。

- [ ] **Step 2: 写出“带开关时必须真正展示窗口”的失败测试**

在 `BaiduAcceptanceProbeLauncherTests.cs` 使用假账号、假窗口工厂和假输出目录：

```csharp
[Fact]
public async Task LaunchAsync_WhenEnabledAndAccountExists_ShowsProbeWindow()
{
    var presenter = new RecordingProbePresenter();
    var launcher = Fixture.CreateLauncher(presenter, accounts: [Fixture.Account]);

    var result = await launcher.LaunchAsync(enabled: true, CancellationToken.None);

    Assert.True(result.Started);
    Assert.Equal(Fixture.Account.Id, presenter.ShownForAccountId);
}
```

另加无账号、Release/未启用、窗口创建失败和输出目录不可写测试，失败信息只能含稳定错误码。

- [ ] **Step 3: 运行测试确认先失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~BaiduAcceptanceProbe"`

Expected: 新启动器测试 FAIL，并准确暴露此前“程序启动但探针没出现”的入口、账号选择或窗口展示原因。

- [ ] **Step 4: 最小修正启动和窗口展示边界**

把是否启动、账号选择、窗口展示拆成可测试依赖；仅 Debug 且显式命令行开关时展示，普通启动和 Release 均不得自动弹出。探针只读取页面状态和保存脱敏证据，不执行转存/分享写操作。

- [ ] **Step 5: 验证探针测试与 Debug/Release 构建**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~BaiduAcceptanceProbe"`

Expected: PASS。

Run: `dotnet build BaiduBatchTransfer.sln -c Debug -warnaserror`

Expected: Build succeeded，0 warnings。

Run: `dotnet build BaiduBatchTransfer.sln -c Release -warnaserror`

Expected: Build succeeded，0 warnings，Release 不包含自动探针启动路径。

- [ ] **Step 6: 提交探针基础**

```powershell
git add src/BaiduBatchTransfer.App/App.xaml.cs src/BaiduBatchTransfer.App/AppBootstrap.cs src/BaiduBatchTransfer.App/Features/Acceptance tests/BaiduBatchTransfer.App.Tests/Features/BaiduAcceptance*
git commit -m "feat: add safe webview acceptance probe"
```

---

### Task 2: 把主导航收敛为“转存 / 分享”

**Files:**
- Modify: `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`

- [ ] **Step 1: 写导航失败测试**

```csharp
[Fact]
public void Shell_ExposesOnlyTransferAndShareWorkspaces()
{
    var vm = Fixture.CreateShell();

    Assert.Equal("转存", vm.CurrentWorkspaceTitle);
    vm.ShowShareCommand.Execute(null);
    Assert.Equal("分享", vm.CurrentWorkspaceTitle);
    Assert.False(vm.GetType().GetProperties().Any(p => p.Name.Contains("CreateJob")));
}
```

同时添加 XAML 文本断言：左侧不再出现“导入与草稿”“创建任务”“任务结果”“批量预检”。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~AppBootstrapTests"`

Expected: FAIL，仍暴露三工作区和旧命令。

- [ ] **Step 3: 最小替换 Shell 导航**

`MainWindowViewModel` 只接收 `transferWorkspace`、`shareWorkspace`，只公开 `ShowTransferCommand`、`ShowShareCommand`。账号登录、管理和刷新仍留在右上角；进度和历史由两个工作区内部展示。

- [ ] **Step 4: 运行测试并提交**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~AppBootstrapTests"`

Expected: PASS。

```powershell
git add src/BaiduBatchTransfer.App/MainWindow* src/BaiduBatchTransfer.App/AppBootstrap.cs tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs
git commit -m "feat: expose transfer and share workspaces"
```

---

### Task 3: 组合单页“转存”工作区

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceOptions.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Common/WorkspaceRunPanelViewModel.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/PauseBatchService.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/ResumeBatchService.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/RetryFailedTransferService.cs`
- Modify: `src/BaiduBatchTransfer.Core/Abstractions/IUnitOfWork.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteUnitOfWork.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteTransferSchedulingStore.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Imports/ImportViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardViewModel.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/WorkspaceRunPanelViewModelTests.cs`
- Create: `tests/BaiduBatchTransfer.Application.Tests/Jobs/BatchRunControlServiceTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ImportViewModelTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/CreateJobWizardViewModelTests.cs`

- [ ] **Step 1: 写转存页面规则的失败测试**

```csharp
[Theory]
[InlineData(false, null, true)]
[InlineData(true, null, false)]
[InlineData(true, @"C:\results", true)]
public void CanStart_RequiresExportFolderOnlyForAutoShare(
    bool autoShare, string? exportFolder, bool expected)
{
    var vm = Fixture.ValidTransferWorkspace();
    vm.AutoShare = autoShare;
    vm.ShareResultDirectory = exportFolder;

    Assert.Equal(expected, vm.CanStart);
}
```

再覆盖：无有效行、无账号、无目标目录不可开始；单账号、多账号复制、多账号分流能生成与现有分配模型一致的确认请求；未勾选自动分享时 `PlannedShareCount == 0`；批次创建完成后结果保存目录被交给后续持久化接口而不是只留在 ViewModel 内存中。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~TransferWorkspaceViewModelTests"`

Expected: FAIL，类型尚不存在。

- [ ] **Step 3: 实现薄组合层，不复制业务服务**

`TransferWorkspaceViewModel` 组合现有 `LocalImportWorkflow`、预检服务、账号分配/确认服务和结果查询。`StartAsync` 内部顺序固定为：保存当前行修改 → 后台预检 → 仅显示需要处理的问题 → 建批次 → 把结果目录交给 `ILocalResultDestinationStore`（Task 6 实现）→ 启动现有协调器。旧向导 ViewModel 可增加内部服务方法，但不得复制 `CreateBatchService` 或调度规则。在 Task 6 完成前使用内存假实现保持本任务可测试。

- [ ] **Step 4: 实现一页 XAML**

页面同时显示粘贴/Excel、解析行、账号模式、账号列表、目标目录、自动分享勾选、分享结果本机目录和开始按钮。不得显示“草稿”“创建任务”“批量预检”“适配器”。

- [ ] **Step 5: 把原结果能力嵌入当前工作区**

`WorkspaceRunPanelViewModel` 复用 `JobResultsViewModel` / `LocalJobResultsWorkflow`，在转存页面下方显示历史批次和当前进度；运行控制补充三个明确应用服务：

- `PauseBatchService` 以 CAS 把 `Queued/Active` 批次置为 `Paused`，协调器停止领取新操作；已进入网页提交边界的当前项只在安全阶段停下，不强行把未知提交改成失败；
- `ResumeBatchService` 以 CAS 把 `Paused` 批次放回队列，并触发协调器恢复；
- `RetryFailedTransferService` 只把 `Failed + 明确未提交` 的转存重置为等待并增加执行代次。`ResultPending`、提交接受未知和需要用户判断的操作不可通过“失败重试”绕过核对。

为这三项在 `IUnitOfWork` / `SqliteTransferSchedulingStore` 增加事务或 CAS 方法，并测试并发点击、迟到 worker、暂停后重启、继续以及安全/不安全失败的区分。分享失败重试继续复用现有 `LocalJobResultsWorkflow`。删除独立“任务结果”导航后这些能力仍必须可达。

- [ ] **Step 6: 运行相关测试并提交**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~TransferWorkspace|FullyQualifiedName~WorkspaceRunPanel|FullyQualifiedName~ImportViewModel|FullyQualifiedName~CreateJobWizard|FullyQualifiedName~JobResults"`

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~BatchRunControl"`

Expected: PASS。

```powershell
git add src/BaiduBatchTransfer.App/Features/Transfers src/BaiduBatchTransfer.App/Features/Common src/BaiduBatchTransfer.App/Features/Imports/ImportViewModel.cs src/BaiduBatchTransfer.App/Features/Jobs/CreateJobWizardViewModel.cs src/BaiduBatchTransfer.App/AppBootstrap.cs src/BaiduBatchTransfer.Application/Jobs/PauseBatchService.cs src/BaiduBatchTransfer.Application/Jobs/ResumeBatchService.cs src/BaiduBatchTransfer.Application/Jobs/RetryFailedTransferService.cs src/BaiduBatchTransfer.Core/Abstractions/IUnitOfWork.cs src/BaiduBatchTransfer.Infrastructure/Database/SqliteUnitOfWork.cs src/BaiduBatchTransfer.Infrastructure/Database/SqliteTransferSchedulingStore.cs tests/BaiduBatchTransfer.App.Tests/Features tests/BaiduBatchTransfer.Application.Tests/Jobs/BatchRunControlServiceTests.cs
git commit -m "feat: compose single-page transfer workspace"
```

---

### Task 4: 建立账号绑定的网盘资源选择模型

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Domain/OwnedRemoteSelection.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IRemoteResourcePicker.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IStandaloneShareStore.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/StandaloneShareRecords.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceViewModel.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceView.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceView.xaml.cs`
- Create: `tests/BaiduBatchTransfer.Core.Tests/Domain/OwnedRemoteSelectionTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/ShareWorkspaceViewModelTests.cs`

- [ ] **Step 1: 写账号归属与开始条件失败测试**

```csharp
[Fact]
public void Selection_CannotBeMovedToAnotherAccount()
{
    var selected = Fixture.Selection(accountId: Fixture.AccountA);

    var vm = Fixture.ShareWorkspace(activeAccountId: Fixture.AccountB);
    Assert.Throws<InvalidOperationException>(() => vm.Add(selected));
}

[Fact]
public void CanStart_RequiresAccountSelectionAndLocalResultDirectory()
{
    var vm = Fixture.ShareWorkspace();
    vm.Add(Fixture.Selection(Fixture.AccountA));
    Assert.False(vm.CanStart);
    vm.ResultDirectory = @"C:\results";
    Assert.True(vm.CanStart);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~OwnedRemoteSelection"`

Expected: FAIL，领域类型尚不存在。

- [ ] **Step 3: 实现不可变选择契约**

```csharp
public sealed record OwnedRemoteSelection(
    Guid AccountId,
    RemoteItemIdentitySet RemoteItems,
    string DisplayName,
    RemoteResourceKind Kind,
    RemotePath ParentPath);

public interface IRemoteResourcePicker
{
    Task<IReadOnlyList<OwnedRemoteSelection>> PickAsync(
        Guid accountId, CancellationToken cancellationToken);
}
```

构造时校验 `RemoteItems` 是精确稳定标识；显示名只用于 UI，不得用于执行匹配。

定义独立的只分享持久化契约：`StandaloneShareBatchRecord` 保存所选本机结果目录引用和批次状态，`StandaloneShareOperationRecord` 保存 `AccountId + ExactRemoteItems + Generation + 提交状态`。它不能伪造 `TransferOperation`，也不能复用现有非空的 `ShareOperations.TransferOperationId`。

- [ ] **Step 4: 实现分享工作区薄 UI**

按账号分组显示所选资源；切换账号时重新进入该账号自己的 Profile 选择；每个用户选中的文件/文件夹成为独立操作。第一版不提供跨账号复制选择。页面下方同时显示只分享批次的历史、当前进度、暂停、继续和失败重试；本任务以 `IStandaloneShareStore` 假实现测试，真实 SQLite 存储和调度在 Task 6 / Task 10 完成。

- [ ] **Step 5: 运行测试并提交**

Run: `dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~OwnedRemoteSelection"`

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~ShareWorkspaceViewModel"`

Expected: 全部 PASS。

```powershell
git add src/BaiduBatchTransfer.Core/Domain/OwnedRemoteSelection.cs src/BaiduBatchTransfer.Core/Abstractions/IRemoteResourcePicker.cs src/BaiduBatchTransfer.Core/Abstractions/IStandaloneShareStore.cs src/BaiduBatchTransfer.Core/Abstractions/StandaloneShareRecords.cs src/BaiduBatchTransfer.App/Features/Shares tests/BaiduBatchTransfer.Core.Tests/Domain/OwnedRemoteSelectionTests.cs tests/BaiduBatchTransfer.App.Tests/Features/ShareWorkspaceViewModelTests.cs
git commit -m "feat: add account-bound share resource selection"
```

---

### Task 5: 定义可测试的 Web 页面状态与动作协议

**Files:**
- Create: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/BaiduPageState.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/BaiduAutomationAction.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/BaiduSelectorCatalog.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/BaiduPageObservationParser.cs`
- Create: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/BaiduPageObservationParserTests.cs`
- Create: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/BaiduSelectorCatalogTests.cs`
- Create: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/Fixtures/*.json`

- [ ] **Step 1: 用户在场时先采集只读、脱敏页面证据**

使用 Task 1 的 Debug 探针，在不点击真实写按钮的前提下分别打开：外部分享内容页、保存目录选择页、自己网盘资源列表、普通整组分享弹窗、单项分享弹窗和结果弹窗。只保存页面角色、可见文字、稳定属性、候选元素数量和虚构资源标识；不得保存完整分享 URL、提取码、Cookie、请求头、响应正文、百度 UID 或本机真实路径。若某个页面无法安全到达，先把对应能力保持 `NotValidated`，不得凭参考脚本猜生产定位器。

- [ ] **Step 2: 写状态识别失败测试**

```csharp
[Theory]
[InlineData("share-content-ready.json", BaiduPageState.ExternalShareReady)]
[InlineData("verification-required.json", BaiduPageState.UserVerificationRequired)]
[InlineData("transfer-accepted.json", BaiduPageState.TransferAccepted)]
[InlineData("share-result-ready.json", BaiduPageState.ShareResultReady)]
public void Parse_RecognizesStableState(string fixture, BaiduPageState expected)
{
    Assert.Equal(expected, Fixture.Parse(fixture).State);
}
```

加上多候选歧义、未知页面、空目录、页面改版测试，歧义必须返回 `Unknown`，不能任选第一个。

- [ ] **Step 3: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Debug --filter "FullyQualifiedName~WebAutomation"`

Expected: FAIL，协议尚不存在。

- [ ] **Step 4: 实现固定动作和多候选定位器**

动作只允许应用内枚举，例如 `OpenOfficialUri`、`SelectExactItems`、`ClickSave`、`ChooseDestination`、`ClickShare`、`ChoosePermanent`、`SetExtractionCode`、`CreateLink`、`ExportResult`。定位器优先语义/无障碍角色/稳定属性，禁止固定坐标和外来脚本。

- [ ] **Step 5: 运行测试并提交**

Run: `dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Debug --filter "FullyQualifiedName~WebAutomation"`

Expected: PASS。

```powershell
git add src/BaiduBatchTransfer.BaiduAdapter/WebAutomation tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation tests/BaiduBatchTransfer.BaiduAdapter.Tests/Fixtures
git commit -m "feat: define baidu web automation protocol"
```

---

### Task 6: 持久化只分享批次、结果目录和浏览器恢复证据

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Abstractions/IWebAutomationAttemptStore.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/WebAutomationAttemptRecord.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/ILocalResultDestinationStore.cs`
- Create: `src/BaiduBatchTransfer.Core/Abstractions/ILocalResultExportStore.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/Migrations/009_WebViewAutomationAndStandaloneShare.sql`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteWebAutomationAttemptStore.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteStandaloneShareStore.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteLocalResultDestinationStore.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteLocalResultExportStore.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/DatabaseSchemaValidator.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/MigrationTests.cs`
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/WebAutomationAttemptStoreTests.cs`
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/StandaloneShareStoreTests.cs`
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/LocalResultDestinationStoreTests.cs`
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/LocalResultExportStoreTests.cs`

- [ ] **Step 1: 先做字段归属审计并写在测试注释中**

明确复用：`TransferOperations.ConfirmationDescriptorJson/SubmitState/RemoteItemIdsJson`、`ShareOperations.Generation/Baseline*`、`ShareOperationGenerations`。新增数据分三类：

1. `StandaloneShareBatches / StandaloneShareOperations / StandaloneShareRemoteItems / StandaloneShareResults`：只分享专用事实，不伪造 `TransferOperation`；
2. `BatchResultDestinations`：把转存批次与用户选择的本机结果目录关联，路径只在本机数据库使用，`ToString()` 和日志必须脱敏；
3. `LocalResultExports`：以 `OwnerKind + OwnerId + ExportKind + Generation` 唯一标识本机导出，保存 `Pending/InProgress/Succeeded/Failed`、最终文件名和脱敏错误码，支持崩溃后只恢复导出；
4. `WebAutomationAttempts / WebAutomationAttemptRemoteItems`：UI 自动化阶段、动作代次、提交前目录的精确稳定资源 ID 集合、脱敏观察指纹和是否等待人工。

不得在新表重复保存完整外部分享 URL、Cookie、请求头或响应正文。稳定远端资源 ID 是恢复必需证据，可以持久化，但任何日志/异常输出均要隐藏。

- [ ] **Step 2: 写迁移和迟到事件失败测试**

```csharp
[Fact]
public async Task CompleteStage_RejectsLateEventFromOldGeneration()
{
    await store.BeginAsync(Fixture.Attempt(generation: 2));

    var changed = await store.TryAdvanceAsync(
        Fixture.OperationId, generation: 1, "TransferAccepted", Fixture.Fingerprint);

    Assert.False(changed);
}
```

再覆盖迁移 1→9、幂等迁移、账号/操作/代次联合匹配、脱敏字段约束、转存提交前完整目录集合往返、只分享批次重启后恢复、每个所选资源只生成一条操作、结果目录关联不丢失、同一导出代次只能产生一条成功记录以及 `InProgress` 崩溃后可恢复为待处理。

增加一个恢复关键测试：

```csharp
[Fact]
public async Task TransferBaseline_RoundTripsExactPreSubmitDirectorySet()
{
    await store.BeginAsync(Fixture.TransferAttempt(
        baselineItems: Fixture.ExactItems("before-1", "before-2")));

    var restored = await store.GetAsync(Fixture.OperationId, Fixture.Generation);

    Assert.Equal(
        new[] { "before-1", "before-2" },
        restored!.BaselineRemoteItems.Select(x => x.ExactId).Order());
}
```

- [ ] **Step 3: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Debug --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~WebAutomationAttemptStore|FullyQualifiedName~StandaloneShareStore|FullyQualifiedName~LocalResultDestinationStore|FullyQualifiedName~LocalResultExportStore"`

Expected: FAIL，迁移 009 和存储尚不存在。

- [ ] **Step 4: 实现最小迁移和 CAS 存储**

浏览器状态至少包含 `Preparing`、`BaselinePersisted`、`Submitting`、`ResultPending`、`UserHandoff`、`Succeeded`、`DefinitelyNotSubmitted`、`NeedsDecision`。每次推进同时匹配 `OperationId + AccountId + Generation`。`WebAutomationAttemptRemoteItems` 以稳定 ID + canonical sort key 持久化提交前目录全集，崩溃恢复后可以计算集合差；只有指纹而没有明细时不得确认转存成功。

只分享表必须独立支持：创建批次、逐项操作租约、代次、提交基线、ResultPending、暂停/继续/只重试失败项、已接受结果和历史分页。实现应复用现有 `ShareOperationStatus`、`SubmissionAcceptance` 和 `ShareReconciliationPolicy` 的语义，但不得放宽现有转存后分享表的外键约束。`LocalResultExports` 的 CAS API 必须做到同一 owner/generation 已成功时不再生成第二份，失败只重新领取本地导出租约。

- [ ] **Step 5: 运行数据库测试并提交**

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Debug --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~WebAutomationAttemptStore|FullyQualifiedName~StandaloneShareStore|FullyQualifiedName~LocalResultDestinationStore|FullyQualifiedName~LocalResultExportStore"`

Expected: PASS。

```powershell
git add src/BaiduBatchTransfer.Core/Abstractions src/BaiduBatchTransfer.Infrastructure/Database src/BaiduBatchTransfer.App/AppBootstrap.cs tests/BaiduBatchTransfer.Infrastructure.Tests/Database
git commit -m "feat: persist workspace automation state"
```

---

### Task 7: 实现全局可见 WebView2 队列和人工接管

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/IVisibleWebAutomationBroker.cs`
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/VisibleWebAutomationQueue.cs`
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebAutomationWindow.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebAutomationWindow.xaml.cs`
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebView2AutomationSession.cs`
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/UserHandoffCoordinator.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/VisibleWebAutomationQueueTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/UserHandoffCoordinatorTests.cs`

- [ ] **Step 1: 写全局串行与人工接管失败测试**

```csharp
[Fact]
public async Task Verification_PausesGlobalQueueUntilUserResumes()
{
    var first = queue.Enqueue(Fixture.VerificationOperation(AccountA));
    var second = queue.Enqueue(Fixture.SuccessOperation(AccountB));

    await first.WaitUntilHandoffAsync();
    Assert.False(second.HasStarted);
    first.ResumeByUser();
    await Task.WhenAll(first.Completion, second.Completion);
}
```

再覆盖取消、跳过、窗口关闭、同账号 Profile 绑定、迟到事件不影响下一操作。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~VisibleWebAutomationQueue|FullyQualifiedName~UserHandoff"`

Expected: FAIL，队列尚不存在。

- [ ] **Step 3: 实现一次只显示一个写操作的 Broker**

Broker 接收绑定好的账号 Profile 路径和动作请求；所有 WebView2 调用回到 WPF Dispatcher。检测扫码、验证码、安全验证或风控时，窗口保持可见并暂停整个队列，用户只能“已处理，继续”“跳过此项”“取消批次”。

- [ ] **Step 4: 强制官方 HTTPS 导航策略**

复用 `BaiduNavigationPolicy`，只允许百度官方 HTTPS 主机；拒绝 `file:`、`javascript:`、第三方跳转和外部窗口。不得开放 remote debugging port。

- [ ] **Step 5: 运行测试并提交**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~VisibleWebAutomationQueue|FullyQualifiedName~UserHandoff|FullyQualifiedName~BaiduNavigationPolicy"`

Expected: PASS。

```powershell
git add src/BaiduBatchTransfer.App/Features/BrowserAutomation tests/BaiduBatchTransfer.App.Tests/Features/VisibleWebAutomationQueueTests.cs tests/BaiduBatchTransfer.App.Tests/Features/UserHandoffCoordinatorTests.cs
git commit -m "feat: add visible webview automation queue"
```

---

### Task 8: 实现浏览器转存适配器与精确结果核对

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebViewBaiduNetdiskAdapter.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferAutomationFlow.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/OperationExecutor.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/OperationReconciliationService.cs`
- Create: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/TransferAutomationFlowTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/WebViewBaiduNetdiskAdapterTests.cs`
- Modify: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/OperationExecutorTests.cs`
- Modify: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/ReconciliationTests.cs`

- [ ] **Step 1: 写文件夹、APK、多顶层和模糊结果失败测试**

```csharp
[Theory]
[InlineData("folder-transfer.json", 1)]
[InlineData("apk-transfer.json", 1)]
[InlineData("multi-root-transfer.json", 3)]
public async Task Save_ReturnsExactTopLevelIdentitySet(string fixture, int count)
{
    var receipt = await Fixture.RunTransferAsync(fixture);
    Assert.Equal(count, receipt.ExactRemoteItems!.Items.Count);
}

[Fact]
public async Task AmbiguousPostTransferListing_ReturnsResultPending()
{
    var result = await Fixture.RunAmbiguousTransferAsync();
    Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Debug --filter "FullyQualifiedName~TransferAutomationFlow"`

Expected: FAIL，流程尚不存在。

- [ ] **Step 3: 实现可见点击流程**

固定顺序：打开外链 → 等内容稳定/填提取码 → 打开目标目录并把提交前完整稳定资源 ID 集合写入 `WebAutomationAttemptRemoteItems` → 返回分享页 → 点击“保存到网盘” → 选择绑定目标目录 → 等官方接受 → 重新打开目标目录 → 用“当前全集 − 持久化基线全集”求本次新增的精确顶层集合。`TransferReceipt.WithExactEvidence(...)` 只能在差集与页面接受证据共同得到唯一匹配时返回。

- [ ] **Step 4: 对齐现有提交边界**

在 `OperationExecutor` 中只做 WebView 特有错误映射；保留现有“提交后异常进入 ResultPending，不盲目重试”的规则。恢复时调用 `ConfirmExactTransferAsync` 并读取 Task 6 的完整基线资源集合；若基线明细缺失、差集含多个不相容候选或页面最终一致性尚未稳定，则保持 `ResultPending/NeedsDecision`。按名称或时间只能作显示证据，不能确认成功。

- [ ] **Step 5: 运行适配器和调度测试并提交**

Run: `dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Debug --filter "FullyQualifiedName~TransferAutomationFlow"`

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~OperationExecutor|FullyQualifiedName~Reconciliation"`

Expected: PASS。

```powershell
git add src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebViewBaiduNetdiskAdapter.cs src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/TransferAutomationFlow.cs src/BaiduBatchTransfer.Application/Scheduling/OperationExecutor.cs src/BaiduBatchTransfer.Application/Scheduling/OperationReconciliationService.cs tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/TransferAutomationFlowTests.cs tests/BaiduBatchTransfer.App.Tests/Features/WebViewBaiduNetdiskAdapterTests.cs tests/BaiduBatchTransfer.Application.Tests/Scheduling
git commit -m "feat: transfer shares through visible webview"
```

---

### Task 9: 实现自己网盘资源选择器

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebViewRemoteResourcePicker.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/RemoteResourcePickerFlow.cs`
- Create: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/RemoteResourcePickerFlowTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/WebViewRemoteResourcePickerTests.cs`

- [ ] **Step 1: 写选择精确资源失败测试**

覆盖文件夹、APK、普通文件、重名项、空目录、分页/懒加载和切换账号。重名项必须按稳定资源标识区分；只有显示名时禁止确认。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Debug --filter "FullyQualifiedName~RemoteResourcePickerFlow"`

Expected: FAIL。

- [ ] **Step 3: 实现可见资源选择**

打开当前账号网盘，用户在 WebView2 中浏览；软件从被勾选 DOM 行读取稳定资源 ID、类型和父目录，返回 `OwnedRemoteSelection`。用户点击应用内“确认选择”后才关闭窗口。

- [ ] **Step 4: 运行测试并提交**

Run: `dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Debug --filter "FullyQualifiedName~RemoteResourcePickerFlow"`

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~WebViewRemoteResourcePicker"`

Expected: PASS。

```powershell
git add src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebViewRemoteResourcePicker.cs src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/RemoteResourcePickerFlow.cs tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/RemoteResourcePickerFlowTests.cs tests/BaiduBatchTransfer.App.Tests/Features/WebViewRemoteResourcePickerTests.cs
git commit -m "feat: select exact remote resources in webview"
```

---

### Task 10: 实现浏览器分享适配器的两种组合语义

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebViewBaiduShareAdapter.cs`
- Create: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/ShareAutomationFlow.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/ShareSubmissionEngine.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/StandaloneShareCoordinator.cs`
- Create: `src/BaiduBatchTransfer.Application/Scheduling/StandaloneShareOperationExecutor.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/CreateStandaloneShareBatchService.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/ShareOperationExecutor.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/ShareOperationReconciliationService.cs`
- Create: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/ShareAutomationFlowTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/WebViewBaiduShareAdapterTests.cs`
- Modify: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/ShareOperationExecutorTests.cs`
- Modify: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/ShareReconciliationTests.cs`
- Create: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/StandaloneShareOperationExecutorTests.cs`
- Create: `tests/BaiduBatchTransfer.Application.Tests/Jobs/CreateStandaloneShareBatchServiceTests.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ShareWorkspaceViewModelTests.cs`

- [ ] **Step 1: 写两种组合语义失败测试**

```csharp
[Fact]
public async Task AutoShare_MultipleTopLevelItems_CreatesOneGroupedShare()
{
    var result = await Fixture.ShareAsync(Fixture.ExactSet("a", "b", "c"));
    Assert.Single(Fixture.Submissions);
    Assert.Equal(3, Fixture.Submissions[0].SelectedItemCount);
}

[Fact]
public async Task ShareWorkspace_SelectedItems_CreateIndependentOperations()
{
    var operations = await Fixture.CreatePersistedShareOnlyBatchAsync(selectionCount: 3);
    Assert.Equal(3, operations.Count);
    Assert.All(operations, op => Assert.Single(op.RemoteItems.Items));
}
```

再覆盖永久有效、随机码、固定码、已有分享、提交后窗口关闭、唯一结果匹配、多结果歧义、只分享批次重启恢复、暂停/继续、只重试失败项和历史分页。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Debug --filter "FullyQualifiedName~ShareAutomationFlow"`

Expected: FAIL。

- [ ] **Step 3: 实现可见分享流程**

精确选中当前 `PermanentShareRequest.RemoteItems` → 点击普通分享/单操作分享 → 永久有效 → 随机或固定提取码 → 在点击创建前持久化基线 → 点击创建 → 等结果弹窗 → 读取当前代次的分享 ID、HTTPS 链接、提取码和有效设置。多顶层自动分享必须同时选中并只提交一次；分享工作区每个内部操作只处理一个用户选择项。

- [ ] **Step 4: 提取共用提交引擎并接通只分享持久化调度**

从现有 `ShareOperationExecutor` 提取不依赖具体仓储行形状的 `ShareSubmissionEngine`：已有分享发现、提交前基线、`DefinitelyNotAccepted` 重试、提交后 `ResultPending` 和精确结果核对。原转存后分享 executor 与新的 `StandaloneShareOperationExecutor` 都调用该引擎；后者从 `IStandaloneShareStore` 领取租约并将结果写入 `StandaloneShareResults`。`CreateStandaloneShareBatchService` 在一个事务中保存用户选择的结果目录、账号绑定选择和“一项一操作”，然后 `StandaloneShareCoordinator` 执行全局可见队列。

- [ ] **Step 5: 保留分享恢复规则**

`ShareOperationExecutor` 继续在创建前列出已有分享基线；提交后异常只进入 `ResultPending`。`ListSharesAsync` 通过官方页面只读列表实现精确资源集合匹配；无法唯一对应时返回待确认，不得重新创建。

- [ ] **Step 6: 接通分享工作区的历史和控制动作**

`ShareWorkspaceViewModel` 的开始命令调用 `CreateStandaloneShareBatchService`；页面下方列表查询 `IStandaloneShareStore`。暂停、继续和失败重试必须通过带状态/代次比较的仓储方法执行，不能直接改 ViewModel 状态。重启后未完成批次由 `StandaloneShareCoordinator` 恢复。

- [ ] **Step 7: 运行测试并提交**

Run: `dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Debug --filter "FullyQualifiedName~ShareAutomationFlow"`

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~ShareOperationExecutor|FullyQualifiedName~StandaloneShare|FullyQualifiedName~CreateStandaloneShareBatch|FullyQualifiedName~ShareReconciliation"`

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~ShareWorkspaceViewModel"`

Expected: PASS。

```powershell
git add src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebViewBaiduShareAdapter.cs src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/ShareAutomationFlow.cs src/BaiduBatchTransfer.Application/Scheduling src/BaiduBatchTransfer.Application/Jobs/CreateStandaloneShareBatchService.cs tests/BaiduBatchTransfer.BaiduAdapter.Tests/WebAutomation/ShareAutomationFlowTests.cs tests/BaiduBatchTransfer.App.Tests/Features/WebViewBaiduShareAdapterTests.cs tests/BaiduBatchTransfer.App.Tests/Features/ShareWorkspaceViewModelTests.cs tests/BaiduBatchTransfer.Application.Tests/Scheduling tests/BaiduBatchTransfer.Application.Tests/Jobs/CreateStandaloneShareBatchServiceTests.cs
git commit -m "feat: create exact shares through visible webview"
```

---

### Task 11: 接管下载并生成只分享汇总结果

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebViewDownloadCoordinator.cs`
- Modify: `src/BaiduBatchTransfer.BaiduAdapter/WebAutomation/ShareAutomationFlow.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/StandaloneShareResultModels.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/ExportStandaloneShareResultsService.cs`
- Create: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlStandaloneShareResultWriter.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/ResultColumnLayoutBuilder.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/ReplacementWorkbookModels.cs`
- Modify: `src/BaiduBatchTransfer.Application/Jobs/ReplacementWorkbookExportService.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteBatchResultProjectionRepository.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlReplacementResultWriter.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Excel/OpenXmlReplacementWorkbookFilePublisher.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/WebViewDownloadCoordinatorTests.cs`
- Create: `tests/BaiduBatchTransfer.Application.Tests/Jobs/StandaloneShareResultExportTests.cs`
- Modify: `tests/BaiduBatchTransfer.Application.Tests/Jobs/ReplacementWorkbookExportTests.cs`
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Excel/OpenXmlStandaloneShareResultWriterTests.cs`
- Modify: `tests/BaiduBatchTransfer.Infrastructure.Tests/Excel/ReplacementResultWriterTests.cs`

- [ ] **Step 1: 写下载归属和不覆盖失败测试**

覆盖：旧文件、`.crdownload`/临时文件、零字节文件、错误批次、同名文件、窗口关闭后迟到下载。只有匹配当前 `BatchId + OperationId + Generation` 且完整可读的下载可接纳。

- [ ] **Step 2: 写汇总工作簿失败测试**

验证一条已选资源一行，列至少为账号显示名、资源名称、资源类型、新分享链接、提取码、状态；默认名 `百度分享结果_yyyyMMdd_HHmmss.xlsx`，同名追加递增后缀且不覆盖旧文件。

同时先写转存结果布局失败测试。每个参与账号必须有五列：

```text
转存结果_{账号}_实际转存账号
转存结果_{账号}_转存状态
转存结果_{账号}_新分享链接
转存结果_{账号}_提取码
转存结果_{账号}_分享状态
```

断言 `ColumnCount == SourceHeaders.Count + 2 + Accounts.Count * 5`。多账号复制分别填写各账号五列；分流只填写实际负责账号；只转存时填写账号和转存状态，三列分享字段保持空白而不是伪造成功/失败。

- [ ] **Step 3: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~WebViewDownloadCoordinator"`

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Debug --filter "FullyQualifiedName~StandaloneShareResult"`

Expected: FAIL。

- [ ] **Step 4: 实现下载协调和 Open XML 汇总**

WebView2 下载事件先进入批次临时目录，完成后验证格式并原子移动。官方单操作明细保存在 `官方导出明细` 子目录；汇总 Excel 由本地持久化结果生成，不依赖官方文件名或行序。

`ShareAutomationFlow` 的 `ExportResult` 动作必须通过 `IVisibleWebAutomationBroker` 调用 `WebViewDownloadCoordinator`，传入当前 `BatchId + OperationId + Generation`；协调器先订阅下载再点击导出，完成/超时后解除订阅。测试断言动作顺序和关联键，禁止“先点击、后猜下载文件”。

- [ ] **Step 5: 把现有动态结果布局扩为每账号五列**

将 `ResultAccountColumnLayout` 扩为实际转存账号、转存状态、新链接、提取码、分享状态；同步布局指纹版本、投影查询、流式行组装和 Open XML 写入器。实际转存账号来自已确认 `AssignedAccountId` 对应的输出标签，转存状态来自 `TransferOperation`，分享状态单独来自 `ShareOperation`；不得再把转存状态折叠到分享状态。

- [ ] **Step 6: 接通用户选择的结果目录和完成触发**

`TransferWorkspaceViewModel` 在批次创建后把 `ShareResultDirectory` 写入 `ILocalResultDestinationStore`。批次达到完成/带短缺完成时，应用恢复服务通过 `ILocalResultExportStore` 领取导出租约，先持久化该代次的确定性目标文件名，再读取目标目录并调用 `ReplacementWorkbookExportService` + `IReplacementWorkbookFilePublisher` 自动生成新文件；成功后以最终文件名完成 CAS，失败标记本地导出失败。若文件已原子落盘但成功 CAS 前崩溃，恢复时先验证这个持久化目标文件，完整可读则补记成功而不重写。其他崩溃或失败只重新领取本地导出，不重新转存或分享；已成功的同一代次不生成第二份。用户点击“保存结果”也调用同一服务并遵守代次。只分享批次读取 `StandaloneShareBatches` 中的结果目录和同一导出状态机调用 `ExportStandaloneShareResultsService`。

- [ ] **Step 7: 验证两类结果输出**

“转存并自动分享”继续走已扩展为五列的动态账号 `ReplacementWorkbook`；“只转存”不写虚假分享值；“只分享”走新汇总写入器。粘贴输入结果至少保留序号、解析标题、原链接、原提取码。测试必须从 ViewModel 的目录选择一直走到发布器收到相同目录，防止路径只停留在 UI。

- [ ] **Step 8: 运行测试并提交**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~WebViewDownloadCoordinator"`

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~StandaloneShareResult|FullyQualifiedName~ReplacementWorkbook|FullyQualifiedName~ResultColumnLayout"`

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj -c Debug --filter "FullyQualifiedName~StandaloneShareResult|FullyQualifiedName~ReplacementResult"`

Expected: PASS。

```powershell
git add src/BaiduBatchTransfer.App/Features/BrowserAutomation/WebViewDownloadCoordinator.cs src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs src/BaiduBatchTransfer.Application/Jobs src/BaiduBatchTransfer.Infrastructure/Database/Repositories/SqliteBatchResultProjectionRepository.cs src/BaiduBatchTransfer.Infrastructure/Excel tests/BaiduBatchTransfer.App.Tests/Features/WebViewDownloadCoordinatorTests.cs tests/BaiduBatchTransfer.Application.Tests/Jobs tests/BaiduBatchTransfer.Infrastructure.Tests/Excel
git commit -m "feat: export verified share results"
```

---

### Task 12: 接线能力闸门和安全降级

**Files:**
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Acceptance/AcceptanceWriteLease.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Acceptance/AcceptanceWriteLeaseCoordinator.cs`
- Create: `src/BaiduBatchTransfer.App/Features/Acceptance/AcceptanceConfirmationWindow.xaml`
- Create: `src/BaiduBatchTransfer.App/Features/Acceptance/AcceptanceConfirmationWindow.xaml.cs`
- Modify: `src/BaiduBatchTransfer.Application/Scheduling/CoordinatedBaiduAdapters.cs`
- Modify: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteAdapterCapabilityStore.cs`
- Modify: `src/BaiduBatchTransfer.Core/Domain/AdapterCapability.cs`
- Modify: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`
- Create: `tests/BaiduBatchTransfer.App.Tests/Features/AcceptanceWriteLeaseCoordinatorTests.cs`
- Modify: `tests/BaiduBatchTransfer.BaiduAdapter.Tests/LiveAdapterAvailabilityTests.cs`
- Modify: `tests/BaiduBatchTransfer.Application.Tests/Scheduling/NetworkExecutionGateTests.cs`
- Modify: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/RepositoryTests.cs`

- [ ] **Step 1: 写“未验收不得真实写”的失败测试**

```csharp
[Fact]
public async Task UnacceptedSelectorVersion_BlocksWriteOperations()
{
    var adapter = Fixture.WebViewAdapter(capabilityAccepted: false);
    var result = await adapter.CreatePermanentShareAsync(
        Fixture.Session, Fixture.Request, CancellationToken.None);

    Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
    Assert.Empty(Fixture.BrowserClicks);
}
```

再覆盖：定位器版本变化、应用构建版本变化、现场证据过期时自动关闭；只读登录/资源选择不错误开启写能力；普通运行无法创建临时租约；Release 即使命令行带验收参数也无法创建；临时租约不能被另一账号、另一操作、另一适配器或过期请求复用。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Debug --filter "FullyQualifiedName~LiveAdapterAvailability"`

Expected: FAIL。

- [ ] **Step 3: 用版本化验收记录接线**

`AppBootstrap` 注册 WebView2 适配器并继续包裹 `CoordinatedBaiduNetdiskAdapter` / `CoordinatedBaiduShareAdapter`。正式写能力必须同时匹配适配器种类、协议/定位器版本和应用构建版本；未匹配返回 `NotValidated` 并在 UI 提示需要现场检查。

为打通首次验收闭环，增加独立于正式能力记录的 `AcceptanceWriteLease`：

- 只在 Debug + 显式 `--webview-acceptance` + 用户在确认窗口逐项勾选“这是可丢弃测试资源并同意本次真实操作”时创建；
- 只存在内存中，绑定 `AdapterKind + ProtocolVersion + BuildVersion + AccountId + OperationId + Generation`，单次消费且短时过期；
- 只允许当前候选版本执行这一笔用户已确认的测试写操作，不预先调用 `IAdapterCapabilityStore.AcceptAsync`；
- 普通运行、Release、后台恢复、第二笔操作和任何参数不匹配都不能使用该租约；
- 只有测试操作的远端结果、恢复证据和本机导出均通过后，确认窗口才启用“接受此版本”按钮，随后写入正式版本化接受记录。

- [ ] **Step 4: 运行闸门测试并提交**

Run: `dotnet test tests/BaiduBatchTransfer.BaiduAdapter.Tests/BaiduBatchTransfer.BaiduAdapter.Tests.csproj -c Debug --filter "FullyQualifiedName~LiveAdapterAvailability"`

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj -c Debug --filter "FullyQualifiedName~NetworkExecutionGate"`

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~AppBootstrap"`

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Debug --filter "FullyQualifiedName~AcceptanceWriteLease"`

Expected: PASS。

```powershell
git add src/BaiduBatchTransfer.App/AppBootstrap.cs src/BaiduBatchTransfer.App/Features/Acceptance src/BaiduBatchTransfer.Application/Scheduling/CoordinatedBaiduAdapters.cs src/BaiduBatchTransfer.Infrastructure/Database/SqliteAdapterCapabilityStore.cs src/BaiduBatchTransfer.Core/Domain/AdapterCapability.cs tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs tests/BaiduBatchTransfer.App.Tests/Features/AcceptanceWriteLeaseCoordinatorTests.cs tests/BaiduBatchTransfer.BaiduAdapter.Tests/LiveAdapterAvailabilityTests.cs tests/BaiduBatchTransfer.Application.Tests/Scheduling/NetworkExecutionGateTests.cs tests/BaiduBatchTransfer.Infrastructure.Tests/Database/RepositoryTests.cs
git commit -m "feat: gate webview write automation by acceptance"
```

---

### Task 13: 全量回归、脱敏审计和用户现场验收

**Files:**
- Modify: `docs/superpowers/specs/2026-08-24-two-workspace-webview-automation-design.md`（仅在实现事实需要同步时）
- Create: `docs/acceptance/webview-automation-checklist.md`
- Modify: `README.md`（若存在且需要更新用户入口）
- Test: all test projects

- [ ] **Step 1: 运行格式和差异检查**

Run: `git diff --check`

Expected: 无尾随空格或冲突标记。

- [ ] **Step 2: 运行全量 Debug 测试**

Run: `dotnet test BaiduBatchTransfer.sln -c Debug --no-restore`

Expected: 全部 PASS，0 failed。

- [ ] **Step 3: 运行严格 Debug/Release 构建**

Run: `dotnet build BaiduBatchTransfer.sln -c Debug -warnaserror --no-restore`

Run: `dotnet build BaiduBatchTransfer.sln -c Release -warnaserror --no-restore`

Expected: 两次均 Build succeeded，0 warnings。

- [ ] **Step 4: 做敏感信息和危险实现扫描**

Run: `rg -n -i "cookie|authorization|remote-debugging-port|client_secret|app_secret|javascript:|http://" src tests docs/acceptance`

Expected: 每个命中都人工检查；测试名称和脱敏枚举可以保留，生产代码不得记录凭证、开放调试端口、允许不安全协议或执行外来脚本。

- [ ] **Step 5: 创建现场验收清单，不写真实敏感值**

清单逐项记录“通过/失败/证据文件名/稳定错误码”：文件夹转存并自动分享、APK 转存并自动分享、只转存、自己网盘文件和文件夹只分享、随机码、固定码、单账号、两个隔离账号、一次人工验证后继续、结果目录、定位器失效安全暂停。证据只保存脱敏截图和指纹，不保存完整链接/提取码/账号 UID。

- [ ] **Step 6: 用户在场时启动 Debug 现场验收**

Run: `dotnet run --project src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Debug -- --webview-acceptance`

Expected: 显示用户已登录账号对应的可见 WebView2；未得到用户逐笔确认前不执行真实写入。用户选择可丢弃测试资源并确认后，程序只为这一笔操作签发 Task 12 的内存一次性验收租约；租约不等于正式启用。逐项失败时保留 `NotValidated`，不要为了“通过”放宽证据标准。

- [ ] **Step 7: 只有全部关键场景通过才写入版本化能力接受记录**

Expected: 只有一次性租约对应操作的远端结果、崩溃/待确认恢复和本机导出全部通过，用户再次点击“接受此版本”后才写入正式记录。转存和分享分别独立接受；某一项失败只关闭该项能力。页面版本变化后旧接受记录自动失效，Debug 租约在进程结束时消失。

- [ ] **Step 8: 再次运行全量回归并提交**

Run: `dotnet test BaiduBatchTransfer.sln -c Debug --no-restore`

Run: `git status --short`

Expected: 测试全绿；状态只包含本任务预期文档/代码改动。

```powershell
git add docs/acceptance README.md docs/superpowers/specs/2026-08-24-two-workspace-webview-automation-design.md
git commit -m "docs: record webview automation acceptance"
```

若 `README.md` 或设计文档没有变化，不要为了凑提交而修改；只提交实际生成的验收清单和必要同步。

---

## 完成定义

- 用户主窗口左侧只显示“转存”“分享”。
- 转存页不要求用户理解草稿、创建任务或预检；未勾自动分享时转存成功即完成，勾选后只对精确实际资源进入分享队列。
- 分享页从明确账号自己的网盘选择资源，每个用户选择项独立生成结果。
- 一个外链含多个顶层资源时，自动分享生成一个包含该精确集合的链接。
- 所有真实写操作通过可见 WebView2 点击完成；验证码和安全验证由用户处理。
- 提交后异常、程序崩溃和下载失败不会导致盲目重复转存或重复分享。
- 结果写入用户选择的本机目录，旧文件不覆盖，输入列和多账号动态结果列保持正确。
- 页面证据不足或现场验收失效时能力安全关闭，不假成功。
- 全量测试、严格 Debug/Release 构建、数据库迁移/恢复测试和脱敏审计通过。
