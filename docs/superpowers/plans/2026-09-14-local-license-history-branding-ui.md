# 知道综合工具本地授权、历史清理与品牌调整 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Windows 本地客户端显示实际获准卡密的套餐和到期时间，提供不影响百度网盘的终态历史清理，改善激活窗口并统一品牌名为“知道综合工具”。

**Architecture:** 授权门禁在实际接受签名声明时发布安全摘要到运行时状态，主窗口只订阅该状态；历史清理由新的应用服务和 SQLite 存储事务实现，界面日志仍保持纯内存清理。转存与独立分享分别暴露预览/执行命令，统一使用本地确认交互，删除逻辑重新检查终态并拒绝存在活动导出或自动化写入的目标。

**Tech Stack:** .NET 8、WPF、CommunityToolkit.Mvvm、Microsoft.Extensions.DependencyInjection、SQLite、xUnit

---

## 文件结构

- Create: `src/BaiduBatchTransfer.Core/Abstractions/ILocalTerminalHistoryStore.cs` — 本地终态历史预览/删除契约和结果模型。
- Create: `src/BaiduBatchTransfer.Application/Jobs/LocalTerminalHistoryService.cs` — 参数验证、预览与执行编排。
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteLocalTerminalHistoryStore.cs` — 转存/分享目标集合、冲突检测、事务删除和残留校验。
- Create: `src/BaiduBatchTransfer.App/Features/Common/LocalHistoryCleanupInteraction.cs` — 可测试的确认交互与 WPF MessageBox 实现。
- Create: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/SqliteLocalTerminalHistoryStoreTests.cs` — 删除闭包、保留边界、并发冲突和回滚测试。
- Create: `tests/BaiduBatchTransfer.Application.Tests/Jobs/LocalTerminalHistoryServiceTests.cs` — 应用服务契约测试。
- Modify: `src/SuperPan.Licensing.Client/LicenseAuthorizationGate.cs` — 在真实获准路径发布安全授权摘要。
- Modify: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseAcceptanceSmokeRunner.cs` — 在生产 DI 工厂中把 Gate 摘要回调接到运行时状态；独立 smoke factory 保持可选回调。
- Modify: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseRuntimeAuthorizationState.cs` — 保存并通知当前授权摘要。
- Modify: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseActivationViewModel.cs` — 激活中按钮文案/提示与状态。
- Modify: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseActivationWindow.xaml` — 新卡片式激活界面。
- Modify: `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`、`MainWindow.xaml` — 品牌名和授权摘要显示。
- Modify: `src/BaiduBatchTransfer.App/Features/Common/WorkspaceLogEntry.cs` — 内存日志显式 Clear。
- Modify: `src/BaiduBatchTransfer.App/Features/Common/WorkspaceRunPanelViewModel.cs` — 转存历史刷新为空状态。
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsViewModel.cs` — 原子重置批次标识、统计、分页和命令状态。
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`、`TransferWorkspaceView.xaml` — 清空转存历史/日志命令和按钮。
- Modify: `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceViewModel.cs`、`ShareWorkspaceView.xaml` — 清空分享历史/日志命令和按钮。
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`、`App.xaml.cs` — 依赖注入和面向用户品牌文字。
- Modify: 相关 App、Client、Application、Infrastructure 测试 — 回归和结构断言。

### Task 1: 发布并显示实际获准的授权摘要

**Files:**
- Modify: `src/SuperPan.Licensing.Client/LicenseAuthorizationGate.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseAcceptanceSmokeRunner.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseRuntimeAuthorizationState.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml`
- Test: `tests/SuperPan.Licensing.Client.Tests/LicenseAuthorizationGateTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/LicenseOperationBoundaryTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`

- [ ] **Step 1: 写失败测试，证明摘要来自实际获准声明**

覆盖多凭据场景：第一个拒绝、第二个在线或缓存获准时，事件/回调只发布第二个声明的 `PackageName` 与 `ExpiresAt`；失败结果清除旧摘要。

- [ ] **Step 2: 运行定向测试并确认失败**

Run: `dotnet test tests/SuperPan.Licensing.Client.Tests/SuperPan.Licensing.Client.Tests.csproj --filter LicenseAuthorizationGateTests`

Expected: FAIL，因为尚无安全摘要发布契约。

- [ ] **Step 3: 最小实现授权摘要发布**

在客户端层增加不含卡密、机器码或签名载荷的值对象：

```csharp
public sealed record AuthorizedLicenseSummary(string PackageName, DateTimeOffset ExpiresAt);
```

让 `LicenseAuthorizationGate` 构造函数接受可选 `Action<AuthorizedLicenseSummary?>`，在在线响应或验证缓存实际获准的同一分支发布摘要；拒绝/不可用发布 `null`。缓存获准必须把该候选的 `CachedClaims` 传入发布函数，在线获准使用刚验证的 `claims`。`LicenseRuntimeAuthorizationState` 提供线程安全 `PublishSummary(AuthorizedLicenseSummary?)`、当前摘要属性和变更事件，后续获准覆盖旧值。

在 `LicenseServiceRegistration.Add` 的生产 Gate DI 工厂中解析同一个 `LicenseRuntimeAuthorizationState` 单例并把 `state.PublishSummary` 作为回调传入；`LicenseAuthorizationGateFactory.Create` 的 smoke/独立入口保留可选回调参数。`AppShellFactory` 构造函数注入同一状态实例，并在手工创建 `MainWindowViewModel` 时传入；关闭窗口时解除订阅。

- [ ] **Step 4: 为主窗口格式化状态写失败测试并实现**

`MainWindowViewModel` 接收 `LicenseRuntimeAuthorizationState`，订阅摘要变更并通过现有 UI 调度路径更新 `LicensePackageDisplay`、`LicenseExpiryDisplay`、`IsLicenseSummaryVisible`；到期时间转为本地时间并格式化为 `yyyy-MM-dd HH:mm`，未激活显示“未激活”。在产品标题右侧加入授权状态块。测试覆盖启动验证先发生、主窗口后创建，以及激活成功后实时刷新两种顺序。

- [ ] **Step 5: 运行测试并提交**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "LicenseOperationBoundaryTests|MainWindowViewModelTests"`

