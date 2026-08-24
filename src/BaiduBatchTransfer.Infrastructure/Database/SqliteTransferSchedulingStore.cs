using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteTransferSchedulingStore :
    IAllocationTransactionStore,
    ISubmissionBoundaryStore,
    IOperationExecutionStore,
    IReassignmentStore,
    IUserConfirmedBatchResumeStore,
    IInterruptedOperationRecoveryStore,
    IAllocationCounterStore,
    IOperationReconciliationStore,
    ICancelOperationStore,
    IJobResultTransferActionStore,
    IBatchRunControlStore
{
    private readonly SqliteConnectionFactory factory;
    private readonly TimeProvider timeProvider;

    public SqliteTransferSchedulingStore(
        SqliteConnectionFactory factory,
        TimeProvider? timeProvider = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CoordinatorState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var activeBatches = new List<Guid>();
        await using (var command = Command(connection, null,
            "SELECT Id FROM BatchJobs WHERE Status='Active' ORDER BY QueueOrder,Id;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) activeBatches.Add(Guid.Parse(reader.GetString(0)));

        var busy = new List<Guid>();
        var slots = 0;
        if (activeBatches.Count > 0)
        {
            await using var command = Command(connection, null, """
                SELECT AccountId,SUM(NetworkSlots) FROM (
                  SELECT AssignedAccountId AS AccountId,
                    CASE WHEN Status IN ('Reserved','Preparing','Submitting') THEN 1 ELSE 0 END AS NetworkSlots
                  FROM TransferOperations WHERE Status IN ('Reserved','Preparing','Submitting','ResultPending','Paused')
                  UNION ALL
                  SELECT AccountId,
                    CASE WHEN Status IN ('Reserved','Preparing','Submitting') THEN 1 ELSE 0 END AS NetworkSlots
                  FROM ShareOperations WHERE Status IN ('Reserved','Preparing','Submitting','ResultPending','Paused')
                ) GROUP BY AccountId;
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                busy.Add(Guid.Parse(reader.GetString(0)));
                slots = checked(slots + reader.GetInt32(1));
            }
        }
        return new CoordinatorState(activeBatches, slots, busy);
    }

    public async Task<Guid?> TryActivateNextBatchAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(*) FROM BatchJobs WHERE Status='Active';", cancellationToken).ConfigureAwait(false) != 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var idText = await ScalarStringOrNullAsync(connection, transaction,
            "SELECT Id FROM BatchJobs WHERE Status='Queued' ORDER BY QueueOrder,CreatedAt,Id LIMIT 1;",
            cancellationToken).ConfigureAwait(false);
        if (idText is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        await using var update = Command(connection, transaction,
            "UPDATE BatchJobs SET Status='Active',StartedAt=COALESCE(StartedAt,$now) WHERE Id=$id AND Status='Queued' AND NOT EXISTS(SELECT 1 FROM BatchJobs WHERE Status='Active');");
        Add(update, "$now", Time(now)); Add(update, "$id", idText);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1 ? Guid.Parse(idText) : null;
    }

    public async Task<bool> QueuePausedReadyAsync(Guid batchJobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null,
            "UPDATE BatchJobs SET Status='Queued' WHERE Id=$id AND Status='PausedReady';");
        Add(command, "$id", Id(batchJobId));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<PauseBatchDisposition> TryPauseBatchAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var update = Command(connection, transaction,
            "UPDATE BatchJobs SET Status='Paused' WHERE Id=$id AND Status IN ('Queued','Active');"))
        {
            Add(update, "$id", Id(batchJobId));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return PauseBatchDisposition.Paused;
            }
        }

        var status = await ScalarStringOrNullAsync(connection, transaction,
            "SELECT Status FROM BatchJobs WHERE Id=$id;", cancellationToken, ("$id", Id(batchJobId)))
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return status switch
        {
            null => PauseBatchDisposition.NotFound,
            "Paused" => PauseBatchDisposition.AlreadyPaused,
            _ => PauseBatchDisposition.NotPausable,
        };
    }

    public async Task<ResumeBatchDisposition> TryResumeBatchAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var update = Command(connection, transaction,
            "UPDATE BatchJobs SET Status='Queued' WHERE Id=$id AND Status='Paused';"))
        {
            Add(update, "$id", Id(batchJobId));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ResumeBatchDisposition.Queued;
            }
        }

        var status = await ScalarStringOrNullAsync(connection, transaction,
            "SELECT Status FROM BatchJobs WHERE Id=$id;", cancellationToken, ("$id", Id(batchJobId)))
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return status switch
        {
            null => ResumeBatchDisposition.NotFound,
            "Queued" or "Active" => ResumeBatchDisposition.AlreadyResumed,
            _ => ResumeBatchDisposition.NotResumable,
        };
    }

    public async Task<RetryFailedTransferDisposition> TryRetryFailedTransferAsync(
        Guid operationId,
        DateTimeOffset retriedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        RetryFailedTransferSnapshot? snapshot;
        await using (var read = Command(connection, transaction, """
            SELECT o.BatchJobId,o.AssignedAccountId,o.Status,o.OutcomeKind,o.AttemptCount,o.UpdatedAt,
              o.SubmitState,o.TargetSlotState,o.DailyCountedAt,o.ConfirmationDescriptorJson,o.SubmitStartedAt,
              o.AdapterRequestId,o.RemoteItemIdsJson,o.LeaseExpiresAt,b.Status,r.CandidateStatus,
              r.GlobalValidityStatus,r.ReservedAccountId,a.FailedItemCount,
              EXISTS(SELECT 1 FROM Accounts account WHERE account.Id=o.AssignedAccountId
                AND account.Status='Active' AND account.IsEnabled=1 AND account.IsDeleted=0),
              EXISTS(SELECT 1 FROM ShareOperations share WHERE share.TransferOperationId=o.Id
                AND share.Status<>'WaitingTransfer'),b.QuotaBasis,a.TargetSuccessCount,
              a.EffectiveTargetSuccessCount,a.FulfilledCount,a.ReservedSlotCount,
              (SELECT COUNT(*) FROM TransferOperations waiting
                JOIN ResourceItems waitingResource ON waitingResource.Id=waiting.ResourceItemId
                WHERE waiting.BatchJobId=o.BatchJobId AND waiting.AssignedAccountId=o.AssignedAccountId
                  AND waiting.Status='Waiting' AND waiting.TargetSlotState IN ('Unoccupied','Released')
                  AND waitingResource.CandidateStatus='Reserved'
                  AND waitingResource.ReservedAccountId=o.AssignedAccountId),o.ResourceItemId,
              EXISTS(SELECT 1 FROM ShareOperations share WHERE share.TransferOperationId=o.Id)
            FROM TransferOperations o
            JOIN BatchJobs b ON b.Id=o.BatchJobId
            JOIN ResourceItems r ON r.Id=o.ResourceItemId
            JOIN AccountAllocations a ON a.BatchJobId=o.BatchJobId AND a.AccountId=o.AssignedAccountId
            WHERE o.Id=$id;
            """))
        {
            Add(read, "$id", Id(operationId));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            snapshot = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new RetryFailedTransferSnapshot(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2),
                    ReadString(reader, 3), reader.GetInt32(4), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7), ReadString(reader, 8), ReadString(reader, 9), ReadString(reader, 10),
                    ReadString(reader, 11), ReadString(reader, 12), ReadString(reader, 13), reader.GetString(14),
                    reader.GetString(15), reader.GetString(16), ReadString(reader, 17), reader.GetInt32(18),
                    reader.GetInt64(19) != 0, reader.GetInt64(20) != 0, reader.GetString(21),
                    reader.GetInt32(22), reader.GetInt32(23), reader.GetInt32(24), reader.GetInt32(25),
                    reader.GetInt32(26), Guid.Parse(reader.GetString(27)), reader.GetInt64(28) != 0)
                : null;
        }

        if (snapshot is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RetryFailedTransferDisposition.NotFound;
        }
        if (snapshot.Status != "Failed")
        {
            var disposition = snapshot.Status is "Submitting" or "ResultPending" ||
                              snapshot.SubmitState is "Submitting" or "PendingConfirmation"
                ? RetryFailedTransferDisposition.NeedsReconciliation
                : RetryFailedTransferDisposition.NotRetryable;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return disposition;
        }

        var neverSubmitted = snapshot.SubmitState is "NotPrepared" or "PreparingToSubmit" &&
                             snapshot.DailyCountedAt is null && snapshot.ConfirmationDescriptorJson is null &&
                             snapshot.SubmitStartedAt is null && snapshot.AdapterRequestId is null &&
                             snapshot.RemoteItemIdsJson is null;
        if (!neverSubmitted)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RetryFailedTransferDisposition.NeedsReconciliation;
        }

        var batchMayRun = snapshot.BatchStatus is "Active" or "Queued" or "Paused" or "CompletedWithShortfall";
        var sharePlan = await ReadRetrySharePlanAsync(
            connection, transaction, snapshot.BatchJobId, snapshot.AccountId, cancellationToken).ConfigureAwait(false);
        var sharePlanIsConsistent = !snapshot.HasAnyShareOperation &&
                                    (sharePlan is null || !sharePlan.ShareEnabled ||
                                     (!string.IsNullOrWhiteSpace(sharePlan.SettingsFingerprint) &&
                                      sharePlan.PlannedShareCount > 0 &&
                                      sharePlan.CreatedShareOperationCount < sharePlan.PlannedShareCount &&
                                      sharePlan.BatchShareOperationCount == sharePlan.CurrentBatchShareOperationCount));
        var freeSuccessSlots = (long)snapshot.EffectiveTargetSuccessCount - snapshot.FulfilledCount -
                               snapshot.ReservedSlotCount - snapshot.WaitingSlotDemand;
        var restoreSuccessCapacity = snapshot.QuotaBasis == "SuccessTarget" && freeSuccessSlots <= 0;
        var successCapacityIsAvailable = snapshot.QuotaBasis != "SuccessTarget" ||
                                         freeSuccessSlots > 0 ||
                                         snapshot.EffectiveTargetSuccessCount < snapshot.TargetSuccessCount;
        var durableFailureIsConsistent = snapshot.OutcomeKind == "Failed" &&
                                         snapshot.TargetSlotState == "Released" && snapshot.LeaseExpiresAt is null &&
                                         snapshot.CandidateStatus == "Expanded" && snapshot.GlobalValidityStatus != "Invalid" &&
                                         snapshot.ReservedAccountId is null && snapshot.FailedItemCount > 0 &&
                                         snapshot.AccountIsEligible && !snapshot.HasAdvancedShareOperation &&
                                         snapshot.AttemptCount < int.MaxValue &&
                                         successCapacityIsAvailable && sharePlanIsConsistent;
        if (!batchMayRun || !durableFailureIsConsistent)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RetryFailedTransferDisposition.NotRetryable;
        }

        await using (var operation = Command(connection, transaction, """
            UPDATE TransferOperations SET Status='Waiting',OutcomeKind=NULL,SubmitState='PreparingToSubmit',
              AttemptCount=AttemptCount+1,TargetSlotState='Released',TargetSlotReservedAt=NULL,
              ConfirmationDescriptorJson=NULL,AdapterRequestId=NULL,RemoteItemIdsJson=NULL,SubmitStartedAt=NULL,
              LeaseExpiresAt=NULL,LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$now
            WHERE Id=$id AND Status='Failed' AND OutcomeKind='Failed' AND AttemptCount=$attempt
              AND AttemptCount<$max
              AND UpdatedAt=$updated AND SubmitState=$submit AND TargetSlotState='Released' AND LeaseExpiresAt IS NULL;
            """))
        {
            Add(operation, "$now", Time(retriedAt)); Add(operation, "$id", Id(operationId));
            Add(operation, "$attempt", snapshot.AttemptCount); Add(operation, "$updated", snapshot.UpdatedAt);
            Add(operation, "$submit", snapshot.SubmitState);
            Add(operation, "$max", int.MaxValue);
            if (await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return RetryFailedTransferDisposition.NotRetryable;
            }
        }
        await using (var allocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET FailedItemCount=FailedItemCount-1,EffectiveTargetSuccessCount=CASE WHEN $restore=1 THEN EffectiveTargetSuccessCount+1 ELSE EffectiveTargetSuccessCount END WHERE BatchJobId=$batch AND AccountId=$account AND FailedItemCount>0 AND ($restore=0 OR EffectiveTargetSuccessCount<TargetSuccessCount);"))
        {
            Add(allocation, "$batch", Id(snapshot.BatchJobId)); Add(allocation, "$account", Id(snapshot.AccountId));
            Add(allocation, "$restore", restoreSuccessCapacity ? 1 : 0);
            RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                "The failed-operation allocation changed during retry.");
        }
        await using (var resource = Command(connection, transaction, """
            UPDATE ResourceItems SET CandidateStatus='Reserved',ReservedAccountId=$account,
              ReservationExpiresAt=NULL,UpdatedAt=$now
            WHERE Id=(SELECT ResourceItemId FROM TransferOperations WHERE Id=$operation)
              AND CandidateStatus='Expanded' AND ReservedAccountId IS NULL AND GlobalValidityStatus<>'Invalid';
            """))
        {
            Add(resource, "$account", Id(snapshot.AccountId)); Add(resource, "$now", Time(retriedAt));
            Add(resource, "$operation", Id(operationId));
            RequireOne(await resource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                "The failed-operation resource changed during retry.");
        }
        if (sharePlan is { ShareEnabled: true })
        {
            await using (var share = Command(connection, transaction, """
                INSERT INTO ShareOperations(
                  Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,
                  ExactItemSetFingerprint,SettingsFingerprint,Generation,RequestDescriptorJson,AdapterRequestId,
                  SubmissionAcceptance,AttemptCount,LeaseGeneration,LeaseExpiresAt,SubmitStartedAt,NextReconcileAt,
                  LastErrorCode,LastErrorMessage,CreatedAt,UpdatedAt)
                VALUES($id,$transfer,$resource,$batch,$account,$ordinal,'WaitingTransfer',NULL,$settings,1,
                  NULL,NULL,'Unknown',0,0,NULL,NULL,NULL,NULL,NULL,$now,$now);
                """))
            {
                Add(share, "$id", Id(Guid.NewGuid())); Add(share, "$transfer", Id(operationId));
                Add(share, "$resource", Id(snapshot.ResourceItemId)); Add(share, "$batch", Id(snapshot.BatchJobId));
                Add(share, "$account", Id(snapshot.AccountId)); Add(share, "$ordinal", sharePlan.OutputOrdinal);
                Add(share, "$settings", sharePlan.SettingsFingerprint); Add(share, "$now", Time(retriedAt));
                RequireOne(await share.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                    "The planned share handoff could not be restored.");
            }
            await using var batchShareCount = Command(connection, transaction,
                "UPDATE BatchJobs SET ShareOperationCount=ShareOperationCount+1 WHERE Id=$batch AND ShareOperationCount=$expected;");
            Add(batchShareCount, "$batch", Id(snapshot.BatchJobId));
            Add(batchShareCount, "$expected", sharePlan.BatchShareOperationCount);
            RequireOne(await batchShareCount.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                "The batch share count could not be restored.");
        }
        if (snapshot.BatchStatus == "CompletedWithShortfall")
        {
            await using var batch = Command(connection, transaction,
                "UPDATE BatchJobs SET Status='Queued',CompletedAt=NULL WHERE Id=$batch AND Status='CompletedWithShortfall';");
            Add(batch, "$batch", Id(snapshot.BatchJobId));
            RequireOne(await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                "The completed batch changed during retry.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RetryFailedTransferDisposition.Requeued;
    }

    public async Task<ScheduledOperation?> TryReserveNextAsync(
        Guid batchId,
        DateTimeOffset reservedAt,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        if (leaseExpiresAt <= reservedAt) throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(*) FROM BatchJobs WHERE Id=$batch AND Status='Active';", cancellationToken,
                ("$batch", Id(batchId))).ConfigureAwait(false) != 1)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var activeSlots = await ScalarLongAsync(connection, transaction,
            "SELECT (SELECT COUNT(*) FROM TransferOperations WHERE Status IN ('Reserved','Preparing','Submitting') AND (LeaseExpiresAt IS NULL OR LeaseExpiresAt>$now))+(SELECT COUNT(*) FROM ShareOperations WHERE Status IN ('Reserved','Preparing','Submitting') AND (LeaseExpiresAt IS NULL OR LeaseExpiresAt>$now));",
            cancellationToken, ("$now", Time(reservedAt))).ConfigureAwait(false);
        if (activeSlots >= TransferCoordinator.MaximumGlobalConcurrency)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var dateKey = reservedAt.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var waiting = await ReadWaitingAsync(connection, transaction, batchId, dateKey, cancellationToken).ConfigureAwait(false);
        if (waiting is not null)
        {
            await using var reserve = Command(connection, transaction,
                "UPDATE TransferOperations SET Status='Reserved',TargetSlotState='Reserved',TargetSlotReservedAt=$now,LeaseExpiresAt=$lease,UpdatedAt=$now WHERE Id=$id AND Status='Waiting' AND TargetSlotState IN ('Unoccupied','Released');");
            Add(reserve, "$now", Time(reservedAt)); Add(reserve, "$lease", Time(leaseExpiresAt)); Add(reserve, "$id", Id(waiting.OperationId));
            if (await reserve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            await IncrementReservedAsync(connection, transaction, batchId, waiting.AccountId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return waiting!;
        }

        var quota = await ScalarStringOrNullAsync(connection, transaction,
            "SELECT QuotaBasis FROM BatchJobs WHERE Id=$batch;", cancellationToken, ("$batch", Id(batchId))).ConfigureAwait(false);
        if (!string.Equals(quota, "SuccessTarget", StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var account = await SelectSuccessAccountAsync(connection, transaction, batchId, dateKey, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var candidate = await SelectCandidateAsync(connection, transaction, batchId, account.Value.AccountId, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var ordinal = await ScalarLongAsync(connection, transaction,
            "SELECT NextAllocationOrdinal FROM BatchJobs WHERE Id=$batch;",
            cancellationToken, ("$batch", Id(batchId))).ConfigureAwait(false);
        var nextOrdinal = checked(ordinal + 1);
        var operationId = Guid.NewGuid();
        var target = candidate.Value.TargetOverride ?? account.Value.DefaultTarget;
        var normalizedTarget = candidate.Value.NormalizedTargetOverride ?? account.Value.NormalizedDefaultTarget;
        var folder = candidate.Value.Folder ?? string.Empty;
        var source = candidate.Value.ConstraintAccountId is null ? "Weighted" : "Exclusive";
        var fingerprint = Fingerprint(candidate.Value.NormalizedShareId, account.Value.AccountId, normalizedTarget, folder);
        var shareBinding = await ReadPendingShareBindingAsync(
            connection, transaction, batchId, account.Value.AccountId, cancellationToken).ConfigureAwait(false);

        await using (var reserveCandidate = Command(connection, transaction,
            "UPDATE ResourceItems SET CandidateStatus='Reserved',ReservedAccountId=$account,ReservationExpiresAt=$lease,UpdatedAt=$now WHERE Id=$id AND CandidateStatus IN ('Unallocated','Unused') AND (CandidateConstraintAccountId IS NULL OR CandidateConstraintAccountId=$account);"))
        {
            Add(reserveCandidate, "$account", Id(account.Value.AccountId)); Add(reserveCandidate, "$lease", Time(leaseExpiresAt));
            Add(reserveCandidate, "$now", Time(reservedAt)); Add(reserveCandidate, "$id", Id(candidate.Value.ResourceId));
            if (await reserveCandidate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }
        }
        await using (var insert = Command(connection, transaction,
            "INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,OperationFingerprint,SubmitState,TargetSlotState,TargetSlotReservedAt,LeaseExpiresAt,CreatedAt,UpdatedAt) VALUES($id,$resource,$batch,$sequence,$ordinal,$target,$normalized,$folder,$account,$source,'Reserved',0,$fingerprint,'NotPrepared','Reserved',$now,$lease,$now,$now);"))
        {
            Add(insert, "$id", Id(operationId)); Add(insert, "$resource", Id(candidate.Value.ResourceId)); Add(insert, "$batch", Id(batchId));
            Add(insert, "$sequence", candidate.Value.Sequence); Add(insert, "$ordinal", ordinal); Add(insert, "$target", target);
            Add(insert, "$normalized", normalizedTarget); Add(insert, "$folder", folder); Add(insert, "$account", Id(account.Value.AccountId));
            Add(insert, "$source", source); Add(insert, "$fingerprint", fingerprint); Add(insert, "$now", Time(reservedAt)); Add(insert, "$lease", Time(leaseExpiresAt));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (shareBinding is not null)
        {
            await using var insertShare = Command(connection, transaction, """
                INSERT INTO ShareOperations(
                  Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,
                  ExactItemSetFingerprint,SettingsFingerprint,Generation,RequestDescriptorJson,AdapterRequestId,
                  SubmissionAcceptance,AttemptCount,LeaseGeneration,LeaseExpiresAt,SubmitStartedAt,NextReconcileAt,
                  LastErrorCode,LastErrorMessage,CreatedAt,UpdatedAt)
                VALUES(
                  $id,$transfer,$resource,$batch,$account,$ordinal,'WaitingTransfer',NULL,$settings,1,NULL,NULL,
                  'Unknown',0,0,NULL,NULL,NULL,NULL,NULL,$created,$created);
                """);
            Add(insertShare, "$id", Id(Guid.NewGuid())); Add(insertShare, "$transfer", Id(operationId));
            Add(insertShare, "$resource", Id(candidate.Value.ResourceId)); Add(insertShare, "$batch", Id(batchId));
            Add(insertShare, "$account", Id(account.Value.AccountId)); Add(insertShare, "$ordinal", shareBinding.Value.OutputOrdinal);
            Add(insertShare, "$settings", shareBinding.Value.SettingsFingerprint); Add(insertShare, "$created", Time(reservedAt));
            RequireOne(await insertShare.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                "The confirmed share output could not accept the dynamic transfer.");
        }
        await using (var updateAllocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET AssignedItemCount=AssignedItemCount+1,ReservedSlotCount=ReservedSlotCount+1,UnusedCandidateCount=CASE WHEN $unusedExclusive=1 THEN (SELECT COUNT(*) FROM ResourceItems r WHERE r.BatchJobId=$batch AND r.CandidateStatus='Unused' AND r.CandidateConstraintAccountId=$account) ELSE UnusedCandidateCount END WHERE BatchJobId=$batch AND AccountId=$account AND EffectiveTargetSuccessCount-FulfilledCount-ReservedSlotCount>0;"))
        {
            Add(updateAllocation, "$batch", Id(batchId)); Add(updateAllocation, "$account", Id(account.Value.AccountId));
            Add(updateAllocation, "$unusedExclusive", candidate.Value.WasUnused && candidate.Value.ConstraintAccountId == account.Value.AccountId ? 1 : 0);
            if (await updateAllocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The allocation changed during candidate reservation.");
        }
        await using (var updateBatch = Command(connection, transaction,
            "UPDATE BatchJobs SET OperationCount=OperationCount+1,ShareOperationCount=ShareOperationCount+$shares,NextAllocationOrdinal=$next WHERE Id=$batch AND NextAllocationOrdinal=$current;"))
        {
            Add(updateBatch, "$batch", Id(batchId)); Add(updateBatch, "$next", nextOrdinal); Add(updateBatch, "$current", ordinal);
            Add(updateBatch, "$shares", shareBinding is null ? 0 : 1);
            RequireOne(await updateBatch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The batch allocation cursor changed during candidate reservation.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ScheduledOperation(operationId, batchId, account.Value.AccountId, ordinal, source);
    }

    public async Task<BatchExhaustionOutcome> TryFinalizeExhaustedBatchAsync(
        Guid batchId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        string quotaBasis;
        string shortagePolicy;
        await using (var batch = Command(connection, transaction,
            "SELECT QuotaBasis,ShortagePolicy FROM BatchJobs WHERE Id=$batch AND Status='Active';"))
        {
            Add(batch, "$batch", Id(batchId));
            await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return BatchExhaustionOutcome.NotExhausted;
            }
            quotaBasis = reader.GetString(0);
            shortagePolicy = reader.GetString(1);
        }
        if (!string.Equals(quotaBasis, "SuccessTarget", StringComparison.Ordinal))
        {
            var nonTerminal = await ScalarLongAsync(connection, transaction,
                "SELECT COUNT(*) FROM TransferOperations WHERE BatchJobId=$batch AND Status IN ('Waiting','Reserved','Preparing','Submitting','ResultPending','Paused');",
                cancellationToken, ("$batch", Id(batchId))).ConfigureAwait(false);
            if (nonTerminal > 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return BatchExhaustionOutcome.TemporarilyBlocked;
            }
            return await FinalizeBatchAsync(connection, transaction, batchId,
                hasShortfall: false, now, cancellationToken).ConfigureAwait(false);
        }

        var occupied = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM TransferOperations WHERE BatchJobId=$batch AND Status IN ('Waiting','Reserved','Preparing','Submitting','ResultPending','Paused');",
            cancellationToken, ("$batch", Id(batchId))).ConfigureAwait(false);
        var reservedCandidates = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM ResourceItems WHERE BatchJobId=$batch AND CandidateStatus='Reserved';",
            cancellationToken, ("$batch", Id(batchId))).ConfigureAwait(false);
        if (occupied > 0 || reservedCandidates > 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return BatchExhaustionOutcome.TemporarilyBlocked;
        }

        var unmet = await ScalarLongAsync(connection, transaction,
            "SELECT COALESCE(SUM(CASE WHEN EffectiveTargetSuccessCount>FulfilledCount THEN EffectiveTargetSuccessCount-FulfilledCount ELSE 0 END),0) FROM AccountAllocations WHERE BatchJobId=$batch;",
            cancellationToken, ("$batch", Id(batchId))).ConfigureAwait(false);
        if (unmet == 0)
        {
            var originalShortfall = await SuccessShortfallAsync(
                connection, transaction, batchId, cancellationToken).ConfigureAwait(false);
            return await FinalizeBatchAsync(connection, transaction, batchId,
                originalShortfall > 0, now, cancellationToken).ConfigureAwait(false);
        }

        const string noExistingKey =
            "NOT EXISTS(SELECT 1 FROM TransferOperations existing WHERE existing.ResourceItemId=r.Id AND existing.AssignedAccountId=al.AccountId AND existing.NormalizedTargetPath=COALESCE(r.NormalizedTargetPathOverride,al.NormalizedDefaultTargetPath) AND existing.CreateFolderName=COALESCE(r.CreateFolderName,'') AND existing.Status<>'Cancelled')";
        var potentialCandidatePredicate =
            $"r.BatchJobId=$batch AND r.CandidateStatus IN ('Unallocated','Unused') AND r.GlobalValidityStatus<>'Invalid' AND EXISTS(SELECT 1 FROM AccountAllocations al WHERE al.BatchJobId=r.BatchJobId AND al.EffectiveTargetSuccessCount-al.FulfilledCount-al.ReservedSlotCount>0 AND (r.CandidateConstraintAccountId IS NULL OR r.CandidateConstraintAccountId=al.AccountId) AND {noExistingKey})";
        var potentialCandidates = await ScalarLongAsync(connection, transaction,
            $"SELECT COUNT(*) FROM ResourceItems r WHERE {potentialCandidatePredicate};",
            cancellationToken, ("$batch", Id(batchId))).ConfigureAwait(false);
        if (potentialCandidates > 0)
        {
            var dateKey = now.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var claimableCandidates = await ScalarLongAsync(connection, transaction,
                $"SELECT COUNT(*) FROM ResourceItems r WHERE {potentialCandidatePredicate} AND EXISTS(SELECT 1 FROM AccountAllocations al JOIN Accounts a ON a.Id=al.AccountId WHERE al.BatchJobId=r.BatchJobId AND al.EffectiveTargetSuccessCount-al.FulfilledCount-al.ReservedSlotCount>0 AND (r.CandidateConstraintAccountId IS NULL OR r.CandidateConstraintAccountId=al.AccountId) AND {noExistingKey} AND a.Status='Active' AND a.IsEnabled=1 AND a.IsDeleted=0 AND (a.DailyLimit=0 OR a.DailyCountDateKey<>$date OR a.DailyExecutedCount<a.DailyLimit) AND NOT EXISTS(SELECT 1 FROM TransferOperations busy WHERE busy.AssignedAccountId=al.AccountId AND busy.Status IN ('Reserved','Preparing','Submitting','ResultPending','Paused')) AND NOT EXISTS(SELECT 1 FROM ShareOperations shareBusy WHERE shareBusy.AccountId=al.AccountId AND shareBusy.Status IN ('Reserved','Preparing','Submitting','ResultPending','Paused')));",
                cancellationToken, ("$batch", Id(batchId)), ("$date", dateKey)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return claimableCandidates > 0
                ? BatchExhaustionOutcome.NotExhausted
                : BatchExhaustionOutcome.TemporarilyBlocked;
        }

        if (string.Equals(shortagePolicy, "WaitForCandidates", StringComparison.Ordinal))
        {
            await using var awaiting = Command(connection, transaction,
                "UPDATE BatchJobs SET Status='AwaitingCandidates',CompletedAt=NULL WHERE Id=$batch AND Status='Active';");
            Add(awaiting, "$batch", Id(batchId));
            RequireOne(await awaiting.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The batch changed while it was awaiting candidates.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return BatchExhaustionOutcome.AwaitingCandidates;
        }

        await using (var settle = Command(connection, transaction,
            "UPDATE AccountAllocations SET EffectiveTargetSuccessCount=FulfilledCount WHERE BatchJobId=$batch;"))
        {
            Add(settle, "$batch", Id(batchId));
            await settle.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var shortfall = await SuccessShortfallAsync(
            connection, transaction, batchId, cancellationToken).ConfigureAwait(false);
        return await FinalizeBatchAsync(connection, transaction, batchId,
            shortfall > 0, now, cancellationToken).ConfigureAwait(false);
    }

    private static Task<long> SuccessShortfallAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        CancellationToken cancellationToken) =>
        ScalarLongAsync(connection, transaction,
            "SELECT COALESCE(SUM(CASE WHEN TargetSuccessCount>FulfilledCount THEN TargetSuccessCount-FulfilledCount ELSE 0 END),0) FROM AccountAllocations WHERE BatchJobId=$batch;",
            cancellationToken, ("$batch", Id(batchId)));

    private static async Task<BatchExhaustionOutcome> FinalizeBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        bool hasShortfall,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var unfinishedShares = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM ShareOperations WHERE BatchJobId=$batch AND Status IN ('WaitingTransfer','WaitingShare','Reserved','Preparing','Submitting','ResultPending','Paused');",
            cancellationToken, ("$batch", Id(batchId))).ConfigureAwait(false);
        if (unfinishedShares > 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return BatchExhaustionOutcome.TemporarilyBlocked;
        }
        var failedShares = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM ShareOperations WHERE BatchJobId=$batch AND Status IN ('Failed','Cancelled');",
            cancellationToken, ("$batch", Id(batchId))).ConfigureAwait(false);
        hasShortfall |= failedShares > 0;
        await using (var candidates = Command(connection, transaction,
            "UPDATE ResourceItems SET CandidateStatus='Unused',ReservedAccountId=NULL,ReservationExpiresAt=NULL,UpdatedAt=$now WHERE BatchJobId=$batch AND CandidateStatus='Unallocated' AND GlobalValidityStatus<>'Invalid';"))
        {
            Add(candidates, "$now", Time(now)); Add(candidates, "$batch", Id(batchId));
            await candidates.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var counts = Command(connection, transaction,
            "UPDATE AccountAllocations SET UnusedCandidateCount=(SELECT COUNT(*) FROM ResourceItems r WHERE r.BatchJobId=AccountAllocations.BatchJobId AND r.CandidateStatus='Unused' AND r.CandidateConstraintAccountId=AccountAllocations.AccountId) WHERE BatchJobId=$batch;"))
        {
            Add(counts, "$batch", Id(batchId));
            await counts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var finalStatus = hasShortfall ? "CompletedWithShortfall" : "Completed";
        await using (var finish = Command(connection, transaction,
            "UPDATE BatchJobs SET Status=$status,CompletedAt=$now WHERE Id=$batch AND Status='Active';"))
        {
            Add(finish, "$status", finalStatus); Add(finish, "$now", Time(now)); Add(finish, "$batch", Id(batchId));
            RequireOne(await finish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The batch changed while it was being finalized.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return hasShortfall ? BatchExhaustionOutcome.CompletedWithShortfall : BatchExhaustionOutcome.Completed;
    }

    public async Task<LeaseRecoveryResult> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var missingDescriptor = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM TransferOperations WHERE TargetSlotState='Reserved' AND ConfirmationDescriptorJson IS NULL AND (Status='ResultPending' OR (Status IN ('Reserved','Preparing','Submitting') AND LeaseExpiresAt IS NOT NULL AND LeaseExpiresAt<=$now AND (SubmitStartedAt IS NOT NULL OR AdapterRequestId IS NOT NULL OR RemoteItemIdsJson IS NOT NULL OR SubmitState='Submitting' OR Status='Submitting')));",
            cancellationToken, ("$now", Time(now))).ConfigureAwait(false);
        if (missingDescriptor != 0)
            throw new InvalidDataException("An expired uncertain submission cannot be reconciled because its confirmation descriptor is missing.");
        await FinalizeExhaustedPreSubmissionGenerationsAsync(
            connection, transaction, "LeaseExpiresAt IS NOT NULL AND LeaseExpiresAt<=$now", now, cancellationToken)
            .ConfigureAwait(false);
        var retryableReservations = new List<(Guid BatchId, Guid AccountId, int Count)>();
        await using (var read = Command(connection, transaction,
            "SELECT BatchJobId,AssignedAccountId,COUNT(*) FROM TransferOperations WHERE Status IN ('Reserved','Preparing') AND SubmitState IN ('NotPrepared','PreparingToSubmit') AND ConfirmationDescriptorJson IS NULL AND SubmitStartedAt IS NULL AND AdapterRequestId IS NULL AND RemoteItemIdsJson IS NULL AND TargetSlotState='Reserved' AND LeaseExpiresAt IS NOT NULL AND LeaseExpiresAt<=$now GROUP BY BatchJobId,AssignedAccountId;"))
        {
            Add(read, "$now", Time(now));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                retryableReservations.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt32(2)));
        }
        var retryableCount = retryableReservations.Sum(item => item.Count);
        var pendingCount = checked((int)await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM TransferOperations WHERE Status IN ('Reserved','Preparing','Submitting') AND TargetSlotState='Reserved' AND LeaseExpiresAt IS NOT NULL AND LeaseExpiresAt<=$now AND (ConfirmationDescriptorJson IS NOT NULL OR SubmitStartedAt IS NOT NULL OR AdapterRequestId IS NOT NULL OR RemoteItemIdsJson IS NOT NULL OR SubmitState='Submitting' OR Status='Submitting');",
            cancellationToken, ("$now", Time(now))).ConfigureAwait(false));

        await using (var clearCandidateLeases = Command(connection, transaction,
            "UPDATE ResourceItems SET ReservationExpiresAt=NULL,UpdatedAt=$now WHERE Id IN (SELECT ResourceItemId FROM TransferOperations WHERE Status IN ('Reserved','Preparing') AND SubmitState IN ('NotPrepared','PreparingToSubmit') AND ConfirmationDescriptorJson IS NULL AND SubmitStartedAt IS NULL AND AdapterRequestId IS NULL AND RemoteItemIdsJson IS NULL AND TargetSlotState='Reserved' AND LeaseExpiresAt IS NOT NULL AND LeaseExpiresAt<=$now);"))
        {
            Add(clearCandidateLeases, "$now", Time(now));
            await clearCandidateLeases.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var retryable = Command(connection, transaction,
            "UPDATE TransferOperations SET Status='Waiting',SubmitState=CASE WHEN Status='Reserved' AND SubmitState='PreparingToSubmit' THEN 'PreparingToSubmit' ELSE 'NotPrepared' END,TargetSlotState='Unoccupied',TargetSlotReservedAt=NULL,LeaseExpiresAt=NULL,UpdatedAt=$now WHERE Status IN ('Reserved','Preparing') AND SubmitState IN ('NotPrepared','PreparingToSubmit') AND ConfirmationDescriptorJson IS NULL AND SubmitStartedAt IS NULL AND AdapterRequestId IS NULL AND RemoteItemIdsJson IS NULL AND TargetSlotState='Reserved' AND LeaseExpiresAt IS NOT NULL AND LeaseExpiresAt<=$now;"))
        {
            Add(retryable, "$now", Time(now));
            if (await retryable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != retryableCount)
                throw new InvalidOperationException("Expired retryable leases changed during recovery.");
        }
        foreach (var reservation in retryableReservations)
        {
            await using var release = Command(connection, transaction,
                "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-$count WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>=$count;");
            Add(release, "$count", reservation.Count); Add(release, "$batch", Id(reservation.BatchId)); Add(release, "$account", Id(reservation.AccountId));
            RequireOne(await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "An expired allocation reservation is inconsistent.");
        }
        await using (var pending = Command(connection, transaction,
            "UPDATE TransferOperations SET Status='ResultPending',SubmitState='PendingConfirmation',LeaseExpiresAt=NULL,LastErrorCode='UnknownResponse',LastErrorMessage='The service returned an unrecognized response.',UpdatedAt=$now WHERE Status IN ('Reserved','Preparing','Submitting') AND TargetSlotState='Reserved' AND LeaseExpiresAt IS NOT NULL AND LeaseExpiresAt<=$now AND (ConfirmationDescriptorJson IS NOT NULL OR SubmitStartedAt IS NOT NULL OR AdapterRequestId IS NOT NULL OR RemoteItemIdsJson IS NOT NULL OR SubmitState='Submitting' OR Status='Submitting');"))
        {
            Add(pending, "$now", Time(now));
            if (await pending.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != pendingCount)
                throw new InvalidOperationException("Expired uncertain leases changed during recovery.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LeaseRecoveryResult(retryableCount, pendingCount);
    }

    public async Task<LeaseRecoveryResult> RecoverInterruptedOperationsAsync(
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        const string active = "Status IN ('Reserved','Preparing','Submitting','ResultPending') AND TargetSlotState='Reserved'";
        const string safe = "ConfirmationDescriptorJson IS NULL AND SubmitStartedAt IS NULL AND AdapterRequestId IS NULL AND RemoteItemIdsJson IS NULL AND SubmitState IN ('NotPrepared','PreparingToSubmit') AND Status IN ('Reserved','Preparing')";
        const string unsafeBoundary = "(ConfirmationDescriptorJson IS NOT NULL OR SubmitStartedAt IS NOT NULL OR AdapterRequestId IS NOT NULL OR RemoteItemIdsJson IS NOT NULL OR SubmitState IN ('Submitting','PendingConfirmation') OR Status IN ('Submitting','ResultPending'))";

        var missingDescriptor = await ScalarLongAsync(connection, transaction,
            $"SELECT COUNT(*) FROM TransferOperations WHERE {active} AND {unsafeBoundary} AND ConfirmationDescriptorJson IS NULL;",
            cancellationToken).ConfigureAwait(false);
        if (missingDescriptor != 0)
            throw new InvalidDataException("An interrupted submission cannot be reconciled because its confirmation descriptor is missing.");
        await FinalizeExhaustedPreSubmissionGenerationsAsync(
            connection, transaction, "1=1", recoveredAt, cancellationToken).ConfigureAwait(false);

        var safeCount = checked((int)await ScalarLongAsync(connection, transaction,
            $"SELECT COUNT(*) FROM TransferOperations WHERE {active} AND {safe};",
            cancellationToken).ConfigureAwait(false));
        var pendingCount = checked((int)await ScalarLongAsync(connection, transaction,
            $"SELECT COUNT(*) FROM TransferOperations WHERE {active} AND {unsafeBoundary};",
            cancellationToken).ConfigureAwait(false));

        await using (var clearCandidateLeases = Command(connection, transaction,
            $"UPDATE ResourceItems SET ReservationExpiresAt=NULL,UpdatedAt=$now WHERE Id IN (SELECT ResourceItemId FROM TransferOperations WHERE {active} AND {safe});"))
        {
            Add(clearCandidateLeases, "$now", Time(recoveredAt));
            await clearCandidateLeases.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var retryable = Command(connection, transaction,
            $"UPDATE TransferOperations SET Status='Waiting',SubmitState=CASE WHEN Status='Reserved' AND SubmitState='PreparingToSubmit' THEN 'PreparingToSubmit' ELSE 'NotPrepared' END,TargetSlotState='Unoccupied',TargetSlotReservedAt=NULL,LeaseExpiresAt=NULL,LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$now WHERE {active} AND {safe};"))
        {
            Add(retryable, "$now", Time(recoveredAt));
            if (await retryable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != safeCount)
                throw new InvalidOperationException("Interrupted pre-submission operations changed during recovery.");
        }

        await using (var pending = Command(connection, transaction,
            $"UPDATE TransferOperations SET Status='ResultPending',SubmitState='PendingConfirmation',LeaseExpiresAt=NULL,LastErrorCode='UnknownResponse',LastErrorMessage='The service returned an unrecognized response.',UpdatedAt=$now WHERE {active} AND {unsafeBoundary};"))
        {
            Add(pending, "$now", Time(recoveredAt));
            if (await pending.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != pendingCount)
                throw new InvalidOperationException("Interrupted uncertain operations changed during recovery.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LeaseRecoveryResult(safeCount, pendingCount);
    }

    public async Task<AllocationCounterRebuildResult> RebuildAllocationCountersAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var orphanedSlots = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM TransferOperations o LEFT JOIN AccountAllocations a ON a.BatchJobId=o.BatchJobId AND a.AccountId=o.AssignedAccountId WHERE o.TargetSlotState IN ('Reserved','Fulfilled') AND a.Id IS NULL;",
            cancellationToken).ConfigureAwait(false);
        if (orphanedSlots != 0)
            throw new InvalidDataException("Transfer-operation slot state has no matching account allocation.");

        await using (var rebuild = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=(SELECT COUNT(*) FROM TransferOperations o WHERE o.BatchJobId=AccountAllocations.BatchJobId AND o.AssignedAccountId=AccountAllocations.AccountId AND o.TargetSlotState='Reserved'),FulfilledCount=(SELECT COUNT(*) FROM TransferOperations o WHERE o.BatchJobId=AccountAllocations.BatchJobId AND o.AssignedAccountId=AccountAllocations.AccountId AND o.TargetSlotState='Fulfilled');"))
        {
            await rebuild.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var invalidSuccessTarget = await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM AccountAllocations a JOIN BatchJobs b ON b.Id=a.BatchJobId WHERE b.QuotaBasis='SuccessTarget' AND (a.FulfilledCount>a.EffectiveTargetSuccessCount OR a.ReservedSlotCount>a.EffectiveTargetSuccessCount-a.FulfilledCount);",
            cancellationToken).ConfigureAwait(false);
        if (invalidSuccessTarget != 0)
            throw new InvalidDataException("Rebuilt allocation slot counts exceed an effective success target.");

        var allocationCount = checked((int)await ScalarLongAsync(connection, transaction,
            "SELECT COUNT(*) FROM AccountAllocations;", cancellationToken).ConfigureAwait(false));
        var reserved = checked((int)await ScalarLongAsync(connection, transaction,
            "SELECT COALESCE(SUM(ReservedSlotCount),0) FROM AccountAllocations;", cancellationToken).ConfigureAwait(false));
        var fulfilled = checked((int)await ScalarLongAsync(connection, transaction,
            "SELECT COALESCE(SUM(FulfilledCount),0) FROM AccountAllocations;", cancellationToken).ConfigureAwait(false));

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AllocationCounterRebuildResult(allocationCount, reserved, fulfilled);
    }

    public async Task<PendingReconciliationOperation?> LoadPendingAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null,
            "SELECT o.Id,o.AssignedAccountId,o.AttemptCount,o.ConfirmationDescriptorJson,o.AdapterRequestId,o.RemoteItemIdsJson,EXISTS(SELECT 1 FROM ShareOperations share WHERE share.TransferOperationId=o.Id AND share.Status='WaitingTransfer') FROM TransferOperations o WHERE o.Id=$id AND o.Status='ResultPending' AND o.SubmitState='PendingConfirmation' AND o.TargetSlotState='Reserved' AND o.ConfirmationDescriptorJson IS NOT NULL;");
        Add(command, "$id", Id(operationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PendingReconciliationOperation(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3),
                ReadString(reader, 4),
                ReadString(reader, 5),
                reader.GetInt64(6) != 0)
            : null;
    }

    public async Task MarkConfirmedPresentAsync(
        PendingReconciliationOperation expected,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = await ReadOperationIdsAsync(connection, transaction, expected.OperationId, cancellationToken).ConfigureAwait(false);
        await EnsureLegacySuccessHasNoPendingShareAsync(
            connection,
            transaction,
            expected.OperationId,
            cancellationToken).ConfigureAwait(false);
        await using (var operation = Command(connection, transaction,
            "UPDATE TransferOperations SET Status='Succeeded',OutcomeKind='NewTransfer',SubmitState='ResultReceived',TargetSlotState='Fulfilled',LeaseExpiresAt=NULL,LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$now WHERE Id=$id AND Status='ResultPending' AND SubmitState='PendingConfirmation' AND TargetSlotState='Reserved' AND AttemptCount=$attempt AND ConfirmationDescriptorJson=$descriptor AND AdapterRequestId IS $request AND RemoteItemIdsJson IS $remote;"))
        {
            Add(operation, "$now", Time(confirmedAt)); Add(operation, "$id", Id(expected.OperationId));
            Add(operation, "$attempt", expected.AttemptCount);
            Add(operation, "$descriptor", expected.ConfirmationDescriptorJson); Add(operation, "$request", expected.AdapterRequestId); Add(operation, "$remote", expected.RemoteItemIdsJson);
            RequireOne(await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The pending confirmation changed before it could be finalized.");
        }
        await using (var allocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-1,FulfilledCount=FulfilledCount+1,SuccessItemCount=SuccessItemCount+1,NewSuccessItemCount=NewSuccessItemCount+1 WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>0;"))
        {
            Add(allocation, "$batch", Id(ids.BatchId)); Add(allocation, "$account", Id(ids.AccountId));
            RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Allocation reservation is missing.");
        }
        await using (var resource = Command(connection, transaction,
            "UPDATE ResourceItems SET CandidateStatus='Expanded',ReservedAccountId=NULL,ReservationExpiresAt=NULL,UpdatedAt=$now WHERE Id=$resource AND CandidateStatus='Reserved';"))
        {
            Add(resource, "$now", Time(confirmedAt)); Add(resource, "$resource", Id(ids.ResourceId));
            await resource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkConfirmedAbsentAsync(
        PendingReconciliationOperation expected,
        SubmissionAcceptance submissionAcceptance,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (submissionAcceptance != SubmissionAcceptance.DefinitelyNotAccepted)
            throw new ArgumentException(
                "Only explicit definitely-not-accepted evidence can reset a submitted transfer.",
                nameof(submissionAcceptance));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = await ReadOperationIdsAsync(connection, transaction, expected.OperationId, cancellationToken).ConfigureAwait(false);
        await using (var operation = Command(connection, transaction,
            "UPDATE TransferOperations SET Status='Waiting',OutcomeKind=NULL,SubmitState='NotPrepared',TargetSlotState='Released',TargetSlotReservedAt=NULL,ConfirmationDescriptorJson=NULL,AdapterRequestId=NULL,RemoteItemIdsJson=NULL,SubmitStartedAt=NULL,LeaseExpiresAt=NULL,LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$now WHERE Id=$id AND Status='ResultPending' AND SubmitState='PendingConfirmation' AND TargetSlotState='Reserved' AND AttemptCount=$attempt AND ConfirmationDescriptorJson=$descriptor AND AdapterRequestId IS $request AND RemoteItemIdsJson IS $remote;"))
        {
            Add(operation, "$now", Time(confirmedAt)); Add(operation, "$id", Id(expected.OperationId));
            Add(operation, "$attempt", expected.AttemptCount);
            Add(operation, "$descriptor", expected.ConfirmationDescriptorJson); Add(operation, "$request", expected.AdapterRequestId); Add(operation, "$remote", expected.RemoteItemIdsJson);
            RequireOne(await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The pending confirmation changed before it could be finalized.");
        }
        await using (var allocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-1 WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>0;"))
        {
            Add(allocation, "$batch", Id(ids.BatchId)); Add(allocation, "$account", Id(ids.AccountId));
            RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Allocation reservation is missing.");
        }
        await using (var resource = Command(connection, transaction,
            "UPDATE ResourceItems SET ReservationExpiresAt=NULL,UpdatedAt=$now WHERE Id=$resource AND CandidateStatus='Reserved' AND ReservedAccountId=$account;"))
        {
            Add(resource, "$now", Time(confirmedAt)); Add(resource, "$resource", Id(ids.ResourceId)); Add(resource, "$account", Id(ids.AccountId));
            await resource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetPendingForUserRetryAsync(
        Guid operationId,
        DateTimeOffset resetAt,
        CancellationToken cancellationToken = default)
    {
        _ = operationId;
        _ = resetAt;
        _ = cancellationToken;
        await Task.Yield();
        throw new InvalidOperationException(
            "An unknown transfer result cannot be reset for blind retry; reconcile definite absence first.");
    }

    public async Task CancelBeforeSubmissionAsync(
        Guid operationId,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        Guid batchId; Guid accountId; Guid resourceId; string status; string submitState; string slotState;
        bool hasBoundaryEvidence;
        await using (var read = Command(connection, transaction,
            "SELECT BatchJobId,AssignedAccountId,ResourceItemId,Status,SubmitState,TargetSlotState,CASE WHEN DailyCountedAt IS NOT NULL OR ConfirmationDescriptorJson IS NOT NULL OR SubmitStartedAt IS NOT NULL OR AdapterRequestId IS NOT NULL OR RemoteItemIdsJson IS NOT NULL OR SubmitState IN ('Submitting','PendingConfirmation') OR Status IN ('Submitting','ResultPending','Succeeded','Failed') THEN 1 ELSE 0 END FROM TransferOperations WHERE Id=$id;"))
        {
            Add(read, "$id", Id(operationId));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The operation no longer exists.");
            batchId = Guid.Parse(reader.GetString(0)); accountId = Guid.Parse(reader.GetString(1)); resourceId = Guid.Parse(reader.GetString(2));
            status = reader.GetString(3); submitState = reader.GetString(4); slotState = reader.GetString(5); hasBoundaryEvidence = reader.GetInt64(6) != 0;
        }
        if (hasBoundaryEvidence || status is not ("Waiting" or "Reserved" or "Preparing") || submitState is not ("NotPrepared" or "PreparingToSubmit"))
            throw new InvalidOperationException("Cannot cancel an operation after submission may have occurred.");
        var hadReservation = string.Equals(slotState, "Reserved", StringComparison.Ordinal);
        if (!hadReservation && slotState is not ("Unoccupied" or "Released"))
            throw new InvalidOperationException("The operation slot cannot be cancelled.");

        await using (var operation = Command(connection, transaction,
            "UPDATE TransferOperations SET Status='Cancelled',OutcomeKind='Cancelled',TargetSlotState=CASE WHEN TargetSlotState='Reserved' THEN 'Released' ELSE TargetSlotState END,TargetSlotReservedAt=NULL,LeaseExpiresAt=NULL,UpdatedAt=$now WHERE Id=$id AND Status=$status AND SubmitState=$submit AND TargetSlotState=$slot AND DailyCountedAt IS NULL AND ConfirmationDescriptorJson IS NULL AND SubmitStartedAt IS NULL AND AdapterRequestId IS NULL AND RemoteItemIdsJson IS NULL;"))
        {
            Add(operation, "$now", Time(cancelledAt)); Add(operation, "$id", Id(operationId)); Add(operation, "$status", status); Add(operation, "$submit", submitState); Add(operation, "$slot", slotState);
            RequireOne(await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The operation changed while cancellation was being applied.");
        }
        if (hadReservation)
        {
            await using var allocation = Command(connection, transaction,
                "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-1 WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>0;");
            Add(allocation, "$batch", Id(batchId)); Add(allocation, "$account", Id(accountId));
            RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Allocation reservation is missing.");
        }
        await using (var resource = Command(connection, transaction,
            "UPDATE ResourceItems SET CandidateStatus=CASE WHEN CandidateStatus='Reserved' THEN 'Expanded' ELSE CandidateStatus END,ReservedAccountId=NULL,ReservationExpiresAt=NULL,UpdatedAt=$now WHERE Id=$resource AND (ReservedAccountId=$account OR ReservedAccountId IS NULL);"))
        {
            Add(resource, "$now", Time(cancelledAt)); Add(resource, "$resource", Id(resourceId)); Add(resource, "$account", Id(accountId));
            RequireOne(await resource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The operation resource reservation is inconsistent.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<OperationExecutionSnapshot?> LoadOperationAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        LoadOperationCoreAsync(operationId, cancellationToken);

    Task<OperationExecutionSnapshot?> IOperationExecutionStore.LoadAsync(Guid operationId, CancellationToken cancellationToken) =>
        LoadOperationCoreAsync(operationId, cancellationToken);

    private async Task<OperationExecutionSnapshot?> LoadOperationCoreAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null,
            "SELECT o.Id,o.AssignedAccountId,r.NormalizedShareId,r.ShareUrl,r.ExtractionCode,o.TargetPath,o.CreateFolderName,b.DuplicatePolicy,o.OperationFingerprint,EXISTS(SELECT 1 FROM ShareOperations share WHERE share.TransferOperationId=o.Id AND share.Status='WaitingTransfer'),o.AttemptCount,o.SubmitState FROM TransferOperations o JOIN ResourceItems r ON r.Id=o.ResourceItemId JOIN BatchJobs b ON b.Id=o.BatchJobId WHERE o.Id=$id;");
        Add(command, "$id", Id(operationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var attemptCount = reader.GetInt32(10);
        try
        {
            return new OperationExecutionSnapshot(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                new ShareReference(reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)),
                new RemotePath(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6),
                Enum.Parse<DuplicatePolicy>(reader.GetString(7)), reader.GetString(8), reader.GetInt64(9) != 0,
                attemptCount, string.Equals(reader.GetString(11), "PreparingToSubmit", StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new OperationSnapshotValidationException(attemptCount, exception);
        }
    }

    public Task MarkPreparingAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null) =>
        ExecuteRequiredAsync(
            "UPDATE TransferOperations SET Status='Preparing',SubmitState='PreparingToSubmit',AttemptCount=CASE WHEN SubmitState='NotPrepared' THEN AttemptCount+1 ELSE AttemptCount END,LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$now WHERE Id=$id AND Status='Reserved' AND TargetSlotState='Reserved' AND SubmitState IN ('NotPrepared','PreparingToSubmit') AND (SubmitState='PreparingToSubmit' OR AttemptCount<$max) AND ($attempt IS NULL OR AttemptCount=$attempt);",
            "Operation is not reserved for preparation.", cancellationToken, ("$now", Time(now)),
            ("$id", Id(operationId)), ("$attempt", expectedAttemptCount), ("$max", int.MaxValue));

    public async Task<SubmissionBoundaryResult> PersistAsync(
        SubmissionBoundaryPersistence persistence,
        CancellationToken cancellationToken = default)
    {
        var commandValue = persistence.Command;
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var read = Command(connection, transaction,
            "SELECT o.AssignedAccountId,o.DailyCountedAt,o.SubmitState,a.DailyLimit,a.DailyExecutedCount,a.DailyCountDateKey,o.BatchJobId,o.Status,o.AttemptCount FROM TransferOperations o JOIN Accounts a ON a.Id=o.AssignedAccountId WHERE o.Id=$id;");
        Add(read, "$id", Id(commandValue.OperationId));
        Guid accountId; Guid batchId; bool alreadyCounted; string submitState; string status; int dailyLimit; int count; string dateKey; int attemptCount;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("The operation no longer exists.");
            accountId = Guid.Parse(reader.GetString(0)); alreadyCounted = !reader.IsDBNull(1); submitState = reader.GetString(2);
            dailyLimit = reader.GetInt32(3); count = reader.GetInt32(4); dateKey = reader.GetString(5);
            batchId = Guid.Parse(reader.GetString(6)); status = reader.GetString(7); attemptCount = reader.GetInt32(8);
        }
        if (accountId != commandValue.AccountId) throw new InvalidOperationException("The operation account binding changed.");
        if (commandValue.ExpectedAttemptCount is not null &&
            commandValue.ExpectedAttemptCount.Value != attemptCount)
            throw new InvalidOperationException("The operation attempt changed before the submit boundary.");
        if (alreadyCounted)
        {
            if (status == "Preparing" && submitState == "PreparingToSubmit")
            {
                await using var retryBoundary = Command(connection, transaction,
                    "UPDATE TransferOperations SET ConfirmationDescriptorJson=$descriptor,SubmitState='Submitting',Status='Submitting',SubmitStartedAt=$now,LeaseExpiresAt=$lease,UpdatedAt=$now WHERE Id=$id AND DailyCountedAt IS NOT NULL AND Status='Preparing' AND SubmitState='PreparingToSubmit' AND ConfirmationDescriptorJson IS NULL AND ($attempt IS NULL OR AttemptCount=$attempt);");
                Add(retryBoundary, "$descriptor", commandValue.ConfirmationDescriptorJson); Add(retryBoundary, "$now", Time(persistence.PersistedAt));
                Add(retryBoundary, "$lease", Time(commandValue.LeaseExpiresAt)); Add(retryBoundary, "$id", Id(commandValue.OperationId));
                Add(retryBoundary, "$attempt", commandValue.ExpectedAttemptCount);
                RequireOne(await retryBoundary.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The retry operation is not eligible to cross the submit boundary.");
            }
            else if (submitState is not ("Submitting" or "ResultReceived" or "PendingConfirmation"))
            {
                throw new InvalidOperationException("The persisted daily count has an invalid submit state.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SubmissionBoundaryResult(true, false, count);
        }
        if (!string.Equals(dateKey, persistence.LocalDateKey, StringComparison.Ordinal)) count = 0;
        if (dailyLimit > 0 && count >= dailyLimit)
        {
            await using var reset = Command(connection, transaction,
                "UPDATE Accounts SET DailyExecutedCount=$count,DailyCountDateKey=$date WHERE Id=$account;");
            Add(reset, "$count", count); Add(reset, "$date", persistence.LocalDateKey); Add(reset, "$account", Id(accountId));
            await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SubmissionBoundaryResult(false, false, count);
        }
        var newCount = checked(count + 1);
        await using (var updateOperation = Command(connection, transaction,
            "UPDATE TransferOperations SET ConfirmationDescriptorJson=$descriptor,SubmitState='Submitting',Status='Submitting',DailyCountedAt=$now,SubmitStartedAt=$now,LeaseExpiresAt=$lease,UpdatedAt=$now WHERE Id=$id AND DailyCountedAt IS NULL AND Status='Preparing' AND SubmitState='PreparingToSubmit' AND ($attempt IS NULL OR AttemptCount=$attempt);"))
        {
            Add(updateOperation, "$descriptor", commandValue.ConfirmationDescriptorJson); Add(updateOperation, "$now", Time(persistence.PersistedAt));
            Add(updateOperation, "$lease", Time(commandValue.LeaseExpiresAt)); Add(updateOperation, "$id", Id(commandValue.OperationId));
            Add(updateOperation, "$attempt", commandValue.ExpectedAttemptCount);
            if (await updateOperation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Operation is not eligible to cross the submit boundary.");
        }
        await using (var updateAccount = Command(connection, transaction,
            "UPDATE Accounts SET DailyExecutedCount=$count,DailyCountDateKey=$date WHERE Id=$account;"))
        {
            Add(updateAccount, "$count", newCount); Add(updateAccount, "$date", persistence.LocalDateKey); Add(updateAccount, "$account", Id(accountId));
            if (await updateAccount.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The submission account no longer exists.");
        }
        await using (var updateAllocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET AttemptedItemCount=AttemptedItemCount+1 WHERE BatchJobId=$batch AND AccountId=$account;"))
        {
            Add(updateAllocation, "$batch", Id(batchId)); Add(updateAllocation, "$account", Id(accountId));
            RequireOne(await updateAllocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The submission allocation no longer exists.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SubmissionBoundaryResult(true, true, newCount);
    }

    public Task RecordSubmissionIdentifiersAsync(Guid operationId, string? requestId, IReadOnlyList<string> remoteItemIds,
        DateTimeOffset now, CancellationToken cancellationToken = default, int? expectedAttemptCount = null)
    {
        var remoteJson = System.Text.Json.JsonSerializer.Serialize(remoteItemIds ?? []);
        return ExecuteRequiredAsync(
            "UPDATE TransferOperations SET AdapterRequestId=COALESCE($request,AdapterRequestId),RemoteItemIdsJson=$remote,UpdatedAt=$now WHERE Id=$id AND Status='Submitting' AND SubmitState='Submitting' AND ConfirmationDescriptorJson IS NOT NULL AND ($attempt IS NULL OR AttemptCount=$attempt);",
            "Operation has not crossed the submit boundary.", cancellationToken,
            ("$request", requestId), ("$remote", remoteJson), ("$now", Time(now)), ("$id", Id(operationId)),
            ("$attempt", expectedAttemptCount));
    }

    public async Task CompleteSucceededWithEvidenceAsync(
        Guid operationId,
        string? requestId,
        RemoteItemIdentitySet exactRemoteItems,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null)
        => await CompleteSucceededWithEvidenceCoreAsync(
            operationId,
            requestId,
            exactRemoteItems,
            now,
            expected: null,
            expectedAttemptCount,
            cancellationToken).ConfigureAwait(false);

    public Task MarkConfirmedPresentWithEvidenceAsync(
        PendingReconciliationOperation expected,
        RemoteItemIdentitySet exactRemoteItems,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return CompleteSucceededWithEvidenceCoreAsync(
            expected.OperationId,
            expected.AdapterRequestId,
            exactRemoteItems,
            confirmedAt,
            expected,
            expectedAttemptCount: null,
            cancellationToken);
    }

    private async Task CompleteSucceededWithEvidenceCoreAsync(
        Guid operationId,
        string? requestId,
        RemoteItemIdentitySet exactRemoteItems,
        DateTimeOffset now,
        PendingReconciliationOperation? expected,
        int? expectedAttemptCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactRemoteItems);
        var remoteJson = System.Text.Json.JsonSerializer.Serialize(
            exactRemoteItems.Items.Select(item => item.ExactId));

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = await ReadOperationIdsAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);

        Guid? shareOperationId = null;
        await using (var readShare = Command(connection, transaction,
            "SELECT Id,Status FROM ShareOperations WHERE TransferOperationId=$transfer;"))
        {
            Add(readShare, "$transfer", Id(operationId));
            await using var reader = await readShare.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                shareOperationId = Guid.Parse(reader.GetString(0));
                if (!string.Equals(reader.GetString(1), "WaitingTransfer", StringComparison.Ordinal))
                    throw new InvalidOperationException("The share handoff is no longer waiting for transfer evidence.");
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("A transfer operation has multiple share handoffs.");
            }
        }

        string? resolvedFallback = null;
        if (shareOperationId is not null)
        {
            var persistedTitle = await ScalarStringOrNullAsync(
                connection,
                transaction,
                "SELECT ResolvedOutputTitle FROM ResourceItems WHERE Id=$resource;",
                cancellationToken,
                ("$resource", Id(ids.ResourceId))).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(persistedTitle))
            {
                throw new InvalidOperationException(
                    "Share-enabled transfer work requires a persisted resolved output title before completion.");
            }
        }
        else
        {
            resolvedFallback = ResolvedOutputTitleBuilder.Resolve(
                null,
                exactRemoteItems.Items.Select(item => (string?)item.DisplayedTopLevelName).ToArray());
        }

        var expectedPredicate = expected is null
            ? string.Empty
            : " AND AttemptCount=$attempt AND ConfirmationDescriptorJson=$descriptor AND AdapterRequestId IS $expectedRequest AND RemoteItemIdsJson IS $expectedRemote";
        var executionAttemptPredicate = expectedAttemptCount is null
            ? string.Empty
            : " AND AttemptCount=$executionAttempt";
        await using (var operation = Command(connection, transaction, $"""
            UPDATE TransferOperations
            SET Status='Succeeded',OutcomeKind='NewTransfer',SubmitState='ResultReceived',TargetSlotState='Fulfilled',
                AdapterRequestId=COALESCE($request,AdapterRequestId),RemoteItemIdsJson=$remote,LeaseExpiresAt=NULL,
                LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$now
            WHERE Id=$id AND Status IN ('Submitting','ResultPending') AND TargetSlotState='Reserved'
              AND ConfirmationDescriptorJson IS NOT NULL{expectedPredicate}{executionAttemptPredicate};
            """))
        {
            Add(operation, "$request", requestId); Add(operation, "$remote", remoteJson);
            Add(operation, "$now", Time(now)); Add(operation, "$id", Id(operationId));
            if (expectedAttemptCount is not null)
                Add(operation, "$executionAttempt", expectedAttemptCount.Value);
            if (expected is not null)
            {
                Add(operation, "$attempt", expected.AttemptCount);
                Add(operation, "$descriptor", expected.ConfirmationDescriptorJson);
                Add(operation, "$expectedRequest", expected.AdapterRequestId);
                Add(operation, "$expectedRemote", expected.RemoteItemIdsJson);
            }
            RequireOne(await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                "Operation cannot be completed without an exact submitted transfer boundary.");
        }

        await using (var allocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-1,FulfilledCount=FulfilledCount+1,SuccessItemCount=SuccessItemCount+1,NewSuccessItemCount=NewSuccessItemCount+1 WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>0;"))
        {
            Add(allocation, "$batch", Id(ids.BatchId)); Add(allocation, "$account", Id(ids.AccountId));
            RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                "Allocation reservation is missing.");
        }

        await using (var resource = Command(connection, transaction, """
            UPDATE ResourceItems
            SET CandidateStatus='Expanded',ReservedAccountId=NULL,ReservationExpiresAt=NULL,
                ResolvedOutputTitle=CASE WHEN ResolvedOutputTitle IS NULL OR length(trim(ResolvedOutputTitle))=0
                    THEN $title ELSE ResolvedOutputTitle END,
                UpdatedAt=$now
            WHERE Id=$resource AND CandidateStatus='Reserved';
            """))
        {
            Add(resource, "$title", resolvedFallback); Add(resource, "$now", Time(now));
            Add(resource, "$resource", Id(ids.ResourceId));
            RequireOne(await resource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                "The transfer resource reservation is inconsistent.");
        }

        if (shareOperationId is not null)
        {
            await using (var share = Command(connection, transaction, """
                UPDATE ShareOperations
                SET Status='WaitingShare',ExactItemSetFingerprint=$fingerprint,UpdatedAt=$now
                WHERE Id=$id AND TransferOperationId=$transfer AND Status='WaitingTransfer'
                  AND ExactItemSetFingerprint IS NULL AND Generation=1;
                """))
            {
                Add(share, "$fingerprint", exactRemoteItems.Fingerprint); Add(share, "$now", Time(now));
                Add(share, "$id", Id(shareOperationId.Value)); Add(share, "$transfer", Id(operationId));
                RequireOne(await share.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                    "The pending share handoff changed before exact transfer evidence was persisted.");
            }

            await using (var generation = Command(connection, transaction, """
                INSERT INTO ShareOperationGenerations(
                  ShareOperationId,Generation,BatchJobId,ResourceItemId,AccountId,
                  ExactItemSetFingerprint,SettingsFingerprint,ExactItemCount)
                SELECT Id,Generation,BatchJobId,ResourceItemId,AccountId,
                  ExactItemSetFingerprint,SettingsFingerprint,NULL
                FROM ShareOperations
                WHERE Id=$operation AND Status='WaitingShare' AND Generation=1
                  AND ExactItemSetFingerprint=$fingerprint;
                """))
            {
                Add(generation, "$operation", Id(shareOperationId.Value));
                Add(generation, "$fingerprint", exactRemoteItems.Fingerprint);
                RequireOne(await generation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                    "The exact share generation lineage could not be created.");
            }

            await using (var item = Command(connection, transaction,
                "INSERT INTO ShareOperationRemoteItems(ShareOperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName) VALUES($operation,$ordinal,$remote,$sortKey,$name);"))
            {
                item.Parameters.Add("$operation", Microsoft.Data.Sqlite.SqliteType.Text);
                item.Parameters.Add("$ordinal", Microsoft.Data.Sqlite.SqliteType.Integer);
                item.Parameters.Add("$remote", Microsoft.Data.Sqlite.SqliteType.Text);
                item.Parameters.Add("$sortKey", Microsoft.Data.Sqlite.SqliteType.Blob);
                item.Parameters.Add("$name", Microsoft.Data.Sqlite.SqliteType.Text);
                item.Prepare();
                for (var ordinal = 0; ordinal < exactRemoteItems.Items.Length; ordinal++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remoteItem = exactRemoteItems.Items[ordinal];
                    item.Parameters["$operation"].Value = Id(shareOperationId.Value);
                    item.Parameters["$ordinal"].Value = ordinal;
                    item.Parameters["$remote"].Value = remoteItem.ExactId;
                    item.Parameters["$sortKey"].Value = RemoteItemIdentitySet.GetCanonicalSortKey(remoteItem.ExactId);
                    item.Parameters["$name"].Value = remoteItem.DisplayedTopLevelName;
                    RequireOne(await item.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                        "An exact remote item could not be persisted.");
                }
            }

            await using (var seal = Command(connection, transaction, """
                UPDATE ShareOperationGenerations
                SET ExactItemCount=$count
                WHERE ShareOperationId=$operation AND Generation=1 AND ExactItemCount IS NULL
                  AND ExactItemSetFingerprint=$fingerprint
                  AND $count=(SELECT COUNT(*) FROM ShareOperationRemoteItems item
                    WHERE item.ShareOperationId=$operation);
                """))
            {
                Add(seal, "$count", exactRemoteItems.Count); Add(seal, "$operation", Id(shareOperationId.Value));
                Add(seal, "$fingerprint", exactRemoteItems.Fingerprint);
                RequireOne(await seal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                    "The complete exact-item evidence could not be sealed.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkSucceededAsync(Guid operationId, DateTimeOffset now,
        CancellationToken cancellationToken = default, int? expectedAttemptCount = null)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = await ReadOperationIdsAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        await EnsureLegacySuccessHasNoPendingShareAsync(
            connection,
            transaction,
            operationId,
            cancellationToken).ConfigureAwait(false);
        await using (var operation = Command(connection, transaction,
            "UPDATE TransferOperations SET Status='Succeeded',OutcomeKind='NewTransfer',SubmitState='ResultReceived',TargetSlotState='Fulfilled',LeaseExpiresAt=NULL,LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$now WHERE Id=$id AND Status IN ('Submitting','ResultPending') AND TargetSlotState='Reserved' AND ($attempt IS NULL OR AttemptCount=$attempt);"))
        { Add(operation, "$now", Time(now)); Add(operation, "$id", Id(operationId)); Add(operation, "$attempt", expectedAttemptCount); RequireOne(await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Operation cannot be marked successful."); }
        await using (var allocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-1,FulfilledCount=FulfilledCount+1,SuccessItemCount=SuccessItemCount+1,NewSuccessItemCount=NewSuccessItemCount+1 WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>0;"))
        { Add(allocation, "$batch", Id(ids.BatchId)); Add(allocation, "$account", Id(ids.AccountId)); RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Allocation reservation is missing."); }
        await using (var resource = Command(connection, transaction,
            "UPDATE ResourceItems SET CandidateStatus='Expanded',ReservedAccountId=NULL,ReservationExpiresAt=NULL,UpdatedAt=$now WHERE Id=$resource AND CandidateStatus='Reserved';"))
        { Add(resource, "$now", Time(now)); Add(resource, "$resource", Id(ids.ResourceId)); await resource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureLegacySuccessHasNoPendingShareAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (await ScalarLongAsync(
                connection,
                transaction,
                "SELECT COUNT(*) FROM ShareOperations WHERE TransferOperationId=$transfer AND Status='WaitingTransfer';",
                cancellationToken,
                ("$transfer", Id(operationId))).ConfigureAwait(false) != 0)
        {
            throw new InvalidOperationException(
                "Share-enabled transfer work requires exact remote-item evidence before success.");
        }
    }

    public Task MarkFailedAsync(Guid operationId, string code, string safeMessage, bool globallyInvalid,
        DateTimeOffset now, CancellationToken cancellationToken = default, int? expectedAttemptCount = null) =>
        CompleteFailureAsync(operationId, code, safeMessage, globallyInvalid, resultPending: false, now,
            cancellationToken, expectedAttemptCount);

    public Task MarkResultPendingAsync(Guid operationId, string code, string safeMessage,
        SubmissionAcceptance submissionAcceptance, DateTimeOffset now,
        CancellationToken cancellationToken = default, int? expectedAttemptCount = null)
    {
        ValidateSafeError(code, safeMessage);
        if (submissionAcceptance != SubmissionAcceptance.Unknown)
            throw new ArgumentException("Only an uncertain remote submission acceptance can remain pending.", nameof(submissionAcceptance));
        return CompleteFailureAsync(operationId, code, safeMessage, globallyInvalid: false, resultPending: true, now,
            cancellationToken, expectedAttemptCount);
    }

    public Task PauseAccountBeforeSubmissionAsync(
        Guid operationId,
        string code,
        string safeMessage,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null)
    {
        ValidateSafeError(code, safeMessage);
        var reason = code switch
        {
            "ExpiredLogin" => TransferPauseReason.AccountSessionUnavailable,
            "VerificationRequired" => TransferPauseReason.AccountVerificationRequired,
            "UserInterventionRequired" => TransferPauseReason.UserInterventionRequired,
            _ => throw new ArgumentException("Only an account intervention can pause retryable work.", nameof(code)),
        };
        return PauseForAccountInterventionAsync(
            operationId,
            reason,
            SubmissionAcceptance.DefinitelyNotAccepted,
            observedSessionCapturedAtUtc: null,
            now,
            cancellationToken,
            expectedAttemptCount);
    }

    public async Task PauseForAccountInterventionAsync(
        Guid operationId,
        TransferPauseReason reason,
        SubmissionAcceptance submissionAcceptance,
        DateTimeOffset? observedSessionCapturedAtUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null)
    {
        var (code, safeMessage) = reason switch
        {
            TransferPauseReason.AccountSessionUnavailable => ("ExpiredLogin", "The account session has expired."),
            TransferPauseReason.AccountVerificationRequired => ("VerificationRequired", "Account verification is required."),
            TransferPauseReason.UserInterventionRequired => ("UserInterventionRequired", "User intervention is required."),
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
        ValidateSafeError(code, safeMessage);
        if (submissionAcceptance is not (SubmissionAcceptance.Unknown or SubmissionAcceptance.DefinitelyNotAccepted))
            throw new ArgumentOutOfRangeException(nameof(submissionAcceptance));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = await ReadOperationIdsAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        var submitted = "(ConfirmationDescriptorJson IS NOT NULL OR SubmitStartedAt IS NOT NULL OR AdapterRequestId IS NOT NULL OR RemoteItemIdsJson IS NOT NULL OR SubmitState IN ('Submitting','PendingConfirmation') OR Status IN ('Submitting','ResultPending'))";
        var untouched = "(ConfirmationDescriptorJson IS NULL AND SubmitStartedAt IS NULL AND AdapterRequestId IS NULL AND RemoteItemIdsJson IS NULL AND SubmitState IN ('NotPrepared','PreparingToSubmit') AND DailyCountedAt IS NULL)";
        var acceptanceGuard = submissionAcceptance == SubmissionAcceptance.Unknown
            ? $"Status IN ('Submitting','ResultPending') AND {submitted}"
            : $"((Status IN ('Reserved','Preparing') AND {untouched}) OR (Status='Submitting' AND SubmitState='Submitting'))";
        var nextSubmitState = submissionAcceptance == SubmissionAcceptance.Unknown
            ? "'PendingConfirmation'"
            : "CASE WHEN Status='Submitting' THEN 'ResultReceived' WHEN Status='Reserved' AND SubmitState='PreparingToSubmit' THEN 'PreparingToSubmit' ELSE 'NotPrepared' END";
        await using (var operation = Command(connection, transaction,
            $"UPDATE TransferOperations SET Status='Paused',SubmitState={nextSubmitState},LeaseExpiresAt=NULL,LastErrorCode=$code,LastErrorMessage=$message,UpdatedAt=$now WHERE Id=$id AND TargetSlotState='Reserved' AND {acceptanceGuard} AND ($attempt IS NULL OR AttemptCount=$attempt);"))
        {
            Add(operation, "$code", code); Add(operation, "$message", safeMessage); Add(operation, "$now", Time(now)); Add(operation, "$id", Id(operationId));
            Add(operation, "$attempt", expectedAttemptCount);
            RequireOne(await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The account intervention pause is no longer current.");
        }
        if (observedSessionCapturedAtUtc is not null)
        {
            await using var account = Command(connection, transaction, """
                UPDATE Accounts SET Status='Invalid'
                WHERE Id=$account AND Status='Active' AND IsDeleted=0
                  AND (LastValidatedAt IS NULL OR julianday(LastValidatedAt)<=julianday($observedSession));
                """);
            Add(account, "$observedSession", Time(observedSessionCapturedAtUtc.Value));
            Add(account, "$account", Id(ids.AccountId));
            await account.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ResumePausedBeforeSubmissionAsync(
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = await ReadOperationIdsAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        await using (var operation = Command(connection, transaction, """
            UPDATE TransferOperations SET Status='Waiting',TargetSlotState='Released',TargetSlotReservedAt=NULL,
              LeaseExpiresAt=NULL,LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$now
            WHERE Id=$id AND Status='Paused' AND SubmitState IN ('NotPrepared','PreparingToSubmit') AND DailyCountedAt IS NULL
              AND SubmitStartedAt IS NULL AND ConfirmationDescriptorJson IS NULL
              AND AdapterRequestId IS NULL AND RemoteItemIdsJson IS NULL AND TargetSlotState='Reserved'
              AND EXISTS(SELECT 1 FROM Accounts account WHERE account.Id=AssignedAccountId
                AND account.Status='Active' AND account.IsEnabled=1 AND account.IsDeleted=0);
            """))
        {
            Add(operation, "$now", Time(now)); Add(operation, "$id", Id(operationId));
            if (await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
        }
        await using (var allocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-1 WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>0;"))
        {
            Add(allocation, "$batch", Id(ids.BatchId)); Add(allocation, "$account", Id(ids.AccountId));
            RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Allocation reservation is missing.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ResumePausedAsync(
        Guid operationId,
        int expectedAttemptCount,
        DateTimeOffset expectedUpdatedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = await ReadOperationIdsAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        await using (var pending = Command(connection, transaction, """
            UPDATE TransferOperations SET Status='ResultPending',LeaseExpiresAt=NULL,UpdatedAt=$now
            WHERE Id=$id AND Status='Paused' AND AttemptCount=$expectedAttempt AND UpdatedAt=$expectedUpdated AND SubmitState='PendingConfirmation'
              AND TargetSlotState='Reserved' AND ConfirmationDescriptorJson IS NOT NULL
              AND EXISTS(SELECT 1 FROM Accounts account WHERE account.Id=AssignedAccountId
                AND account.Status='Active' AND account.IsEnabled=1 AND account.IsDeleted=0);
            """))
        {
            Add(pending, "$now", Time(now)); Add(pending, "$id", Id(operationId));
            Add(pending, "$expectedAttempt", expectedAttemptCount);
            Add(pending, "$expectedUpdated", Time(expectedUpdatedAt));
            if (await pending.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
        await using (var retryable = Command(connection, transaction, """
            UPDATE TransferOperations SET Status='Waiting',OutcomeKind=NULL,
              SubmitState=CASE WHEN SubmitState='PreparingToSubmit' THEN 'PreparingToSubmit' ELSE 'NotPrepared' END,
              TargetSlotState='Released',TargetSlotReservedAt=NULL,ConfirmationDescriptorJson=NULL,
              AdapterRequestId=NULL,RemoteItemIdsJson=NULL,SubmitStartedAt=NULL,LeaseExpiresAt=NULL,
              LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$now
            WHERE Id=$id AND Status='Paused' AND AttemptCount=$expectedAttempt
              AND (SubmitState='PreparingToSubmit' OR AttemptCount<$max)
              AND UpdatedAt=$expectedUpdated AND SubmitState IN ('NotPrepared','PreparingToSubmit','ResultReceived')
              AND TargetSlotState='Reserved'
              AND EXISTS(SELECT 1 FROM Accounts account WHERE account.Id=AssignedAccountId
                AND account.Status='Active' AND account.IsEnabled=1 AND account.IsDeleted=0);
            """))
        {
            Add(retryable, "$now", Time(now)); Add(retryable, "$id", Id(operationId));
            Add(retryable, "$expectedAttempt", expectedAttemptCount);
            Add(retryable, "$max", int.MaxValue);
            Add(retryable, "$expectedUpdated", Time(expectedUpdatedAt));
            if (await retryable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
        }
        await using (var allocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-1 WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>0;"))
        {
            Add(allocation, "$batch", Id(ids.BatchId)); Add(allocation, "$account", Id(ids.AccountId));
            RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Allocation reservation is missing.");
        }
        await using (var resource = Command(connection, transaction,
            "UPDATE ResourceItems SET ReservationExpiresAt=NULL,UpdatedAt=$now WHERE Id=$resource AND CandidateStatus='Reserved' AND ReservedAccountId=$account;"))
        {
            Add(resource, "$now", Time(now)); Add(resource, "$resource", Id(ids.ResourceId)); Add(resource, "$account", Id(ids.AccountId));
            await resource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task MarkDefiniteAccountFailureAsync(Guid operationId, string code, string safeMessage,
        DateTimeOffset now, CancellationToken cancellationToken = default, int? expectedAttemptCount = null)
    {
        ValidateSafeError(code, safeMessage);
        if (code is not ("InsufficientCapacity" or "InvalidTargetPath"))
            throw new ArgumentException("Only definitive capacity or target-path failures can release an account operation.", nameof(code));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = await ReadOperationIdsAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        await using (var account = Command(connection, transaction,
            "UPDATE Accounts SET Status='Invalid',LastValidatedAt=$now WHERE Id=$account AND IsDeleted=0;"))
        {
            Add(account, "$now", Time(now)); Add(account, "$account", Id(ids.AccountId));
            RequireOne(await account.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The unavailable account no longer exists.");
        }
        await using (var failed = Command(connection, transaction,
            "UPDATE TransferOperations SET Status='Failed',OutcomeKind='Failed',SubmitState=CASE WHEN SubmitState IN ('Submitting','PendingConfirmation') OR DailyCountedAt IS NOT NULL THEN 'ResultReceived' ELSE SubmitState END,TargetSlotState='Released',TargetSlotReservedAt=NULL,LeaseExpiresAt=NULL,LastErrorCode=$code,LastErrorMessage=$message,UpdatedAt=$now WHERE Id=$id AND Status IN ('Reserved','Preparing','Submitting','ResultPending') AND TargetSlotState='Reserved' AND ($attempt IS NULL OR AttemptCount=$attempt);"))
        {
            Add(failed, "$code", code); Add(failed, "$message", safeMessage); Add(failed, "$now", Time(now)); Add(failed, "$id", Id(operationId));
            Add(failed, "$attempt", expectedAttemptCount);
            RequireOne(await failed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The account failure operation cannot be finalized.");
        }
        await using (var allocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-1,FailedItemCount=FailedItemCount+1,UnusedCandidateCount=CASE WHEN EXISTS(SELECT 1 FROM ResourceItems r WHERE r.Id=$resource AND r.CandidateStatus='Reserved' AND r.ReservedAccountId=$account AND r.CandidateConstraintAccountId=$account) THEN (SELECT COUNT(*)+1 FROM ResourceItems unused WHERE unused.BatchJobId=$batch AND unused.CandidateStatus='Unused' AND unused.CandidateConstraintAccountId=$account) ELSE UnusedCandidateCount END WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>0;"))
        {
            Add(allocation, "$batch", Id(ids.BatchId)); Add(allocation, "$account", Id(ids.AccountId));
            Add(allocation, "$resource", Id(ids.ResourceId));
            RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Allocation reservation is missing.");
        }
        await using (var resource = Command(connection, transaction,
            "UPDATE ResourceItems SET CandidateStatus='Unused',ReservedAccountId=NULL,ReservationExpiresAt=NULL,UpdatedAt=$now WHERE Id=$resource AND CandidateStatus='Reserved';"))
        { Add(resource, "$now", Time(now)); Add(resource, "$resource", Id(ids.ResourceId)); await resource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReturnToWaitingForDailyLimitAsync(Guid operationId, DateTimeOffset now,
        CancellationToken cancellationToken = default, int? expectedAttemptCount = null)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = await ReadOperationIdsAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        await using var operation = Command(connection, transaction,
            "UPDATE TransferOperations SET Status='Waiting',SubmitState='NotPrepared',TargetSlotState='Unoccupied',TargetSlotReservedAt=NULL,LeaseExpiresAt=NULL,UpdatedAt=$now WHERE Id=$id AND Status='Preparing' AND DailyCountedAt IS NULL AND ($attempt IS NULL OR AttemptCount=$attempt);");
        Add(operation, "$now", Time(now)); Add(operation, "$id", Id(operationId));
        Add(operation, "$attempt", expectedAttemptCount);
        RequireOne(await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Operation cannot be released for the daily limit.");
        await using var allocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-1 WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>0;");
        Add(allocation, "$batch", Id(ids.BatchId)); Add(allocation, "$account", Id(ids.AccountId));
        RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Allocation reservation is missing.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReassignmentState?> LoadAsync(Guid batchJobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadReassignmentStateAsync(connection, null, batchJobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ApplyAsync(ReassignmentMutation mutation, string expectedBatchVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadReassignmentStateAsync(connection, transaction, mutation.BatchJobId, cancellationToken).ConfigureAwait(false);
        if (current is null || !string.Equals(current.BatchVersion, expectedBatchVersion, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        if (current.QuotaBasis == QuotaBasis.SuccessTarget &&
            (mutation.OperationMoves.Count != 0 || mutation.CandidateMoves.Count != 0))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        var currentAllocations = current.Allocations.ToDictionary(item => item.AccountId);
        if (mutation.AllocationDeltas.Select(item => item.AccountId).Distinct().Count() != mutation.AllocationDeltas.Count ||
            mutation.AllocationDeltas.Any(delta =>
                !currentAllocations.TryGetValue(delta.AccountId, out var allocation) ||
                delta.OldTarget != allocation.TargetCount ||
                delta.NewTarget < allocation.MinimumRetainedTargetCount ||
                delta.Delta != delta.NewTarget - delta.OldTarget) ||
            mutation.AllocationDeltas.Sum(delta => (long)delta.Delta) != 0)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        var mutationNow = timeProvider.GetUtcNow();
        foreach (var move in mutation.OperationMoves)
        {
            string shareId;
            string targetPath;
            string normalizedPath;
            string folder;
            await using (var fingerprintSource = Command(connection, transaction,
                "SELECT r.NormalizedShareId,COALESCE(r.TargetPathOverride,al.DefaultTargetPath),COALESCE(r.NormalizedTargetPathOverride,al.NormalizedDefaultTargetPath),COALESCE(r.CreateFolderName,o.CreateFolderName) FROM TransferOperations o JOIN ResourceItems r ON r.Id=o.ResourceItemId JOIN AccountAllocations al ON al.BatchJobId=o.BatchJobId AND al.AccountId=$new WHERE o.Id=$id;"))
            {
                Add(fingerprintSource, "$new", Id(move.NewAccountId)); Add(fingerprintSource, "$id", Id(move.OperationId));
                await using var reader = await fingerprintSource.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("A reassigned operation no longer exists.");
                shareId = reader.GetString(0); targetPath = reader.GetString(1); normalizedPath = reader.GetString(2); folder = reader.GetString(3);
            }
            var fingerprint = Fingerprint(shareId, move.NewAccountId, normalizedPath, folder);
            await using var command = Command(connection, transaction,
                "UPDATE TransferOperations SET AssignedAccountId=$new,TargetPath=$target,NormalizedTargetPath=$normalized,CreateFolderName=$folder,OperationFingerprint=$fingerprint,UpdatedAt=$now WHERE Id=$id AND BatchJobId=$batch AND AssignedAccountId=$old AND Status='Waiting' AND SubmitState='NotPrepared' AND DailyCountedAt IS NULL AND AllocationSource NOT IN ('Locked','Exclusive') AND NOT EXISTS(SELECT 1 FROM ResourceItems r WHERE r.Id=TransferOperations.ResourceItemId AND r.CandidateConstraintAccountId IS NOT NULL) AND NOT EXISTS(SELECT 1 FROM TransferOperations existing WHERE existing.Id<>TransferOperations.Id AND existing.ResourceItemId=TransferOperations.ResourceItemId AND existing.AssignedAccountId=$new AND existing.NormalizedTargetPath=$normalized AND existing.CreateFolderName=$folder AND existing.Status<>'Cancelled');" );
            Add(command, "$new", Id(move.NewAccountId)); Add(command, "$old", Id(move.OldAccountId)); Add(command, "$id", Id(move.OperationId));
            Add(command, "$target", targetPath); Add(command, "$normalized", normalizedPath); Add(command, "$folder", folder);
            Add(command, "$fingerprint", fingerprint); Add(command, "$batch", Id(mutation.BatchJobId)); Add(command, "$now", Time(mutationNow));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
            if (current.QuotaBasis == QuotaBasis.SuccessTarget)
            {
                await using var counts = Command(connection, transaction,
                    "UPDATE AccountAllocations SET AssignedItemCount=AssignedItemCount-1 WHERE BatchJobId=$batch AND AccountId=$old AND AssignedItemCount>0; UPDATE AccountAllocations SET AssignedItemCount=AssignedItemCount+1 WHERE BatchJobId=$batch AND AccountId=$new;");
                Add(counts, "$batch", Id(mutation.BatchJobId)); Add(counts, "$old", Id(move.OldAccountId)); Add(counts, "$new", Id(move.NewAccountId));
                if (await counts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 2)
                    throw new InvalidOperationException("A reassigned success-target allocation count is inconsistent.");
            }
        }
        foreach (var move in mutation.CandidateMoves)
        {
            await using var candidate = Command(connection, transaction,
                "UPDATE ResourceItems SET ReservedAccountId=$new,UpdatedAt=$now WHERE Id=$id AND BatchJobId=$batch AND CandidateStatus='Reserved' AND ReservedAccountId=$old AND CandidateConstraintAccountId IS NULL AND EXISTS(SELECT 1 FROM TransferOperations o WHERE o.ResourceItemId=ResourceItems.Id AND o.AssignedAccountId=$new AND o.Status='Waiting' AND o.SubmitState='NotPrepared');");
            Add(candidate, "$new", move.NewAccountId is null ? null : Id(move.NewAccountId.Value));
            Add(candidate, "$old", move.OldAccountId is null ? null : Id(move.OldAccountId.Value));
            Add(candidate, "$id", Id(move.ResourceItemId)); Add(candidate, "$batch", Id(mutation.BatchJobId)); Add(candidate, "$now", Time(mutationNow));
            RequireOne(await candidate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "A reassigned candidate reservation is no longer movable.");
        }
        foreach (var delta in mutation.AllocationDeltas)
        {
            var column = current.QuotaBasis == QuotaBasis.TaskCount ? "TargetItemCount" : "TargetSuccessCount";
            var effective = current.QuotaBasis == QuotaBasis.SuccessTarget ? ",EffectiveTargetSuccessCount=$new" : string.Empty;
            var assigned = current.QuotaBasis == QuotaBasis.TaskCount ? ",AssignedItemCount=$new" : string.Empty;
            await using var command = Command(connection, transaction,
                $"UPDATE AccountAllocations SET {column}=$new{effective}{assigned} WHERE BatchJobId=$batch AND AccountId=$account AND {column}=$old;");
            Add(command, "$new", delta.NewTarget); Add(command, "$old", delta.OldTarget); Add(command, "$batch", Id(mutation.BatchJobId)); Add(command, "$account", Id(delta.AccountId));
            RequireOne(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "An allocation target changed after preview.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task CompleteFailureAsync(Guid operationId, string code, string safeMessage, bool globallyInvalid,
        bool resultPending, DateTimeOffset now, CancellationToken cancellationToken, int? expectedAttemptCount)
    {
        ValidateSafeError(code, safeMessage);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = await ReadOperationIdsAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        if (resultPending)
        {
            await using var pending = Command(connection, transaction,
                "UPDATE TransferOperations SET Status='ResultPending',SubmitState='PendingConfirmation',LastErrorCode=$code,LastErrorMessage=$message,UpdatedAt=$now WHERE Id=$id AND Status IN ('Preparing','Submitting') AND TargetSlotState='Reserved' AND ($attempt IS NULL OR AttemptCount=$attempt);");
            Add(pending, "$code", code); Add(pending, "$message", safeMessage); Add(pending, "$now", Time(now)); Add(pending, "$id", Id(operationId));
            Add(pending, "$attempt", expectedAttemptCount);
            RequireOne(await pending.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Operation cannot be marked result pending.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await using (var failed = Command(connection, transaction,
            "UPDATE TransferOperations SET Status='Failed',OutcomeKind=$outcome,SubmitState=CASE WHEN SubmitState='Submitting' THEN 'ResultReceived' ELSE SubmitState END,TargetSlotState='Released',LeaseExpiresAt=NULL,LastErrorCode=$code,LastErrorMessage=$message,UpdatedAt=$now WHERE Id=$id AND Status IN ('Reserved','Preparing','Submitting','ResultPending') AND TargetSlotState='Reserved' AND ($attempt IS NULL OR AttemptCount=$attempt);"))
        {
            Add(failed, "$outcome", globallyInvalid ? "GloballyInvalid" : "Failed"); Add(failed, "$code", code); Add(failed, "$message", safeMessage);
            Add(failed, "$now", Time(now)); Add(failed, "$id", Id(operationId)); Add(failed, "$attempt", expectedAttemptCount);
            RequireOne(await failed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Operation cannot be failed.");
        }
        await using (var allocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-1,FailedItemCount=FailedItemCount+1 WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>0;"))
        { Add(allocation, "$batch", Id(ids.BatchId)); Add(allocation, "$account", Id(ids.AccountId)); RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Allocation reservation is missing."); }
        if (globallyInvalid)
        {
            var siblingReservations = new List<(Guid AccountId, int Count)>();
            await using (var readSiblings = Command(connection, transaction,
                "SELECT AssignedAccountId,COUNT(*) FROM TransferOperations WHERE ResourceItemId=$resource AND Id<>$id AND Status IN ('Reserved','Preparing') AND SubmitState IN ('NotPrepared','PreparingToSubmit') AND DailyCountedAt IS NULL AND TargetSlotState='Reserved' GROUP BY AssignedAccountId;"))
            {
                Add(readSiblings, "$resource", Id(ids.ResourceId)); Add(readSiblings, "$id", Id(operationId));
                await using var reader = await readSiblings.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    siblingReservations.Add((Guid.Parse(reader.GetString(0)), reader.GetInt32(1)));
            }
            await using (var cancelSiblings = Command(connection, transaction,
                "UPDATE TransferOperations SET Status='Cancelled',OutcomeKind='GloballyInvalid',TargetSlotState=CASE WHEN TargetSlotState='Reserved' THEN 'Released' ELSE TargetSlotState END,LeaseExpiresAt=NULL,LastErrorCode=$code,LastErrorMessage=$message,UpdatedAt=$now WHERE ResourceItemId=$resource AND Id<>$id AND Status IN ('Waiting','Reserved','Preparing') AND SubmitState IN ('NotPrepared','PreparingToSubmit') AND DailyCountedAt IS NULL;"))
            {
                Add(cancelSiblings, "$code", code); Add(cancelSiblings, "$message", safeMessage); Add(cancelSiblings, "$now", Time(now));
                Add(cancelSiblings, "$resource", Id(ids.ResourceId)); Add(cancelSiblings, "$id", Id(operationId));
                await cancelSiblings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var sibling in siblingReservations)
            {
                await using var release = Command(connection, transaction,
                    "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-$count WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>=$count;");
                Add(release, "$count", sibling.Count); Add(release, "$batch", Id(ids.BatchId)); Add(release, "$account", Id(sibling.AccountId));
                RequireOne(await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "A sibling allocation reservation is inconsistent.");
            }
            await using var resource = Command(connection, transaction,
                "UPDATE ResourceItems SET CandidateStatus='GloballyInvalid',GlobalValidityStatus='Invalid',ReservedAccountId=NULL,ReservationExpiresAt=NULL,UpdatedAt=$now WHERE Id=$resource;");
            Add(resource, "$now", Time(now)); Add(resource, "$resource", Id(ids.ResourceId)); await resource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var resource = Command(connection, transaction,
                "UPDATE ResourceItems SET CandidateStatus='Expanded',ReservedAccountId=NULL,ReservationExpiresAt=NULL,UpdatedAt=$now WHERE Id=$resource AND CandidateStatus='Reserved' AND ReservedAccountId=$account;");
            Add(resource, "$now", Time(now)); Add(resource, "$resource", Id(ids.ResourceId)); Add(resource, "$account", Id(ids.AccountId));
            await resource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReassignmentState?> LoadReassignmentStateAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid batchId, CancellationToken cancellationToken)
    {
        string quota;
        string batchIdentity;
        await using (var batch = Command(connection, transaction,
            "SELECT QuotaBasis,Status,OperationCount,ResourceCount,QueueOrder FROM BatchJobs WHERE Id=$batch;"))
        {
            Add(batch, "$batch", Id(batchId));
            await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            quota = reader.GetString(0);
            batchIdentity = $"{reader.GetString(1)}|{reader.GetInt32(2)}|{reader.GetInt32(3)}|{reader.GetInt64(4)}";
        }
        var allocations = new List<ReassignmentAllocation>();
        await using (var command = Command(connection, transaction,
            "SELECT al.AccountId,CASE WHEN $quota='TaskCount' THEN al.TargetItemCount ELSE al.TargetSuccessCount END,al.EffectiveTargetSuccessCount,al.ReservedSlotCount+al.FulfilledCount+CASE WHEN $quota='SuccessTarget' THEN (SELECT COUNT(*) FROM TransferOperations o JOIN ResourceItems r ON r.Id=o.ResourceItemId WHERE o.BatchJobId=al.BatchJobId AND o.AssignedAccountId=al.AccountId AND o.Status='Waiting' AND o.SubmitState IN ('NotPrepared','PreparingToSubmit') AND o.TargetSlotState IN ('Unoccupied','Released') AND o.DailyCountedAt IS NULL AND r.CandidateStatus='Reserved' AND r.ReservedAccountId=al.AccountId) ELSE 0 END,a.Status,a.IsEnabled,a.IsDeleted FROM AccountAllocations al JOIN Accounts a ON a.Id=al.AccountId WHERE al.BatchJobId=$batch ORDER BY al.AccountId;"))
        {
            Add(command, "$quota", quota); Add(command, "$batch", Id(batchId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) allocations.Add(new(
                Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetString(4), reader.GetInt64(5) != 0, reader.GetInt64(6) != 0));
        }
        var operations = new List<ReassignmentOperation>();
        await using (var command = Command(connection, transaction,
            "SELECT o.Id,o.SequenceNumber,o.AssignedAccountId,o.Status,o.SubmitState,CASE WHEN o.AllocationSource IN ('Locked','Exclusive') OR r.CandidateConstraintAccountId IS NOT NULL THEN 1 ELSE 0 END,(SELECT GROUP_CONCAT(al.AccountId,'|') FROM AccountAllocations al WHERE al.BatchJobId=o.BatchJobId AND NOT EXISTS(SELECT 1 FROM TransferOperations existing WHERE existing.Id<>o.Id AND existing.ResourceItemId=o.ResourceItemId AND existing.AssignedAccountId=al.AccountId AND existing.NormalizedTargetPath=COALESCE(r.NormalizedTargetPathOverride,al.NormalizedDefaultTargetPath) AND existing.CreateFolderName=COALESCE(r.CreateFolderName,o.CreateFolderName) AND existing.Status<>'Cancelled')),CASE WHEN o.DailyCountedAt IS NOT NULL THEN 1 ELSE 0 END FROM TransferOperations o JOIN ResourceItems r ON r.Id=o.ResourceItemId WHERE o.BatchJobId=$batch ORDER BY o.SequenceNumber,o.Id;"))
        {
            Add(command, "$batch", Id(batchId)); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var compatible = reader.IsDBNull(6)
                    ? new HashSet<Guid>()
                    : reader.GetString(6).Split('|', StringSplitOptions.RemoveEmptyEntries)
                        .Select(Guid.Parse).ToHashSet();
                operations.Add(new(Guid.Parse(reader.GetString(0)),reader.GetInt64(1),Guid.Parse(reader.GetString(2)),reader.GetString(3),reader.GetString(4),reader.GetInt64(5)!=0,compatible,reader.GetInt64(7)!=0));
            }
        }
        var candidates = new List<ReassignmentCandidate>();
        await using (var command = Command(connection, transaction,
            "SELECT Id,CandidateSequence,CandidateStatus,ReservedAccountId,CandidateConstraintAccountId FROM ResourceItems WHERE BatchJobId=$batch ORDER BY CandidateSequence,Id;"))
        {
            Add(command, "$batch", Id(batchId)); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) candidates.Add(new(Guid.Parse(reader.GetString(0)),reader.GetInt64(1),reader.GetString(2),ReadGuid(reader,3),ReadGuid(reader,4)));
        }
        var version = ComputeVersion(batchId, quota, batchIdentity, allocations, operations, candidates);
        return new ReassignmentState(batchId, version, Enum.Parse<QuotaBasis>(quota), allocations, operations, candidates);
    }

    private static string ComputeVersion(Guid batchId, string quota, string batchIdentity, IEnumerable<ReassignmentAllocation> allocations, IEnumerable<ReassignmentOperation> operations, IEnumerable<ReassignmentCandidate> candidates)
    {
        var builder = new StringBuilder().Append("batch-v1|").Append(batchId.ToString("N")).Append('|').Append(quota).Append('|').Append(batchIdentity);
        foreach (var item in allocations) builder.Append("|A:").Append(item.AccountId.ToString("N")).Append(':').Append(item.TargetCount).Append(':').Append(item.EffectiveTargetCount).Append(':').Append(item.MinimumRetainedTargetCount).Append(':').Append(item.AccountStatus).Append(':').Append(item.IsEnabled).Append(':').Append(item.IsDeleted);
        foreach (var item in operations)
        {
            builder.Append("|O:").Append(item.OperationId.ToString("N")).Append(':').Append(item.AssignedAccountId.ToString("N")).Append(':').Append(item.Status).Append(':').Append(item.SubmitState).Append(':').Append(item.IsAccountExclusive).Append(':').Append(item.HasCrossedSubmissionBoundary);
            if (item.CompatibleTargetAccountIds is not null)
                foreach (var accountId in item.CompatibleTargetAccountIds.OrderBy(value => value)) builder.Append(':').Append(accountId.ToString("N"));
        }
        foreach (var item in candidates) builder.Append("|C:").Append(item.ResourceItemId.ToString("N")).Append(':').Append(item.CandidateStatus).Append(':').Append(item.ReservedAccountId).Append(':').Append(item.ConstraintAccountId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static async Task<ScheduledOperation?> ReadWaitingAsync(SqliteConnection connection, SqliteTransaction transaction, Guid batchId, string dateKey, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "SELECT o.Id,o.AssignedAccountId,o.AllocationOrdinal,o.AllocationSource FROM TransferOperations o JOIN Accounts a ON a.Id=o.AssignedAccountId JOIN BatchJobs b ON b.Id=o.BatchJobId JOIN AccountAllocations al ON al.BatchJobId=o.BatchJobId AND al.AccountId=o.AssignedAccountId JOIN ResourceItems r ON r.Id=o.ResourceItemId WHERE o.BatchJobId=$batch AND o.Status='Waiting' AND o.TargetSlotState IN ('Unoccupied','Released') AND r.CandidateStatus<>'GloballyInvalid' AND r.GlobalValidityStatus<>'Invalid' AND (b.QuotaBasis<>'SuccessTarget' OR al.EffectiveTargetSuccessCount-al.FulfilledCount-al.ReservedSlotCount>0) AND a.Status='Active' AND a.IsEnabled=1 AND a.IsDeleted=0 AND (o.DailyCountedAt IS NOT NULL OR a.DailyLimit=0 OR a.DailyCountDateKey<>$date OR a.DailyExecutedCount<a.DailyLimit) AND NOT EXISTS(SELECT 1 FROM TransferOperations busy WHERE busy.AssignedAccountId=o.AssignedAccountId AND busy.Status IN ('Reserved','Preparing','Submitting','ResultPending','Paused')) AND NOT EXISTS(SELECT 1 FROM ShareOperations shareBusy WHERE shareBusy.AccountId=o.AssignedAccountId AND shareBusy.Status IN ('Reserved','Preparing','Submitting','ResultPending','Paused')) ORDER BY o.AllocationOrdinal,o.SequenceNumber,o.Id LIMIT 1;");
        Add(command, "$batch", Id(batchId)); Add(command, "$date", dateKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ScheduledOperation(Guid.Parse(reader.GetString(0)),batchId,Guid.Parse(reader.GetString(1)),reader.GetInt64(2),reader.GetString(3)) : null;
    }

    private static async Task<(Guid AccountId, string DefaultTarget, string NormalizedDefaultTarget)?> SelectSuccessAccountAsync(SqliteConnection connection, SqliteTransaction transaction, Guid batchId, string dateKey, CancellationToken cancellationToken)
    {
        const string noExistingKey = "NOT EXISTS(SELECT 1 FROM TransferOperations existing WHERE existing.ResourceItemId=r.Id AND existing.AssignedAccountId=al.AccountId AND existing.NormalizedTargetPath=COALESCE(r.NormalizedTargetPathOverride,al.NormalizedDefaultTargetPath) AND existing.CreateFolderName=COALESCE(r.CreateFolderName,'') AND existing.Status<>'Cancelled')";
        var exclusiveUnallocated = $"EXISTS(SELECT 1 FROM ResourceItems r WHERE r.BatchJobId=al.BatchJobId AND r.CandidateStatus='Unallocated' AND r.CandidateConstraintAccountId=al.AccountId AND r.GlobalValidityStatus<>'Invalid' AND {noExistingKey})";
        var exclusiveUnused = $"EXISTS(SELECT 1 FROM ResourceItems r WHERE r.BatchJobId=al.BatchJobId AND r.CandidateStatus='Unused' AND r.CandidateConstraintAccountId=al.AccountId AND r.GlobalValidityStatus<>'Invalid' AND {noExistingKey})";
        var publicUnallocated = $"EXISTS(SELECT 1 FROM ResourceItems r WHERE r.BatchJobId=al.BatchJobId AND r.CandidateStatus='Unallocated' AND r.CandidateConstraintAccountId IS NULL AND r.GlobalValidityStatus<>'Invalid' AND {noExistingKey})";
        var publicUnused = $"EXISTS(SELECT 1 FROM ResourceItems r WHERE r.BatchJobId=al.BatchJobId AND r.CandidateStatus='Unused' AND r.CandidateConstraintAccountId IS NULL AND r.GlobalValidityStatus<>'Invalid' AND {noExistingKey})";
        await using var command = Command(connection, transaction,
            $"SELECT al.AccountId,al.DefaultTargetPath,al.NormalizedDefaultTargetPath,al.TargetSuccessCount,al.EffectiveTargetSuccessCount,al.FulfilledCount,al.ReservedSlotCount,al.PriorityOrder,b.ShortagePolicy FROM AccountAllocations al JOIN Accounts a ON a.Id=al.AccountId JOIN BatchJobs b ON b.Id=al.BatchJobId WHERE al.BatchJobId=$batch AND al.EffectiveTargetSuccessCount-al.FulfilledCount-al.ReservedSlotCount>0 AND a.Status='Active' AND a.IsEnabled=1 AND a.IsDeleted=0 AND (a.DailyLimit=0 OR a.DailyCountDateKey<>$date OR a.DailyExecutedCount<a.DailyLimit) AND NOT EXISTS(SELECT 1 FROM TransferOperations busy WHERE busy.AssignedAccountId=al.AccountId AND busy.Status IN ('Reserved','Preparing','Submitting','ResultPending','Paused')) AND NOT EXISTS(SELECT 1 FROM ShareOperations shareBusy WHERE shareBusy.AccountId=al.AccountId AND shareBusy.Status IN ('Reserved','Preparing','Submitting','ResultPending','Paused')) AND ({exclusiveUnallocated} OR {exclusiveUnused} OR {publicUnallocated} OR {publicUnused}) ORDER BY al.PriorityOrder,al.AccountId;");
        Add(command, "$batch", Id(batchId)); Add(command, "$date", dateKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var eligible = new List<(SuccessTargetAccount State, string DefaultTarget, string NormalizedDefaultTarget)>();
        string? shortagePolicy = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var accountId = Guid.Parse(reader.GetString(0));
            eligible.Add((new SuccessTargetAccount(
                accountId,
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7)), reader.GetString(1), reader.GetString(2)));
            shortagePolicy ??= reader.GetString(8);
        }
        if (eligible.Count == 0) return null;
        var selected = string.Equals(shortagePolicy, "AccountPriority", StringComparison.Ordinal)
            ? eligible.OrderBy(item => item.State.PriorityOrder).ThenBy(item => item.State.AccountId).First().State
            : new SuccessTargetAllocator().SelectAccount(eligible.Select(item => item.State));
        if (selected is null) return null;
        var binding = eligible.Single(item => item.State.AccountId == selected.AccountId);
        return (selected.AccountId, binding.DefaultTarget, binding.NormalizedDefaultTarget);
    }

    private static async Task<(int OutputOrdinal,string SettingsFingerprint)?> ReadPendingShareBindingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT output.OutputOrdinal,settings.SettingsFingerprint
            FROM BatchOutputAccounts output
            JOIN BatchShareSettings settings ON settings.BatchJobId=output.BatchJobId
            WHERE output.BatchJobId=$batch AND output.AccountId=$account AND output.ShareEnabled=1;
            """);
        Add(command, "$batch", Id(batchId)); Add(command, "$account", Id(accountId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt32(0), reader.GetString(1))
            : null;
    }

    private static async Task<RetrySharePlan?> ReadRetrySharePlanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT output.ShareEnabled,output.OutputOrdinal,output.PlannedShareCount,
              output.CreatedShareOperationCount,settings.SettingsFingerprint,
              (SELECT batch.ShareOperationCount FROM BatchJobs batch WHERE batch.Id=output.BatchJobId),
              (SELECT COUNT(*) FROM ShareOperations share WHERE share.BatchJobId=output.BatchJobId)
            FROM BatchOutputAccounts output
            LEFT JOIN BatchShareSettings settings ON settings.BatchJobId=output.BatchJobId
            WHERE output.BatchJobId=$batch AND output.AccountId=$account;
            """);
        Add(command, "$batch", Id(batchId)); Add(command, "$account", Id(accountId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var result = new RetrySharePlan(
            reader.GetInt32(0) != 0,
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            ReadString(reader, 4),
            reader.GetInt32(5),
            reader.GetInt32(6));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("A batch account has multiple output definitions.");
        return result;
    }

    private static async Task<(Guid ResourceId,long Sequence,string NormalizedShareId,string? TargetOverride,string? NormalizedTargetOverride,string? Folder,Guid? ConstraintAccountId,bool WasUnused)?> SelectCandidateAsync(SqliteConnection connection, SqliteTransaction transaction, Guid batchId, Guid accountId, CancellationToken cancellationToken)
    {
        var exclusive = Earlier(
            await ProbeCandidateAsync(connection, transaction, batchId, accountId, "Unallocated", exclusive: true, cancellationToken).ConfigureAwait(false),
            await ProbeCandidateAsync(connection, transaction, batchId, accountId, "Unused", exclusive: true, cancellationToken).ConfigureAwait(false));
        if (exclusive is not null) return exclusive;
        return Earlier(
            await ProbeCandidateAsync(connection, transaction, batchId, accountId, "Unallocated", exclusive: false, cancellationToken).ConfigureAwait(false),
            await ProbeCandidateAsync(connection, transaction, batchId, accountId, "Unused", exclusive: false, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<(Guid ResourceId,long Sequence,string NormalizedShareId,string? TargetOverride,string? NormalizedTargetOverride,string? Folder,Guid? ConstraintAccountId,bool WasUnused)?> ProbeCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        Guid accountId,
        string status,
        bool exclusive,
        CancellationToken cancellationToken)
    {
        var constraint = exclusive ? "CandidateConstraintAccountId=$account" : "CandidateConstraintAccountId IS NULL";
        await using var command = Command(connection, transaction,
            $"SELECT r.Id,r.CandidateSequence,r.NormalizedShareId,r.TargetPathOverride,r.NormalizedTargetPathOverride,r.CreateFolderName,r.CandidateConstraintAccountId FROM ResourceItems r JOIN AccountAllocations al ON al.BatchJobId=r.BatchJobId AND al.AccountId=$account WHERE r.BatchJobId=$batch AND r.CandidateStatus=$status AND r.{constraint} AND r.GlobalValidityStatus<>'Invalid' AND NOT EXISTS(SELECT 1 FROM TransferOperations existing WHERE existing.ResourceItemId=r.Id AND existing.AssignedAccountId=$account AND existing.NormalizedTargetPath=COALESCE(r.NormalizedTargetPathOverride,al.NormalizedDefaultTargetPath) AND existing.CreateFolderName=COALESCE(r.CreateFolderName,'') AND existing.Status<>'Cancelled') ORDER BY r.CandidateSequence LIMIT 1;");
        Add(command, "$batch", Id(batchId)); Add(command, "$status", status); Add(command, "$account", Id(accountId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (Guid.Parse(reader.GetString(0)),reader.GetInt64(1),reader.GetString(2),ReadString(reader,3),ReadString(reader,4),ReadString(reader,5),ReadGuid(reader,6),string.Equals(status, "Unused", StringComparison.Ordinal)) : null;
    }

    private static (Guid ResourceId,long Sequence,string NormalizedShareId,string? TargetOverride,string? NormalizedTargetOverride,string? Folder,Guid? ConstraintAccountId,bool WasUnused)? Earlier(
        (Guid ResourceId,long Sequence,string NormalizedShareId,string? TargetOverride,string? NormalizedTargetOverride,string? Folder,Guid? ConstraintAccountId,bool WasUnused)? left,
        (Guid ResourceId,long Sequence,string NormalizedShareId,string? TargetOverride,string? NormalizedTargetOverride,string? Folder,Guid? ConstraintAccountId,bool WasUnused)? right) =>
        left is null ? right : right is null ? left : left.Value.Sequence <= right.Value.Sequence ? left : right;

    private static async Task IncrementReservedAsync(SqliteConnection connection, SqliteTransaction transaction, Guid batchId, Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount+1 WHERE BatchJobId=$batch AND AccountId=$account AND (EXISTS(SELECT 1 FROM BatchJobs b WHERE b.Id=$batch AND b.QuotaBasis<>'SuccessTarget') OR EffectiveTargetSuccessCount-FulfilledCount-ReservedSlotCount>0);");
        Add(command, "$batch", Id(batchId)); Add(command, "$account", Id(accountId));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The operation allocation no longer exists.");
    }

    private static async Task<int> FinalizeExhaustedPreSubmissionGenerationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scope,
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken)
    {
        var predicate = $"TargetSlotState='Reserved' AND ConfirmationDescriptorJson IS NULL AND SubmitStartedAt IS NULL AND AdapterRequestId IS NULL AND RemoteItemIdsJson IS NULL AND AttemptCount=$max AND (((Status='Preparing') AND SubmitState='PreparingToSubmit') OR ((Status='Reserved') AND SubmitState='NotPrepared')) AND {scope}";
        var allocations = new List<(Guid BatchId, Guid AccountId, int Count)>();
        await using (var read = Command(connection, transaction,
            $"SELECT BatchJobId,AssignedAccountId,COUNT(*) FROM TransferOperations WHERE {predicate} GROUP BY BatchJobId,AssignedAccountId;"))
        {
            Add(read, "$max", int.MaxValue); Add(read, "$now", Time(recoveredAt));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                allocations.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt32(2)));
        }
        var exhaustedCount = allocations.Sum(item => item.Count);
        if (exhaustedCount == 0) return 0;

        await using (var resources = Command(connection, transaction,
            $"UPDATE ResourceItems SET CandidateStatus='Expanded',ReservedAccountId=NULL,ReservationExpiresAt=NULL,UpdatedAt=$now WHERE Id IN (SELECT ResourceItemId FROM TransferOperations WHERE {predicate});"))
        {
            Add(resources, "$max", int.MaxValue); Add(resources, "$now", Time(recoveredAt));
            await resources.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var operations = Command(connection, transaction,
            $"UPDATE TransferOperations SET Status='Failed',OutcomeKind='Failed',TargetSlotState='Released',TargetSlotReservedAt=NULL,LeaseExpiresAt=NULL,LastErrorCode='AttemptLimitReached',LastErrorMessage='The operation attempt limit has been reached.',UpdatedAt=$now WHERE {predicate};"))
        {
            Add(operations, "$max", int.MaxValue); Add(operations, "$now", Time(recoveredAt));
            if (await operations.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != exhaustedCount)
                throw new InvalidOperationException("Attempt-exhausted operations changed during recovery.");
        }
        foreach (var allocation in allocations)
        {
            await using var update = Command(connection, transaction,
                "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-$count,FailedItemCount=FailedItemCount+$count WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>=$count;");
            Add(update, "$count", allocation.Count); Add(update, "$batch", Id(allocation.BatchId));
            Add(update, "$account", Id(allocation.AccountId));
            RequireOne(await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                "An attempt-exhausted allocation reservation is inconsistent.");
        }
        return exhaustedCount;
    }

    private static async Task<(Guid BatchId,Guid ResourceId,Guid AccountId)> ReadOperationIdsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "SELECT BatchJobId,ResourceItemId,AssignedAccountId FROM TransferOperations WHERE Id=$id;"); Add(command, "$id", Id(operationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("The operation no longer exists.");
        return (Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),Guid.Parse(reader.GetString(2)));
    }

    private async Task ExecuteRequiredAsync(string sql, string message, CancellationToken cancellationToken, params (string Name,object? Value)[] values)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, sql); foreach (var value in values) Add(command,value.Name,value.Value);
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), message);
    }

    private static void ValidateSafeError(string code, string safeMessage)
    {
        var allowed = Enum.GetNames<BaiduErrorKind>().Append("DailyLimit").Append("UserInterventionRequired").ToHashSet(StringComparer.Ordinal);
        if (!allowed.Contains(code)) throw new ArgumentException("The scheduler error code is not allowlisted.", nameof(code));
        var expected = code switch
        {
            "DailyLimit" => "The account daily limit has been reached.",
            "UserInterventionRequired" => "User intervention is required.",
            _ when Enum.TryParse<BaiduErrorKind>(code, out var kind) => BaiduAdapterResult.Failure(kind, null).SafeMessage,
            _ => null,
        };
        if (!string.Equals(expected, safeMessage, StringComparison.Ordinal)) throw new ArgumentException("Only canonical safe scheduler messages may be persisted.", nameof(safeMessage));
    }

    private sealed record RetryFailedTransferSnapshot(
        Guid BatchJobId,
        Guid AccountId,
        string Status,
        string? OutcomeKind,
        int AttemptCount,
        string UpdatedAt,
        string SubmitState,
        string TargetSlotState,
        string? DailyCountedAt,
        string? ConfirmationDescriptorJson,
        string? SubmitStartedAt,
        string? AdapterRequestId,
        string? RemoteItemIdsJson,
        string? LeaseExpiresAt,
        string BatchStatus,
        string CandidateStatus,
        string GlobalValidityStatus,
        string? ReservedAccountId,
        int FailedItemCount,
        bool AccountIsEligible,
        bool HasAdvancedShareOperation,
        string QuotaBasis,
        int TargetSuccessCount,
        int EffectiveTargetSuccessCount,
        int FulfilledCount,
        int ReservedSlotCount,
        int WaitingSlotDemand,
        Guid ResourceItemId,
        bool HasAnyShareOperation);

    private sealed record RetrySharePlan(
        bool ShareEnabled,
        int OutputOrdinal,
        int PlannedShareCount,
        int CreatedShareOperationCount,
        string? SettingsFingerprint,
        int BatchShareOperationCount,
        int CurrentBatchShareOperationCount);

    private static void RequireOne(int changed, string message) { if (changed != 1) throw new InvalidOperationException(message); }
    private static string Fingerprint(string shareId, Guid accountId, string path, string folder) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"operation-v1\n{shareId}\n{accountId:N}\n{path}\n{folder}")));
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql) { var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText=sql; return command; }
    private static void Add(SqliteCommand command,string name,object? value)=>command.Parameters.AddWithValue(name,value??DBNull.Value);
    private static string Id(Guid id)=>id.ToString("D");
    private static string Time(DateTimeOffset time)=>time.ToString("O");
    private static string? ReadString(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?null:reader.GetString(index);
    private static Guid? ReadGuid(SqliteDataReader reader,int index)=>reader.IsDBNull(index)?null:Guid.Parse(reader.GetString(index));
    private static async Task<long> ScalarLongAsync(SqliteConnection c,SqliteTransaction? t,string sql,CancellationToken ct,params (string,object?)[] values) { await using var cmd=Command(c,t,sql); foreach(var v in values) Add(cmd,v.Item1,v.Item2); return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)); }
    private static async Task<string?> ScalarStringOrNullAsync(SqliteConnection c,SqliteTransaction? t,string sql,CancellationToken ct,params (string,object?)[] values) { await using var cmd=Command(c,t,sql); foreach(var v in values) Add(cmd,v.Item1,v.Item2); return Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)); }
}
