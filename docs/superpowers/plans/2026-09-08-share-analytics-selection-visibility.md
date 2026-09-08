# 分享数据选中行可见性修复实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让分享数据表格的选中行在聚焦和失焦状态下都保持文字与操作内容清晰可见。

**Architecture:** 在 `ShareAnalyticsView.xaml` 内添加表格专用的局部行/单元格选中样式，显式覆盖系统选中颜色，不修改全局主题。使用现有 XAML 结构测试验证颜色声明及作用域，避免回归影响其他表格。

**Tech Stack:** .NET 8、WPF XAML、xUnit、LINQ to XML

---

### Task 1: 锁定并修复选中颜色冲突

**Files:**
- Modify: `tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewStructureTests.cs`
- Modify: `src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsView.xaml`

- [ ] **Step 1: 写失败测试**

新增结构测试，要求分享数据视图声明局部 `DataGridRow` 与 `DataGridCell` 样式，并让表格显式使用行样式。测试分别验证活动选中和 `Selector.IsSelectionActive=False` 的非活动选中状态都设置 `TextBrush` 前景；普通选中单元格使用 `NavSelectedBrush`，排序列的派生样式继续拥有最后生效的浅黄色背景触发器。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~ShareAnalyticsViewStructureTests.Selection" --no-restore`

Expected: FAIL，提示缺少局部选中样式或绑定。

- [ ] **Step 3: 最小实现**

在 `ShareAnalyticsView.xaml` 的 `UserControl.Resources` 中添加专用行样式：默认透明背景和深色前景，分别为活动选中与非活动选中声明 `NavSelectedBrush` 背景和 `TextBrush` 前景。在基础单元格样式中为两种选中状态声明相同颜色，以覆盖系统模板颜色；排序列样式继续基于该基础样式，并由现有派生触发器覆盖背景为 `ShareAnalyticsSortedCellBrush`。把表格 `RowStyle` 显式绑定到局部样式，所有改动均留在 `ShareAnalyticsView` 资源范围内。

- [ ] **Step 4: 运行定向测试确认通过**

Run: `dotnet test tests/BaiduBatchTransfer.App.Tests/BaiduBatchTransfer.App.Tests.csproj --filter "FullyQualifiedName~ShareAnalyticsViewStructureTests" --no-restore`

Expected: PASS。

- [ ] **Step 5: 构建应用并复核差异**

Run: `dotnet build src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj --no-restore`

Expected: Build succeeded；差异仅包含规格、计划、测试与分享数据视图的局部样式。
