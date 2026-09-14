# 2026-09-14 本地授权、历史清理与品牌调整交接

## 范围

本次只修改 Windows 本地客户端，未部署服务器、未调用百度网盘删除接口、未清理用户现有任务数据，也未启动客户端连接真实账号做写入验收。

## 已实现

- 主窗口显示本次实际验证通过卡密的套餐和本地到期时间。
- 软件面向用户名称统一为“知道综合工具”。
- 卡密窗口完成视觉、缩放、高对比度、无障碍和取消竞态调整。
- 转存与分享运行日志支持纯内存清空。
- 转存与独立分享结果支持删除本地终态历史；不影响百度网盘文件和远端分享。
- 转存顶部“选择 Excel”旁新增紧凑的“清空”按钮（无障碍名称“清空当前导入”），可清除当前本地待处理导入并恢复空白工作区；不会删除已结束任务历史。
- 删除使用确认快照、事务内复检、活动租约/未决操作冲突保护、完整本地证据闭包、回滚和外键校验。
- 清理后完整重置或重载界面；冲突会自动重查，迟到异步请求不会恢复已删除的旧结果。

## 新鲜验证结果

- RED：新增结构测试后，`dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter FullyQualifiedName~TransferWorkspaceViewStructureTests` 因页面尚无清理按钮而失败，1 failed、0 passed。
- GREEN：同一结构测试加入按钮后为 1 passed、0 failed；同时 `TopConfigurationToolbarStaysOnOneLineAtTypicalWorkspaceWidthOnSta` 与结构测试合跑为 2 passed、0 failed，836px 工作区中 7 项保持单行。
- `dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter FullyQualifiedName~LocalImportDraftCleanupServiceTests`：13 passed、0 failed。
- `dotnet test tests/BaiduBatchTransfer.Infrastructure.Tests/BaiduBatchTransfer.Infrastructure.Tests.csproj --no-restore --filter FullyQualifiedName~SqliteLocalImportDraftCleanupStoreTests`：12 passed、0 failed。
- `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~ImportViewModelTests|FullyQualifiedName~CreateJobWizardViewModelTests|FullyQualifiedName~TransferWorkspaceViewModelTests|FullyQualifiedName~CurrentImportCleanupInteractionTests|FullyQualifiedName~TransferWorkspaceViewStructureTests|FullyQualifiedName~LocalHistoryCleanup"`：302 passed、0 failed。
- Updates 聚焦验证命令：`dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~BaiduBatchTransfer.App.Tests.Updates.ApplicationBuildInfoTests|FullyQualifiedName~BaiduBatchTransfer.App.Tests.Updates.ApplicationUpdateServiceTests|FullyQualifiedName~BaiduBatchTransfer.App.Tests.Updates.VelopackUpdateRuntimeTests|FullyQualifiedName~BaiduBatchTransfer.App.Tests.Updates.VelopackStartupStructureTests|FullyQualifiedName~BaiduBatchTransfer.App.Tests.Updates.UpdateShutdownIntegrationTests|FullyQualifiedName~BaiduBatchTransfer.App.Tests.Updates.SignedFeedUpdateSourceTests|FullyQualifiedName~BaiduBatchTransfer.App.Tests.Updates.SignedFeedStateStoreTests|FullyQualifiedName~BaiduBatchTransfer.App.Tests.Updates.SignedFeedManifestTests|FullyQualifiedName~BaiduBatchTransfer.App.Tests.Updates.OtaPublisherTrustTests"`。覆盖 9 个类，202 passed、2 skipped、0 failed（总计 204）；跳过项为需要证书存储环境的 `OtaPublisherTrustTests` 用例。
- `dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj --no-restore --configuration Debug`：成功，0 warnings、0 errors。
- `git diff --check`：通过。

## 已知的非本次范围基线问题

曾启动 `BaiduBatchTransfer.App.Tests` 全套验证，但在看到多项既有失败后主动停止，未把它计为通过：

- `LicenseLocalStackScriptTests` 多项依赖本机运行时清理状态的断言失败。
- `ManagedBrowserHelperClientTests` 出现辅助进程启动超时。
- `TransferCoordinatorHostedServiceTests`、`VisibleWebAutomationQueueTests` 和 `OtaDeploymentStructureTests` 出现既有断言失败。

这些失败不位于本次改动文件；本轮 Task 4 指定的 App 回归筛选已单独完成 302 项全绿，但不据此宣称完整 App 测试套件通过。完整 Release 构建仍需要生产授权信任参数（包括 ServiceUrl），本次本地 UI 调整未提供或猜测该参数。

## 本地验收入口

Debug 可执行文件：

`C:\Users\45203\Documents\dakazhandian\.worktrees\license-production-deploy\src\BaiduBatchTransfer.App\bin\Debug\net8.0-windows\BaiduBatchTransfer.App.exe`

建议人工验收：

1. 查看主界面“知道综合工具”、套餐和有效期。
2. 打开激活窗口检查布局、示例提示和按钮状态。
3. 分别验证转存/分享“清空日志”。
4. 使用测试数据验证“清空转存历史”“清空分享历史”的确认文案与重启后不再加载。
5. 导入测试数据后验证顶部“清空”按钮：确认取消不改变数据，确认清理后工作区为空且可重新导入。
6. 确认百度网盘中的文件和分享链接保持不变。

本轮仅执行本地测试和 Debug 构建；未部署服务器，未调用百度网盘远程删除接口，也未删除百度网盘文件或分享链接。

## Git 状态

- 分支：`codex/license-production-deploy`
- 工作树：`C:\Users\45203\Documents\dakazhandian\.worktrees\license-production-deploy`
- 保留该工作树供用户本地验收；未合并、未推送、未部署。
