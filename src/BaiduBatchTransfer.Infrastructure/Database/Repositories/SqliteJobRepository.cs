using BaiduBatchTransfer.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database.Repositories;

public sealed class SqliteJobRepository : SqliteRepositoryBase, IJobRepository
{
    internal SqliteJobRepository(SqliteConnection connection, SqliteTransaction transaction) : base(connection, transaction) { }

    public async Task AddAsync(BatchJobRecord j, CancellationToken cancellationToken = default)
    {
        await using var c = Command("INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,StartedAt,CompletedAt,ImportSessionId,QueueOrder) VALUES($id,$name,$source,$mode,$quota,$shortage,$resources,$operations,$target,$folder,$duplicate,$validation,$status,$created,$started,$completed,$import,$queue);");
        Add(c,"$id",Id(j.Id)); Add(c,"$name",j.Name); Add(c,"$source",j.SourceType); Add(c,"$mode",j.TransferMode); Add(c,"$quota",j.QuotaBasis); Add(c,"$shortage",j.ShortagePolicy); Add(c,"$resources",j.ResourceCount); Add(c,"$operations",j.OperationCount); Add(c,"$target",j.DefaultTargetPath); Add(c,"$folder",j.FolderCreationMode); Add(c,"$duplicate",j.DuplicatePolicy); Add(c,"$validation",j.ValidationMode); Add(c,"$status",j.Status); Add(c,"$created",Time(j.CreatedAt)); Add(c,"$started",NullableTime(j.StartedAt)); Add(c,"$completed",NullableTime(j.CompletedAt)); Add(c,"$import",NullableId(j.ImportSessionId)); Add(c,"$queue",j.QueueOrder);
        await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(BatchJobRecord j, CancellationToken cancellationToken = default)
    {
        await using var c = Command("UPDATE BatchJobs SET Name=$name,SourceType=$source,TransferMode=$mode,QuotaBasis=$quota,ShortagePolicy=$shortage,ResourceCount=$resources,OperationCount=$operations,DefaultTargetPath=$target,FolderCreationMode=$folder,DuplicatePolicy=$duplicate,ValidationMode=$validation,Status=$status,StartedAt=$started,CompletedAt=$completed,ImportSessionId=$import,QueueOrder=$queue WHERE Id=$id;");
        Add(c,"$id",Id(j.Id)); Add(c,"$name",j.Name); Add(c,"$source",j.SourceType); Add(c,"$mode",j.TransferMode); Add(c,"$quota",j.QuotaBasis); Add(c,"$shortage",j.ShortagePolicy); Add(c,"$resources",j.ResourceCount); Add(c,"$operations",j.OperationCount); Add(c,"$target",j.DefaultTargetPath); Add(c,"$folder",j.FolderCreationMode); Add(c,"$duplicate",j.DuplicatePolicy); Add(c,"$validation",j.ValidationMode); Add(c,"$status",j.Status); Add(c,"$started",NullableTime(j.StartedAt)); Add(c,"$completed",NullableTime(j.CompletedAt)); Add(c,"$import",NullableId(j.ImportSessionId)); Add(c,"$queue",j.QueueOrder);
        if (await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The batch no longer exists.");
    }

    public async Task<BatchJobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var c = Command("SELECT Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,StartedAt,CompletedAt,ImportSessionId,QueueOrder FROM BatchJobs WHERE Id=$id;"); Add(c,"$id",Id(id));
        await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await r.ReadAsync(cancellationToken).ConfigureAwait(false) ? new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt32(6),r.GetInt32(7),r.GetString(8),r.GetString(9),r.GetString(10),r.GetString(11),r.GetString(12),ReadTime(r,13),ReadNullableTime(r,14),ReadNullableTime(r,15),ReadNullableId(r,16),r.GetInt64(17)) : null;
    }

