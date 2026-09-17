# 账号成功/失败分表导出交接

## 当前分支

- 工作树：`.worktrees/transfer-share-link-replacement`
- 分支：`codex/transfer-share-link-replacement`
- 本次需求提交：
  - `5ecac93 feat: persist operation share input and latest values`
  - `6f5daf4 feat: select failed job results across pages`
  - `af7454c feat: split replacement exports by account outcome`

## 已完成

- 结果数据保存本次输入链接/提取码和最新生成链接/提取码。
- 成功记录导出时替换链接与提取码；新分享无提取码时清空提取码。
- 失败、暂停、待处理、等待、取消和未知状态进入失败文件，并保留原链接/提取码。
- 每个账号按账号 ID 拆分，文件命名为：
  - `账号名_成功_yyyyMMddHHmmss.xlsx`
  - `账号名_失败_yyyyMMddHHmmss.xlsx`
- 没有对应记录的成功/失败文件不生成，历史文件不覆盖。
- 结果页增加“选中失败”和“清除选择”，选择跨分页和刷新保持，切换批次清空。

## 已验证

```text
dotnet test tests/BaiduBatchTransfer.Core.Tests/BaiduBatchTransfer.Core.Tests.csproj --no-restore
通过：356 项

dotnet build src/BaiduBatchTransfer.Infrastructure/BaiduBatchTransfer.Infrastructure.csproj --no-restore
成功：0 警告，0 错误

dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --no-restore --filter "FullyQualifiedName~SelectFailed|FullyQualifiedName~FailedSelection"
通过：2 项

dotnet test tests/BaiduBatchTransfer.Application.Tests/BaiduBatchTransfer.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~ReplacementWorkbookExportTests"
通过：34 项
```

## 尚未完成

- 真实 OpenXML 文件读回测试尚未补齐；当前导出测试使用 writer/publisher 捕获行验证。
- 新的分表导出 API 尚未接入现有“另存结果表格”按钮的完整 UI 流程。
- 尚未完成失败表重新导入的 round-trip 集成测试。
- 当前工作树存在其他历史未提交文档和临时文件，未触碰、未清理。
- 未执行任何服务器、SSH、staging 或生产操作。

## 下一步

1. 补真实 `.xlsx` 读回测试，锁定标准列、成功链接/提取码替换和失败值保留。
2. 将 `ExportSplitToDirectoryAsync` 接入结果页的导出命令，并显示生成文件清单。
3. 增加失败表重新导入后账号/目录/链接/提取码保持的集成测试。
4. 完成 UI 回归后再更新主交接文件。

## 服务器边界

当前没有进行服务器操作。任何 staging 或生产连接前必须先通知用户并取得明确确认。
