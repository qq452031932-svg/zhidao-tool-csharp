using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.Results;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LocalResultExportArtifactServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"bbt-result-inspection-{Guid.NewGuid():N}");

    [Fact]
    public async Task PublishCreatesPreviouslyAuthorizedMissingDirectory()
    {
        const string target = "result.xlsx";

        var receipt = await CreateService().PublishAsync(Owner(expectedRows: 1), target);

        Assert.Equal(target, receipt.FinalFileName);
        Assert.True(File.Exists(Path.Combine(root, target)));
    }

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

    [Fact]
    public async Task FinalWorkbookVerifierReturnsOnlyExactManifestCandidateFromTheLockedArtifact()
    {
        Directory.CreateDirectory(root);
        const string target = "result.xlsx";
        var operation = new LocalResultOperationEvidence(
            "BaiduShare",
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            2,
            new string('a', 64));
        var service = CreateService(operation);
        await service.PublishAsync(
            Owner(expectedRows: 1), target,
            provenOperations: [operation]);

        var evidence = await service.GetVerifiedEvidenceAsync(
            Owner(expectedRows: 1), target,
            [new(operation.AdapterKind, operation.AccountId, operation.OperationId, operation.Generation)]);

        Assert.NotNull(evidence);
        Assert.Equal(Owner(expectedRows: 1).Key, evidence.ExportKey);
        Assert.Equal(operation, Assert.Single(evidence.Operations));
        Assert.True(LocalResultWorkbookManifest.IsFingerprint(evidence.LayoutFingerprint));
        Assert.True(LocalResultWorkbookManifest.IsFingerprint(evidence.ArtifactFingerprint));
    }

    [Fact]
    public async Task MissingOrDifferentManifestCandidateReturnsNoAcceptanceEvidence()
    {
        Directory.CreateDirectory(root);
        const string target = "result.xlsx";
        var candidate = new LocalResultOperationEvidence(
            "BaiduShare", Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', 64));
        var service = CreateService(candidate);
        await service.PublishAsync(Owner(expectedRows: 1), target);

        var evidence = await service.GetVerifiedEvidenceAsync(
            Owner(expectedRows: 1), target,
            [new(candidate.AdapterKind, candidate.AccountId, candidate.OperationId, candidate.Generation)]);

        Assert.Null(evidence);
    }

    [Fact]
    public async Task LegacyWorkbookWithoutManifestReturnsNoAcceptanceEvidence()
    {
        Directory.CreateDirectory(root);
        const string target = "legacy.xlsx";
        var standalone = new ExportStandaloneShareResultsService(new StandaloneRepository());
        await using (var output = File.Create(Path.Combine(root, target)))
            await new OpenXmlReplacementResultWriter().WriteAsync(
                output, standalone.GetExpectedLayout(),
                Rows(new ReplacementWorkbookRow(1, [
                    "账号甲", "资源甲", "文件", "https://pan.baidu.com/s/result", "a1b2", "分享成功"])));
        var operation = new LocalResultOperationEvidence(
            "BaiduShare", Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', 64));

        var evidence = await CreateService(operation).GetVerifiedEvidenceAsync(
            Owner(expectedRows: 1), target,
            [new(operation.AdapterKind, operation.AccountId, operation.OperationId, operation.Generation)]);

        Assert.Null(evidence);
    }

    [Fact]
    public async Task MetadataSheetThatIsNotVeryHiddenReturnsNoAcceptanceEvidence()
    {
        Directory.CreateDirectory(root);
        const string target = "result.xlsx";
        var operation = new LocalResultOperationEvidence(
            "BaiduShare", Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', 64));
        var service = CreateService(operation);
        await service.PublishAsync(Owner(expectedRows: 1), target, provenOperations: [operation]);
        using (var document = SpreadsheetDocument.Open(Path.Combine(root, target), true))
        {
            var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
            var workbook = Assert.IsType<Workbook>(workbookPart.Workbook);
            var metadata = Assert.IsType<Sheets>(workbook.Sheets).Elements<Sheet>()
                .Single(sheet => sheet.Name == LocalResultWorkbookManifest.SheetName);
            metadata.State = SheetStateValues.Hidden;
            workbook.Save();
        }

        var evidence = await service.GetVerifiedEvidenceAsync(
            Owner(expectedRows: 1), target,
            [new(operation.AdapterKind, operation.AccountId, operation.OperationId, operation.Generation)]);

        Assert.Null(evidence);
    }

    [Fact]
    public async Task RenamedPublicSheetReturnsNoAcceptanceEvidence()
    {
        Directory.CreateDirectory(root);
        const string target = "result.xlsx";
        var operation = new LocalResultOperationEvidence(
            "BaiduShare", Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', 64));
        var service = CreateService(operation);
        await service.PublishAsync(Owner(expectedRows: 1), target, provenOperations: [operation]);
        using (var document = SpreadsheetDocument.Open(Path.Combine(root, target), true))
        {
            var workbook = Assert.IsType<Workbook>(
                Assert.IsType<WorkbookPart>(document.WorkbookPart).Workbook);
            var publicSheet = Assert.IsType<Sheets>(workbook.Sheets).Elements<Sheet>()
                .Single(sheet => sheet.Name != LocalResultWorkbookManifest.SheetName);
            publicSheet.Name = "lookalike";
            workbook.Save();
        }

        var evidence = await service.GetVerifiedEvidenceAsync(
            Owner(expectedRows: 1), target,
            [new(operation.AdapterKind, operation.AccountId, operation.OperationId, operation.Generation)]);

        Assert.Null(evidence);
    }

    [Fact]
    public async Task ForgedWorkbookRowNotEqualToCurrentCanonicalRepositoryProofReturnsNoEvidence()
    {
        Directory.CreateDirectory(root);
        const string target = "result.xlsx";
        var workbookRow = new LocalResultOperationEvidence(
            "BaiduShare", Guid.NewGuid(), Guid.NewGuid(), 2, new string('a', 64));
        var currentProof = workbookRow with { ContentFingerprint = new string('b', 64) };
        var service = CreateService(currentProof);
        await service.PublishAsync(Owner(expectedRows: 1), target, provenOperations: [workbookRow]);

        var evidence = await service.GetVerifiedEvidenceAsync(
            Owner(expectedRows: 1), target,
            [new(workbookRow.AdapterKind, workbookRow.AccountId,
                workbookRow.OperationId, workbookRow.Generation)]);

        Assert.Null(evidence);
    }

    [Fact]
    public async Task MissingOrExtraCanonicalRepositoryProofReturnsNoEvidence()
    {
        Directory.CreateDirectory(root);
        var workbookRow = new LocalResultOperationEvidence(
            "BaiduShare", Guid.NewGuid(), Guid.NewGuid(), 2, new string('a', 64));
        var extra = workbookRow with { OperationId = Guid.NewGuid() };
        var extraProof = CreateService(workbookRow, extra);
        await extraProof.PublishAsync(
            Owner(expectedRows: 1), "extra.xlsx", provenOperations: [workbookRow]);
        var missingProof = CreateService(workbookRow);
        await missingProof.PublishAsync(
            Owner(expectedRows: 1), "missing.xlsx", provenOperations: [workbookRow, extra]);

        var extraEvidence = await extraProof.GetVerifiedEvidenceAsync(
            Owner(expectedRows: 1), "extra.xlsx",
            [new(workbookRow.AdapterKind, workbookRow.AccountId,
                workbookRow.OperationId, workbookRow.Generation)]);
        var missingEvidence = await missingProof.GetVerifiedEvidenceAsync(
            Owner(expectedRows: 1), "missing.xlsx",
            [
                new(workbookRow.AdapterKind, workbookRow.AccountId,
                    workbookRow.OperationId, workbookRow.Generation),
                new(extra.AdapterKind, extra.AccountId, extra.OperationId, extra.Generation),
            ]);

        Assert.Null(extraEvidence);
        Assert.Null(missingEvidence);
    }

    [Fact]
    public async Task CanonicalRepositoryProofFailureReturnsNoEvidence()
    {
        Directory.CreateDirectory(root);
        const string target = "result.xlsx";
        var workbookRow = new LocalResultOperationEvidence(
            "BaiduShare", Guid.NewGuid(), Guid.NewGuid(), 2, new string('a', 64));
        var service = CreateService(new ThrowingMembershipRepository());
        await service.PublishAsync(Owner(expectedRows: 1), target, provenOperations: [workbookRow]);

        var evidence = await service.GetVerifiedEvidenceAsync(
            Owner(expectedRows: 1), target,
            [new(workbookRow.AdapterKind, workbookRow.AccountId,
                workbookRow.OperationId, workbookRow.Generation)]);

        Assert.Null(evidence);
    }

    private LocalResultExportArtifactService CreateService(
        params LocalResultOperationEvidence[] currentProof) =>
        CreateService(new MembershipRepository(currentProof));

    private LocalResultExportArtifactService CreateService(
        ILocalResultExportOwnerRepository membershipRepository)
    {
        var standalone = new ExportStandaloneShareResultsService(new StandaloneRepository());
        var writer = new OpenXmlReplacementResultWriter();
        return new(
            new ReplacementWorkbookExportService(
                new UnusedBatchRepository(), new ReplacementWorkbookFileNameBuilder()),
            standalone,
            new OpenXmlReplacementWorkbookFilePublisher(writer),
            new OpenXmlDownloadedWorkbookValidator(),
            membershipRepository);
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

    private sealed class MembershipRepository(IReadOnlyList<LocalResultOperationEvidence> proof) :
        ILocalResultExportOwnerRepository
    {
        public Task<LocalResultExportOwnerSnapshot?> GetAsync(
            LocalResultExportKey key,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Page<LocalResultExportOwnerSnapshot>> GetReadyPageAsync(
            PageQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<LocalResultOperationEvidence>> GetProvenOperationEvidenceAsync(
            LocalResultExportKey key,
            IReadOnlyList<LocalResultOperationIdentity> candidates,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(proof);
    }

    private sealed class ThrowingMembershipRepository : ILocalResultExportOwnerRepository
    {
        public Task<LocalResultExportOwnerSnapshot?> GetAsync(
            LocalResultExportKey key,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Page<LocalResultExportOwnerSnapshot>> GetReadyPageAsync(
            PageQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<LocalResultOperationEvidence>> GetProvenOperationEvidenceAsync(
            LocalResultExportKey key,
            IReadOnlyList<LocalResultOperationIdentity> candidates,
            CancellationToken cancellationToken = default) => throw new InvalidDataException("tampered lineage");
    }
}