    public async Task AddResourcesAsync(IEnumerable<ResourceItemRecord> resources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        foreach (var x in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var c = Command("INSERT INTO ResourceItems(Id,BatchJobId,StagedImportRowId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,Note,TargetPathOverride,NormalizedTargetPathOverride,CreateFolderName,FolderCreationModeOverride,GlobalValidityStatus,CandidateConstraintAccountId,CandidateStatus,ReservedAccountId,ReservationExpiresAt,CreatedAt,UpdatedAt) VALUES($id,$job,$staged,$sequence,$url,$share,$code,$note,$target,$normalizedTarget,$folder,$folderMode,$validity,$constraint,$status,$reserved,$expires,$created,$updated);");
            Add(c,"$id",Id(x.Id)); Add(c,"$job",Id(x.BatchJobId)); Add(c,"$staged",NullableId(x.StagedImportRowId)); Add(c,"$sequence",x.CandidateSequence); Add(c,"$url",x.ShareUrl); Add(c,"$share",x.NormalizedShareId); Add(c,"$code",x.ExtractionCode); Add(c,"$note",x.Note); Add(c,"$target",x.TargetPathOverride); Add(c,"$normalizedTarget",x.NormalizedTargetPathOverride); Add(c,"$folder",x.CreateFolderName); Add(c,"$folderMode",x.FolderCreationModeOverride); Add(c,"$validity",x.GlobalValidityStatus); Add(c,"$constraint",NullableId(x.CandidateConstraintAccountId)); Add(c,"$status",x.CandidateStatus); Add(c,"$reserved",NullableId(x.ReservedAccountId)); Add(c,"$expires",NullableTime(x.ReservationExpiresAt)); Add(c,"$created",Time(x.CreatedAt)); Add(c,"$updated",Time(x.UpdatedAt));
            await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Page<ResourceItemRecord>> GetResourcesPageAsync(Guid batchJobId, PageQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var total = await CountAsync("ResourceItems", batchJobId, cancellationToken).ConfigureAwait(false);
        await using var c = Command("SELECT Id,BatchJobId,StagedImportRowId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,Note,TargetPathOverride,NormalizedTargetPathOverride,CreateFolderName,FolderCreationModeOverride,GlobalValidityStatus,CandidateConstraintAccountId,CandidateStatus,ReservedAccountId,ReservationExpiresAt,CreatedAt,UpdatedAt FROM ResourceItems WHERE BatchJobId=$job ORDER BY CandidateSequence,Id LIMIT $limit OFFSET $offset;"); AddPage(c,batchJobId,query);
        var items = new List<ResourceItemRecord>(); await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(new(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),ReadNullableId(r,2),r.GetInt64(3),r.GetString(4),r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:r.GetString(9),r.IsDBNull(10)?null:r.GetString(10),r.GetString(11),r.GetString(12),ReadNullableId(r,13),r.GetString(14),ReadNullableId(r,15),ReadNullableTime(r,16),ReadTime(r,17),ReadTime(r,18)));
        return new(items,total,query.PageNumber,query.PageSize);
    }

    public async Task AddOperationsAsync(IEnumerable<TransferOperationRecord> operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        foreach (var x in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var c = Command("INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,AdapterRequestId,RemoteItemIdsJson,SubmitState,TargetSlotState,TargetSlotReservedAt,DailyCountedAt,SubmitStartedAt,LastErrorCode,LastErrorMessage,LeaseExpiresAt,CreatedAt,UpdatedAt) VALUES($id,$resource,$job,$sequence,$ordinal,$target,$normalized,$folder,$account,$source,$status,$outcome,$attempt,$fingerprint,$confirmation,$request,$remote,$submit,$slot,$reserved,$counted,$started,$errorCode,$errorMessage,$lease,$created,$updated);");
            Add(c,"$id",Id(x.Id)); Add(c,"$resource",Id(x.ResourceItemId)); Add(c,"$job",Id(x.BatchJobId)); Add(c,"$sequence",x.SequenceNumber); Add(c,"$ordinal",x.AllocationOrdinal); Add(c,"$target",x.TargetPath); Add(c,"$normalized",x.NormalizedTargetPath); Add(c,"$folder",x.CreateFolderName); Add(c,"$account",Id(x.AssignedAccountId)); Add(c,"$source",x.AllocationSource); Add(c,"$status",x.Status); Add(c,"$outcome",x.OutcomeKind); Add(c,"$attempt",x.AttemptCount); Add(c,"$fingerprint",x.OperationFingerprint); Add(c,"$confirmation",x.ConfirmationDescriptorJson); Add(c,"$request",x.AdapterRequestId); Add(c,"$remote",x.RemoteItemIdsJson); Add(c,"$submit",x.SubmitState); Add(c,"$slot",x.TargetSlotState); Add(c,"$reserved",NullableTime(x.TargetSlotReservedAt)); Add(c,"$counted",NullableTime(x.DailyCountedAt)); Add(c,"$started",NullableTime(x.SubmitStartedAt)); Add(c,"$errorCode",x.LastErrorCode); Add(c,"$errorMessage",x.LastErrorMessage); Add(c,"$lease",NullableTime(x.LeaseExpiresAt)); Add(c,"$created",Time(x.CreatedAt)); Add(c,"$updated",Time(x.UpdatedAt));
            await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Page<TransferOperationRecord>> GetOperationsPageAsync(Guid batchJobId, PageQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var total = await CountAsync("TransferOperations",batchJobId,cancellationToken).ConfigureAwait(false);
        await using var c = Command("SELECT Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,AdapterRequestId,RemoteItemIdsJson,SubmitState,TargetSlotState,TargetSlotReservedAt,DailyCountedAt,SubmitStartedAt,LastErrorCode,LastErrorMessage,LeaseExpiresAt,CreatedAt,UpdatedAt FROM TransferOperations WHERE BatchJobId=$job ORDER BY SequenceNumber,AllocationOrdinal,Id LIMIT $limit OFFSET $offset;"); AddPage(c,batchJobId,query);
        var items = new List<TransferOperationRecord>(); await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(new(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),Guid.Parse(r.GetString(2)),r.GetInt64(3),r.GetInt64(4),r.GetString(5),r.GetString(6),r.GetString(7),Guid.Parse(r.GetString(8)),r.GetString(9),r.GetString(10),r.IsDBNull(11)?null:r.GetString(11),r.GetInt32(12),r.GetString(13),r.IsDBNull(14)?null:r.GetString(14),r.IsDBNull(15)?null:r.GetString(15),r.IsDBNull(16)?null:r.GetString(16),r.GetString(17),r.GetString(18),ReadNullableTime(r,19),ReadNullableTime(r,20),ReadNullableTime(r,21),r.IsDBNull(22)?null:r.GetString(22),r.IsDBNull(23)?null:r.GetString(23),ReadNullableTime(r,24),ReadTime(r,25),ReadTime(r,26)));
        return new(items,total,query.PageNumber,query.PageSize);
    }

    public async Task PrepareSubmissionAsync(OperationSubmissionPreparation preparation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(preparation.ConfirmationDescriptorJson)) throw new ArgumentException("A confirmation descriptor is required.", nameof(preparation));
        await using var c = Command("UPDATE TransferOperations SET ConfirmationDescriptorJson=$descriptor,SubmitState='Submitting',Status='Submitting',LeaseExpiresAt=$lease,SubmitStartedAt=$updated,UpdatedAt=$updated WHERE Id=$id AND SubmitState IN ('NotPrepared','PreparingToSubmit') AND Status IN ('Reserved','Preparing');");
        Add(c,"$descriptor",preparation.ConfirmationDescriptorJson); Add(c,"$lease",Time(preparation.LeaseExpiresAt)); Add(c,"$updated",Time(preparation.UpdatedAt)); Add(c,"$id",Id(preparation.OperationId));
        if (await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new InvalidOperationException("Operation is not eligible to enter the submit boundary.");
    }

    public async Task RecordSubmissionResponseAsync(OperationSubmissionResponse response, CancellationToken cancellationToken = default)
    {
        if (response.SubmitState is not ("ResultReceived" or "PendingConfirmation") ||
            response.Status is not ("ResultPending" or "Succeeded" or "Failed"))
            throw new ArgumentException("A submission response must move monotonically to a post-submit state.", nameof(response));
        await using var c = Command("UPDATE TransferOperations SET AdapterRequestId=COALESCE($request,AdapterRequestId),RemoteItemIdsJson=COALESCE($remote,RemoteItemIdsJson),SubmitState=$submit,Status=$status,UpdatedAt=$updated WHERE Id=$id AND ConfirmationDescriptorJson IS NOT NULL AND SubmitState IN ('Submitting','PendingConfirmation');");
        Add(c,"$request",response.AdapterRequestId); Add(c,"$remote",response.RemoteItemIdsJson); Add(c,"$submit",response.SubmitState); Add(c,"$status",response.Status); Add(c,"$updated",Time(response.UpdatedAt)); Add(c,"$id",Id(response.OperationId));
        if (await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new InvalidOperationException("Operation has not crossed a prepared submit boundary.");
    }

    public async Task AddAllocationsAsync(IEnumerable<AccountAllocationRecord> allocations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allocations);
        foreach (var x in allocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var c = Command("INSERT INTO AccountAllocations(Id,BatchJobId,AccountId,PriorityOrder,DefaultTargetPath,NormalizedDefaultTargetPath,TargetItemCount,TargetSuccessCount,EffectiveTargetSuccessCount,AssignedItemCount,AttemptedItemCount,NewSuccessItemCount,AlreadySatisfiedItemCount,SuccessItemCount,FailedItemCount,LockedItemCount,ExclusiveCandidateCount,UnusedCandidateCount,ReservedSlotCount,FulfilledCount) VALUES($id,$job,$account,$priority,$target,$normalized,$targetItems,$targetSuccess,$effective,$assigned,$attempted,$newSuccess,$satisfied,$success,$failed,$locked,$exclusive,$unused,$reserved,$fulfilled);");
            Add(c,"$id",Id(x.Id)); Add(c,"$job",Id(x.BatchJobId)); Add(c,"$account",Id(x.AccountId)); Add(c,"$priority",x.PriorityOrder); Add(c,"$target",x.DefaultTargetPath); Add(c,"$normalized",x.NormalizedDefaultTargetPath); Add(c,"$targetItems",x.TargetItemCount); Add(c,"$targetSuccess",x.TargetSuccessCount); Add(c,"$effective",x.EffectiveTargetSuccessCount); Add(c,"$assigned",x.AssignedItemCount); Add(c,"$attempted",x.AttemptedItemCount); Add(c,"$newSuccess",x.NewSuccessItemCount); Add(c,"$satisfied",x.AlreadySatisfiedItemCount); Add(c,"$success",x.SuccessItemCount); Add(c,"$failed",x.FailedItemCount); Add(c,"$locked",x.LockedItemCount); Add(c,"$exclusive",x.ExclusiveCandidateCount); Add(c,"$unused",x.UnusedCandidateCount); Add(c,"$reserved",x.ReservedSlotCount); Add(c,"$fulfilled",x.FulfilledCount);
            await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateAllocationsAsync(IEnumerable<AccountAllocationRecord> allocations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allocations);
        foreach (var x in allocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var c = Command("UPDATE AccountAllocations SET PriorityOrder=$priority,DefaultTargetPath=$target,NormalizedDefaultTargetPath=$normalized,TargetItemCount=$targetItems,TargetSuccessCount=$targetSuccess,EffectiveTargetSuccessCount=$effective,AssignedItemCount=$assigned,AttemptedItemCount=$attempted,NewSuccessItemCount=$newSuccess,AlreadySatisfiedItemCount=$satisfied,SuccessItemCount=$success,FailedItemCount=$failed,LockedItemCount=$locked,ExclusiveCandidateCount=$exclusive,UnusedCandidateCount=$unused,ReservedSlotCount=$reserved,FulfilledCount=$fulfilled WHERE Id=$id AND BatchJobId=$job AND AccountId=$account;");
            Add(c,"$id",Id(x.Id)); Add(c,"$job",Id(x.BatchJobId)); Add(c,"$account",Id(x.AccountId)); Add(c,"$priority",x.PriorityOrder); Add(c,"$target",x.DefaultTargetPath); Add(c,"$normalized",x.NormalizedDefaultTargetPath); Add(c,"$targetItems",x.TargetItemCount); Add(c,"$targetSuccess",x.TargetSuccessCount); Add(c,"$effective",x.EffectiveTargetSuccessCount); Add(c,"$assigned",x.AssignedItemCount); Add(c,"$attempted",x.AttemptedItemCount); Add(c,"$newSuccess",x.NewSuccessItemCount); Add(c,"$satisfied",x.AlreadySatisfiedItemCount); Add(c,"$success",x.SuccessItemCount); Add(c,"$failed",x.FailedItemCount); Add(c,"$locked",x.LockedItemCount); Add(c,"$exclusive",x.ExclusiveCandidateCount); Add(c,"$unused",x.UnusedCandidateCount); Add(c,"$reserved",x.ReservedSlotCount); Add(c,"$fulfilled",x.FulfilledCount);
            if (await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The account allocation no longer exists.");
        }
    }

    public async Task<Page<AccountAllocationRecord>> GetAllocationsPageAsync(Guid batchJobId, PageQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var total = await CountAsync("AccountAllocations",batchJobId,cancellationToken).ConfigureAwait(false);
        await using var c = Command("SELECT Id,BatchJobId,AccountId,PriorityOrder,DefaultTargetPath,NormalizedDefaultTargetPath,TargetItemCount,TargetSuccessCount,EffectiveTargetSuccessCount,AssignedItemCount,AttemptedItemCount,NewSuccessItemCount,AlreadySatisfiedItemCount,SuccessItemCount,FailedItemCount,LockedItemCount,ExclusiveCandidateCount,UnusedCandidateCount,ReservedSlotCount,FulfilledCount FROM AccountAllocations WHERE BatchJobId=$job ORDER BY PriorityOrder,Id LIMIT $limit OFFSET $offset;"); AddPage(c,batchJobId,query);
        var items = new List<AccountAllocationRecord>(); await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(new(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),Guid.Parse(r.GetString(2)),r.GetInt32(3),r.GetString(4),r.GetString(5),r.GetInt32(6),r.GetInt32(7),r.GetInt32(8),r.GetInt32(9),r.GetInt32(10),r.GetInt32(11),r.GetInt32(12),r.GetInt32(13),r.GetInt32(14),r.GetInt32(15),r.GetInt32(16),r.GetInt32(17),r.GetInt32(18),r.GetInt32(19)));
        return new(items,total,query.PageNumber,query.PageSize);
    }

    public async Task AddExecutionLogsAsync(IEnumerable<ExecutionLogRecord> logs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logs);
        foreach (var x in logs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var c = Command("INSERT INTO ExecutionLogs(Id,BatchJobId,ResourceItemId,TransferOperationId,AccountId,Stage,Level,EventCode,SanitizedMessage,CreatedAt) VALUES($id,$job,$resource,$operation,$account,$stage,$level,$code,$message,$created);");
            Add(c,"$id",Id(x.Id)); Add(c,"$job",NullableId(x.BatchJobId)); Add(c,"$resource",NullableId(x.ResourceItemId)); Add(c,"$operation",NullableId(x.TransferOperationId)); Add(c,"$account",NullableId(x.AccountId)); Add(c,"$stage",x.Stage); Add(c,"$level",x.Level); Add(c,"$code",x.EventCode); Add(c,"$message",x.SanitizedMessage); Add(c,"$created",Time(x.CreatedAt));
            await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<long> CountAsync(string table, Guid batchJobId, CancellationToken cancellationToken)
    {
        var sql = table switch { "ResourceItems" => "SELECT COUNT(*) FROM ResourceItems WHERE BatchJobId=$job;", "TransferOperations" => "SELECT COUNT(*) FROM TransferOperations WHERE BatchJobId=$job;", "AccountAllocations" => "SELECT COUNT(*) FROM AccountAllocations WHERE BatchJobId=$job;", _ => throw new ArgumentOutOfRangeException(nameof(table)) };
        await using var c = Command(sql); Add(c,"$job",Id(batchJobId)); return Convert.ToInt64(await c.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static void AddPage(SqliteCommand c, Guid job, PageQuery query) { Add(c,"$job",Id(job)); Add(c,"$limit",query.PageSize); Add(c,"$offset",query.Offset); }
}