Expected: PASS。

Commit: `feat: display active license package and expiry`

### Task 2: 统一品牌并重做激活窗口

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseActivationViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Licensing/LicenseActivationWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/MainWindowViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/MainWindow.xaml`
- Modify: `src/BaiduBatchTransfer.App/App.xaml.cs`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/LicenseActivationViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/LicenseActivationViewStructureTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/MainWindowViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`

- [ ] **Step 1: 写品牌与结构失败测试**

断言所有面向用户标题使用“知道综合工具”，验收版为“知道综合工具 · OTA 本地验收版”，导出名称为“知道综合工具结果”，副标题为“百度网盘批量管理工作台”。激活窗口必须包含“软件授权”“请输入卡密”“卡密将绑定当前电脑”“立即激活”“暂不激活”和状态区域。

- [ ] **Step 2: 运行定向测试确认旧文案导致失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "LicenseActivation|MainWindowViewModelTests|AppBootstrapTests"`

Expected: FAIL，仍含旧产品名和旧窗口结构。

- [ ] **Step 3: 实现激活窗口状态与视觉结构**

增加 `ActivateButtonText => IsActivating ? "正在激活…" : "立即激活"` 并在 `IsActivating` 变化时通知。XAML 使用白色卡片、青绿色标题区、字段标签、说明文本、较高输入框、品牌主按钮和次按钮；保留默认/取消键和自动化名称。

- [ ] **Step 4: 全局替换面向用户的产品名**

仅替换产品身份文字；“百度网盘账号”“百度网盘链接”等业务描述保留。

- [ ] **Step 5: 运行测试并提交**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "LicenseActivation|MainWindowViewModelTests|AppBootstrapTests"`

Expected: PASS。

Commit: `feat: refresh activation dialog and product branding`

### Task 3: 为界面日志增加纯内存清空

**Files:**
- Modify: `src/BaiduBatchTransfer.App/Features/Common/WorkspaceLogEntry.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceView.xaml`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/WorkspaceLogEntryTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/ShareWorkspaceViewModelTests.cs`

- [ ] **Step 1: 写 Clear 行为失败测试**

追加日志后执行 `Clear()`，断言集合为空、重复折叠状态重置，之后可正常追加同样文本。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "WorkspaceLog|TransferWorkspaceViewModelTests|ShareWorkspaceViewModelTests"`

Expected: FAIL，因为没有公开清空行为与命令。

- [ ] **Step 3: 实现内存清空和两个工作区命令**

`WorkspaceLogBuffer.Clear()` 只执行 `entries.Clear()` 与 `lastAppendedEntry = null`。两个工作区各增加 `ClearLogsCommand`；执行后不写“已清空”新日志，改用状态消息反馈。

