using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class StandaloneShareResultExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task ExportsOneSelectedResourcePerRowInStableSequenceAcrossPages()
    {
        var batch = Guid.NewGuid();
        var repository = new StubRepository(
            new StandaloneShareResultProjectionPage(
            [
                Projection(1, "账号A", "文件夹", RemoteResourceKind.Folder,
                    StandaloneShareOperationStatus.Succeeded, "https://pan.baidu.com/s/one", "a1b2"),
                Projection(2, "账号B", "安装包.apk", RemoteResourceKind.File,
                    StandaloneShareOperationStatus.ResultPending),
            ], 2),
            new StandaloneShareResultProjectionPage(
            [Projection(3, "账号A", "普通文件.txt", RemoteResourceKind.File,
                StandaloneShareOperationStatus.Failed, stableErrorCode: "SHARE_FAILED")], null));
        var writer = new CapturingWriter();
        var service = new ExportStandaloneShareResultsService(repository, new FixedTimeProvider(Now));

        var result = await service.ExportAsync(
            new StandaloneShareResultExportRequest(batch, PageSize: 2), Stream.Null, writer);

        Assert.Equal("百度分享结果_20260825_010203.xlsx", result.SuggestedFileName);
        Assert.Equal(3, result.ExportedRows);
        Assert.Equal(
            new[] { "账号", "资源名称", "资源类型", "新分享链接", "提取码", "状态" },
            writer.Layout!.Headers.ToArray());
        Assert.Collection(writer.Rows,
            row => Assert.Equal(new[] { "账号A", "文件夹", "文件夹", "https://pan.baidu.com/s/one", "a1b2", "分享成功" }, row.TextCells),
            row => Assert.Equal(new[] { "账号B", "安装包.apk", "文件", "", "", "分享结果待确认" }, row.TextCells),
            row => Assert.Equal(new[] { "账号A", "普通文件.txt", "文件", "", "", "分享失败" }, row.TextCells));
        Assert.Equal([(0, 2), (2, 2)], repository.Requests);
    }

    [Theory]
    [InlineData(StandaloneShareOperationStatus.Queued, "等待分享")]
    [InlineData(StandaloneShareOperationStatus.Preparing, "准备中")]
    [InlineData(StandaloneShareOperationStatus.Submitting, "分享中")]
    [InlineData(StandaloneShareOperationStatus.NeedsDecision, "需要人工确认")]
    [InlineData(StandaloneShareOperationStatus.Cancelled, "已取消")]
    public async Task NonSuccessfulStatusesNeverExposeShareSecrets(
        StandaloneShareOperationStatus status,
        string expectedStatus)
    {
        var writer = new CapturingWriter();
        var repository = new StubRepository(new StandaloneShareResultProjectionPage(
            [Projection(1, "账号", "资源", RemoteResourceKind.File, status,
                "https://pan.baidu.com/s/must-not-leak", "x9y8")], null));

        await new ExportStandaloneShareResultsService(repository, new FixedTimeProvider(Now))
            .ExportAsync(new(Guid.NewGuid()), Stream.Null, writer);

        Assert.Equal(new[] { "账号", "资源", "文件", "", "", expectedStatus },
            Assert.Single(writer.Rows).TextCells);
    }

    [Fact]
    public async Task DirectoryExportForwardsUserSelectedPathAndNeverChoosesAnExistingName()
    {
        var destination = Path.Combine(Path.GetTempPath(), "用户选择的分享结果目录");
        var publisher = new CapturingPublisher();
        var service = new ExportStandaloneShareResultsService(
            new StubRepository(new StandaloneShareResultProjectionPage(
                [Projection(1, "账号", "资源", RemoteResourceKind.File,
                    StandaloneShareOperationStatus.Succeeded, "https://pan.baidu.com/s/result", "r4nd")], null)),
            new FixedTimeProvider(Now));

        await service.ExportToDirectoryAsync(new(Guid.NewGuid()), destination, publisher);

        Assert.Equal(destination, publisher.DestinationDirectory);
        Assert.Equal("百度分享结果_20260825_010203.xlsx", publisher.FinalFileName);
    }

    private static StandaloneShareResultProjection Projection(
        int sequence,
        string account,
        string resource,
        RemoteResourceKind kind,
        StandaloneShareOperationStatus status,
        string? url = null,
        string? code = null,
        string? stableErrorCode = null) =>
        new(Guid.NewGuid(), sequence, account, resource, kind, status, url, code, stableErrorCode);

    private sealed class StubRepository(params StandaloneShareResultProjectionPage[] pages)
        : IStandaloneShareResultProjectionRepository
    {
        private readonly Queue<StandaloneShareResultProjectionPage> pages = new(pages);
        public List<(int After, int PageSize)> Requests { get; } = [];

        public Task<StandaloneShareResultProjectionPage> GetPageAsync(
            Guid batchId,
            int afterSequenceNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((afterSequenceNumber, pageSize));
            return Task.FromResult(pages.Dequeue());
        }
    }

    private sealed class CapturingWriter : IReplacementWorkbookWriter
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

    private sealed class CapturingPublisher : IReplacementWorkbookFilePublisher
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
            long count = 0;
            await foreach (var _ in rows.WithCancellation(cancellationToken)) count++;
            return new(Path.Combine(destinationDirectory, finalFileName), count, 1);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
