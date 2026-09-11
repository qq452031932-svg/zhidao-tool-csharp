using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Database.Repositories;
using BaiduBatchTransfer.Infrastructure.Excel;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LocalJobResultsWorkflowTests
{
    [Fact]
    public async Task LoadPagePropagatesTargetPathAndAcceptedCurrentShareFieldsAndFailsClosedForOtherResults()
    {
        var root = Path.Combine(Path.GetTempPath(), $"job-results-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new SqliteConnectionFactory(Path.Combine(root, "jobs.db"));
            await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test")
                .MigrateAsync();
            var batch = Guid.NewGuid();
            var account = Guid.NewGuid();
            await SeedAsync(factory, batch, account);
            var units = new SqliteUnitOfWorkFactory(factory);
            var workflow = new LocalJobResultsWorkflow(
                new JobPageQuery(units),
                new BatchStatisticsQuery(units),
                new ReplacementWorkbookExportService(
                    new SqliteBatchResultProjectionRepository(factory),
                    new ReplacementWorkbookFileNameBuilder()),
                new OpenXmlReplacementWorkbookFilePublisher(new OpenXmlReplacementResultWriter()),
                new ThrowingActions());

            var page = await workflow.LoadPageAsync(batch, null, 10);
            var accepted = page.Items.Single(row => row.CandidateSequence == 1);
            var unavailable = page.Items.Single(row => row.CandidateSequence == 2);

            Assert.Equal("https://pan.baidu.com/s/original-accepted", accepted.OriginalShareUrl);
            Assert.Equal("/target", accepted.TargetPath);
            Assert.Equal("https://pan.baidu.com/s/accepted", accepted.GeneratedShareUrl);
            Assert.Equal("newcode", accepted.GeneratedExtractionCode);
            Assert.Equal("成功", accepted.TransferStatusText);
            Assert.Equal("https://pan.baidu.com/s/original-unavailable", unavailable.OriginalShareUrl);
            Assert.Null(unavailable.GeneratedShareUrl);
            Assert.Null(unavailable.GeneratedExtractionCode);
            Assert.Equal("成功", unavailable.TransferStatusText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedAsync(SqliteConnectionFactory factory, Guid batch, Guid account)
    {
        var acceptedResource = Guid.NewGuid();
        var unavailableResource = Guid.NewGuid();
        var acceptedTransfer = Guid.NewGuid();
        var unavailableTransfer = Guid.NewGuid();
        var acceptedShare = Guid.NewGuid();
        var unavailableShare = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
            VALUES($account,'results-user','结果账号','WebView2','Active',0,0,0,1,'2026-08-31',0);
            INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,QueueOrder)
            VALUES($batch,'results','Text','Distribution','TaskCount','ProportionalReduction',2,2,'/target','None','Skip','Immediate','Active',$now,1);
            INSERT INTO AccountAllocations(Id,BatchJobId,AccountId,PriorityOrder,DefaultTargetPath,NormalizedDefaultTargetPath,TargetItemCount,TargetSuccessCount,EffectiveTargetSuccessCount,AssignedItemCount,AttemptedItemCount,NewSuccessItemCount,AlreadySatisfiedItemCount,SuccessItemCount,FailedItemCount,LockedItemCount,ExclusiveCandidateCount,UnusedCandidateCount,ReservedSlotCount,FulfilledCount)
            VALUES($allocation,$batch,$account,1,'/target','/target',2,2,2,2,2,2,0,2,0,0,0,0,0,2);
            INSERT INTO ResourceItems(Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
            VALUES($acceptedResource,$batch,1,'https://pan.baidu.com/s/original-accepted','source-accepted','None','Valid','Expanded',$now,$now),
                  ($unavailableResource,$batch,2,'https://pan.baidu.com/s/original-unavailable','source-unavailable','None','Valid','Expanded',$now,$now);
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,RemoteItemIdsJson,SubmitState,TargetSlotState,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId)
            VALUES($acceptedTransfer,$acceptedResource,$batch,1,1,'/target','/target','',$account,'Automatic','Succeeded','NewTransfer',1,'transfer-accepted','{}','["remote-accepted"]','ResultReceived','Fulfilled',$now,$now,'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account,'-',''))),
                  ($unavailableTransfer,$unavailableResource,$batch,2,2,'/target','/target','',$account,'Automatic','Succeeded','NewTransfer',1,'transfer-unavailable','{}','["remote-unavailable"]','ResultReceived','Fulfilled',$now,$now,'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account,'-','')));
            INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,FixedExtractionCode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
            VALUES($batch,1,1,'Random',NULL,'Reuse','settings-fp',$now);
            INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled)
            VALUES($batch,$account,1,'结果账号',2,2,1);
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,AttemptCount,LeaseGeneration,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId)
            VALUES($acceptedShare,$acceptedTransfer,$acceptedResource,$batch,$account,1,'Succeeded','exact-accepted','settings-fp',1,'DefinitelyNotAccepted',1,1,$now,$now,'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account,'-',''))),
                  ($unavailableShare,$unavailableTransfer,$unavailableResource,$batch,$account,1,'Succeeded','exact-unavailable','settings-fp',1,'DefinitelyNotAccepted',1,1,$now,$now,'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account,'-','')));
            INSERT INTO ShareOperationRemoteItems(ShareOperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName)
            VALUES($acceptedShare,0,'remote-accepted',X'01','accepted'),
                  ($unavailableShare,0,'remote-unavailable',X'02','unavailable');
            UPDATE ShareOperationGenerations SET ExactItemCount=1
            WHERE ShareOperationId IN ($acceptedShare,$unavailableShare) AND Generation=1;
            INSERT INTO BaiduShareResults(Id,ShareOperationId,BatchJobId,ResourceItemId,AccountId,CandidateSequence,OutputOrdinal,ExactItemSetFingerprint,SettingsFingerprint,Generation,ShareId,ShareUrl,ExtractionCode,IsAccepted,IsCurrent,SharedAt,UpdatedAt)
            VALUES($acceptedResult,$acceptedShare,$batch,$acceptedResource,$account,1,1,'exact-accepted','settings-fp',1,'accepted','https://pan.baidu.com/s/accepted','newcode',1,1,$now,$now),
                  ($rejectedResult,$unavailableShare,$batch,$unavailableResource,$account,2,1,'exact-unavailable','settings-fp',1,'rejected','https://pan.baidu.com/s/rejected','reject',0,1,$now,$now),
                  ($supersededResult,$unavailableShare,$batch,$unavailableResource,$account,2,1,'exact-unavailable','settings-fp',1,'superseded','https://pan.baidu.com/s/superseded','oldcode',1,0,$now,$now);
            """;
        command.Parameters.AddWithValue("$account", account.ToString("D"));
        command.Parameters.AddWithValue("$batch", batch.ToString("D"));
        command.Parameters.AddWithValue("$allocation", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$acceptedResource", acceptedResource.ToString("D"));
        command.Parameters.AddWithValue("$unavailableResource", unavailableResource.ToString("D"));
        command.Parameters.AddWithValue("$acceptedTransfer", acceptedTransfer.ToString("D"));
        command.Parameters.AddWithValue("$unavailableTransfer", unavailableTransfer.ToString("D"));
        command.Parameters.AddWithValue("$acceptedShare", acceptedShare.ToString("D"));
        command.Parameters.AddWithValue("$unavailableShare", unavailableShare.ToString("D"));
        command.Parameters.AddWithValue("$acceptedResult", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$rejectedResult", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$supersededResult", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ThrowingActions : IJobResultActions
    {
        public Task<JobResultActionResult> RetryShareOnlyAsync(Guid shareOperationId, int expectedGeneration, DateTimeOffset expectedUpdatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionResult> ResumeShareAsync(Guid shareOperationId, int expectedGeneration, DateTimeOffset expectedUpdatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionResult> ResumeTransferAsync(Guid transferOperationId, int expectedAttemptCount, DateTimeOffset expectedUpdatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionResult> ReconcileTransferAsync(Guid transferOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionResult> ReconcileShareAsync(Guid shareOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