- [ ] **Step 4: 加入清空日志按钮并验证结构**

按钮放在各自“运行日志/最近 100 条”标题行，文案为“清空日志”，无日志或工作区繁忙时禁用。

- [ ] **Step 5: 运行测试并提交**

Expected: 定向测试 PASS。

Commit: `feat: clear workspace runtime logs`

### Task 4: 建立本地终态历史清理服务

**Files:**
- Create: `src/BaiduBatchTransfer.Core/Abstractions/ILocalTerminalHistoryStore.cs`
- Create: `src/BaiduBatchTransfer.Application/Jobs/LocalTerminalHistoryService.cs`
- Create: `tests/BaiduBatchTransfer.Application.Tests/Jobs/LocalTerminalHistoryServiceTests.cs`

- [ ] **Step 1: 写应用服务失败测试**

定义 `LocalHistoryKind.Transfer` 与 `LocalHistoryKind.StandaloneShare`；预览返回终态数量和 `HasConflicts`，执行返回删除数量或 `Conflict`。验证空预览不会执行，取消令牌透传。

- [ ] **Step 2: 运行测试确认缺失类型失败**

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --filter LocalTerminalHistoryServiceTests`

- [ ] **Step 3: 写最小契约与服务**

```csharp
public enum LocalHistoryKind { Transfer, StandaloneShare }
public sealed record LocalHistoryCleanupPreview(int TerminalBatchCount, bool HasConflicts);
public sealed record LocalHistoryCleanupResult(int DeletedBatchCount, LocalHistoryCleanupDisposition Disposition);
public interface ILocalTerminalHistoryStore
{
    Task<LocalHistoryCleanupPreview> PreviewAsync(LocalHistoryKind kind, CancellationToken token);
    Task<LocalHistoryCleanupResult> DeleteAsync(LocalHistoryKind kind, CancellationToken token);
}
```

服务只接受已知 kind，不持有 UI 依赖。

- [ ] **Step 4: 运行测试并提交**

Expected: PASS。

Commit: `feat: define local terminal history cleanup service`

### Task 5: 实现 SQLite 安全删除闭包

**Files:**
- Create: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteLocalTerminalHistoryStore.cs`
- Test: `tests/BaiduBatchTransfer.Infrastructure.Tests/Database/SqliteLocalTerminalHistoryStoreTests.cs`
- Reference: `src/BaiduBatchTransfer.Infrastructure/Database/SqliteAccountRemovalStore.cs`

- [ ] **Step 1: 写转存历史删除失败测试**

建立终态与非终态批次、账号、ImportSessions、LocalResultExports、Transfer/TransferShare WebAutomationAttempts 及子证据。执行后断言终态闭包消失、ImportSessions 引用置空，非终态、账号、会话、设置、原始导入行保留，并通过 `PRAGMA foreign_key_check`。

- [ ] **Step 2: 写独立分享历史删除失败测试**

覆盖 StandaloneShareBatch、操作、结果、LocalResultExports、StandaloneShare WebAutomationAttempts 及子证据；断言终态闭包消失且非终态保留。

- [ ] **Step 3: 写冲突与回滚失败测试**

目标存在 `LocalResultExports.Status='InProgress'`/有效租约、非终态 WebAutomationAttempts，或任何仍处于等待、保留、准备、提交、结果待确认、暂停、等待决定、可恢复/可重试状态或持有租约的 `TransferOperations`、`ShareOperations`、`StandaloneShareOperations` 时返回 `Conflict` 且零删除。特别构造 `CompletedWithShortfall` 批次内部仍含待处理/可重试分享操作的场景，必须拒绝删除。制造中途约束失败时整个事务回滚。

