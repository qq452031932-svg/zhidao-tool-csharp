# 分享数据序号列收窄 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将分享数据表格的“序号”列从 `58px` 收窄为 `44px`，且不影响其他输入框、列和行为。

**Architecture:** 保持现有 WPF DataGrid 结构，只在结构测试中固定目标宽度，再做一处最小 XAML 修改。标题缓存、数据同步、排序、选择和导出逻辑均不改动。

**Tech Stack:** .NET 8、WPF XAML、xUnit

---

### Task 1: 收窄序号列并验证

**Files:**
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewStructureTests.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml`

- [ ] **Step 1: 写入失败的结构断言**

在 `DataGridDeclaresTheTwelveVisibleColumnsInProductOrder` 中按语义查找“序号”列并断言：

```csharp
var sequenceColumn = columns.Single(column => ColumnSemantic(column) == "序号");
Assert.Equal("44", Attribute(sequenceColumn, "Width"));
```

- [ ] **Step 2: 运行定向测试并确认先失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --filter "FullyQualifiedName~ShareAnalyticsViewStructureTests" --logger "console;verbosity=minimal"`

Expected: FAIL，实际宽度为 `58`，期望为 `44`。

- [ ] **Step 3: 做最小实现**

把 `ShareAnalyticsView.xaml` 中表头为“序号”的 `DataGridTemplateColumn` 从：

```xml
<DataGridTemplateColumn Width="58">
```

改为：

```xml
<DataGridTemplateColumn Width="44">
```

不要修改前一列选择框的 `44px`，也不要修改工具栏“从/到”输入框的 `54px`。

- [ ] **Step 4: 运行定向测试并确认通过**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj -c Release --filter "FullyQualifiedName~ShareAnalyticsViewStructureTests" --logger "console;verbosity=minimal"`

Expected: PASS。

- [ ] **Step 5: 构建 Release**

Run: `dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj -c Release --no-restore`

Expected: Build succeeded，0 errors。

- [ ] **Step 6: 启动并检查真实窗口**

启动 Release 应用，打开“分享数据”，确认序号列明显收窄且完整显示；表头仍居中、行序号仍右对齐，范围输入框及其他列不变。

- [ ] **Step 7: 提交**

```bash
git add docs/superpowers/specs/2026-09-03-share-analytics-sequence-width-design.md docs/superpowers/plans/2026-09-03-share-analytics-sequence-width.md tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewStructureTests.cs src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml
git commit -m "fix: narrow analytics sequence column"
```
