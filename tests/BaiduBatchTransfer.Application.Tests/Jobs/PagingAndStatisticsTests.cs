using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using Xunit.Abstractions;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class PagingAndStatisticsTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper output = output;

    [Fact]
    public async Task OperationQueryProjectsStableChineseStatusLabels()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var statuses = new[]
        {
            (Raw: "Waiting", Chinese: "等待"),
            (Raw: "Reserved", Chinese: "处理中"),
            (Raw: "Preparing", Chinese: "处理中"),
            (Raw: "Submitting", Chinese: "处理中"),
            (Raw: "ResultPending", Chinese: "结果待确认"),
            (Raw: "Succeeded", Chinese: "成功"),
            (Raw: "Failed", Chinese: "失败"),
            (Raw: "Paused", Chinese: "暂停"),
            (Raw: "Cancelled", Chinese: "已取消"),
        };
        var resources = statuses.Select((_, index) => Resource(batch, index + 1)).ToArray();
        await db.SeedAsync(
            [BatchTestData.Account(account, "projection")],
            job: Job(batch) with { ResourceCount = statuses.Length, OperationCount = statuses.Length },
            resources: resources,
            operations: statuses.Select((status, index) =>
                Operation(batch, resources[index], account, index + 1, status.Raw, null)),
            allocations: [Allocation(batch, account, 1)]);

        var page = await new JobPageQuery(db.UnitOfWorkFactory).GetOperationsAsync(
            new(batch, account, null, null, null, 100));

        Assert.Equal(
            statuses.Select(status => (status.Raw, status.Chinese)),
            page.Items.Select(item => (item.Status, item.StatusText)));
    }

    [Fact]
    public void UnknownPersistedTransferStatusDoesNotLeakRawStatusText()
    {
        var record = new OperationResultRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1,
            "https://pan.baidu.com/s/redacted", null, Guid.NewGuid(), "account", "/target",
            "FutureTransferState", null, "SAFE_CODE", "safe message", BatchTestData.Now);

        var item = OperationPageItem.FromPersistence(record);

        Assert.Equal("FutureTransferState", item.Status);
        Assert.Equal("状态未知", item.StatusText);
    }

    [Fact]
    public async Task OperationKeysetPageProjectsIndependentTransferAndShareStatesWithoutDuplicates()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resources = new[]
        {
            Resource(batch, 1) with { ResolvedOutputTitle = "源标题" },
            Resource(batch, 2),
        };
        var operations = new[]
        {
            Operation(batch, resources[0], account, 1, "Succeeded", "NewTransfer") with
            {
                SubmitState = "ResultReceived",
                ConfirmationDescriptorJson = "{}",
                RemoteItemIdsJson = "[\"remote-1\"]",
                TargetSlotState = "Fulfilled",
            },
            Operation(batch, resources[1], account, 2, "ResultPending", null) with
            {
                SubmitState = "PendingConfirmation",
                ConfirmationDescriptorJson = "{}",
                TargetSlotState = "Reserved",
                TargetSlotReservedAt = BatchTestData.Now,
            },
        };
        await db.SeedAsync(
            [BatchTestData.Account(account, "share-page")],
            job: Job(batch) with { Status = "Active", ResourceCount = 2, OperationCount = 2 },
            resources: resources,
            operations: operations,
            allocations: [Allocation(batch, account, 1)]);
        var failedShare = await SeedShareAsync(
            db, batch, account, operations[0], resources[0], "Failed", "SHARE_SAFE_FAILURE");

        var query = new JobPageQuery(db.UnitOfWorkFactory);
        var first = await query.GetOperationsAsync(new(batch, null, null, null, null, 1));
        var second = await query.GetOperationsAsync(new(batch, null, null, null, null, 1, first.NextCursor));

        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].OperationId, second.Items[0].OperationId);
        dynamic projected = first.Items[0];
        Assert.Equal(failedShare, (Guid?)projected.ShareOperationId);
        Assert.Equal("源标题", (string)projected.ResolvedTitle);
        Assert.Equal("成功", (string)projected.TransferStatusText);
        Assert.Equal("失败", (string)projected.ShareStatusText);
        Assert.Equal("SHARE_SAFE_FAILURE", (string)projected.ShareLastErrorCode);
        Assert.True((bool)projected.CanRetryShare);
        Assert.False((bool)projected.CanReconcileShare);
        Assert.True((bool)((dynamic)second.Items[0]).CanReconcileTransfer);
        Assert.Equal("等待生成分享", (string)((dynamic)second.Items[0]).ShareStatusText);
    }

    [Fact]
    public async Task ConcurrentShareOnlyRetryChangesOnlyFailedShareAndAcceptsExactlyOnce()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1);
        var transfer = Operation(batch, resource, account, 1, "Succeeded", "NewTransfer") with
        {
            SubmitState = "ResultReceived",
            ConfirmationDescriptorJson = "{}",
            RemoteItemIdsJson = "[\"remote-1\"]",
            TargetSlotState = "Fulfilled",
        };
        await db.SeedAsync(
            [BatchTestData.Account(account, "retry")],
            job: Job(batch) with { Status = "Active", ResourceCount = 1, OperationCount = 1 },
            resources: [resource],
            operations: [transfer],
            allocations: [Allocation(batch, account, 1)]);
        var share = await SeedShareAsync(db, batch, account, transfer, resource, "Failed", "SHARE_FAILED");
        var actions = Actions(db);

        var results = await Task.WhenAll(
            actions.RetryShareOnlyAsync(share, 1, BatchTestData.Now),
            actions.RetryShareOnlyAsync(share, 1, BatchTestData.Now));

        Assert.Equal(1, results.Count(result => result.Disposition == JobResultActionDisposition.Accepted));
        Assert.Equal(1, results.Count(result => result.Disposition == JobResultActionDisposition.Stale));
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Status,t.OutcomeKind,t.SubmitState,t.RemoteItemIdsJson,
                   s.Status,s.Generation,s.LeaseExpiresAt,s.RequestDescriptorJson,s.LastErrorCode
            FROM TransferOperations t JOIN ShareOperations s ON s.TransferOperationId=t.Id
            WHERE s.Id=$share;
            """;
        command.Parameters.AddWithValue("$share", share.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(("Succeeded", "NewTransfer", "ResultReceived", "[\"remote-1\"]"),
            (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        Assert.Equal("WaitingShare", reader.GetString(4));
        Assert.Equal(2, reader.GetInt32(5));
        Assert.True(reader.IsDBNull(6));
        Assert.True(reader.IsDBNull(7));
        Assert.True(reader.IsDBNull(8));
    }

    [Fact]
    public async Task OldShareSnapshotCannotRetryANewerFailedGeneration()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1);
        var transfer = Operation(batch, resource, account, 1, "Succeeded", "NewTransfer") with
        {
            SubmitState = "ResultReceived", ConfirmationDescriptorJson = "{}",
            RemoteItemIdsJson = "[\"remote-1\"]", TargetSlotState = "Fulfilled",
        };
        await db.SeedAsync(
            [BatchTestData.Account(account, "aba")],
            job: Job(batch) with { Status = "Active", ResourceCount = 1, OperationCount = 1 },
            resources: [resource], operations: [transfer], allocations: [Allocation(batch, account, 1)]);
        var share = await SeedShareAsync(db, batch, account, transfer, resource, "Failed", "FIRST_FAILURE");
        var actions = Actions(db);

        Assert.True((await actions.RetryShareOnlyAsync(share, 1, BatchTestData.Now)).Accepted);
        await using (var connection = await db.ConnectionFactory.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, """
                UPDATE ShareOperations
                SET Status='Failed',LastErrorCode='SECOND_FAILURE',UpdatedAt=$updated
                WHERE Id=$id AND Generation=2;
                """,
                ("$id", share.ToString("D")),
                ("$updated", BatchTestData.Now.AddMinutes(20).ToString("O")));
        }

        var stale = await actions.RetryShareOnlyAsync(share, 1, BatchTestData.Now);

        Assert.Equal(JobResultActionDisposition.Stale, stale.Disposition);
        await using var verify = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = "SELECT Status,Generation,LastErrorCode FROM ShareOperations WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", share.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(("Failed", 2, "SECOND_FAILURE"),
            (reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("account")]
    [InlineData("share-disabled")]
    [InlineData("transfer")]
    public async Task ShareOnlyRetryFailsClosedWhenSafetyGateChanges(string gate)
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1);
        var transfer = Operation(batch, resource, account, 1, "Succeeded", "NewTransfer") with
        {
            SubmitState = "ResultReceived", ConfirmationDescriptorJson = "{}",
            RemoteItemIdsJson = "[\"remote-1\"]", TargetSlotState = "Fulfilled",
        };
        await db.SeedAsync(
            [BatchTestData.Account(account, "gate")],
            job: Job(batch) with { Status = "Active", ResourceCount = 1, OperationCount = 1 },
            resources: [resource], operations: [transfer], allocations: [Allocation(batch, account, 1)]);
        var share = await SeedShareAsync(db, batch, account, transfer, resource, "Failed", "SHARE_FAILED");
        await using (var connection = await db.ConnectionFactory.OpenConnectionAsync())
        {
            var sql = gate switch
            {
                "batch" => "UPDATE BatchJobs SET Status='PausedReady' WHERE Id=$id;",
                "account" => "UPDATE Accounts SET Status='Invalid' WHERE Id=$id;",
                "share-disabled" => "UPDATE BatchOutputAccounts SET ShareEnabled=0,PlannedShareCount=0,CreatedShareOperationCount=0 WHERE BatchJobId=$id;",
                "transfer" => "UPDATE TransferOperations SET Status='Failed',OutcomeKind='Failed' WHERE Id=$id;",
                _ => throw new ArgumentOutOfRangeException(nameof(gate)),
            };
            var id = gate switch
            {
                "account" => account,
                "transfer" => transfer.Id,
                _ => batch,
            };
            await ExecuteAsync(connection, sql, ("$id", id.ToString("D")));
        }

        var result = await Actions(db).RetryShareOnlyAsync(share, 1, BatchTestData.Now);

        Assert.Equal(JobResultActionDisposition.Stale, result.Disposition);
        await using var verify = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = "SELECT Status,Generation FROM ShareOperations WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", share.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Failed", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task ShareOnlyRetryReopensCompletedWithShortfallWithoutRequeueingTransfer()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1);
        var transfer = Operation(batch, resource, account, 1, "Succeeded", "NewTransfer") with
        {
            SubmitState = "ResultReceived", ConfirmationDescriptorJson = "{}",
            RemoteItemIdsJson = "[\"remote-1\"]", TargetSlotState = "Fulfilled",
        };
        await db.SeedAsync(
            [BatchTestData.Account(account, "completed-retry")],
            job: Job(batch) with
            {
                Status = "CompletedWithShortfall", CompletedAt = BatchTestData.Now,
                ResourceCount = 1, OperationCount = 1,
            },
            resources: [resource], operations: [transfer], allocations: [Allocation(batch, account, 1)]);
        var share = await SeedShareAsync(db, batch, account, transfer, resource, "Failed", "SHARE_FAILED");

        await using (var diagnostic = await db.ConnectionFactory.OpenConnectionAsync())
        {
            await using var diagnosticCommand = diagnostic.CreateCommand();
            diagnosticCommand.CommandText = """
                SELECT s.Status,s.SubmitStartedAt,s.SubmissionAcceptance,t.Status,t.SubmitState,t.RemoteItemIdsJson,
                       b.Status,a.Status,a.IsEnabled,a.IsDeleted,o.ShareEnabled,
                       l.ExactItemCount,(SELECT COUNT(*) FROM ShareOperationRemoteItems r WHERE r.ShareOperationId=s.Id)
                FROM ShareOperations s
                JOIN TransferOperations t ON t.Id=s.TransferOperationId
                JOIN BatchJobs b ON b.Id=s.BatchJobId
                JOIN Accounts a ON a.Id=s.AccountId
                JOIN BatchOutputAccounts o ON o.BatchJobId=s.BatchJobId AND o.AccountId=s.AccountId
                JOIN ShareOperationGenerations l ON l.ShareOperationId=s.Id AND l.Generation=s.Generation
                WHERE s.Id=$id;
                """;
            diagnosticCommand.Parameters.AddWithValue("$id", share.ToString("D"));
            await using var diagnosticReader = await diagnosticCommand.ExecuteReaderAsync();
            Assert.True(await diagnosticReader.ReadAsync());
            Assert.Equal("Failed", diagnosticReader.GetString(0));
            Assert.True(diagnosticReader.IsDBNull(1));
            Assert.Equal("DefinitelyNotAccepted", diagnosticReader.GetString(2));
            Assert.Equal(("Succeeded", "ResultReceived", "[\"remote-1\"]"),
                (diagnosticReader.GetString(3), diagnosticReader.GetString(4), diagnosticReader.GetString(5)));
            Assert.Equal("CompletedWithShortfall", diagnosticReader.GetString(6));
            Assert.Equal(("Active", 1L, 0L, 1L, 1L, 1L),
                (diagnosticReader.GetString(7), diagnosticReader.GetInt64(8), diagnosticReader.GetInt64(9), diagnosticReader.GetInt64(10),
                 diagnosticReader.GetInt64(11), diagnosticReader.GetInt64(12)));
        }

        var result = await Actions(db).RetryShareOnlyAsync(share, 1, BatchTestData.Now);

        Assert.True(result.Accepted);
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT batch.Status,batch.CompletedAt,transfer.Status,share.Status
            FROM BatchJobs batch
            JOIN TransferOperations transfer ON transfer.BatchJobId=batch.Id
            JOIN ShareOperations share ON share.TransferOperationId=transfer.Id
            WHERE batch.Id=$batch;
            """;
        command.Parameters.AddWithValue("$batch", batch.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Active", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal("Succeeded", reader.GetString(2));
        Assert.Equal("WaitingShare", reader.GetString(3));
    }

    [Fact]
    public async Task ReconciliationFacadeRoutesTransferAndShareIdsIndependently()
    {
        var transfer = new RecordingTransferReconciler(TransferConfirmation.ConfirmedPresent);
        var share = new RecordingShareReconciler(new(ShareReconciliationDisposition.RequiresUser, "等待账号登录"));
        var actions = new JobResultActions(
            new RejectingShareActionStore(), new RejectingTransferActionStore(), transfer, share,
            new FrozenTimeProvider(BatchTestData.Now), new EnabledActionCapability());
        var transferId = Guid.NewGuid();
        var shareId = Guid.NewGuid();

        var transferResult = await actions.ReconcileTransferAsync(transferId);
        var shareResult = await actions.ReconcileShareAsync(shareId);

        Assert.Equal(transferId, transfer.ObservedId);
        Assert.Equal(shareId, share.ObservedId);
        Assert.Equal(JobResultActionDisposition.ConfirmedPresent, transferResult.Disposition);
        Assert.Equal(JobResultActionDisposition.RequiresUser, shareResult.Disposition);
        Assert.Equal("等待账号登录", shareResult.StatusText);
    }

    [Fact]
    public async Task ResultActionsAreUnavailableByDefaultAndDoNotTouchStoresOrReconcilers()
    {
        var transfer = new RecordingTransferReconciler(TransferConfirmation.ConfirmedPresent);
        var share = new RecordingShareReconciler(new(ShareReconciliationDisposition.Accepted, "已确认"));
        var actions = new JobResultActions(
            new ThrowingShareActionStore(), new ThrowingTransferActionStore(), transfer, share,
            new FrozenTimeProvider(BatchTestData.Now));
        var operationId = Guid.NewGuid();

        var results = new[]
        {
            await actions.RetryShareOnlyAsync(operationId, 1, BatchTestData.Now),
            await actions.ResumeShareAsync(operationId, 1, BatchTestData.Now),
            await actions.ResumeTransferAsync(operationId, 0, BatchTestData.Now),
            await actions.ReconcileTransferAsync(operationId),
            await actions.ReconcileShareAsync(operationId),
        };

        Assert.All(results, result => Assert.Equal(JobResultActionDisposition.RequiresUser, result.Disposition));
        Assert.Null(transfer.ObservedId);
        Assert.Null(share.ObservedId);
    }

    [Fact]
    public async Task ExplicitShareResumeSafelyReturnsPausedShareToCoordinatorQueue()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1);
        var transfer = Operation(batch, resource, account, 1, "Succeeded", "NewTransfer") with
        {
            SubmitState = "ResultReceived", ConfirmationDescriptorJson = "{}",
            RemoteItemIdsJson = "[\"remote-1\"]", TargetSlotState = "Fulfilled",
        };
        await db.SeedAsync(
            [BatchTestData.Account(account, "share-resume")],
            job: Job(batch) with { Status = "Active", ResourceCount = 1, OperationCount = 1 },
            resources: [resource], operations: [transfer], allocations: [Allocation(batch, account, 1)]);
        var share = await SeedShareAsync(db, batch, account, transfer, resource, "Paused", "ExpiredLogin");

        var result = await Actions(db).ResumeShareAsync(share, 1, BatchTestData.Now);

        Assert.True(result.Accepted);
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status,Generation,LeaseExpiresAt FROM ShareOperations WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", share.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("WaitingShare", reader.GetString(0));
        Assert.Equal(2, reader.GetInt32(1));
        Assert.True(reader.IsDBNull(2));
    }

    [Fact]
    public async Task ExplicitTransferResumeUsesExistingAtomicPausedTransition()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1) with
        {
            CandidateStatus = "Reserved",
            ReservedAccountId = account,
            ReservationExpiresAt = BatchTestData.Now.AddMinutes(5),
        };
        var transfer = Operation(batch, resource, account, 1, "Paused", null) with
        {
            TargetSlotState = "Reserved",
            TargetSlotReservedAt = BatchTestData.Now,
        };
        await db.SeedAsync(
            [BatchTestData.Account(account, "transfer-resume")],
            job: Job(batch) with { Status = "Active", ResourceCount = 1, OperationCount = 1 },
            resources: [resource], operations: [transfer],
            allocations: [Allocation(batch, account, 1) with { ReservedSlotCount = 1 }]);

        var result = await Actions(db).ResumeTransferAsync(transfer.Id, transfer.AttemptCount, transfer.UpdatedAt);

        Assert.True(result.Accepted);
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status,TargetSlotState FROM TransferOperations WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", transfer.Id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Waiting", reader.GetString(0));
        Assert.Equal("Released", reader.GetString(1));
    }

    [Fact]
    public async Task OldTransferSnapshotCannotResumeANewerPausedState()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1) with
        {
            CandidateStatus = "Reserved",
            ReservedAccountId = account,
            ReservationExpiresAt = BatchTestData.Now.AddMinutes(5),
        };
        var transfer = Operation(batch, resource, account, 1, "Paused", null) with
        {
            TargetSlotState = "Reserved",
            TargetSlotReservedAt = BatchTestData.Now,
        };
        await db.SeedAsync(
            [BatchTestData.Account(account, "transfer-aba")],
            job: Job(batch) with { Status = "Active", ResourceCount = 1, OperationCount = 1 },
            resources: [resource], operations: [transfer],
            allocations: [Allocation(batch, account, 1) with { ReservedSlotCount = 1 }]);
        var actions = Actions(db);

        Assert.True((await actions.ResumeTransferAsync(transfer.Id, transfer.AttemptCount, transfer.UpdatedAt)).Accepted);
        await using (var connection = await db.ConnectionFactory.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, """
                UPDATE TransferOperations
                SET Status='Paused',TargetSlotState='Reserved',UpdatedAt=$updated
                WHERE Id=$id;
                """,
                ("$id", transfer.Id.ToString("D")),
                ("$updated", transfer.UpdatedAt.ToString("O")));
        }

        var stale = await actions.ResumeTransferAsync(transfer.Id, transfer.AttemptCount, transfer.UpdatedAt);

        Assert.Equal(JobResultActionDisposition.Stale, stale.Disposition);
        await using var verify = await db.ConnectionFactory.OpenConnectionAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = "SELECT Status,TargetSlotState,UpdatedAt,AttemptCount FROM TransferOperations WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", transfer.Id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(("Paused", "Reserved", transfer.UpdatedAt, transfer.AttemptCount + 1),
            (reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)), reader.GetInt32(3)));
    }

    [Fact]
    public async Task ImportPagingUsesStableCursorAndBoundsPageSize()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var importId = Guid.NewGuid();
        await db.SeedAsync(
            [],
            BatchTestData.ReadySession(importId, 4, 3, invalidRows: 1),
            [
                BatchTestData.ReadyRow(importId, 1),
                BatchTestData.ReadyRow(importId, 2),
                BatchTestData.ReadyRow(importId, 3),
                BatchTestData.ReadyRow(importId, 4) with { Disposition = "Invalid", ValidationMessage = "Safe validation failure." },
            ]);
        var query = new ImportPageQuery(db.UnitOfWorkFactory);

        var first = await query.ExecuteAsync(new ImportPageRequest(importId, "Ready", null, 2));
        var second = await query.ExecuteAsync(new ImportPageRequest(importId, "Ready", null, 2, first.NextSourceRowNumber));

        Assert.Equal([1, 2], first.Items.Select(item => item.SourceRowNumber));
        Assert.Equal([3], second.Items.Select(item => item.SourceRowNumber));
        Assert.Equal(3, first.TotalCount);
        Assert.Null(second.NextSourceRowNumber);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => query.ExecuteAsync(new ImportPageRequest(importId, null, null, 1_001)));
    }

    [Fact]
    public async Task JobPagingAndStatisticsKeepResourceAndOperationCountsSeparate()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var resources = Enumerable.Range(1, 3).Select(i => Resource(batch, i)).ToArray();
        var operations = new[]
        {
            Operation(batch, resources[0], accountA, 1, "Succeeded", "NewTransfer"),
            Operation(batch, resources[0], accountB, 2, "Waiting", null),
            Operation(batch, resources[1], accountA, 3, "Succeeded", "AlreadySatisfied"),
            Operation(batch, resources[1], accountB, 4, "Succeeded", "NewTransfer"),
            Operation(batch, resources[2], accountA, 5, "Failed", "Failed", "SAFE_FAILURE", "A safe failure."),
            Operation(batch, resources[2], accountB, 6, "Paused", null) with
            {
                TargetSlotState = "Reserved",
                TargetSlotReservedAt = BatchTestData.Now,
            },
        };
        await db.SeedAsync(
            [BatchTestData.Account(accountA, "A"), BatchTestData.Account(accountB, "B")],
            job: Job(batch),
            resources: resources,
            operations: operations,
            allocations:
            [
                Allocation(batch, accountA, 1),
                Allocation(batch, accountB, 2),
            ]);
        var pages = new JobPageQuery(db.UnitOfWorkFactory);

        var first = await pages.GetOperationsAsync(new OperationPageRequest(batch, accountA, "Succeeded", null, null, 1));
        var second = await pages.GetOperationsAsync(new OperationPageRequest(batch, accountA, "Succeeded", null, null, 1, first.NextCursor));
        var timeFiltered = await pages.GetOperationsAsync(new OperationPageRequest(
            batch,
            accountA,
            null,
            new DateTimeOffset(2026, 8, 19, 9, 2, 30, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 8, 19, 9, 3, 30, TimeSpan.FromHours(8)),
            10));
        var resourcePage = await pages.GetResourcesAsync(new ResourcePageRequest(batch, null, null, 2));
        var statistics = await new BatchStatisticsQuery(db.UnitOfWorkFactory).ExecuteAsync(batch);

        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].OperationId, second.Items[0].OperationId);
        Assert.Single(timeFiltered.Items);
        Assert.Equal(1, timeFiltered.Items[0].AllocationOrdinal);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(3, resourcePage.TotalCount);
        Assert.DoesNotContain("pan.baidu.com", resourcePage.Items[0].ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, statistics.ResourceCount);
        Assert.Equal(6, statistics.OperationCount);
        Assert.Collection(
            statistics.Accounts,
            a => { Assert.Equal(accountA, a.AccountId); Assert.Equal((1, 1, 1, 0, 0), (a.NewCount, a.ExistingCount, a.FailedCount, a.PendingCount, a.PausedCount)); },
            a => { Assert.Equal(accountB, a.AccountId); Assert.Equal((1, 0, 0, 1, 1), (a.NewCount, a.ExistingCount, a.FailedCount, a.PendingCount, a.PausedCount)); });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            pages.GetOperationsAsync(new(batch, null, null, null, null, 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            pages.GetResourcesAsync(new(batch, null, null, 1_001)));
    }

    [Fact]
    public async Task OperationPagingRejectsDuplicateAllocationOrdinalsInsteadOfSkippingRows()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resources = new[] { Resource(batch, 1), Resource(batch, 2) };
        var first = Operation(batch, resources[0], account, 1, "Waiting", null);
        var second = Operation(batch, resources[1], account, 2, "Waiting", null) with
        {
            AllocationOrdinal = first.AllocationOrdinal,
        };
        await db.SeedAsync(
            [BatchTestData.Account(account, "duplicate-ordinal")],
            job: Job(batch) with { ResourceCount = 2, OperationCount = 2 },
            resources: resources,
            operations: [first, second],
            allocations: [Allocation(batch, account, 1)]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new JobPageQuery(db.UnitOfWorkFactory).GetOperationsAsync(
                new(batch, null, null, null, null, 1)));

        Assert.Contains("duplicate allocation ordinals", error.Message, StringComparison.Ordinal);
        var resumed = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new JobPageQuery(db.UnitOfWorkFactory).GetOperationsAsync(
                new(batch, null, null, null, null, 1, first.AllocationOrdinal)));
        Assert.Contains("cursor is ambiguous", resumed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultExportStreamsKeysetPagesAndEscapesFormulaText()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resources = Enumerable.Range(1, 3).Select(i => Resource(batch, i)).ToArray();
        await db.SeedAsync(
            [BatchTestData.Account(account, "export")],
            job: Job(batch) with { OperationCount = 3 },
            resources: resources,
            operations: resources.Select((resource, i) =>
                Operation(batch, resource, account, i + 1, "Failed", "Failed", "SAFE_FAILURE", "=unsafe error")),
            allocations: [Allocation(batch, account, 1)]);
        var service = new ExportBatchResultsService(db.UnitOfWorkFactory);
        await using var output = new MemoryStream();

        var exported = await service.ExportAsync(
            new ExportBatchResultsRequest(batch, null, null, null, 1),
            output,
            new OpenXmlResultWriter());

        Assert.Equal(3, exported.ExportedRows);
        output.Position = 0;
        using var workbook = SpreadsheetDocument.Open(output, false);
        var worksheet = workbook.WorkbookPart!.WorksheetParts.Single().Worksheet
            ?? throw new InvalidDataException("The result worksheet is missing.");
        var rows = worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>().ToArray();
        Assert.Equal(4, rows.Length);
        var headers = rows[0].Descendants<DocumentFormat.OpenXml.Spreadsheet.Text>().Select(text => text.Text).ToArray();
        Assert.Equal("源行号", headers[0]);
        var firstData = rows[1].Descendants<DocumentFormat.OpenXml.Spreadsheet.Text>().Select(text => text.Text).ToArray();
        Assert.Equal("'=unsafe note", firstData[4]);
        Assert.Equal("'=unsafe error", firstData[5]);
    }

    [Fact]
    public async Task ResultExportDoesNotReportARowUntilTheWriterConsumesIt()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1);
        await db.SeedAsync(
            [BatchTestData.Account(account, "progress")],
            job: Job(batch) with { ResourceCount = 1, OperationCount = 1 },
            resources: [resource],
            operations: [Operation(batch, resource, account, 1, "Succeeded", "NewTransfer")],
            allocations: [Allocation(batch, account, 1)]);
        var reports = new List<long>();

        await Assert.ThrowsAsync<IOException>(() => new ExportBatchResultsService(db.UnitOfWorkFactory).ExportAsync(
            new(batch, null, null, null, 10),
            Stream.Null,
            new RejectingResultWriter(),
            new InlineProgress<long>(reports.Add)));

        Assert.Empty(reports);
    }

    [Fact]
    public async Task TimeFilteringComparesLegacyOffsetTimestampsAsUtcInstants()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var resource = Resource(batch, 1);
        var operation = Operation(batch, resource, account, 1, "Waiting", null);
        await db.SeedAsync(
            [BatchTestData.Account(account, "offset")],
            job: Job(batch) with { ResourceCount = 1, OperationCount = 1 },
            resources: [resource],
            operations: [operation],
            allocations: [Allocation(batch, account, 1)]);
        await using (var connection = await db.ConnectionFactory.OpenConnectionAsync())
        {
            await ExecuteAsync(
                connection,
                "UPDATE TransferOperations SET UpdatedAt='2026-08-19T09:00:00.0000000+08:00' WHERE Id=$id;",
                ("$id", operation.Id.ToString("D")));
        }

        var page = await new JobPageQuery(db.UnitOfWorkFactory).GetOperationsAsync(new(
            batch,
            account,
            null,
            new DateTimeOffset(2026, 8, 19, 1, 30, 0, TimeSpan.Zero),
            null,
            10));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task TwoHundredThousandResultsUseIndexedKeysetsAndBoundedExportMemory()
    {
        const int count = 200_000;
        await using var db = await BatchTestDatabase.CreateAsync();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        await db.SeedAsync(
            [BatchTestData.Account(account, "scale")],
            job: Job(batch) with { ResourceCount = count, OperationCount = count },
            allocations: [Allocation(batch, account, 1)]);
        await using (var connection = await db.ConnectionFactory.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, $"""
                WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<{count})
                INSERT INTO ResourceItems(
                    Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,FolderCreationModeOverride,
                    GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
                SELECT printf('10000000-0000-0000-0000-%012d',x),$batch,x,
                    'https://pan.baidu.com/s/'||x,'share-'||x,'Keep','Unknown','Expanded',$time,$time
                FROM seq;
                """, ("$batch", batch.ToString("D")), ("$time", BatchTestData.Now.ToString("O")));
            await ExecuteAsync(connection, $"""
                WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<{count})
                INSERT INTO TransferOperations(
                    Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,
                    AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,
                    SubmitState,TargetSlotState,CreatedAt,UpdatedAt)
                SELECT printf('20000000-0000-0000-0000-%012d',x),
                    printf('10000000-0000-0000-0000-%012d',x),$batch,x,x,'/target','/target',$account,'Rule',
                    CASE x%4 WHEN 0 THEN 'Waiting' WHEN 3 THEN 'Failed' ELSE 'Succeeded' END,
                    CASE x%4 WHEN 1 THEN 'NewTransfer' WHEN 2 THEN 'AlreadySatisfied' WHEN 3 THEN 'Failed' ELSE NULL END,
                    0,'fp-'||x,'NotPrepared','Unoccupied',$time,$time
                FROM seq;
                """, ("$batch", batch.ToString("D")), ("$account", account.ToString("D")), ("$time", BatchTestData.Now.ToString("O")));

            var accountStatusPlan = await ExplainAsync(connection, """
                EXPLAIN QUERY PLAN
                SELECT o.Id FROM TransferOperations o INDEXED BY IX_TransferOperations_Result_Account_Keyset
                WHERE o.BatchJobId=$batch AND o.AssignedAccountId=$account AND o.Status=$status
                  AND o.AllocationOrdinal>$after
                ORDER BY o.AllocationOrdinal,o.SequenceNumber,o.Id LIMIT $limit;
                """,
                ("$batch", batch.ToString("D")), ("$account", account.ToString("D")),
                ("$status", "Succeeded"), ("$after", 100_000), ("$limit", 101));
            Assert.Contains(accountStatusPlan, line => line.Contains(
                "IX_TransferOperations_Result_Account_Keyset", StringComparison.Ordinal));

            var instantPlan = await ExplainAsync(connection, """
                EXPLAIN QUERY PLAN
                SELECT o.Id FROM TransferOperations o INDEXED BY IX_TransferOperations_Batch_AllocationOrdinal
                WHERE o.BatchJobId=$batch AND julianday(o.UpdatedAt)>=julianday($from)
                  AND o.AllocationOrdinal>$after
                ORDER BY o.AllocationOrdinal,o.SequenceNumber,o.Id LIMIT $limit;
                """,
                ("$batch", batch.ToString("D")), ("$from", "2026-08-19T00:00:00.0000000Z"),
                ("$after", 100_000), ("$limit", 101));
            Assert.Contains(instantPlan, line => line.Contains(
                "IX_TransferOperations_Batch_AllocationOrdinal", StringComparison.Ordinal));
            Assert.DoesNotContain(instantPlan, line => line.Contains("USE TEMP B-TREE", StringComparison.Ordinal));

            var instantCountPlan = await ExplainAsync(connection, """
                EXPLAIN QUERY PLAN
                SELECT COUNT(*) FROM TransferOperations o
                WHERE o.BatchJobId=$batch AND julianday(o.UpdatedAt)>=julianday($from);
                """,
                ("$batch", batch.ToString("D")), ("$from", "2026-08-19T00:00:00.0000000Z"));
            Assert.Contains(instantCountPlan, line => line.Contains(
                "IX_TransferOperations_Result_UpdatedInstant_Keyset", StringComparison.Ordinal));

            var accountOnlyPlan = await ExplainAsync(connection, """
                EXPLAIN QUERY PLAN
                SELECT o.Id FROM TransferOperations o INDEXED BY IX_TransferOperations_Result_Account_Keyset
                WHERE o.BatchJobId=$batch AND o.AssignedAccountId=$account
                  AND o.AllocationOrdinal>$after
                ORDER BY o.AllocationOrdinal,o.SequenceNumber,o.Id LIMIT $limit;
                """,
                ("$batch", batch.ToString("D")), ("$account", account.ToString("D")),
                ("$after", 100_000), ("$limit", 101));
            Assert.DoesNotContain(accountOnlyPlan, line => line.Contains("USE TEMP B-TREE", StringComparison.Ordinal));
            Assert.Contains(accountOnlyPlan, line => line.Contains(
                "IX_TransferOperations_Result_Account_Keyset", StringComparison.Ordinal));

            var resourcePlan = await ExplainAsync(connection, """
                EXPLAIN QUERY PLAN
                SELECT Id FROM ResourceItems
                WHERE BatchJobId=$batch AND GlobalValidityStatus=$validity AND CandidateStatus=$status
                  AND CandidateSequence>$after
                ORDER BY CandidateSequence,Id LIMIT $limit;
                """,
                ("$batch", batch.ToString("D")), ("$validity", "Unknown"),
                ("$status", "Expanded"), ("$after", 100_000), ("$limit", 101));
            Assert.Contains(resourcePlan, line => line.Contains(
                "IX_ResourceItems_Result_Filter_Keyset", StringComparison.Ordinal));

            var statisticsPlan = await ExplainAsync(connection, """
                EXPLAIN QUERY PLAN
                SELECT AssignedAccountId,Status,OutcomeKind,COUNT(*) FROM TransferOperations
                WHERE BatchJobId=$batch
                GROUP BY AssignedAccountId,Status,OutcomeKind;
                """, ("$batch", batch.ToString("D")));
            Assert.Contains(statisticsPlan, line => line.Contains(
                "SEARCH TransferOperations USING INDEX IX_TransferOperations_", StringComparison.Ordinal));
            Assert.DoesNotContain(statisticsPlan, line => line.Contains(
                "SCAN TransferOperations", StringComparison.Ordinal));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(true);
        var timer = Stopwatch.StartNew();
        var pages = new JobPageQuery(db.UnitOfWorkFactory);
        var page = await pages.GetOperationsAsync(new(batch, account, "Succeeded", null, null, 100));
        var resources = await pages.GetResourcesAsync(new(batch, null, null, 100));
        var statistics = await new BatchStatisticsQuery(db.UnitOfWorkFactory).ExecuteAsync(batch);
        var accountWriter = new CountingResultWriter();
        var export = await new ExportBatchResultsService(db.UnitOfWorkFactory).ExportAsync(
            new(batch, account, null, null, 1_000),
            Stream.Null,
            accountWriter);
        var timeWriter = new CountingResultWriter();
        var timeExport = await new ExportBatchResultsService(db.UnitOfWorkFactory).ExportAsync(
            new(batch, null, null, BatchTestData.Now.AddMinutes(-1), 1_000),
            Stream.Null,
            timeWriter);
        timer.Stop();
        output.WriteLine(
            "QUERY_EXPORT_200K_FILTERED elapsed_ms={0} peak_managed_delta_bytes={1} account_rows={2} time_rows={3}",
            timer.ElapsedMilliseconds,
            Math.Max(accountWriter.MaximumManagedBytes, timeWriter.MaximumManagedBytes) - baseline,
            accountWriter.RowCount,
            timeWriter.RowCount);

        Assert.Equal(100, page.Items.Count);
        Assert.Equal(count / 2, page.TotalCount);
        Assert.Equal(100, resources.Items.Count);
        Assert.Equal(count, statistics.ResourceCount);
        Assert.Equal(count, statistics.OperationCount);
        Assert.Equal((50_000L, 50_000L, 50_000L, 50_000L),
            (statistics.Accounts[0].NewCount, statistics.Accounts[0].ExistingCount,
             statistics.Accounts[0].FailedCount, statistics.Accounts[0].PendingCount));
        Assert.Equal(0L, statistics.Accounts[0].PausedCount);
        Assert.Equal(count, export.ExportedRows);
        Assert.Equal(count, timeExport.ExportedRows);
        Assert.Equal(count, accountWriter.RowCount);
        Assert.Equal(count, timeWriter.RowCount);
        var peakManagedBytes = Math.Max(accountWriter.MaximumManagedBytes, timeWriter.MaximumManagedBytes);
        Assert.True(peakManagedBytes - baseline < 128L * 1024 * 1024,
            $"Managed memory grew by {peakManagedBytes - baseline:N0} bytes.");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"Scale query/export took {timer.Elapsed}.");
    }

    private static BatchJobRecord Job(Guid id) => new(
        id, "batch", "Excel", "Replication", "TaskCount", "ProportionalReduction", 3, 5,
        "/", "Keep", "Skip", "Immediate", "Queued", BatchTestData.Now, null, null, null, 1);

    private static ResourceItemRecord Resource(Guid batch, int sequence) => new(
        Guid.NewGuid(), batch, null, sequence, $"https://pan.baidu.com/s/{sequence}", $"share-{sequence}",
        null, sequence == 1 ? "=unsafe note" : $"note-{sequence}", null, null, null, "Keep", "Unknown",
        null, "Expanded", null, null, BatchTestData.Now, BatchTestData.Now);

    private static TransferOperationRecord Operation(
        Guid batch,
        ResourceItemRecord resource,
        Guid account,
        long ordinal,
        string status,
        string? outcome,
        string? errorCode = null,
        string? errorMessage = null) => new(
            Guid.NewGuid(), resource.Id, batch, ordinal, ordinal, "/target", "/target", "", account, "Rule",
            status, outcome, 1, $"fingerprint-{ordinal}", null, null, null, "NotPrepared", "Unoccupied",
            null, null, null, errorCode, errorMessage, null, BatchTestData.Now, BatchTestData.Now.AddMinutes(ordinal));

    private static AccountAllocationRecord Allocation(Guid batch, Guid account, int priority) => new(
        Guid.NewGuid(), batch, account, priority, "/", "/", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static async Task<Guid> SeedShareAsync(
        BatchTestDatabase db,
        Guid batch,
        Guid account,
        TransferOperationRecord transfer,
        ResourceItemRecord resource,
        string status,
        string? errorCode)
    {
        var share = Guid.NewGuid();
        await using var connection = await db.ConnectionFactory.OpenConnectionAsync();
        await ExecuteAsync(connection, """
            INSERT INTO BatchShareSettings(
              BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,FixedExtractionCode,
              ExistingSharePolicy,SettingsFingerprint,CreatedAt)
            VALUES($batch,1,1,'Random',NULL,'Reuse','settings-fp',$time);
            INSERT INTO BatchOutputAccounts(
              BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled)
            VALUES($batch,$account,1,'账号',1,1,1);
            INSERT INTO ShareOperations(
              Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,
              ExactItemSetFingerprint,SettingsFingerprint,Generation,RequestDescriptorJson,
              AdapterRequestId,SubmissionAcceptance,AttemptCount,LeaseGeneration,LeaseExpiresAt,
              SubmitStartedAt,NextReconcileAt,LastErrorCode,LastErrorMessage,CreatedAt,UpdatedAt)
            VALUES($share,$transfer,$resource,$batch,$account,1,$status,'exact-fp','settings-fp',1,
              NULL,NULL,'DefinitelyNotAccepted',1,1,NULL,NULL,NULL,$error,'safe share failure',$time,$time);
            INSERT INTO ShareOperationRemoteItems(
              ShareOperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName)
            VALUES($share,0,'remote-1',X'01','remote');
            UPDATE ShareOperationGenerations SET ExactItemCount=1
            WHERE ShareOperationId=$share AND Generation=1;
            """,
            ("$batch", batch.ToString("D")),
            ("$account", account.ToString("D")),
            ("$share", share.ToString("D")),
            ("$transfer", transfer.Id.ToString("D")),
            ("$resource", resource.Id.ToString("D")),
            ("$status", status),
            ("$error", errorCode ?? (object)DBNull.Value),
            ("$time", BatchTestData.Now.ToString("O")));
        return share;
    }

    private static IJobResultActions Actions(BatchTestDatabase db) => new JobResultActions(
        new SqliteShareSchedulingStore(db.ConnectionFactory),
        new SqliteTransferSchedulingStore(db.ConnectionFactory),
        new RecordingTransferReconciler(TransferConfirmation.Ambiguous),
        new RecordingShareReconciler(new(ShareReconciliationDisposition.Stale, "任务状态已变化")),
        new FrozenTimeProvider(BatchTestData.Now.AddMinutes(10)), new EnabledActionCapability());

    private sealed class EnabledActionCapability : IJobResultActionCapability
    {
        public bool CanUseTransferActions => true;
        public bool CanUseShareActions => true;
    }

    private sealed class RecordingTransferReconciler(TransferConfirmation result) : ITransferResultReconciler
    {
        public Guid? ObservedId { get; private set; }
        public Task<TransferConfirmation> ReconcileAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            ObservedId = operationId;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingShareReconciler(ShareReconciliationResult result) : IShareResultReconciler
    {
        public Guid? ObservedId { get; private set; }
        public Task<ShareReconciliationResult> ReconcileAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            ObservedId = operationId;
            return Task.FromResult(result);
        }
    }

    private sealed class RejectingShareActionStore : IJobResultShareActionStore
    {
        public Task<bool> TryQueueFailedShareAsync(Guid operationId, int expectedGeneration, DateTimeOffset expectedUpdatedAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task<bool> TryResumePausedShareAsync(Guid operationId, int expectedGeneration, DateTimeOffset expectedUpdatedAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class RejectingTransferActionStore : IJobResultTransferActionStore
    {
        public Task<bool> ResumePausedAsync(Guid operationId, int expectedAttemptCount, DateTimeOffset expectedUpdatedAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class ThrowingShareActionStore : IJobResultShareActionStore
    {
        public Task<bool> TryQueueFailedShareAsync(Guid operationId, int expectedGeneration, DateTimeOffset expectedUpdatedAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The disabled share store must not be called.");

        public Task<bool> TryResumePausedShareAsync(Guid operationId, int expectedGeneration, DateTimeOffset expectedUpdatedAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The disabled share store must not be called.");
    }

    private sealed class ThrowingTransferActionStore : IJobResultTransferActionStore
    {
        public Task<bool> ResumePausedAsync(Guid operationId, int expectedAttemptCount, DateTimeOffset expectedUpdatedAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The disabled transfer store must not be called.");
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> ExplainAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        return plan;
    }

    private sealed class CountingResultWriter : IBatchResultWriter
    {
        public long RowCount { get; private set; }
        public long MaximumManagedBytes { get; private set; }

        public async Task WriteAsync(Stream stream, IAsyncEnumerable<BatchResultExportRow> rows, CancellationToken cancellationToken = default)
        {
            await foreach (var _ in rows.WithCancellation(cancellationToken))
            {
                RowCount++;
                if ((RowCount & 1023) == 0)
                    MaximumManagedBytes = Math.Max(MaximumManagedBytes, GC.GetTotalMemory(false));
            }
            MaximumManagedBytes = Math.Max(MaximumManagedBytes, GC.GetTotalMemory(false));
        }
    }

    private sealed class RejectingResultWriter : IBatchResultWriter
    {
        public async Task WriteAsync(
            Stream stream,
            IAsyncEnumerable<BatchResultExportRow> rows,
            CancellationToken cancellationToken = default)
        {
            await using var enumerator = rows.GetAsyncEnumerator(cancellationToken);
            Assert.True(await enumerator.MoveNextAsync());
            throw new IOException("The destination rejected the first row.");
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
