using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class ManagedTransferAcceptanceStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"bbt-managed-acceptance-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExactBatchReservationMutatesOnlyItsSinglePersistedGraph()
    {
        var graph = await CreateGraphAsync();
        var store = new SqliteManagedTransferAcceptanceStore(graph.Factory);

        Assert.Null(await store.TryReserveTransferAsync(
            Guid.NewGuid(), graph.Now, graph.Now.AddMinutes(5)));
        var reserved = Assert.IsType<ManagedTransferAcceptanceReservation>(
            await store.TryReserveTransferAsync(
                graph.BatchId, graph.Now, graph.Now.AddMinutes(5)));

        Assert.Equal(graph.BatchId, reserved.BatchId);
        Assert.Equal(graph.TransferId, reserved.TransferOperationId);
        Assert.Equal(graph.ShareId, reserved.ShareOperationId);
        Assert.Equal(graph.AccountId, reserved.AccountId);
        Assert.Equal(1, reserved.TransferGeneration);
        Assert.Equal("Active", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM BatchJobs WHERE Id=$id;", graph.BatchId));
        Assert.Equal("Reserved", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM TransferOperations WHERE Id=$id;", graph.TransferId));
        Assert.Equal("Reserved", await ScalarAsync<string>(graph.Factory,
            "SELECT CandidateStatus FROM ResourceItems WHERE Id=$id;", graph.ResourceId));
        Assert.Equal(1L, await ScalarAsync<long>(graph.Factory,
            "SELECT ReservedSlotCount FROM AccountAllocations WHERE BatchJobId=$id;", graph.BatchId));
        Assert.Equal("WaitingTransfer", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareId));
    }

    [Fact]
    public async Task AllocationCasFailureRollsBackBatchResourceAndTransferReservation()
    {
        var graph = await CreateGraphAsync();
        await ExecuteAsync(graph.Factory, """
            CREATE TRIGGER FailAcceptanceAllocationCas
            BEFORE UPDATE OF ReservedSlotCount ON AccountAllocations
            BEGIN SELECT RAISE(ABORT,'forced acceptance allocation failure'); END;
            """, graph.BatchId);
        var store = new SqliteManagedTransferAcceptanceStore(graph.Factory);

        await Assert.ThrowsAsync<SqliteException>(() => store.TryReserveTransferAsync(
            graph.BatchId, graph.Now, graph.Now.AddMinutes(5)));

        Assert.Equal("Queued", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM BatchJobs WHERE Id=$id;", graph.BatchId));
        Assert.Equal("Waiting", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM TransferOperations WHERE Id=$id;", graph.TransferId));
        Assert.Equal("Expanded", await ScalarAsync<string>(graph.Factory,
            "SELECT CandidateStatus FROM ResourceItems WHERE Id=$id;", graph.ResourceId));
        Assert.Equal(0L, await ScalarAsync<long>(graph.Factory,
            "SELECT ReservedSlotCount FROM AccountAllocations WHERE BatchJobId=$id;", graph.BatchId));

        await ExecuteAsync(graph.Factory, "DROP TRIGGER FailAcceptanceAllocationCas;", graph.BatchId);
        Assert.NotNull(await store.TryReserveTransferAsync(
            graph.BatchId, graph.Now.AddSeconds(1), graph.Now.AddMinutes(5)));
    }

    [Fact]
    public async Task RepeatedReservationDoesNotExecuteOrAdvanceTheAlreadyReservedTransfer()
    {
        var graph = await CreateGraphAsync();
        var store = new SqliteManagedTransferAcceptanceStore(graph.Factory);
        var first = Assert.IsType<ManagedTransferAcceptanceReservation>(
            await store.TryReserveTransferAsync(
                graph.BatchId, graph.Now, graph.Now.AddMinutes(5)));

        var repeated = await store.TryReserveTransferAsync(
            graph.BatchId, graph.Now.AddSeconds(1), graph.Now.AddMinutes(6));

        Assert.Null(repeated);
        Assert.Equal(1, first.TransferGeneration);
        Assert.Equal(0L, await ScalarAsync<long>(graph.Factory,
            "SELECT AttemptCount FROM TransferOperations WHERE Id=$id;", graph.TransferId));
        Assert.Equal(1L, await ScalarAsync<long>(graph.Factory,
            "SELECT ReservedSlotCount FROM AccountAllocations WHERE BatchJobId=$id;", graph.BatchId));
    }

    [Theory]
    [InlineData("non-acceptance")]
    [InlineData("multiple-operations")]
    [InlineData("active-batch")]
    public async Task NonExactOrGloballyUnsafeScopeCannotReserve(string scenario)
    {
        var graph = await CreateGraphAsync(acceptanceOnly: scenario != "non-acceptance");
        if (scenario == "multiple-operations")
        {
            await ExecuteAsync(graph.Factory,
                "UPDATE BatchJobs SET OperationCount=2 WHERE Id=$id;", graph.BatchId);
        }
        else if (scenario == "active-batch")
        {
            await using var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory);
            await unit.Jobs.AddAsync(new(
                Guid.NewGuid(), "active", "Text", "Distribution", "TaskCount",
                "ProportionalReduction", 0, 0, "/", "None", "Skip", "Immediate",
                "Active", graph.Now, graph.Now, null, null, 0));
            await unit.CommitAsync();
        }

        var reserved = await new SqliteManagedTransferAcceptanceStore(graph.Factory)
            .TryReserveTransferAsync(graph.BatchId, graph.Now, graph.Now.AddMinutes(5));

        Assert.Null(reserved);
        Assert.Equal("Queued", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM BatchJobs WHERE Id=$id;", graph.BatchId));
        Assert.Equal("Waiting", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM TransferOperations WHERE Id=$id;", graph.TransferId));
    }

    [Fact]
    public async Task LinkedShareCannotReserveUntilExactTransferSuccessAndThenUsesOnlyThatShareId()
    {
        var graph = await CreateGraphAsync();
        var store = new SqliteManagedTransferAcceptanceStore(graph.Factory);
        var reservation = Assert.IsType<ManagedTransferAcceptanceReservation>(
            await store.TryReserveTransferAsync(
                graph.BatchId, graph.Now, graph.Now.AddMinutes(5)));

        Assert.Null(await store.TryReserveLinkedShareAsync(
            reservation, graph.Now.AddSeconds(1), graph.Now.AddMinutes(5)));
        await CompleteTransferAsync(graph);

        var shareLease = Assert.IsType<ShareExecutionLease>(
            await store.TryReserveLinkedShareAsync(
                reservation, graph.Now.AddSeconds(2), graph.Now.AddMinutes(5)));

        Assert.Equal(graph.ShareId, shareLease.OperationId);
        Assert.Equal(1, shareLease.Generation);
        Assert.Equal(1, shareLease.LeaseGeneration);
        var context = Assert.IsType<ShareExecutionContext>(
            await new SqliteShareSchedulingStore(graph.Factory).LoadAsync(graph.ShareId));
        Assert.Equal([graph.RemoteItemId],
            context.ExactRemoteItems.Items.Select(item => item.ExactId));
        Assert.Null(await store.TryReserveLinkedShareAsync(
            reservation, graph.Now.AddSeconds(3), graph.Now.AddMinutes(6)));
    }

    [Fact]
    public async Task FinalizeRequiresAnAcceptedCurrentResultForTheExactLinkedShare()
    {
        var graph = await CreateGraphAsync();
        var store = new SqliteManagedTransferAcceptanceStore(graph.Factory);
        var reservation = Assert.IsType<ManagedTransferAcceptanceReservation>(
            await store.TryReserveTransferAsync(
                graph.BatchId, graph.Now, graph.Now.AddMinutes(5)));
        await CompleteTransferAsync(graph);
        Assert.NotNull(await store.TryReserveLinkedShareAsync(
            reservation, graph.Now.AddSeconds(1), graph.Now.AddMinutes(5)));
        await ExecuteAsync(graph.Factory,
            "UPDATE ShareOperations SET Status='Succeeded' WHERE Id=$id;", graph.ShareId);

        Assert.False(await store.TryFinalizeAsync(
            reservation, graph.Now.AddSeconds(2)));
        await InsertShareResultAsync(graph, accepted: false, current: true, "rejected-share");
        Assert.False(await store.TryFinalizeAsync(
            reservation, graph.Now.AddSeconds(3)));
        await InsertShareResultAsync(graph, accepted: true, current: true, "accepted-share");

        Assert.True(await store.TryFinalizeAsync(
            reservation, graph.Now.AddSeconds(4)));
        Assert.Equal("Completed", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM BatchJobs WHERE Id=$id;", graph.BatchId));
    }

    [Fact]
    public async Task GeneralCoordinatorStateActivationAndReservationCannotSeeAcceptanceOnlyBatch()
    {
        var graph = await CreateGraphAsync();
        var general = new SqliteTransferSchedulingStore(graph.Factory);

        Assert.Null(await general.TryActivateNextBatchAsync(graph.Now));
        var reservation = Assert.IsType<ManagedTransferAcceptanceReservation>(
            await new SqliteManagedTransferAcceptanceStore(graph.Factory).TryReserveTransferAsync(
                graph.BatchId, graph.Now, graph.Now.AddMinutes(5)));
        var state = await general.GetStateAsync();

        Assert.Empty(state.ActiveBatchIds);
        Assert.Empty(state.BusyAccountIds);
        Assert.Equal(0, state.ActiveNetworkOperationCount);
        Assert.Null(await general.TryReserveNextAsync(
            reservation.BatchId, graph.Now.AddSeconds(1), graph.Now.AddMinutes(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneralTransferRecoveryLeavesAcceptanceOnlyLeaseUntouched(bool expiredOnly)
    {
        var graph = await CreateGraphAsync();
        await new SqliteManagedTransferAcceptanceStore(graph.Factory).TryReserveTransferAsync(
            graph.BatchId, graph.Now, graph.Now.AddMinutes(5));
        var general = new SqliteTransferSchedulingStore(graph.Factory);

        var recovered = expiredOnly
            ? await general.RecoverExpiredLeasesAsync(graph.Now.AddMinutes(6))
            : await general.RecoverInterruptedOperationsAsync(graph.Now.AddSeconds(1));

        Assert.Equal(new LeaseRecoveryResult(0, 0), recovered);
        Assert.Equal("Reserved", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM TransferOperations WHERE Id=$id;", graph.TransferId));
        Assert.Equal(1L, await ScalarAsync<long>(graph.Factory,
            "SELECT ReservedSlotCount FROM AccountAllocations WHERE BatchJobId=$id;", graph.BatchId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneralTransferRecoveryLeavesAcceptanceOnlyUncertainBoundaryUntouched(bool expiredOnly)
    {
        var graph = await CreateGraphAsync();
        await new SqliteManagedTransferAcceptanceStore(graph.Factory).TryReserveTransferAsync(
            graph.BatchId, graph.Now, graph.Now.AddMinutes(5));
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE TransferOperations SET Status='Submitting',SubmitState='Submitting',
                  ConfirmationDescriptorJson='{}',SubmitStartedAt=$now WHERE Id=$id;
                """;
            command.Parameters.AddWithValue("$id", graph.TransferId.ToString("D"));
            command.Parameters.AddWithValue("$now", graph.Now.AddSeconds(1).ToString("O"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var general = new SqliteTransferSchedulingStore(graph.Factory);

        var recovered = expiredOnly
            ? await general.RecoverExpiredLeasesAsync(graph.Now.AddMinutes(6))
            : await general.RecoverInterruptedOperationsAsync(graph.Now.AddSeconds(2));

        Assert.Equal(new LeaseRecoveryResult(0, 0), recovered);
        Assert.Equal("Submitting", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM TransferOperations WHERE Id=$id;", graph.TransferId));
    }

    [Fact]
    public async Task GeneralFailedTransferRetryCannotRequeueAcceptanceOnlyOperation()
    {
        var graph = await CreateGraphAsync();
        await new SqliteManagedTransferAcceptanceStore(graph.Factory).TryReserveTransferAsync(
            graph.BatchId, graph.Now, graph.Now.AddMinutes(5));
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            await using (var transfer = connection.CreateCommand())
            {
                transfer.Transaction = transaction;
                transfer.CommandText = """
                    UPDATE TransferOperations SET Status='Failed',OutcomeKind='Failed',TargetSlotState='Released',
                      TargetSlotReservedAt=NULL,LeaseExpiresAt=NULL,LastErrorCode='TransientService',
                      LastErrorMessage='Retryable failure',UpdatedAt=$now WHERE Id=$id;
                    """;
                transfer.Parameters.AddWithValue("$id", graph.TransferId.ToString("D"));
                transfer.Parameters.AddWithValue("$now", graph.Now.AddSeconds(1).ToString("O"));
                Assert.Equal(1, await transfer.ExecuteNonQueryAsync());
            }
            await using (var resource = connection.CreateCommand())
            {
                resource.Transaction = transaction;
                resource.CommandText = "UPDATE ResourceItems SET CandidateStatus='Expanded',ReservedAccountId=NULL,ReservationExpiresAt=NULL WHERE Id=$id;";
                resource.Parameters.AddWithValue("$id", graph.ResourceId.ToString("D"));
                Assert.Equal(1, await resource.ExecuteNonQueryAsync());
            }
            await using (var allocation = connection.CreateCommand())
            {
                allocation.Transaction = transaction;
                allocation.CommandText = "UPDATE AccountAllocations SET ReservedSlotCount=0,FailedItemCount=1 WHERE BatchJobId=$id;";
                allocation.Parameters.AddWithValue("$id", graph.BatchId.ToString("D"));
                Assert.Equal(1, await allocation.ExecuteNonQueryAsync());
            }
            await transaction.CommitAsync();
        }

        var result = await new SqliteTransferSchedulingStore(graph.Factory)
            .TryRetryFailedTransferAsync(graph.TransferId, graph.Now.AddSeconds(2));

        Assert.Equal(RetryFailedTransferDisposition.NotFound, result);
        Assert.Equal("Failed", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM TransferOperations WHERE Id=$id;", graph.TransferId));
        Assert.Equal(0L, await ScalarAsync<long>(graph.Factory,
            "SELECT COUNT(*) FROM ShareOperations WHERE Id=$id;", graph.ShareId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneralShareRecoveryLeavesAcceptanceOnlyLeaseUntouched(bool expiredOnly)
    {
        var graph = await CreateGraphAsync();
        var exact = new SqliteManagedTransferAcceptanceStore(graph.Factory);
        var reservation = Assert.IsType<ManagedTransferAcceptanceReservation>(
            await exact.TryReserveTransferAsync(
                graph.BatchId, graph.Now, graph.Now.AddMinutes(5)));
        await CompleteTransferAsync(graph);
        Assert.NotNull(await exact.TryReserveLinkedShareAsync(
            reservation, graph.Now.AddSeconds(2), graph.Now.AddMinutes(5)));
        var general = new SqliteShareSchedulingStore(graph.Factory);

        var recovered = expiredOnly
            ? await general.RecoverExpiredShareOperationsAsync(graph.Now.AddMinutes(6))
            : await general.RecoverInterruptedShareOperationsAsync(graph.Now.AddSeconds(3));

        Assert.Equal(new ShareLeaseRecoveryResult(0, 0), recovered);
        Assert.Equal("Reserved", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneralShareRecoveryLeavesAcceptanceOnlyUncertainBoundaryUntouched(bool expiredOnly)
    {
        var graph = await CreateGraphAsync();
        var exact = new SqliteManagedTransferAcceptanceStore(graph.Factory);
        var reservation = Assert.IsType<ManagedTransferAcceptanceReservation>(
            await exact.TryReserveTransferAsync(
                graph.BatchId, graph.Now, graph.Now.AddMinutes(5)));
        await CompleteTransferAsync(graph);
        Assert.NotNull(await exact.TryReserveLinkedShareAsync(
            reservation, graph.Now.AddSeconds(2), graph.Now.AddMinutes(5)));
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE ShareOperations SET Status='Submitting',RequestDescriptorJson='{}',
                  SubmitStartedAt=$now,SubmissionAcceptance='Unknown' WHERE Id=$id;
                """;
            command.Parameters.AddWithValue("$id", graph.ShareId.ToString("D"));
            command.Parameters.AddWithValue("$now", graph.Now.AddSeconds(3).ToString("O"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var general = new SqliteShareSchedulingStore(graph.Factory);

        var recovered = expiredOnly
            ? await general.RecoverExpiredShareOperationsAsync(graph.Now.AddMinutes(6))
            : await general.RecoverInterruptedShareOperationsAsync(graph.Now.AddSeconds(4));

        Assert.Equal(new ShareLeaseRecoveryResult(0, 0), recovered);
        Assert.Equal("Submitting", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareId));
    }

    [Fact]
    public async Task GeneralPendingAndManualRetrySelectorsIgnoreAcceptanceOnlyShare()
    {
        var graph = await CreateGraphAsync();
        var exact = new SqliteManagedTransferAcceptanceStore(graph.Factory);
        var reservation = Assert.IsType<ManagedTransferAcceptanceReservation>(
            await exact.TryReserveTransferAsync(
                graph.BatchId, graph.Now, graph.Now.AddMinutes(5)));
        await CompleteTransferAsync(graph);
        Assert.NotNull(await exact.TryReserveLinkedShareAsync(
            reservation, graph.Now.AddSeconds(2), graph.Now.AddMinutes(5)));
        var updated = graph.Now.AddSeconds(3);
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE ShareOperations SET Status='ResultPending',RequestDescriptorJson='{}',
                  SubmitStartedAt=$updated,NextReconcileAt=$updated,LeaseExpiresAt=NULL,
                  SubmissionAcceptance='Unknown',UpdatedAt=$updated WHERE Id=$id;
                """;
            command.Parameters.AddWithValue("$id", graph.ShareId.ToString("D"));
            command.Parameters.AddWithValue("$updated", updated.ToString("O"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var general = new SqliteShareSchedulingStore(graph.Factory);

        Assert.Null(await general.TryGetDueResultPendingAsync(
            graph.BatchId, updated.AddSeconds(1), []));
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE ShareOperations SET Status='Failed',SubmissionAcceptance='DefinitelyNotAccepted',
                  SubmitStartedAt=NULL,RequestDescriptorJson=NULL,LastErrorCode='TransientService',
                  LastErrorMessage='Retryable failure',UpdatedAt=$updated WHERE Id=$id;
                """;
            command.Parameters.AddWithValue("$id", graph.ShareId.ToString("D"));
            command.Parameters.AddWithValue("$updated", updated.ToString("O"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.False(await general.TryQueueFailedShareAsync(
            graph.ShareId, 1, updated, updated.AddSeconds(1)));
        Assert.Equal("Failed", await ScalarAsync<string>(graph.Factory,
            "SELECT Status FROM ShareOperations WHERE Id=$id;", graph.ShareId));

        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE ShareOperations SET Status='Paused',LastErrorCode='AwaitingExistingShareDecision',
                  LastErrorMessage='Decision required',UpdatedAt=$updated WHERE Id=$id;
                """;
            command.Parameters.AddWithValue("$id", graph.ShareId.ToString("D"));
            command.Parameters.AddWithValue("$updated", updated.AddSeconds(2).ToString("O"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.Empty(await general.GetPendingDecisionBatchIdsAsync(1));
    }

    [Fact]
    public async Task GeneralExistingShareDecisionSelectorIgnoresAcceptanceOnlyShare()
    {
        var graph = await CreateGraphAsync();
        var exact = new SqliteManagedTransferAcceptanceStore(graph.Factory);
        var reservation = Assert.IsType<ManagedTransferAcceptanceReservation>(
            await exact.TryReserveTransferAsync(
                graph.BatchId, graph.Now, graph.Now.AddMinutes(5)));
        await CompleteTransferAsync(graph);
        Assert.NotNull(await exact.TryReserveLinkedShareAsync(
            reservation, graph.Now.AddSeconds(2), graph.Now.AddMinutes(5)));
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE ShareOperations SET Status='Paused',LastErrorCode='AwaitingExistingShareDecision',
                  LastErrorMessage='Decision required' WHERE Id=$id;
                """;
            command.Parameters.AddWithValue("$id", graph.ShareId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var general = new SqliteShareSchedulingStore(graph.Factory);

        Assert.Empty(await general.GetPendingDecisionBatchIdsAsync(1));
    }

    private async Task<Graph> CreateGraphAsync(bool acceptanceOnly = true)
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(
            Path.Combine(root, $"{Guid.NewGuid():N}.db"));
        await new DatabaseMigrator(
            factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")),
            "acceptance-store-test").MigrateAsync();
        var now = new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);
        var accountId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var shareId = Guid.NewGuid();
        var account = AccountRecord.Create(accountId, "managed-user", "Managed account") with
        {
            LoginMethod = acceptanceOnly ? "ManagedChromium" : "WebView2",
        };
        ExecutionProfileSnapshot profile;
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account);
            await unit.CommitAsync();
        }
        if (acceptanceOnly)
        {
            var browser = new SqliteManagedBrowserStateStore(factory);
            var installationDirectory = Path.GetFullPath(
                Path.Combine(root, $"chrome-{Guid.NewGuid():N}"));
            var installation = new BrowserInstallationIdentity(
                Guid.NewGuid(), BrowserFamily.Chrome,
                installationDirectory,
                Path.Combine(installationDirectory, "chrome.exe"),
                "Google LLC", "Google Chrome", $"file-{Guid.NewGuid():N}");
            var storedInstallation = await browser.CreateInstallationAsync(installation, default);
            Assert.Equal(ManagedBrowserTransitionStatus.Applied,
                (await browser.TryReplaceActiveInstallationAsync(new(null, storedInstallation), default)).Status);
            var session = new ManagedBrowserSessionReference(
                Guid.NewGuid(), installation.InstallationId, accountId, account.BaiduUserId,
                BrowserFamily.Chrome, $"profile-{Guid.NewGuid():N}", $"owner-{Guid.NewGuid():N}");
            var storedSession = await browser.CreateSessionAsync(session, default);
            Assert.Equal(ManagedBrowserTransitionStatus.Applied,
                (await browser.TryReplaceActiveSessionAsync(new(null, storedSession), default)).Status);
            profile = ExecutionProfileSnapshot.Managed(
                BrowserFamily.Chrome, session.ProfileId, installation.InstallationId, session.SessionId);
        }
        else
        {
            profile = ExecutionProfileSnapshot.LegacyForAccount(accountId);
        }
        var settingsFingerprint = ShareSettingsFingerprint.Compute(
            true, true, ShareExtractionCodeBehavior.Random, null);
        var batch = new BatchJobRecord(
            batchId, "acceptance", "Text", "Distribution", "TaskCount", "ProportionalReduction",
            1, 1, "/", "None", "Skip", "Immediate", "Queued", now,
            null, null, null, 1, 1, acceptanceOnly);
        var resource = new ResourceItemRecord(
            resourceId, batchId, null, 1, "https://pan.baidu.com/s/source", "source", null,
            null, null, null, null, "None", "Valid", null, "Expanded",
            null, null, now, now, "Exact object");
        var transfer = new TransferOperationRecord(
            transferId, resourceId, batchId, 1, 1, "/target", "/target", "", accountId,
            "Acceptance", "Waiting", null, 0, "transfer-fingerprint", null, null, null,
            "NotPrepared", "Unoccupied", null, null, null, null, null, null, now, now,
            profile, acceptanceOnly);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Jobs.AddAsync(batch);
            await unit.Jobs.AddResourcesAsync([resource]);
            await unit.Jobs.AddOperationsAsync([transfer]);
            await unit.Jobs.AddAllocationsAsync([new(
                Guid.NewGuid(), batchId, accountId, 1, "/target", "/target",
                1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)]);
            await unit.Jobs.SetShareSettingsAsync(new(
                batchId, true, true, "Random", null, "Ask", now, settingsFingerprint));
            await unit.Jobs.AddOutputAccountsAsync([new(
                batchId, accountId, 1, "Managed account", 1, 1, true)]);
            await unit.Jobs.AddPendingShareOperationsAsync([new(
                shareId, transferId, resourceId, batchId, accountId, 1,
                settingsFingerprint, now)]);
            await unit.CommitAsync();
        }
        return new(
            factory, now, batchId, resourceId, transferId, shareId, accountId,
            settingsFingerprint, "remote-exact-1");
    }

    private static async Task CompleteTransferAsync(Graph graph)
    {
        var store = new SqliteTransferSchedulingStore(graph.Factory);
        await store.MarkPreparingAsync(
            graph.TransferId, graph.Now, expectedAttemptCount: 0);
        var boundary = await store.PersistAsync(new(
            new(graph.TransferId, graph.AccountId, "{\"accepted\":true}",
                graph.Now.AddMinutes(5), 1),
            "2026-08-31",
            graph.Now));
        Assert.True(boundary.Accepted);
        await store.CompleteSucceededWithEvidenceAsync(
            graph.TransferId,
            "transfer-request",
            new RemoteItemIdentitySet([new(graph.RemoteItemId, "Exact object")]),
            graph.Now.AddSeconds(1),
            expectedAttemptCount: 1);
    }

    private static async Task InsertShareResultAsync(
        Graph graph,
        bool accepted,
        bool current,
        string shareId)
    {
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO BaiduShareResults(
              Id,ShareOperationId,BatchJobId,ResourceItemId,AccountId,CandidateSequence,OutputOrdinal,
              ExactItemSetFingerprint,SettingsFingerprint,Generation,ShareId,ShareUrl,ExtractionCode,
              IsAccepted,IsCurrent,SharedAt,UpdatedAt)
            SELECT $id,share.Id,share.BatchJobId,share.ResourceItemId,share.AccountId,1,share.OutputOrdinal,
              share.ExactItemSetFingerprint,share.SettingsFingerprint,share.Generation,$shareId,
              'https://pan.baidu.com/s/result','a1b2',$accepted,$current,$now,$now
            FROM ShareOperations share WHERE share.Id=$operation;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$operation", graph.ShareId.ToString("D"));
        command.Parameters.AddWithValue("$shareId", shareId);
        command.Parameters.AddWithValue("$accepted", accepted ? 1 : 0);
        command.Parameters.AddWithValue("$current", current ? 1 : 0);
        command.Parameters.AddWithValue("$now", graph.Now.ToString("O"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task ExecuteAsync(
        SqliteConnectionFactory factory,
        string sql,
        Guid id)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        SqliteConnectionFactory factory,
        string sql,
        Guid id)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return Assert.IsType<T>(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed record Graph(
        SqliteConnectionFactory Factory,
        DateTimeOffset Now,
        Guid BatchId,
        Guid ResourceId,
        Guid TransferId,
        Guid ShareId,
        Guid AccountId,
        string SettingsFingerprint,
        string RemoteItemId);
}