- [ ] **Step 4: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --filter SqliteLocalTerminalHistoryStoreTests`

- [ ] **Step 5: 实现事务内目标重选和显式删除顺序**

使用临时目标表保存事务开始时仍处于终态且其全部子操作均已不可继续、无活动租约的批次/操作。先对导出、自动化尝试及所有转存/分享子操作做冲突查询，再删除 `LocalResultExports`、自动化尝试全部子表和尝试根记录；转存路径继续删除 ExecutionLogs、分享操作、转存操作、账号分配、资源，置空 ImportSessions 引用，最后删除 BatchJobs；独立分享路径删除操作后再删除批次。复用 `SqliteAccountRemovalStore` 中的自动化证据删除 SQL 结构，避免遗漏。

- [ ] **Step 6: 执行残留与外键校验**

提交前查询所有目标 owner/id 是否仍有记录，并执行 `PRAGMA foreign_key_check`；不满足则抛出并回滚。

- [ ] **Step 7: 运行测试并提交**

Expected: PASS。

Commit: `feat: safely delete local terminal task history`

### Task 6: 接入转存和分享清空历史交互

**Files:**
- Create: `src/BaiduBatchTransfer.App/Features/Common/LocalHistoryCleanupInteraction.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Common/WorkspaceRunPanelViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Jobs/JobResultsViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceViewModel.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml`
- Modify: `src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceView.xaml`
- Modify: `src/BaiduBatchTransfer.App/AppBootstrap.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/WorkspaceRunPanelViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/JobResultsViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/Features/ShareWorkspaceViewModelTests.cs`
- Test: `tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs`

- [ ] **Step 1: 写 ViewModel 失败测试**

覆盖无历史提示、用户取消、确认文本包含“仅删除本地记录，不影响百度网盘文件和分享链接”、工作区繁忙禁用、预览发现冲突时 `CanExecute == false`、执行时竞态冲突、成功后重新加载和完整空状态。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "WorkspaceRunPanelViewModelTests|TransferWorkspaceViewModelTests|ShareWorkspaceViewModelTests|AppBootstrapTests"`

- [ ] **Step 3: 实现确认交互和命令**

确认交互只返回 bool，不执行删除。转存和分享 ViewModel 在初始化、手动刷新、批次进入终态以及清理完成后异步刷新 cleanup preview，保存终态数量和冲突状态，并据此更新 `CanClearHistory`/命令状态；执行路径仍做 Preview → Confirm → Delete，数据库事务内再次筛选与复检。删除成功后调用各自已有初始化流程重新加载。

`JobResultsViewModel.Reset()` 必须取消保存、递增 generation、清空 `BatchJobId`、Items、选择、分页游标、已加载 ID、全部统计数、生命周期快照与状态消息，并统一触发属性/命令状态通知。`WorkspaceRunPanelViewModel` 的显式重载在空历史时清空 Batches、SelectedBatch 并调用该原子 Reset，确保旧批次 ID、统计和可执行命令都不残留。

- [ ] **Step 4: 增加历史按钮和禁用状态**

“处理结果”加入“清空转存历史”，“分享结果”加入“清空分享历史”；使用清晰的 AutomationProperties.Name。按钮不得放入“更多”隐藏菜单。

- [ ] **Step 5: 注册 DI 并运行测试**

注册 SQLite store、应用服务和 WPF 确认交互；定向测试 Expected: PASS。

- [ ] **Step 6: 提交**

Commit: `feat: add local terminal history cleanup controls`

### Task 7: 全量验证与本地验收产物

**Files:**
- Modify: 必要的结构测试或交接说明（仅当验证发现缺口）

- [ ] **Step 1: 搜索旧品牌残留并人工分类**

Run: `rg -n "百度批量转存|百度网盘批量工作台" src tests`

Expected: 面向用户位置零残留；仅历史测试数据或明确业务描述可保留。

- [ ] **Step 2: 运行受影响项目测试**

Run: `dotnet test tests/SuperPan.Licensing.Client.Tests/SuperPan.Licensing.Client.Tests.csproj`

Run: `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj`

Run: `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj`

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj`

Expected: 全部 PASS。

- [ ] **Step 2a: 验证本地安全边界文件不变**

基础设施集成测试在临时目录创建模拟授权状态文件和已导出的 Excel/CSV 文件，记录哈希后执行两类历史清理，断言文件仍存在且哈希不变；同时断言应用数据库之外的授权状态路径从未被打开或写入。

- [ ] **Step 3: 构建桌面应用**

Run: `dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Release --no-restore`

Expected: 0 warnings/errors（已有明确基线警告除外且需记录）。

- [ ] **Step 4: 本地启动做手工验收**

验证标题、授权套餐/有效期、激活窗口、四个清空按钮、二次确认、重启后终态历史不再出现、百度网盘远端文件/分享未触发任何删除请求。

- [ ] **Step 5: 检查差异并提交最终验证记录**

Run: `git diff --check`

Run: `git status --short`

Commit: `test: verify local license and history interface updates`

不得部署服务器；完成后只向用户提供本地可验收路径和测试结果。
