# 2026-09-14 本地授权、历史清理与品牌调整交接

## 范围

本次只修改 Windows 本地客户端，未部署服务器、未调用百度网盘删除接口、未清理用户现有任务数据，也未启动客户端连接真实账号做写入验收。

## 已实现

- 主窗口显示本次实际验证通过卡密的套餐和本地到期时间。
- 软件面向用户名称统一为“知道综合工具”。
- 卡密窗口完成视觉、缩放、高对比度、无障碍和取消竞态调整。
- 转存与分享运行日志支持纯内存清空。
- 转存与独立分享结果支持删除本地终态历史；不影响百度网盘文件和远端分享。
- 删除使用确认快照、事务内复检、活动租约/未决操作冲突保护、完整本地证据闭包、回滚和外键校验。
- 清理后完整重置或重载界面；冲突会自动重查，迟到异步请求不会恢复已删除的旧结果。

## 新鲜验证结果

- `SuperPan.Licensing.Client.Tests` 全套：104 passed，0 failed。
- `BaiduBatchTransfer.Application.Tests` 全套：1195 passed，0 failed。
- 本次变更相关 App 测试：495 passed，0 failed。
- `SqliteLocalTerminalHistoryStoreTests`：107 passed，0 failed。
- `dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj --no-restore --configuration Debug`：成功，0 warnings，0 errors。
- `git diff --check`：通过。
- 最终独立代码审阅：Ready Yes，无 Critical / Important / Minor 遗留。

## 已知的非本次范围基线问题

曾启动 `BaiduBatchTransfer.App.Tests` 全套验证，但在看到多项既有失败后主动停止，未把它计为通过：

- `LicenseLocalStackScriptTests` 多项依赖本机运行时清理状态的断言失败。
- `ManagedBrowserHelperClientTests` 出现辅助进程启动超时。
- `TransferCoordinatorHostedServiceTests`、`VisibleWebAutomationQueueTests` 和 `OtaDeploymentStructureTests` 出现既有断言失败。

这些失败不位于本次改动文件；本次相关 App 测试已单独完成 495 项全绿。完整 Release 构建仍需要生产授权信任参数（包括 ServiceUrl），本次本地 UI 调整未提供或猜测该参数。

## 本地验收入口

Debug 可执行文件：

`C:\Users\45203\Documents\dakazhandian\.worktrees\license-production-deploy\src\BaiduBatchTransfer.App\bin\Debug\net8.0-windows\BaiduBatchTransfer.App.exe`

建议人工验收：

1. 查看主界面“知道综合工具”、套餐和有效期。
2. 打开激活窗口检查布局、示例提示和按钮状态。
3. 分别验证转存/分享“清空日志”。
4. 使用测试数据验证“清空转存历史”“清空分享历史”的确认文案与重启后不再加载。
5. 确认百度网盘中的文件和分享链接保持不变。

## Git 状态

- 分支：`codex/license-production-deploy`
- 工作树：`C:\Users\45203\Documents\dakazhandian\.worktrees\license-production-deploy`
- 保留该工作树供用户本地验收；未合并、未推送、未部署。
