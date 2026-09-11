using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.Results;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Database.Repositories;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedAcceptanceReplacementWorkbookEndToEndTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private const string OriginalUrl = "https://pan.baidu.com/s/original";
    private const string ExactRemoteItemId = "remote-exact-42";
    private const string ReplacementUrl = "https://pan.baidu.com/s/replacement";
    private const string ReplacementCode = "n3w4";
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"bbt-acceptance-replacement-{Guid.NewGuid():N}");

    [Fact]
    public async Task AcceptanceOnlyExactTransferAndSharePublishReplacementWorkbookWithoutOverwritingExistingFile()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "jobs.db"));
        await new DatabaseMigrator(
            factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")),
            "acceptance-export-test").MigrateAsync();
        var clock = new FixedTimeProvider(Now);
        var accountId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var profile = ExecutionProfileSnapshot.Managed(
            BrowserFamily.Chrome, "acceptance-profile", Guid.NewGuid(), Guid.NewGuid());
        await SeedImportAndManagedAccountAsync(factory, accountId, sessionId, profile);

        var draft = new BatchDraft(
            Guid.NewGuid(),
            sessionId,
            "Acceptance",
            TransferMode.Distribution,
            QuotaBasis.TaskCount,
            ShortagePolicy.ProportionalReduction,
            [new BatchAccountDraft(
                accountId, 0, "/target", 1, 0,
                BatchAccountSelectionDecision.Selected, shareEnabled: true, profile)],
            "/",
            "None",
            DuplicatePolicy.Skip,
            "Immediate",
            1,
            true,
            acceptanceOnly: true,
            resultDirectory: root);
        var batches = new CreateBatchService(
            new SqliteUnitOfWorkFactory(factory),
            new BatchDraftValidator(),
            new ReplicationPreflightEstimator(new UnlimitedDiskSpaceProbe(), factory.DatabasePath));
        var preview = await batches.PreviewAsync(draft);
        var created = await batches.ConfirmAsync(draft, preview.ConfirmationToken);
        await new SqliteLocalResultDestinationStore(factory).SetAsync(
            LocalResultDestinationRecord.Create(created.BatchJobId, root, Now));

        var projections = new SqliteBatchResultProjectionRepository(factory);
        var replacementExports = new ReplacementWorkbookExportService(
            projections, new ReplacementWorkbookFileNameBuilder(), clock);
        var existingName = await replacementExports.GetSuggestedFileNameAsync(created.BatchJobId);
        var existingPath = Path.Combine(root, existingName);
        var sentinel = new byte[] { 1, 3, 3, 7 };
        await File.WriteAllBytesAsync(existingPath, sentinel);

        var exportStore = new SqliteLocalResultExportStore(factory);
        var owners = new SqliteLocalResultExportOwnerRepository(factory);
        var artifacts = new LocalResultExportArtifactService(
            replacementExports,
            new ExportStandaloneShareResultsService(new SqliteStandaloneShareStore(factory), clock),
            new OpenXmlReplacementWorkbookFilePublisher(new OpenXmlReplacementResultWriter()),
            new OpenXmlDownloadedWorkbookValidator(),
            owners);
        var coordinator = new LocalResultExportCoordinator(
            exportStore, owners, artifacts, clock, TimeSpan.FromMinutes(5));
        var shareExecutor = new CompletingExactShareExecutor(factory, clock);
        var runner = new ManagedTransferAcceptanceRunner(
            new SqliteManagedTransferAcceptanceStore(factory),
            new CompletingExactTransferExecutor(factory, clock),
            shareExecutor,
            coordinator,
            clock,
            TimeSpan.FromMinutes(5));

        var outcome = await runner.ExecuteAsync(created.BatchJobId);

        var export = Assert.IsType<LocalResultExportRecord>(await exportStore.GetAsync(new(
            LocalResultOwnerKind.TransferBatch,
            created.BatchJobId,
            LocalResultExportKind.ReplacementWorkbook,
            1)));
        Assert.True(
            outcome.Disposition == ManagedTransferAcceptanceRunDisposition.Completed,
            $"Acceptance export stopped with {outcome.Disposition}; export state={export.Status}; code={export.SanitizedErrorCode}.");
        Assert.Equal([ExactRemoteItemId], shareExecutor.RequestedRemoteItemIds);
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(existingPath));
        Assert.Equal(LocalResultExportStatus.Succeeded, export.Status);
        var finalFileName = Assert.IsType<string>(export.FinalFileName);
        Assert.NotEqual(existingName, finalFileName);
        var replacementPath = Path.Combine(root, finalFileName);
        Assert.True(File.Exists(replacementPath));

        using var workbook = SpreadsheetDocument.Open(replacementPath, false);
        var workbookPart = Assert.IsType<WorkbookPart>(workbook.WorkbookPart);
        var resultSheet = Assert.Single(
            Assert.IsType<Sheets>(workbookPart.Workbook!.Sheets).Elements<Sheet>(),
            sheet => sheet.Name != LocalResultWorkbookManifest.SheetName);
        var worksheet = Assert.IsType<WorksheetPart>(workbookPart.GetPartById(resultSheet.Id!.Value!));
        var sheetData = Assert.IsType<SheetData>(
            Assert.IsType<Worksheet>(worksheet.Worksheet).GetFirstChild<SheetData>());
        var row = Assert.Single(sheetData.Elements<Row>().Skip(1));
        Assert.Equal(
            new[]
            {
                "源标题", "源链接原文", "精确对象", OriginalUrl,
                "验收账号", "转存成功", ReplacementUrl, ReplacementCode, "分享成功",
            },
            row.Elements<Cell>().Select(cell => cell.InnerText));

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT transfer.RemoteItemIdsJson,item.RemoteItemId,result.ShareUrl,result.ExtractionCode
            FROM TransferOperations transfer
            JOIN ShareOperations share ON share.TransferOperationId=transfer.Id
            JOIN ShareOperationRemoteItems item ON item.ShareOperationId=share.Id
            JOIN BaiduShareResults result ON result.ShareOperationId=share.Id
              AND result.IsAccepted=1 AND result.IsCurrent=1
            WHERE transfer.BatchJobId=$batch;
            """;
        command.Parameters.AddWithValue("$batch", created.BatchJobId.ToString("D"));
        await using var persisted = await command.ExecuteReaderAsync();
        Assert.True(await persisted.ReadAsync());
        Assert.Equal("[\"remote-exact-42\"]", persisted.GetString(0));
        Assert.Equal(ExactRemoteItemId, persisted.GetString(1));
        Assert.Equal(ReplacementUrl, persisted.GetString(2));
        Assert.Equal(ReplacementCode, persisted.GetString(3));
        Assert.False(await persisted.ReadAsync());

        var repeated = await coordinator.ExportTransferAsync(created.BatchJobId);
        Assert.Equal(LocalResultExportDisposition.AlreadySucceeded, repeated.Disposition);
        Assert.Equal(finalFileName, repeated.FinalFileName);
    }

    private static async Task SeedImportAndManagedAccountAsync(
        SqliteConnectionFactory factory,
        Guid accountId,
        Guid importSessionId,
        ExecutionProfileSnapshot profile)
    {
        var rowId = Guid.NewGuid();
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(AccountRecord.Create(
                accountId, "managed-user", "验收账号") with { LoginMethod = "ManagedChromium" });
            await unit.Imports.AddSessionAsync(new(
                importSessionId, "Excel", "input.xlsx", "ReadyToConfirm",
                1, 1, 0, 0, 0, null, null, null, Now, Now));
            await unit.Imports.AddRowsAsync([new(
                rowId, importSessionId, 1, OriginalUrl, "source-id", "o1d2",
                null, null, null, null, null, "Ready", null, null, null, null, null, Now)]);
            await unit.Imports.AddRowPayloadsAsync([new(
                rowId, "源标题", "精确对象", OriginalUrl, "[\"源标题\",\"源链接原文\"]")]);
            await unit.Imports.SetColumnDefinitionsAsync(importSessionId, [
                new(importSessionId, 0, "原标题", "Title", false),
                new(importSessionId, 1, "原链接字段", "ShareUrl", true),
            ]);
            await unit.CommitAsync();
        }

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO BrowserInstallations(
              InstallationId,BrowserFamily,InstallationDirectory,ExecutablePath,Publisher,ProductName,
              FileIdentity,State,Revision,CreatedAt,UpdatedAt)
            VALUES($installation,'Chrome','C:\\Browser','C:\\Browser\\chrome.exe','Google LLC',
              'Chrome','acceptance-file','Active',1,$now,$now);
            INSERT INTO ManagedBrowserSessions(
              SessionId,AccountId,BaiduUserId,InstallationId,BrowserFamily,ProfileId,ProfileOwnershipId,
              State,Revision,CreatedAt,UpdatedAt)
            VALUES($session,$account,'managed-user',$installation,'Chrome',$profile,'acceptance-owner',
              'Active',1,$now,$now);
            """;
        command.Parameters.AddWithValue("$installation", profile.InstallationId!.Value.ToString("D"));
        command.Parameters.AddWithValue("$session", profile.SessionId!.Value.ToString("D"));
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$profile", profile.ProfileId);
        command.Parameters.AddWithValue("$now", Now.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class CompletingExactTransferExecutor(
        SqliteConnectionFactory factory,
        TimeProvider clock) : ITransferOperationExecutor
    {
        public async Task ExecuteAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            var store = new SqliteTransferSchedulingStore(factory);
            var now = clock.GetUtcNow();
            await store.MarkPreparingAsync(operationId, now, cancellationToken, expectedAttemptCount: 0);
            var snapshot = Assert.IsType<OperationExecutionSnapshot>(
                await store.LoadOperationAsync(operationId, cancellationToken));
            var boundary = await store.PersistAsync(new(
                new(operationId, snapshot.AccountId, "{\"accepted\":true}", now.AddMinutes(5), 1),
                "2026-08-31",
                now), cancellationToken);
            Assert.True(boundary.Accepted);
            await store.CompleteSucceededWithEvidenceAsync(
                operationId,
                "transfer-request",
                new RemoteItemIdentitySet([new(ExactRemoteItemId, "精确对象")]),
                now,
                cancellationToken,
                expectedAttemptCount: 1);
        }
    }

    private sealed class CompletingExactShareExecutor(
        SqliteConnectionFactory factory,
        TimeProvider clock) : IShareOperationExecutor
    {
        public IReadOnlyList<string> RequestedRemoteItemIds { get; private set; } = [];

        public async Task<ShareExecutionResult> ExecuteAsync(
            ShareExecutionLease lease,
            CancellationToken cancellationToken = default)
        {
            var store = new SqliteShareSchedulingStore(factory);
            var context = Assert.IsType<ShareExecutionContext>(
                await store.LoadAsync(lease.OperationId, cancellationToken));
            RequestedRemoteItemIds = context.ExactRemoteItems.Items
                .Select(item => item.ExactId)
                .ToArray();
            Assert.Equal([ExactRemoteItemId], RequestedRemoteItemIds);
            var now = clock.GetUtcNow();
            Assert.True(await store.TryMarkPreparingAsync(
                lease.OperationId, lease.Generation, lease.LeaseGeneration, now, cancellationToken));
            Assert.True(await store.TryCompleteDiscoveryBaselineAsync(
                lease.OperationId, lease.Generation, lease.LeaseGeneration,
                "empty-baseline", 0, now, cancellationToken));
            Assert.True(await store.TryMarkSubmittingAsync(
                lease.OperationId, lease.Generation, lease.LeaseGeneration,
                "{\"exact\":true}", now, cancellationToken));
            Assert.True(await store.TryRecordAdapterRequestAsync(
                lease.OperationId, lease.Generation, lease.LeaseGeneration,
                "share-request", now, cancellationToken));
            Assert.True(await store.AcceptResultAsync(
                lease.OperationId,
                ShareOperationStatus.Submitting,
                lease.LeaseGeneration,
                new(
                    Guid.NewGuid(),
                    context.Operation.Id,
                    context.Operation.BatchJobId,
                    context.Operation.ResourceItemId,
                    context.Operation.AccountId,
                    context.Operation.ExactItemSetFingerprint,
                    context.Operation.SettingsFingerprint,
                    context.Operation.Generation,
                    "share-new",
                    ReplacementUrl,
                    ReplacementCode,
                    true,
                    true,
                    now,
                    now),
                now,
                cancellationToken));
            return new(ShareExecutionDisposition.Succeeded, "分享成功");
        }
    }

    private sealed class UnlimitedDiskSpaceProbe : IDiskSpaceProbe
    {
        public Task<long> GetAvailableBytesAsync(
            string path,
            CancellationToken cancellationToken = default) => Task.FromResult(long.MaxValue);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
