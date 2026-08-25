using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.Results;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Excel;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LocalResultExportArtifactServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"bbt-result-inspection-{Guid.NewGuid():N}");

    [Fact]
    public async Task ValidButWrongWorkbookIsRejectedForRecoveredBatch()
    {
        Directory.CreateDirectory(root);
        const string target = "result.xlsx";
        await using (var output = File.Create(Path.Combine(root, target)))
        {
            await new OpenXmlReplacementResultWriter().WriteAsync(
                output,
                new ReplacementWorkbookLayout(["别的任务"]),
                Rows(new ReplacementWorkbookRow(1, ["wrong"])));
        }
        var service = CreateService();

        var state = await service.InspectAsync(Owner(expectedRows: 1), target);

        Assert.Equal(LocalResultArtifactState.Invalid, state);
    }

    [Fact]
    public async Task ExactStandaloneLayoutAndRowCountIsAcceptedForRecovery()
    {
        Directory.CreateDirectory(root);
        const string target = "result.xlsx";
        var service = CreateService();
        await service.PublishAsync(Owner(expectedRows: 1), target);

        var state = await service.InspectAsync(Owner(expectedRows: 1), target);

        Assert.Equal(LocalResultArtifactState.Valid, state);
    }

    private LocalResultExportArtifactService CreateService()
    {
        var standalone = new ExportStandaloneShareResultsService(new StandaloneRepository());
        var writer = new OpenXmlReplacementResultWriter();
        return new(
            new ReplacementWorkbookExportService(
                new UnusedBatchRepository(), new ReplacementWorkbookFileNameBuilder()),
            standalone,
            new OpenXmlReplacementWorkbookFilePublisher(writer),
            new OpenXmlDownloadedWorkbookValidator());
    }

    private LocalResultExportOwnerSnapshot Owner(long expectedRows) => new(
        new LocalResultExportKey(
            LocalResultOwnerKind.StandaloneShareBatch,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            LocalResultExportKind.StandaloneShareWorkbook,
            1),
        root,
        true,
        expectedRows);

    private static async IAsyncEnumerable<ReplacementWorkbookRow> Rows(
        params ReplacementWorkbookRow[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            await Task.Yield();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class StandaloneRepository : IStandaloneShareResultProjectionRepository
    {
        public Task<StandaloneShareResultProjectionPage> GetPageAsync(
            Guid batchId,
            int afterSequenceNumber,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(afterSequenceNumber == 0
                ? new StandaloneShareResultProjectionPage(
                    [new(
                        Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        1,
                        "账号甲",
                        "资源甲",
                        RemoteResourceKind.File,
                        StandaloneShareOperationStatus.Succeeded,
                        "https://pan.baidu.com/s/result",
                        "a1b2",
                        null)],
                    null)
                : new StandaloneShareResultProjectionPage([], null));
    }

    private sealed class UnusedBatchRepository : IBatchResultProjectionRepository
    {
        public Task<BatchResultProjectionDefinition?> GetDefinitionAsync(
            Guid batchJobId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<BatchResultProjectionDefinition?>(null);

        public Task<BatchResultProjectionPage> GetProjectionPageAsync(
            Guid batchJobId,
            long afterCandidateSequence,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
