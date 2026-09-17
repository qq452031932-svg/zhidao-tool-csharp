using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class ReplacementWorkbookExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 9, 10, 11, TimeSpan.Zero);

    [Fact]
    public async Task OneResourcePreservesOriginalColumnsAndAddsOrderedAccountResultGroups()
    {
        var batch = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var repository = new StubProjectionRepository(
            new BatchResultProjectionDefinition(
                "源文件.xlsx",
                [
                    new(2, "关键词"),
                    new(0, "原标题"),
                    new(1, "链接原文"),
                ],
                [
                    new(accountB, 2, "账号B", ShareEnabled: false),
                    new(accountA, 1, "账号A", ShareEnabled: true),
                ]),
            new BatchResultProjectionPage(
                [new(resource, 1,
                    [new(1, "含链接的原始文案"), new(0, "用户标题"), new(2, "教程")],
                    "用户标题",
                    "https://pan.baidu.com/s/original")],
                [
                    new(resource, accountA, 1, "Succeeded", ShareOperationStatus.Succeeded,
                        "https://pan.baidu.com/s/replacement?pwd=a1b2", "a1b2", null),
                    new(resource, accountB, 2, "Succeeded", null, null, null, null),
                ],
                NextCandidateSequence: null));
        var writer = new CapturingReplacementWriter();
        var service = new ReplacementWorkbookExportService(
            repository,
            new ReplacementWorkbookFileNameBuilder(),
            new FixedTimeProvider(Now));

        var result = await service.ExportAsync(
            new ReplacementWorkbookExportRequest(batch, PageSize: 100),
            Stream.Null,
            writer);

        Assert.Equal("源文件_转存分享结果_20260823091011.xlsx", result.SuggestedFileName);
        Assert.Equal(1, result.ExportedRows);
        Assert.Equal(
        new[]
        {
            "原标题", "链接原文", "关键词",
            "转存结果_解析后标题", "转存结果_原分享链接",
            "转存结果_账号A_实际转存账号", "转存结果_账号A_转存状态",
            "转存结果_账号A_新分享链接", "转存结果_账号A_提取码", "转存结果_账号A_分享状态",
            "转存结果_账号B_实际转存账号", "转存结果_账号B_转存状态",
            "转存结果_账号B_新分享链接", "转存结果_账号B_提取码", "转存结果_账号B_分享状态",
        }, writer.Layout!.Headers.ToArray());
        var row = Assert.Single(writer.Rows);
        Assert.Equal(
        new[]
        {
            "用户标题", "含链接的原始文案", "教程",
            "用户标题", "https://pan.baidu.com/s/original",
            "账号A", "转存成功", "https://pan.baidu.com/s/replacement?pwd=a1b2", "a1b2", "分享成功",
            "账号B", "转存成功", "", "", "",
        }, row.TextCells.ToArray());
        Assert.Equal("?pwd=" + row.TextCells[8], new Uri(row.TextCells[7]).Query);
        Assert.Matches("^[A-Za-z0-9]{4}$", row.TextCells[8]);
        Assert.Equal([(0L, 100)], repository.ProjectionPageRequests);
    }

    [Fact]
    public async Task DistributionAccountWithoutOutcomeLeavesItsFiveCellsBlank()
    {
        var writer = await ExportSingleRowAsync(
            OriginalDefinition(shareEnabled: true),
            OriginalResource(),
            []);

        Assert.Equal(
            new[] { "标题", "原值", "标题", "原链接", "", "", "", "", "" },
            Assert.Single(writer.Rows).TextCells.ToArray());
    }

    [Fact]
    public async Task ReplicationExportsOneResultGroupForEverySelectedAccount()
    {
        var resource = Guid.NewGuid();
        var firstAccount = Guid.NewGuid();
        var secondAccount = Guid.NewGuid();
        var definition = OriginalDefinition(firstAccount) with
        {
            Accounts =
            [
                new(firstAccount, 1, "账号一", true),
                new(secondAccount, 2, "账号二", true),
            ],
        };
        var writer = await ExportSingleRowAsync(
            definition,
            OriginalResource(resource),
            [
                new(resource, firstAccount, 1, "Succeeded", ShareOperationStatus.Succeeded, "https://new/1", "a111", null),
                new(resource, secondAccount, 2, "Succeeded", ShareOperationStatus.ReusedExisting, "https://new/2", "b222", null),
            ]);

        Assert.Equal(
            new[]
            {
                "标题", "原值", "标题", "原链接",
                "账号一", "转存成功", "https://new/1", "a111", "分享成功",
                "账号二", "转存成功", "https://new/2", "b222", "复用已有分享",
            },
            Assert.Single(writer.Rows).TextCells.ToArray());
    }

    [Fact]
    public async Task ShareDisabledAccountWithoutTransferOutcomeStillLeavesItsFiveCellsBlank()
    {
        var writer = await ExportSingleRowAsync(
            OriginalDefinition(shareEnabled: false),
            OriginalResource(),
            []);

        Assert.Equal(
            new[] { "标题", "原值", "标题", "原链接", "", "", "", "", "" },
            Assert.Single(writer.Rows).TextCells.ToArray());
    }

    [Fact]
    public async Task MissingSourceTitleFallbackSurvivesShareDisabledTransferFailure()
    {
        var account = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var writer = await ExportSingleRowAsync(
            OriginalDefinition(account, shareEnabled: false),
            OriginalResource(
                resource,
                originalValues: [new(0, null), new(1, "源表中的链接原文")],
                resolvedTitle: "网盘内首项名称 等 3 项"),
            [new(resource, account, 1, "Failed", null, null, null, "TRANSFER_FAILED")]);

        Assert.Equal(
            new[]
            {
                "", "源表中的链接原文",
                "网盘内首项名称 等 3 项", "原链接",
                "账号A", "转存失败", "", "", "",
            },
            Assert.Single(writer.Rows).TextCells.ToArray());
    }

    [Fact]
    public async Task OutputOrdinalsMustBeTheContiguousSequenceStartingAtOne()
    {
        var account = Guid.NewGuid();
        var definition = OriginalDefinition(accountId: account) with
        {
            Accounts = [new(account, 2, "账号A", ShareEnabled: true)],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportSingleRowAsync(
            definition,
            OriginalResource(),
            []));
    }

    [Theory]
    [InlineData("Failed", ShareOperationStatus.Succeeded, "转存失败", false)]
    [InlineData("Paused", ShareOperationStatus.Succeeded, "转存暂停", false)]
    [InlineData("ResultPending", ShareOperationStatus.Succeeded, "转存结果待确认", false)]
    [InlineData("Succeeded", ShareOperationStatus.Failed, "分享失败", false)]
    [InlineData("Succeeded", ShareOperationStatus.ResultPending, "分享结果待确认", false)]
    [InlineData("Succeeded", ShareOperationStatus.Succeeded, "分享成功", true)]
    [InlineData("Succeeded", ShareOperationStatus.ReusedExisting, "复用已有分享", true)]
    public async Task OutcomeStatusControlsStableTextAndWhetherShareSecretsAreExported(
        string transferStatus,
        ShareOperationStatus shareStatus,
        string expectedStatus,
        bool exposesShareResult)
    {
        var account = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var writer = await ExportSingleRowAsync(
            OriginalDefinition(accountId: account, shareEnabled: true),
            OriginalResource(resource),
            [new(resource, account, 1, transferStatus, shareStatus, "https://pan.baidu.com/s/new", "a1b2", null)]);

        var cells = Assert.Single(writer.Rows).TextCells;
        Assert.Equal(new[] { "标题", "原值", "标题", "原链接" }, cells[..4].ToArray());
        Assert.Equal("账号A", cells[4]);
        Assert.Equal(transferStatus switch
        {
            "Failed" => "转存失败",
            "Paused" => "转存暂停",
            "ResultPending" => "转存结果待确认",
            "Succeeded" => "转存成功",
            _ => throw new InvalidOperationException(),
        }, cells[5]);
        Assert.Equal(exposesShareResult ? "https://pan.baidu.com/s/new" : "", cells[6]);
        Assert.Equal(exposesShareResult ? "a1b2" : "", cells[7]);
        Assert.Equal(transferStatus == "Succeeded" ? expectedStatus : "", cells[8]);
    }

    [Fact]
    public void ResultLayoutUsesFiveColumnsPerParticipatingAccount()
    {
        var accounts = new[]
        {
            new BatchWorkloadAccountInput(Guid.NewGuid(), 1, "账号A", BatchAccountSelectionDecision.Selected, true, 1),
            new BatchWorkloadAccountInput(Guid.NewGuid(), 2, "账号B", BatchAccountSelectionDecision.Selected, false, 1),
        };

        var layout = new ResultColumnLayoutBuilder().Build(["原列1", "原列2"], accounts);

        Assert.Equal(layout.SourceHeaders.Length + 2 + accounts.Length * 5, layout.ColumnCount);
        Assert.All(layout.Accounts, account =>
        {
            Assert.False(string.IsNullOrWhiteSpace(account.ActualTransferAccountHeader));
            Assert.False(string.IsNullOrWhiteSpace(account.TransferStatusHeader));
        });
    }

    [Theory]
    [InlineData("Pasted")]
    [InlineData("ExcelWithoutTitleMapping")]
    public async Task PersistedResolvedFallbackIsExportedIndependentlyOfImportSource(string sourceKind)
    {
        var account = Guid.NewGuid();
        var definition = sourceKind == "Pasted"
            ? new BatchResultProjectionDefinition(
                "粘贴内容.xlsx",
                [new(0, "分享链接")],
                [new(account, 1, "账号A", false)])
            : new BatchResultProjectionDefinition(
                "无标题映射.xlsx",
                [new(0, "关键词"), new(1, "分享链接")],
                [new(account, 1, "账号A", false)]);
        IReadOnlyList<BatchResultOriginalValue> originalValues = sourceKind == "Pasted"
            ? [new(0, "https://source/pasted")]
            : [new(0, "教程"), new(1, "https://source/excel")];
        var writer = await ExportSingleRowAsync(
            definition,
            new(Guid.NewGuid(), 1, originalValues, "网盘显示名称 等 2 项", "https://original"),
            []);

        var row = Assert.Single(writer.Rows);
        Assert.Equal("网盘显示名称 等 2 项", row.TextCells[definition.OriginalColumns.Count]);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Duplicate")]
    [InlineData("Unknown")]
    public async Task OriginalValuesMustMatchDefinedOrdinalsExactly(string mismatch)
    {
        IReadOnlyList<BatchResultOriginalValue> values = mismatch switch
        {
            "Missing" => [new(0, "标题")],
            "Duplicate" => [new(0, "标题"), new(0, "重复"), new(1, "原值")],
            "Unknown" => [new(0, "标题"), new(1, "原值"), new(2, "未知")],
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportSingleRowAsync(
            OriginalDefinition(),
            OriginalResource(originalValues: values),
            []));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolvedTitleMustBeNonBlank(string resolvedTitle)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportSingleRowAsync(
            OriginalDefinition(),
            OriginalResource(resolvedTitle: resolvedTitle),
            []));
    }

    [Theory]
    [InlineData("UnknownAccount")]
    [InlineData("WrongOutputOrdinal")]
    [InlineData("UnknownResource")]
    [InlineData("Duplicate")]
    public async Task AccountOutcomeMustBelongToCurrentResourceAndAccountLayout(string mismatch)
    {
        var account = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var outcome = new BatchResultAccountProjection(
            mismatch == "UnknownResource" ? Guid.NewGuid() : resource,
            mismatch == "UnknownAccount" ? Guid.NewGuid() : account,
            mismatch == "WrongOutputOrdinal" ? 2 : 1,
            "Succeeded",
            ShareOperationStatus.Succeeded,
            "https://pan.baidu.com/s/new",
            "a1b2",
            null);
        IReadOnlyList<BatchResultAccountProjection> outcomes = mismatch == "Duplicate"
            ? [outcome, outcome]
            : [outcome];

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportSingleRowAsync(
            OriginalDefinition(accountId: account),
            OriginalResource(resource),
            outcomes));
    }

    [Theory]
    [InlineData("Descending")]
    [InlineData("Duplicate")]
    public async Task ResourcePageCandidateSequenceMustBeStrictlyIncreasing(string mismatch)
    {
        var account = Guid.NewGuid();
        var first = OriginalResource(candidateSequence: mismatch == "Descending" ? 2 : 1);
        var second = OriginalResource(candidateSequence: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportAsync(
            OriginalDefinition(accountId: account),
            new([first, second], [], NextCandidateSequence: null),
            []));
    }

    [Fact]
    public async Task NonNullNextCursorMustEqualTheLastCandidateSequence()
    {
        var repository = new StubProjectionRepository(
            OriginalDefinition(),
            new([OriginalResource(candidateSequence: 1)], [], NextCandidateSequence: 2));
        var service = new ReplacementWorkbookExportService(
            repository,
            new ReplacementWorkbookFileNameBuilder(),
            new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportAsync(new(Guid.NewGuid(), 100), Stream.Null, new CapturingReplacementWriter()));

        Assert.Single(repository.ProjectionPageRequests);
    }

    [Fact]
    public async Task ResourcePageSizeShrinksToBoundHighAccountFanout()
    {
        var accounts = Enumerable.Range(1, 1_000)
            .Select(index => new BatchResultProjectionAccount(Guid.NewGuid(), index, $"账号{index}", true))
            .ToArray();
        var repository = new StubProjectionRepository(
            OriginalDefinition() with { Accounts = accounts },
            new([OriginalResource()], [], NextCandidateSequence: null));
        var service = new ReplacementWorkbookExportService(
            repository,
            new ReplacementWorkbookFileNameBuilder(),
            new FixedTimeProvider(Now));

        await service.ExportAsync(
            new ReplacementWorkbookExportRequest(Guid.NewGuid(), PageSize: 1_000),
            Stream.Null,
            new CapturingReplacementWriter());

        Assert.Equal([(0L, 1)], repository.ProjectionPageRequests);
    }

    [Fact]
    public async Task DirectoryExportDerivesFinalNameFromPersistedSourceAndReturnsRedactedReceipt()
    {
        var batch = Guid.NewGuid();
        var destinationDirectory = Path.Combine(Path.GetTempPath(), "caller-selected-directory");
        var publisher = new CapturingReplacementPublisher();
        var service = new ReplacementWorkbookExportService(
            new StubProjectionRepository(
                OriginalDefinition() with { SourceFileName = "C:\\不可信原目录\\用户原始文件.xlsx" },
                new([OriginalResource()], [], NextCandidateSequence: null)),
            new ReplacementWorkbookFileNameBuilder(),
            new FixedTimeProvider(Now));

        var receipt = await service.ExportToDirectoryAsync(
            new ReplacementWorkbookExportRequest(batch, 100),
            destinationDirectory,
            publisher);

        Assert.Equal(destinationDirectory, publisher.DestinationDirectory);
        const string expectedFileName = "用户原始文件_转存分享结果_20260823091011.xlsx";
        Assert.Equal(expectedFileName, publisher.FinalFileName);
        Assert.Equal(1, receipt.ExportedRows);
        Assert.Equal(1, receipt.SheetCount);
        Assert.Equal(Path.Combine(destinationDirectory, expectedFileName), receipt.FinalPath);
        Assert.DoesNotContain(destinationDirectory, receipt.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(expectedFileName, receipt.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SuggestedFileNameSanitizesInvalidSourceCharacters()
    {
        var result = new ReplacementWorkbookFileNameBuilder().Build("bad:name?.xlsx", Now);

        Assert.Equal("bad_name__转存分享结果_20260823091011.xlsx", result);
    }

    [Fact]
    public void SplitFileNameUsesAccountResultAndTimestamp()
    {
        var result = new ReplacementWorkbookFileNameBuilder().BuildSplit("账号A", "成功", Now);
        Assert.Equal("账号A_成功_20260823091011.xlsx", result);
    }

    [Fact]
    public async Task SplitExportPublishesSuccessAndFailureFilesPerAccount()
    {
        var account = Guid.NewGuid(); var resource = Guid.NewGuid();
        var publisher = new SplitCapturingPublisher();
        var service = new ReplacementWorkbookExportService(
            new StubProjectionRepository(
                OriginalDefinition(account),
                new([OriginalResource(resource)], [
                    new(resource, account, 1, "Succeeded", ShareOperationStatus.Succeeded, "https://new", "n123", null),
                    new(Guid.NewGuid(), account, 1, "Failed", null, null, null, "ERR")], null)),
            new ReplacementWorkbookFileNameBuilder(), new FixedTimeProvider(Now));

        var receipts = await service.ExportSplitToDirectoryAsync(new(Guid.NewGuid()), "out", publisher);
        Assert.Single(receipts);
        Assert.Equal("账号A_成功_20260823091011.xlsx", publisher.Files[0].Name);
        Assert.Contains("成功", publisher.Files[0].Rows.Single().TextCells);
    }

    [Fact]
    public void SuggestedFileNameBoundsLongSourceNameWithStableHash()
    {
        var source = new string('长', 300) + ".xlsx";
        var builder = new ReplacementWorkbookFileNameBuilder();

        var first = builder.Build(source, Now);
        var second = builder.Build(source, Now);

        Assert.Equal(first, second);
        Assert.True(first.Length <= 240, $"Generated file name had {first.Length} characters.");
        Assert.EndsWith("_转存分享结果_20260823091011.xlsx", first, StringComparison.Ordinal);
    }

    private static BatchResultProjectionDefinition OriginalDefinition(
        Guid? accountId = null,
        bool shareEnabled = true) =>
        new(
            "源文件.xlsx",
            [new(0, "原标题"), new(1, "链接原文")],
            [new(accountId ?? Guid.NewGuid(), 1, "账号A", shareEnabled)]);

    private static BatchResultResourceProjection OriginalResource(
        Guid? resourceId = null,
        long candidateSequence = 1,
        IReadOnlyList<BatchResultOriginalValue>? originalValues = null,
        string resolvedTitle = "标题") =>
        new(
            resourceId ?? Guid.NewGuid(),
            candidateSequence,
            originalValues ?? [new(0, "标题"), new(1, "原值")],
            resolvedTitle,
            "原链接");

    private static async Task<CapturingReplacementWriter> ExportSingleRowAsync(
        BatchResultProjectionDefinition definition,
        BatchResultResourceProjection resource,
        IReadOnlyList<BatchResultAccountProjection> outcomes) =>
        await ExportAsync(definition, new([resource], [], NextCandidateSequence: null), outcomes);

    private static async Task<CapturingReplacementWriter> ExportAsync(
        BatchResultProjectionDefinition definition,
        BatchResultProjectionPage page,
        IReadOnlyList<BatchResultAccountProjection> outcomes)
    {
        var writer = new CapturingReplacementWriter();
        await new ReplacementWorkbookExportService(
                new StubProjectionRepository(definition, page with { AccountResults = outcomes }),
                new ReplacementWorkbookFileNameBuilder(),
                new FixedTimeProvider(Now))
            .ExportAsync(new(Guid.NewGuid(), 100), Stream.Null, writer);
        return writer;
    }

    private sealed class StubProjectionRepository(
        BatchResultProjectionDefinition definition,
        BatchResultProjectionPage page) : IBatchResultProjectionRepository
    {
        public List<(long After, int PageSize)> ProjectionPageRequests { get; } = [];

        public Task<BatchResultProjectionDefinition?> GetDefinitionAsync(
            Guid batchJobId,
            CancellationToken cancellationToken = default) => Task.FromResult<BatchResultProjectionDefinition?>(definition);

        public Task<BatchResultProjectionPage> GetProjectionPageAsync(
            Guid batchJobId,
            long afterCandidateSequence,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            ProjectionPageRequests.Add((afterCandidateSequence, pageSize));
            return Task.FromResult(page);
        }
    }

    private sealed class CapturingReplacementWriter : IReplacementWorkbookWriter
    {
        public ReplacementWorkbookLayout? Layout { get; private set; }
        public List<ReplacementWorkbookRow> Rows { get; } = [];

        public async Task WriteAsync(
            Stream output,
            ReplacementWorkbookLayout layout,
            IAsyncEnumerable<ReplacementWorkbookRow> rows,
            CancellationToken cancellationToken = default)
        {
            Layout = layout;
            await foreach (var row in rows.WithCancellation(cancellationToken)) Rows.Add(row);
        }
    }

    private sealed class CapturingReplacementPublisher : IReplacementWorkbookFilePublisher
    {
        public string? DestinationDirectory { get; private set; }
        public string? FinalFileName { get; private set; }

        public async Task<ReplacementWorkbookFilePublishReceipt> PublishAsync(
            string destinationDirectory,
            string finalFileName,
            ReplacementWorkbookLayout layout,
            IAsyncEnumerable<ReplacementWorkbookRow> rows,
            CancellationToken cancellationToken = default)
        {
            DestinationDirectory = destinationDirectory;
            FinalFileName = finalFileName;
            long rowCount = 0;
            await foreach (var _ in rows.WithCancellation(cancellationToken)) rowCount++;
            return new(
                Path.Combine(destinationDirectory, finalFileName),
                rowCount,
                SheetCount: 1);
        }
    }

    private sealed class SplitCapturingPublisher : IReplacementWorkbookFilePublisher
    {
        public List<(string Name, List<ReplacementWorkbookRow> Rows)> Files { get; } = [];
        public async Task<ReplacementWorkbookFilePublishReceipt> PublishAsync(string destinationDirectory, string finalFileName, ReplacementWorkbookLayout layout, IAsyncEnumerable<ReplacementWorkbookRow> rows, CancellationToken cancellationToken = default)
        {
            var list = new List<ReplacementWorkbookRow>(); await foreach (var row in rows.WithCancellation(cancellationToken)) list.Add(row);
            Files.Add((finalFileName, list)); return new(Path.Combine(destinationDirectory, finalFileName), list.Count, 1);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
