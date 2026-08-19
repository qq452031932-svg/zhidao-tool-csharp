using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed class SqliteTransferSchedulingStore :
    IAllocationTransactionStore,
    ISubmissionBoundaryStore,
    IOperationExecutionStore,
    IReassignmentStore,
    IUserConfirmedBatchResumeStore
{
    private readonly SqliteConnectionFactory factory;

    public SqliteTransferSchedulingStore(SqliteConnectionFactory factory) =>
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

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
            await using var command = Command(connection, null,
                "SELECT AssignedAccountId,COUNT(*) FROM TransferOperations WHERE Status IN ('Reserved','Preparing','Submitting','ResultPending') GROUP BY AssignedAccountId;");
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
            "SELECT COUNT(*) FROM TransferOperations WHERE Status IN ('Reserved','Preparing','Submitting','ResultPending');",
            cancellationToken).ConfigureAwait(false);
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

        var ordinal = checked(await ScalarLongAsync(connection, transaction,
            "SELECT COALESCE(MAX(AllocationOrdinal),0)+1 FROM TransferOperations WHERE BatchJobId=$batch;",
            cancellationToken, ("$batch", Id(batchId))).ConfigureAwait(false));
        var operationId = Guid.NewGuid();
        var target = candidate.Value.TargetOverride ?? account.Value.DefaultTarget;
        var normalizedTarget = candidate.Value.NormalizedTargetOverride ?? account.Value.NormalizedDefaultTarget;
        var folder = candidate.Value.Folder ?? string.Empty;
        var source = candidate.Value.ConstraintAccountId is null ? "Weighted" : "Exclusive";
        var fingerprint = Fingerprint(candidate.Value.NormalizedShareId, account.Value.AccountId, normalizedTarget, folder);

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
        await using (var updateAllocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET AssignedItemCount=AssignedItemCount+1,ReservedSlotCount=ReservedSlotCount+1 WHERE BatchJobId=$batch AND AccountId=$account AND EffectiveTargetSuccessCount-FulfilledCount-ReservedSlotCount>0;"))
        {
            Add(updateAllocation, "$batch", Id(batchId)); Add(updateAllocation, "$account", Id(account.Value.AccountId));
            if (await updateAllocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The allocation changed during candidate reservation.");
        }
        await using (var updateBatch = Command(connection, transaction,
            "UPDATE BatchJobs SET OperationCount=OperationCount+1 WHERE Id=$batch;"))
        { Add(updateBatch, "$batch", Id(batchId)); await updateBatch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ScheduledOperation(operationId, batchId, account.Value.AccountId, ordinal, source);
    }

    public Task<OperationExecutionSnapshot?> LoadOperationAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        LoadOperationCoreAsync(operationId, cancellationToken);

    Task<OperationExecutionSnapshot?> IOperationExecutionStore.LoadAsync(Guid operationId, CancellationToken cancellationToken) =>
        LoadOperationCoreAsync(operationId, cancellationToken);

    private async Task<OperationExecutionSnapshot?> LoadOperationCoreAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null,
            "SELECT o.Id,o.AssignedAccountId,r.NormalizedShareId,r.ShareUrl,r.ExtractionCode,o.TargetPath,o.CreateFolderName,b.DuplicatePolicy,o.OperationFingerprint FROM TransferOperations o JOIN ResourceItems r ON r.Id=o.ResourceItemId JOIN BatchJobs b ON b.Id=o.BatchJobId WHERE o.Id=$id;");
        Add(command, "$id", Id(operationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new OperationExecutionSnapshot(
            Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            new ShareReference(reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)),
            new RemotePath(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6),
            Enum.Parse<DuplicatePolicy>(reader.GetString(7)), reader.GetString(8));
    }

    public Task MarkPreparingAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteRequiredAsync(
            "UPDATE TransferOperations SET Status='Preparing',SubmitState='PreparingToSubmit',AttemptCount=AttemptCount+1,LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$now WHERE Id=$id AND Status='Reserved' AND TargetSlotState='Reserved';",
            "Operation is not reserved for preparation.", cancellationToken, ("$now", Time(now)), ("$id", Id(operationId)));

    public async Task<SubmissionBoundaryResult> PersistAsync(
        SubmissionBoundaryPersistence persistence,
        CancellationToken cancellationToken = default)
    {
        var commandValue = persistence.Command;
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var read = Command(connection, transaction,
            "SELECT o.AssignedAccountId,o.DailyCountedAt,o.SubmitState,a.DailyLimit,a.DailyExecutedCount,a.DailyCountDateKey,o.BatchJobId FROM TransferOperations o JOIN Accounts a ON a.Id=o.AssignedAccountId WHERE o.Id=$id;");
        Add(read, "$id", Id(commandValue.OperationId));
        Guid accountId; Guid batchId; bool alreadyCounted; string submitState; int dailyLimit; int count; string dateKey;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("The operation no longer exists.");
            accountId = Guid.Parse(reader.GetString(0)); alreadyCounted = !reader.IsDBNull(1); submitState = reader.GetString(2);
            dailyLimit = reader.GetInt32(3); count = reader.GetInt32(4); dateKey = reader.GetString(5);
            batchId = Guid.Parse(reader.GetString(6));
        }
        if (accountId != commandValue.AccountId) throw new InvalidOperationException("The operation account binding changed.");
        if (alreadyCounted)
        {
            if (submitState is not ("Submitting" or "ResultReceived" or "PendingConfirmation"))
                throw new InvalidOperationException("The persisted daily count has an invalid submit state.");
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
            "UPDATE TransferOperations SET ConfirmationDescriptorJson=$descriptor,SubmitState='Submitting',Status='Submitting',DailyCountedAt=$now,SubmitStartedAt=$now,LeaseExpiresAt=$lease,UpdatedAt=$now WHERE Id=$id AND DailyCountedAt IS NULL AND Status='Preparing' AND SubmitState='PreparingToSubmit';"))
        {
            Add(updateOperation, "$descriptor", commandValue.ConfirmationDescriptorJson); Add(updateOperation, "$now", Time(persistence.PersistedAt));
            Add(updateOperation, "$lease", Time(commandValue.LeaseExpiresAt)); Add(updateOperation, "$id", Id(commandValue.OperationId));
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

    public Task RecordSubmissionIdentifiersAsync(Guid operationId, string? requestId, IReadOnlyList<string> remoteItemIds, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var remoteJson = System.Text.Json.JsonSerializer.Serialize(remoteItemIds ?? []);
        return ExecuteRequiredAsync(
            "UPDATE TransferOperations SET AdapterRequestId=COALESCE($request,AdapterRequestId),RemoteItemIdsJson=$remote,UpdatedAt=$now WHERE Id=$id AND Status='Submitting' AND SubmitState='Submitting' AND ConfirmationDescriptorJson IS NOT NULL;",
            "Operation has not crossed the submit boundary.", cancellationToken,
            ("$request", requestId), ("$remote", remoteJson), ("$now", Time(now)), ("$id", Id(operationId)));
    }

    public async Task MarkSucceededAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = await ReadOperationIdsAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        await using (var operation = Command(connection, transaction,
            "UPDATE TransferOperations SET Status='Succeeded',OutcomeKind='NewTransfer',SubmitState='ResultReceived',TargetSlotState='Fulfilled',LeaseExpiresAt=NULL,LastErrorCode=NULL,LastErrorMessage=NULL,UpdatedAt=$now WHERE Id=$id AND Status IN ('Submitting','ResultPending') AND TargetSlotState='Reserved';"))
        { Add(operation, "$now", Time(now)); Add(operation, "$id", Id(operationId)); RequireOne(await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Operation cannot be marked successful."); }
        await using (var allocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-1,FulfilledCount=FulfilledCount+1,SuccessItemCount=SuccessItemCount+1,NewSuccessItemCount=NewSuccessItemCount+1 WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>0;"))
        { Add(allocation, "$batch", Id(ids.BatchId)); Add(allocation, "$account", Id(ids.AccountId)); RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Allocation reservation is missing."); }
        await using (var resource = Command(connection, transaction,
            "UPDATE ResourceItems SET CandidateStatus='Expanded',ReservationExpiresAt=NULL,UpdatedAt=$now WHERE Id=$resource AND CandidateStatus='Reserved';"))
        { Add(resource, "$now", Time(now)); Add(resource, "$resource", Id(ids.ResourceId)); await resource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task MarkFailedAsync(Guid operationId, string code, string safeMessage, bool globallyInvalid, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        CompleteFailureAsync(operationId, code, safeMessage, globallyInvalid, resultPending: false, now, cancellationToken);

    public Task MarkResultPendingAsync(Guid operationId, string code, string safeMessage, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateSafeError(code, safeMessage);
        if (code is not ("TransientService" or "UnknownResponse"))
            throw new ArgumentException("Only an uncertain remote execution result can remain pending.", nameof(code));
        return CompleteFailureAsync(operationId, code, safeMessage, globallyInvalid: false, resultPending: true, now, cancellationToken);
    }

    public async Task MarkDefiniteAccountFailureAsync(Guid operationId, string code, string safeMessage, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateSafeError(code, safeMessage);
        if (code is not ("ExpiredLogin" or "VerificationRequired" or "InsufficientCapacity" or "InvalidTargetPath"))
            throw new ArgumentException("Only account-scoped failures can pause an account.", nameof(code));
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
            "UPDATE TransferOperations SET Status='Failed',OutcomeKind='Failed',SubmitState=CASE WHEN SubmitState IN ('Submitting','PendingConfirmation') OR DailyCountedAt IS NOT NULL THEN 'ResultReceived' ELSE SubmitState END,TargetSlotState='Released',TargetSlotReservedAt=NULL,LeaseExpiresAt=NULL,LastErrorCode=$code,LastErrorMessage=$message,UpdatedAt=$now WHERE Id=$id AND Status IN ('Reserved','Preparing','Submitting','ResultPending') AND TargetSlotState='Reserved';"))
        {
            Add(failed, "$code", code); Add(failed, "$message", safeMessage); Add(failed, "$now", Time(now)); Add(failed, "$id", Id(operationId));
            RequireOne(await failed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The account failure operation cannot be finalized.");
        }
        await using (var allocation = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount-1,FailedItemCount=FailedItemCount+1 WHERE BatchJobId=$batch AND AccountId=$account AND ReservedSlotCount>0;"))
        {
            Add(allocation, "$batch", Id(ids.BatchId)); Add(allocation, "$account", Id(ids.AccountId));
            RequireOne(await allocation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Allocation reservation is missing.");
        }
        await using (var resource = Command(connection, transaction,
            "UPDATE ResourceItems SET CandidateStatus='Unused',ReservedAccountId=NULL,ReservationExpiresAt=NULL,UpdatedAt=$now WHERE Id=$resource AND CandidateStatus='Reserved';"))
        { Add(resource, "$now", Time(now)); Add(resource, "$resource", Id(ids.ResourceId)); await resource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReturnToWaitingForDailyLimitAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = await ReadOperationIdsAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        await using var operation = Command(connection, transaction,
            "UPDATE TransferOperations SET Status='Waiting',SubmitState='NotPrepared',TargetSlotState='Unoccupied',TargetSlotReservedAt=NULL,LeaseExpiresAt=NULL,UpdatedAt=$now WHERE Id=$id AND Status='Preparing' AND DailyCountedAt IS NULL;");
        Add(operation, "$now", Time(now)); Add(operation, "$id", Id(operationId));
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
        foreach (var move in mutation.OperationMoves)
        {
            string shareId;
            string normalizedPath;
            string folder;
            await using (var fingerprintSource = Command(connection, transaction,
                "SELECT r.NormalizedShareId,o.NormalizedTargetPath,o.CreateFolderName FROM TransferOperations o JOIN ResourceItems r ON r.Id=o.ResourceItemId WHERE o.Id=$id;"))
            {
                Add(fingerprintSource, "$id", Id(move.OperationId));
                await using var reader = await fingerprintSource.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("A reassigned operation no longer exists.");
                shareId = reader.GetString(0); normalizedPath = reader.GetString(1); folder = reader.GetString(2);
            }
            var fingerprint = Fingerprint(shareId, move.NewAccountId, normalizedPath, folder);
            await using var command = Command(connection, transaction,
                "UPDATE TransferOperations SET AssignedAccountId=$new,OperationFingerprint=$fingerprint,UpdatedAt=$now WHERE Id=$id AND BatchJobId=$batch AND AssignedAccountId=$old AND Status='Waiting' AND SubmitState='NotPrepared' AND NOT EXISTS(SELECT 1 FROM ResourceItems r WHERE r.Id=TransferOperations.ResourceItemId AND r.CandidateConstraintAccountId IS NOT NULL);" );
            Add(command, "$new", Id(move.NewAccountId)); Add(command, "$old", Id(move.OldAccountId)); Add(command, "$id", Id(move.OperationId));
            Add(command, "$fingerprint", fingerprint); Add(command, "$batch", Id(mutation.BatchJobId)); Add(command, "$now", Time(DateTimeOffset.UtcNow));
            RequireOne(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "A reassigned operation is no longer movable.");
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

    private async Task CompleteFailureAsync(Guid operationId, string code, string safeMessage, bool globallyInvalid, bool resultPending, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ValidateSafeError(code, safeMessage);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var ids = await ReadOperationIdsAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        if (resultPending)
        {
            await using var pending = Command(connection, transaction,
                "UPDATE TransferOperations SET Status='ResultPending',SubmitState='PendingConfirmation',LastErrorCode=$code,LastErrorMessage=$message,UpdatedAt=$now WHERE Id=$id AND Status IN ('Preparing','Submitting') AND TargetSlotState='Reserved';");
            Add(pending, "$code", code); Add(pending, "$message", safeMessage); Add(pending, "$now", Time(now)); Add(pending, "$id", Id(operationId));
            RequireOne(await pending.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Operation cannot be marked result pending.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await using (var failed = Command(connection, transaction,
            "UPDATE TransferOperations SET Status='Failed',OutcomeKind=$outcome,SubmitState=CASE WHEN SubmitState='Submitting' THEN 'ResultReceived' ELSE SubmitState END,TargetSlotState='Released',LeaseExpiresAt=NULL,LastErrorCode=$code,LastErrorMessage=$message,UpdatedAt=$now WHERE Id=$id AND Status IN ('Reserved','Preparing','Submitting','ResultPending') AND TargetSlotState='Reserved';"))
        {
            Add(failed, "$outcome", globallyInvalid ? "GloballyInvalid" : "Failed"); Add(failed, "$code", code); Add(failed, "$message", safeMessage);
            Add(failed, "$now", Time(now)); Add(failed, "$id", Id(operationId)); RequireOne(await failed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Operation cannot be failed.");
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
            "SELECT AccountId,CASE WHEN $quota='TaskCount' THEN TargetItemCount ELSE TargetSuccessCount END,EffectiveTargetSuccessCount,ReservedSlotCount+FulfilledCount FROM AccountAllocations WHERE BatchJobId=$batch ORDER BY AccountId;"))
        {
            Add(command, "$quota", quota); Add(command, "$batch", Id(batchId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) allocations.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
        }
        var operations = new List<ReassignmentOperation>();
        await using (var command = Command(connection, transaction,
            "SELECT o.Id,o.SequenceNumber,o.AssignedAccountId,o.Status,o.SubmitState,CASE WHEN o.AllocationSource IN ('Locked','Exclusive') OR r.CandidateConstraintAccountId IS NOT NULL THEN 1 ELSE 0 END FROM TransferOperations o JOIN ResourceItems r ON r.Id=o.ResourceItemId WHERE o.BatchJobId=$batch ORDER BY o.SequenceNumber,o.Id;"))
        {
            Add(command, "$batch", Id(batchId)); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) operations.Add(new(Guid.Parse(reader.GetString(0)),reader.GetInt64(1),Guid.Parse(reader.GetString(2)),reader.GetString(3),reader.GetString(4),reader.GetInt64(5)!=0));
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
        foreach (var item in allocations) builder.Append("|A:").Append(item.AccountId.ToString("N")).Append(':').Append(item.TargetCount).Append(':').Append(item.EffectiveTargetCount).Append(':').Append(item.MinimumRetainedTargetCount);
        foreach (var item in operations) builder.Append("|O:").Append(item.OperationId.ToString("N")).Append(':').Append(item.AssignedAccountId.ToString("N")).Append(':').Append(item.Status).Append(':').Append(item.SubmitState);
        foreach (var item in candidates) builder.Append("|C:").Append(item.ResourceItemId.ToString("N")).Append(':').Append(item.CandidateStatus).Append(':').Append(item.ReservedAccountId).Append(':').Append(item.ConstraintAccountId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static async Task<ScheduledOperation?> ReadWaitingAsync(SqliteConnection connection, SqliteTransaction transaction, Guid batchId, string dateKey, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "SELECT o.Id,o.AssignedAccountId,o.AllocationOrdinal,o.AllocationSource FROM TransferOperations o JOIN Accounts a ON a.Id=o.AssignedAccountId JOIN BatchJobs b ON b.Id=o.BatchJobId JOIN AccountAllocations al ON al.BatchJobId=o.BatchJobId AND al.AccountId=o.AssignedAccountId JOIN ResourceItems r ON r.Id=o.ResourceItemId WHERE o.BatchJobId=$batch AND o.Status='Waiting' AND o.TargetSlotState IN ('Unoccupied','Released') AND r.CandidateStatus<>'GloballyInvalid' AND r.GlobalValidityStatus<>'Invalid' AND (b.QuotaBasis<>'SuccessTarget' OR al.EffectiveTargetSuccessCount-al.FulfilledCount-al.ReservedSlotCount>0) AND a.Status='Active' AND a.IsEnabled=1 AND a.IsDeleted=0 AND (a.DailyLimit=0 OR a.DailyCountDateKey<>$date OR a.DailyExecutedCount<a.DailyLimit) AND NOT EXISTS(SELECT 1 FROM TransferOperations busy WHERE busy.AssignedAccountId=o.AssignedAccountId AND busy.Status IN ('Reserved','Preparing','Submitting','ResultPending')) ORDER BY o.AllocationOrdinal,o.SequenceNumber,o.Id LIMIT 1;");
        Add(command, "$batch", Id(batchId)); Add(command, "$date", dateKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ScheduledOperation(Guid.Parse(reader.GetString(0)),batchId,Guid.Parse(reader.GetString(1)),reader.GetInt64(2),reader.GetString(3)) : null;
    }

    private static async Task<(Guid AccountId, string DefaultTarget, string NormalizedDefaultTarget)?> SelectSuccessAccountAsync(SqliteConnection connection, SqliteTransaction transaction, Guid batchId, string dateKey, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "SELECT al.AccountId,al.DefaultTargetPath,al.NormalizedDefaultTargetPath FROM AccountAllocations al JOIN Accounts a ON a.Id=al.AccountId WHERE al.BatchJobId=$batch AND al.EffectiveTargetSuccessCount-al.FulfilledCount-al.ReservedSlotCount>0 AND a.Status='Active' AND a.IsEnabled=1 AND a.IsDeleted=0 AND (a.DailyLimit=0 OR a.DailyCountDateKey<>$date OR a.DailyExecutedCount<a.DailyLimit) AND NOT EXISTS(SELECT 1 FROM TransferOperations busy WHERE busy.AssignedAccountId=al.AccountId AND busy.Status IN ('Reserved','Preparing','Submitting','ResultPending')) AND EXISTS(SELECT 1 FROM ResourceItems r WHERE r.BatchJobId=al.BatchJobId AND r.CandidateStatus IN ('Unallocated','Unused') AND r.GlobalValidityStatus<>'Invalid' AND (r.CandidateConstraintAccountId IS NULL OR r.CandidateConstraintAccountId=al.AccountId)) ORDER BY (CAST(al.FulfilledCount+al.ReservedSlotCount AS REAL)/al.EffectiveTargetSuccessCount),al.PriorityOrder,al.AccountId LIMIT 1;");
        Add(command, "$batch", Id(batchId)); Add(command, "$date", dateKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? (Guid.Parse(reader.GetString(0)),reader.GetString(1),reader.GetString(2)) : null;
    }

    private static async Task<(Guid ResourceId,long Sequence,string NormalizedShareId,string? TargetOverride,string? NormalizedTargetOverride,string? Folder,Guid? ConstraintAccountId)?> SelectCandidateAsync(SqliteConnection connection, SqliteTransaction transaction, Guid batchId, Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "SELECT Id,CandidateSequence,NormalizedShareId,TargetPathOverride,NormalizedTargetPathOverride,CreateFolderName,CandidateConstraintAccountId FROM ResourceItems WHERE BatchJobId=$batch AND CandidateStatus IN ('Unallocated','Unused') AND GlobalValidityStatus<>'Invalid' AND (CandidateConstraintAccountId=$account OR CandidateConstraintAccountId IS NULL) ORDER BY CASE WHEN CandidateConstraintAccountId=$account THEN 0 ELSE 1 END,CandidateSequence,Id LIMIT 1;");
        Add(command, "$batch", Id(batchId)); Add(command, "$account", Id(accountId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (Guid.Parse(reader.GetString(0)),reader.GetInt64(1),reader.GetString(2),ReadString(reader,3),ReadString(reader,4),ReadString(reader,5),ReadGuid(reader,6)) : null;
    }

    private static async Task IncrementReservedAsync(SqliteConnection connection, SqliteTransaction transaction, Guid batchId, Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "UPDATE AccountAllocations SET ReservedSlotCount=ReservedSlotCount+1 WHERE BatchJobId=$batch AND AccountId=$account AND (EXISTS(SELECT 1 FROM BatchJobs b WHERE b.Id=$batch AND b.QuotaBasis<>'SuccessTarget') OR EffectiveTargetSuccessCount-FulfilledCount-ReservedSlotCount>0);");
        Add(command, "$batch", Id(batchId)); Add(command, "$account", Id(accountId));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "The operation allocation no longer exists.");
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
        var allowed = Enum.GetNames<BaiduErrorKind>().Append("DailyLimit").ToHashSet(StringComparer.Ordinal);
        if (!allowed.Contains(code)) throw new ArgumentException("The scheduler error code is not allowlisted.", nameof(code));
        var expected = Enum.TryParse<BaiduErrorKind>(code, out var kind) ? BaiduAdapterResult.Failure(kind, null).SafeMessage : "The account daily limit has been reached.";
        if (!string.Equals(expected, safeMessage, StringComparison.Ordinal)) throw new ArgumentException("Only canonical safe scheduler messages may be persisted.", nameof(safeMessage));
    }

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
