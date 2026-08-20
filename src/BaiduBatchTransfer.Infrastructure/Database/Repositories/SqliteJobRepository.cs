using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database.Repositories;

public sealed class SqliteJobRepository : SqliteRepositoryBase, IJobRepository
{
    internal SqliteJobRepository(SqliteConnection connection, SqliteTransaction transaction) : base(connection, transaction) { }

    public async Task AddAsync(BatchJobRecord j, CancellationToken cancellationToken = default)
    {
        await using var c = Command("INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,StartedAt,CompletedAt,ImportSessionId,QueueOrder,ShareOperationCount) VALUES($id,$name,$source,$mode,$quota,$shortage,$resources,$operations,$target,$folder,$duplicate,$validation,$status,$created,$started,$completed,$import,$queue,$shares);");
        Add(c,"$id",Id(j.Id)); Add(c,"$name",j.Name); Add(c,"$source",j.SourceType); Add(c,"$mode",j.TransferMode); Add(c,"$quota",j.QuotaBasis); Add(c,"$shortage",j.ShortagePolicy); Add(c,"$resources",j.ResourceCount); Add(c,"$operations",j.OperationCount); Add(c,"$target",j.DefaultTargetPath); Add(c,"$folder",j.FolderCreationMode); Add(c,"$duplicate",j.DuplicatePolicy); Add(c,"$validation",j.ValidationMode); Add(c,"$status",j.Status); Add(c,"$created",Time(j.CreatedAt)); Add(c,"$started",NullableTime(j.StartedAt)); Add(c,"$completed",NullableTime(j.CompletedAt)); Add(c,"$import",NullableId(j.ImportSessionId)); Add(c,"$queue",j.QueueOrder); Add(c,"$shares",j.ShareOperationCount);
        await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(BatchJobRecord j, CancellationToken cancellationToken = default)
    {
        await using var c = Command("UPDATE BatchJobs SET Name=$name,SourceType=$source,TransferMode=$mode,QuotaBasis=$quota,ShortagePolicy=$shortage,ResourceCount=$resources,OperationCount=$operations,ShareOperationCount=$shares,DefaultTargetPath=$target,FolderCreationMode=$folder,DuplicatePolicy=$duplicate,ValidationMode=$validation,Status=$status,StartedAt=$started,CompletedAt=$completed,ImportSessionId=$import,QueueOrder=$queue WHERE Id=$id;");
        Add(c,"$id",Id(j.Id)); Add(c,"$name",j.Name); Add(c,"$source",j.SourceType); Add(c,"$mode",j.TransferMode); Add(c,"$quota",j.QuotaBasis); Add(c,"$shortage",j.ShortagePolicy); Add(c,"$resources",j.ResourceCount); Add(c,"$operations",j.OperationCount); Add(c,"$shares",j.ShareOperationCount); Add(c,"$target",j.DefaultTargetPath); Add(c,"$folder",j.FolderCreationMode); Add(c,"$duplicate",j.DuplicatePolicy); Add(c,"$validation",j.ValidationMode); Add(c,"$status",j.Status); Add(c,"$started",NullableTime(j.StartedAt)); Add(c,"$completed",NullableTime(j.CompletedAt)); Add(c,"$import",NullableId(j.ImportSessionId)); Add(c,"$queue",j.QueueOrder);
        if (await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The batch no longer exists.");
    }

    public async Task<BatchJobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var c = Command("SELECT Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,StartedAt,CompletedAt,ImportSessionId,QueueOrder,ShareOperationCount FROM BatchJobs WHERE Id=$id;"); Add(c,"$id",Id(id));
        await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await r.ReadAsync(cancellationToken).ConfigureAwait(false) ? new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt32(6),r.GetInt32(7),r.GetString(8),r.GetString(9),r.GetString(10),r.GetString(11),r.GetString(12),ReadTime(r,13),ReadNullableTime(r,14),ReadNullableTime(r,15),ReadNullableId(r,16),r.GetInt64(17),r.GetInt32(18)) : null;
    }

    public async Task AddResourcesAsync(IEnumerable<ResourceItemRecord> resources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        await using var c = Command("INSERT INTO ResourceItems(Id,BatchJobId,StagedImportRowId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,Note,TargetPathOverride,NormalizedTargetPathOverride,CreateFolderName,FolderCreationModeOverride,GlobalValidityStatus,CandidateConstraintAccountId,CandidateStatus,ReservedAccountId,ReservationExpiresAt,CreatedAt,UpdatedAt,ResolvedOutputTitle) VALUES($id,$job,$staged,$sequence,$url,$share,$code,$note,$target,$normalizedTarget,$folder,$folderMode,$validity,$constraint,$status,$reserved,$expires,$created,$updated,$title);");
        var id = c.Parameters.Add("$id", SqliteType.Text);
        var job = c.Parameters.Add("$job", SqliteType.Text);
        var staged = c.Parameters.Add("$staged", SqliteType.Text);
        var sequence = c.Parameters.Add("$sequence", SqliteType.Integer);
        var url = c.Parameters.Add("$url", SqliteType.Text);
        var share = c.Parameters.Add("$share", SqliteType.Text);
        var code = c.Parameters.Add("$code", SqliteType.Text);
        var note = c.Parameters.Add("$note", SqliteType.Text);
        var target = c.Parameters.Add("$target", SqliteType.Text);
        var normalizedTarget = c.Parameters.Add("$normalizedTarget", SqliteType.Text);
        var folder = c.Parameters.Add("$folder", SqliteType.Text);
        var folderMode = c.Parameters.Add("$folderMode", SqliteType.Text);
        var validity = c.Parameters.Add("$validity", SqliteType.Text);
        var constraint = c.Parameters.Add("$constraint", SqliteType.Text);
        var status = c.Parameters.Add("$status", SqliteType.Text);
        var reserved = c.Parameters.Add("$reserved", SqliteType.Text);
        var expires = c.Parameters.Add("$expires", SqliteType.Text);
        var created = c.Parameters.Add("$created", SqliteType.Text);
        var updated = c.Parameters.Add("$updated", SqliteType.Text);
        var title = c.Parameters.Add("$title", SqliteType.Text);
        c.Prepare();
        foreach (var x in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            id.Value = Id(x.Id); job.Value = Id(x.BatchJobId); staged.Value = NullableId(x.StagedImportRowId);
            sequence.Value = x.CandidateSequence; url.Value = x.ShareUrl; share.Value = x.NormalizedShareId;
            code.Value = x.ExtractionCode ?? (object)DBNull.Value; note.Value = x.Note ?? (object)DBNull.Value;
            target.Value = x.TargetPathOverride ?? (object)DBNull.Value;
            normalizedTarget.Value = x.NormalizedTargetPathOverride ?? (object)DBNull.Value;
            folder.Value = x.CreateFolderName ?? (object)DBNull.Value; folderMode.Value = x.FolderCreationModeOverride;
            validity.Value = x.GlobalValidityStatus; constraint.Value = NullableId(x.CandidateConstraintAccountId);
            status.Value = x.CandidateStatus; reserved.Value = NullableId(x.ReservedAccountId);
            expires.Value = NullableTime(x.ReservationExpiresAt); created.Value = Time(x.CreatedAt); updated.Value = Time(x.UpdatedAt);
            title.Value = x.ResolvedOutputTitle ?? (object)DBNull.Value;
            await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Page<ResourceItemRecord>> GetResourcesPageAsync(Guid batchJobId, PageQuery query, CancellationToken cancellationToken = default)
    {
        query.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var total = await CountAsync("ResourceItems", batchJobId, cancellationToken).ConfigureAwait(false);
        await using var c = Command("SELECT Id,BatchJobId,StagedImportRowId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,Note,TargetPathOverride,NormalizedTargetPathOverride,CreateFolderName,FolderCreationModeOverride,GlobalValidityStatus,CandidateConstraintAccountId,CandidateStatus,ReservedAccountId,ReservationExpiresAt,CreatedAt,UpdatedAt,ResolvedOutputTitle FROM ResourceItems WHERE BatchJobId=$job ORDER BY CandidateSequence,Id LIMIT $limit OFFSET $offset;"); AddPage(c,batchJobId,query);
        var items = new List<ResourceItemRecord>(); await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false)) items.Add(new(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),ReadNullableId(r,2),r.GetInt64(3),r.GetString(4),r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:r.GetString(9),r.IsDBNull(10)?null:r.GetString(10),r.GetString(11),r.GetString(12),ReadNullableId(r,13),r.GetString(14),ReadNullableId(r,15),ReadNullableTime(r,16),ReadTime(r,17),ReadTime(r,18),r.IsDBNull(19)?null:r.GetString(19)));
        return new(items,total,query.PageNumber,query.PageSize);
    }

    public async Task AddOperationsAsync(IEnumerable<TransferOperationRecord> operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        await using var c = Command("INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,AdapterRequestId,RemoteItemIdsJson,SubmitState,TargetSlotState,TargetSlotReservedAt,DailyCountedAt,SubmitStartedAt,LastErrorCode,LastErrorMessage,LeaseExpiresAt,CreatedAt,UpdatedAt) VALUES($id,$resource,$job,$sequence,$ordinal,$target,$normalized,$folder,$account,$source,$status,$outcome,$attempt,$fingerprint,$confirmation,$request,$remote,$submit,$slot,$reserved,$counted,$started,$errorCode,$errorMessage,$lease,$created,$updated);");
        var id = c.Parameters.Add("$id", SqliteType.Text);
        var resource = c.Parameters.Add("$resource", SqliteType.Text);
        var job = c.Parameters.Add("$job", SqliteType.Text);
        var sequence = c.Parameters.Add("$sequence", SqliteType.Integer);
        var ordinal = c.Parameters.Add("$ordinal", SqliteType.Integer);
        var target = c.Parameters.Add("$target", SqliteType.Text);
        var normalized = c.Parameters.Add("$normalized", SqliteType.Text);
        var folder = c.Parameters.Add("$folder", SqliteType.Text);
        var account = c.Parameters.Add("$account", SqliteType.Text);
        var source = c.Parameters.Add("$source", SqliteType.Text);
        var status = c.Parameters.Add("$status", SqliteType.Text);
        var outcome = c.Parameters.Add("$outcome", SqliteType.Text);
        var attempt = c.Parameters.Add("$attempt", SqliteType.Integer);
        var fingerprint = c.Parameters.Add("$fingerprint", SqliteType.Text);
        var confirmation = c.Parameters.Add("$confirmation", SqliteType.Text);
        var request = c.Parameters.Add("$request", SqliteType.Text);
        var remote = c.Parameters.Add("$remote", SqliteType.Text);
        var submit = c.Parameters.Add("$submit", SqliteType.Text);
        var slot = c.Parameters.Add("$slot", SqliteType.Text);
        var reserved = c.Parameters.Add("$reserved", SqliteType.Text);
        var counted = c.Parameters.Add("$counted", SqliteType.Text);
        var started = c.Parameters.Add("$started", SqliteType.Text);
        var errorCode = c.Parameters.Add("$errorCode", SqliteType.Text);
        var errorMessage = c.Parameters.Add("$errorMessage", SqliteType.Text);
        var lease = c.Parameters.Add("$lease", SqliteType.Text);
        var created = c.Parameters.Add("$created", SqliteType.Text);
        var updated = c.Parameters.Add("$updated", SqliteType.Text);
        c.Prepare();
        foreach (var x in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            id.Value = Id(x.Id); resource.Value = Id(x.ResourceItemId); job.Value = Id(x.BatchJobId);
            sequence.Value = x.SequenceNumber; ordinal.Value = x.AllocationOrdinal; target.Value = x.TargetPath;
            normalized.Value = x.NormalizedTargetPath; folder.Value = x.CreateFolderName; account.Value = Id(x.AssignedAccountId);
            source.Value = x.AllocationSource; status.Value = x.Status; outcome.Value = x.OutcomeKind ?? (object)DBNull.Value;
            attempt.Value = x.AttemptCount; fingerprint.Value = x.OperationFingerprint;
            confirmation.Value = x.ConfirmationDescriptorJson ?? (object)DBNull.Value;
            request.Value = x.AdapterRequestId ?? (object)DBNull.Value; remote.Value = x.RemoteItemIdsJson ?? (object)DBNull.Value;
            submit.Value = x.SubmitState; slot.Value = x.TargetSlotState; reserved.Value = NullableTime(x.TargetSlotReservedAt);
            counted.Value = NullableTime(x.DailyCountedAt); started.Value = NullableTime(x.SubmitStartedAt);
            errorCode.Value = x.LastErrorCode ?? (object)DBNull.Value; errorMessage.Value = x.LastErrorMessage ?? (object)DBNull.Value;
            lease.Value = NullableTime(x.LeaseExpiresAt); created.Value = Time(x.CreatedAt); updated.Value = Time(x.UpdatedAt);
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

    public async Task WriteAuditAsync(AuditEventRecord audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        var (eventCode, message) = audit.EventKind switch
        {
            AuditEventKind.BatchCreated => ("BATCH_CREATED", "Batch creation was confirmed."),
            AuditEventKind.CandidatesAppended => ("CANDIDATES_APPENDED", "Candidate append was confirmed."),
            AuditEventKind.ShortfallReopened => ("SHORTFALL_REOPENED", "Shortfall batch reopen was confirmed."),
            _ => throw new ArgumentOutOfRangeException(nameof(audit), "The audit event is not allowlisted."),
        };
        await using var c = Command("INSERT INTO ExecutionLogs(Id,BatchJobId,ResourceItemId,TransferOperationId,AccountId,Stage,Level,EventCode,SanitizedMessage,CreatedAt) VALUES($id,$job,$resource,$operation,$account,'Finalization','Information',$code,$message,$created);");
        Add(c,"$id",Id(audit.Id)); Add(c,"$job",NullableId(audit.BatchJobId)); Add(c,"$resource",NullableId(audit.ResourceItemId)); Add(c,"$operation",NullableId(audit.TransferOperationId)); Add(c,"$account",NullableId(audit.AccountId)); Add(c,"$code",eventCode); Add(c,"$message",message); Add(c,"$created",Time(audit.CreatedAt));
        await c.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionLogRecord?> GetExecutionLogAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var c = Command("SELECT Id,BatchJobId,ResourceItemId,TransferOperationId,AccountId,Stage,Level,EventCode,SanitizedMessage,CreatedAt FROM ExecutionLogs WHERE Id=$id;");
        Add(c,"$id",Id(id));
        await using var r = await c.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await r.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ExecutionLogRecord(
                Guid.Parse(r.GetString(0)), ReadNullableId(r,1), ReadNullableId(r,2), ReadNullableId(r,3),
                ReadNullableId(r,4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), ReadTime(r,9))
            : null;
    }

    public async Task<KeysetPage<ResourceItemRecord>> GetResourceResultsKeysetPageAsync(
        ResourceResultKeysetQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var statusFilter = query.CandidateStatus is null ? string.Empty : " AND CandidateStatus=$candidateStatus";
        var validityFilter = query.GlobalValidityStatus is null ? string.Empty : " AND GlobalValidityStatus=$validity";
        var filter = statusFilter + validityFilter;
        await using var count = Command("SELECT COUNT(*) FROM ResourceItems WHERE BatchJobId=$job" + filter + ";");
        Add(count, "$job", Id(query.BatchJobId));
        if (query.CandidateStatus is not null) Add(count, "$candidateStatus", query.CandidateStatus);
        if (query.GlobalValidityStatus is not null) Add(count, "$validity", query.GlobalValidityStatus);
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        await using var command = Command(
            "SELECT Id,BatchJobId,StagedImportRowId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,Note,TargetPathOverride,NormalizedTargetPathOverride,CreateFolderName,FolderCreationModeOverride,GlobalValidityStatus,CandidateConstraintAccountId,CandidateStatus,ReservedAccountId,ReservationExpiresAt,CreatedAt,UpdatedAt,ResolvedOutputTitle " +
            "FROM ResourceItems WHERE BatchJobId=$job" + filter +
            " AND CandidateSequence>$after ORDER BY CandidateSequence,Id LIMIT $limit;");
        Add(command, "$job", Id(query.BatchJobId));
        if (query.CandidateStatus is not null) Add(command, "$candidateStatus", query.CandidateStatus);
        if (query.GlobalValidityStatus is not null) Add(command, "$validity", query.GlobalValidityStatus);
        Add(command, "$after", query.AfterCandidateSequence);
        Add(command, "$limit", checked(query.PageSize + 1));
        var items = new List<ResourceItemRecord>(query.PageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), ReadNullableId(reader, 2),
                reader.GetInt64(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11), reader.GetString(12),
                ReadNullableId(reader, 13), reader.GetString(14), ReadNullableId(reader, 15),
                ReadNullableTime(reader, 16), ReadTime(reader, 17), ReadTime(reader, 18),
                reader.IsDBNull(19) ? null : reader.GetString(19)));
        }
        var hasMore = items.Count > query.PageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new(items, total, hasMore ? items[^1].CandidateSequence : null);
    }

    public async Task<KeysetPage<OperationResultRecord>> GetOperationResultsKeysetPageAsync(
        OperationResultKeysetQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var accountFilter = query.AccountId is null ? string.Empty : " AND o.AssignedAccountId=$account";
        var statusFilter = query.Status is null ? string.Empty : " AND o.Status=$status";
        var fromFilter = query.UpdatedFrom is null ? string.Empty : " AND julianday(o.UpdatedAt)>=julianday($from)";
        var toFilter = query.UpdatedTo is null ? string.Empty : " AND julianday(o.UpdatedAt)<=julianday($to)";
        var filter = accountFilter + statusFilter + fromFilter + toFilter;
        var pageIndex = query.AccountId is not null
            ? "IX_TransferOperations_Result_Account_Keyset"
            : query.Status is not null
                ? "IX_TransferOperations_Batch_Status_AllocationOrdinal"
                : "IX_TransferOperations_Batch_AllocationOrdinal";
        long? total = null;
        if (query.IncludeTotalCount)
        {
            await using var count = Command("SELECT COUNT(*) FROM TransferOperations o WHERE o.BatchJobId=$job" + filter + ";");
            BindOperationFilters(count, query);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        await using var command = Command(
            "SELECT o.Id,o.ResourceItemId,o.BatchJobId,r.CandidateSequence,o.AllocationOrdinal,r.ShareUrl,r.Note," +
            "o.AssignedAccountId,a.DisplayName,o.TargetPath,o.Status,o.OutcomeKind,o.LastErrorCode,o.LastErrorMessage,o.UpdatedAt " +
            $"FROM TransferOperations o INDEXED BY {pageIndex} JOIN ResourceItems r ON r.Id=o.ResourceItemId JOIN Accounts a ON a.Id=o.AssignedAccountId " +
            "WHERE o.BatchJobId=$job" + filter +
            " AND o.AllocationOrdinal>$after ORDER BY o.AllocationOrdinal,o.SequenceNumber,o.Id LIMIT $limit;");
        BindOperationFilters(command, query);
        Add(command, "$after", query.AfterAllocationOrdinal);
        Add(command, "$limit", checked(query.PageSize + 1));
        var items = new List<OperationResultRecord>(query.PageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), Guid.Parse(reader.GetString(7)),
                reader.GetString(8), reader.GetString(9), reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13), ReadTime(reader, 14)));
        }
        var hasMore = items.Count > query.PageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new(items, total, hasMore ? items[^1].AllocationOrdinal : null);
    }

    public async Task<BatchStatisticsRecord> GetBatchStatisticsAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        var resourceCount = await CountAsync("ResourceItems", batchJobId, cancellationToken).ConfigureAwait(false);
        var operationCount = await CountAsync("TransferOperations", batchJobId, cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            "SELECT al.AccountId,a.DisplayName,al.PriorityOrder," +
            "COALESCE(SUM(CASE WHEN o.Status='Succeeded' AND o.OutcomeKind='NewTransfer' THEN 1 ELSE 0 END),0)," +
            "COALESCE(SUM(CASE WHEN o.Status='Succeeded' AND o.OutcomeKind='AlreadySatisfied' THEN 1 ELSE 0 END),0)," +
            "COALESCE(SUM(CASE WHEN o.Status IN ('Failed','Cancelled') OR o.OutcomeKind IN ('Failed','GloballyInvalid','Cancelled') THEN 1 ELSE 0 END),0)," +
            "COALESCE(SUM(CASE WHEN o.Status IN ('Waiting','Reserved','Preparing','Submitting','ResultPending') THEN 1 ELSE 0 END),0) " +
            "FROM AccountAllocations al JOIN Accounts a ON a.Id=al.AccountId " +
            "LEFT JOIN TransferOperations o ON o.BatchJobId=al.BatchJobId AND o.AssignedAccountId=al.AccountId " +
            "WHERE al.BatchJobId=$job GROUP BY al.AccountId,a.DisplayName,al.PriorityOrder " +
            "ORDER BY al.PriorityOrder,al.AccountId;");
        Add(command, "$job", Id(batchJobId));
        var accounts = new List<AccountBatchStatisticsRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            accounts.Add(new(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
        }
        return new(batchJobId, resourceCount, operationCount, accounts);
    }

    public async Task SetShareSettingsAsync(BatchShareSettingsRecord settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.BatchJobId == Guid.Empty || settings.ExtractionCodeMode is not ("Random" or "Fixed") ||
            settings.ExistingSharePolicy is not ("Ask" or "Reuse" or "Regenerate" or "Cancel") ||
            string.IsNullOrWhiteSpace(settings.SettingsFingerprint) ||
            (settings.ExtractionCodeMode == "Random") != (settings.FixedExtractionCode is null))
            throw new ArgumentException("The batch share settings are invalid.", nameof(settings));
        var behavior = settings.ExtractionCodeMode == "Random"
            ? ShareExtractionCodeBehavior.Random
            : ShareExtractionCodeBehavior.Fixed;
        if (settings.FixedExtractionCode is not null &&
            !string.Equals(settings.FixedExtractionCode, settings.FixedExtractionCode.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("The fixed extraction code must be canonical.", nameof(settings));
        var canonicalFingerprint = ShareSettingsFingerprint.Compute(
            settings.IsPermanent,
            settings.IsAccessUnlimited,
            behavior,
            settings.FixedExtractionCode);
        if (!string.Equals(canonicalFingerprint, settings.SettingsFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("The settings fingerprint is not canonical.", nameof(settings));
        await using var command = Command("""
            INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,FixedExtractionCode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
            VALUES($batch,$permanent,$unlimited,$mode,$code,$policy,$fingerprint,$created)
            ON CONFLICT(BatchJobId) DO NOTHING;
            """);
        Add(command,"$batch",Id(settings.BatchJobId)); Add(command,"$permanent",settings.IsPermanent ? 1 : 0);
        Add(command,"$unlimited",settings.IsAccessUnlimited ? 1 : 0); Add(command,"$mode",settings.ExtractionCodeMode);
        Add(command,"$code",settings.FixedExtractionCode); Add(command,"$policy",settings.ExistingSharePolicy);
        Add(command,"$fingerprint",settings.SettingsFingerprint);
        Add(command,"$created",Time(settings.CreatedAt));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) return;
        if (await GetShareSettingsAsync(settings.BatchJobId, cancellationToken).ConfigureAwait(false) == settings) return;
        throw new InvalidOperationException("The confirmed batch share settings cannot be changed.");
    }

    public async Task<BatchShareSettingsRecord?> GetShareSettingsAsync(Guid batchJobId, CancellationToken cancellationToken = default)
    {
        await using var command = Command("SELECT BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,FixedExtractionCode,ExistingSharePolicy,CreatedAt,SettingsFingerprint FROM BatchShareSettings WHERE BatchJobId=$batch;");
        Add(command,"$batch",Id(batchJobId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(Guid.Parse(reader.GetString(0)),reader.GetInt64(1)!=0,reader.GetInt64(2)!=0,reader.GetString(3),
                reader.IsDBNull(4)?null:reader.GetString(4),reader.GetString(5),ReadTime(reader,6),reader.GetString(7))
            : null;
    }

    public async Task AddOutputAccountsAsync(IEnumerable<BatchOutputAccountRecord> accounts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var snapshot = accounts.OrderBy(value => value.OutputOrdinal).ThenBy(value => value.AccountId).ToArray();
        if (snapshot.Length == 0) return;
        var batchJobId = snapshot[0].BatchJobId;
        if (batchJobId == Guid.Empty || snapshot.Any(value => value.BatchJobId != batchJobId || value.AccountId == Guid.Empty) ||
            snapshot.Select(value => value.AccountId).Distinct().Count() != snapshot.Length ||
            snapshot.Select(value => value.OutputOrdinal).Distinct().Count() != snapshot.Length ||
            snapshot.Select(value => value.OutputLabel).Distinct(StringComparer.Ordinal).Count() != snapshot.Length ||
            snapshot.Any(value => value.OutputOrdinal <= 0 || string.IsNullOrWhiteSpace(value.OutputLabel) ||
                value.PlannedTransferCount < 0 || value.PlannedShareCount < 0 ||
                value.PlannedShareCount > value.PlannedTransferCount ||
                (value.ShareEnabled ? value.PlannedShareCount <= 0 : value.PlannedShareCount != 0)))
            throw new ArgumentException("The batch output-account snapshot is invalid.", nameof(accounts));
        var existing = await GetOutputAccountsAsync(batchJobId, cancellationToken).ConfigureAwait(false);
        if (existing.Count != 0)
        {
            if (existing.SequenceEqual(snapshot)) return;
            throw new InvalidOperationException("The confirmed batch output-account snapshot cannot be changed.");
        }
        await using var command = Command("INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled) VALUES($batch,$account,$ordinal,$label,$transfers,$shares,$enabled);");
        foreach (var value in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.Parameters.Clear();
            Add(command,"$batch",Id(value.BatchJobId)); Add(command,"$account",Id(value.AccountId));
            Add(command,"$ordinal",value.OutputOrdinal); Add(command,"$label",value.OutputLabel);
            Add(command,"$transfers",value.PlannedTransferCount); Add(command,"$shares",value.PlannedShareCount);
            Add(command,"$enabled",value.ShareEnabled ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<BatchOutputAccountRecord>> GetOutputAccountsAsync(Guid batchJobId, CancellationToken cancellationToken = default)
    {
        await using var command = Command("SELECT BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled FROM BatchOutputAccounts WHERE BatchJobId=$batch ORDER BY OutputOrdinal,AccountId;");
        Add(command,"$batch",Id(batchJobId));
        var values = new List<BatchOutputAccountRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(new(Guid.Parse(reader.GetString(0)),Guid.Parse(reader.GetString(1)),reader.GetInt32(2),reader.GetString(3),reader.GetInt32(4),reader.GetInt32(5),reader.GetInt64(6)!=0));
        return values;
    }

    public async Task AddShareOperationsAsync(
        IEnumerable<ShareOperationRecord> operations,
        IEnumerable<ShareOperationRemoteItemRecord> remoteItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(remoteItems);
        var operationSnapshot = TakeBounded(operations, 500, nameof(operations));
        var itemSnapshot = TakeBounded(remoteItems, 1000, nameof(remoteItems));
        var itemsByOperation = ValidateExactRemoteItems(operationSnapshot, itemSnapshot);
        await ValidateShareOutputsAsync(operationSnapshot, cancellationToken).ConfigureAwait(false);
        await using (var command = Command("""
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,
              ExactItemSetFingerprint,SettingsFingerprint,Generation,RequestDescriptorJson,AdapterRequestId,SubmissionAcceptance,AttemptCount,
              LeaseGeneration,LeaseExpiresAt,SubmitStartedAt,NextReconcileAt,LastErrorCode,LastErrorMessage,CreatedAt,UpdatedAt)
            VALUES($id,$transfer,$resource,$batch,$account,$ordinal,$status,$fingerprint,$settings,$generation,$descriptor,$request,
              $acceptance,$attempts,$leaseGeneration,$lease,$submitted,$reconcile,$errorCode,$errorMessage,$created,$updated);
            """))
        {
            var prepared = false;
            foreach (var value in operationSnapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BindShareOperation(command, value, add: !prepared);
                if (!prepared)
                {
                    command.Prepare();
                    prepared = true;
                }
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await using var item = Command("INSERT INTO ShareOperationRemoteItems(ShareOperationId,ItemOrdinal,RemoteItemId,DisplayName) VALUES($operation,$ordinal,$remote,$name);");
        var itemPrepared = false;
        foreach (var operation in operationSnapshot)
        {
            foreach (var value in itemsByOperation[operation.Id])
            {
                cancellationToken.ThrowIfCancellationRequested();
                Bind(item,"$operation",Id(value.ShareOperationId),!itemPrepared);
                Bind(item,"$ordinal",value.ItemOrdinal,!itemPrepared);
                Bind(item,"$remote",value.RemoteItemId,!itemPrepared);
                Bind(item,"$name",value.DisplayName,!itemPrepared);
                if (!itemPrepared)
                {
                    item.Prepare();
                    itemPrepared = true;
                }
                await item.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static IReadOnlyDictionary<Guid, ShareOperationRemoteItemRecord[]> ValidateExactRemoteItems(
        IReadOnlyList<ShareOperationRecord> operations,
        IReadOnlyList<ShareOperationRemoteItemRecord> remoteItems)
    {
        var operationIds = operations.Select(value => value.Id).ToHashSet();
        if (operationIds.Count != operations.Count)
            throw new ArgumentException("Share operation identifiers must be unique.", nameof(operations));
        if (remoteItems.Any(value => !operationIds.Contains(value.ShareOperationId)))
            throw new ArgumentException("Every exact remote item must belong to the supplied share operations.", nameof(remoteItems));

        var grouped = remoteItems
            .GroupBy(value => value.ShareOperationId)
            .ToDictionary(group => group.Key, group => group.OrderBy(value => value.ItemOrdinal).ToArray());

        foreach (var operation in operations)
        {
            if (string.IsNullOrWhiteSpace(operation.SettingsFingerprint))
                throw new ArgumentException("Each share operation requires an immutable settings fingerprint.", nameof(operations));
            if (!grouped.TryGetValue(operation.Id, out var items))
                items = [];
            if (items.Length == 0 || items.Where((value, index) => value.ItemOrdinal != index).Any())
                throw new ArgumentException("Each share operation requires one contiguous exact remote-item set.", nameof(remoteItems));
            var fingerprint = new RemoteItemIdentitySet(items.Select(value =>
                new RemoteItemIdentity(value.RemoteItemId, value.DisplayName))).Fingerprint;
            if (!string.Equals(fingerprint, operation.ExactItemSetFingerprint, StringComparison.Ordinal))
                throw new ArgumentException("The exact remote-item set does not match its persisted fingerprint.", nameof(remoteItems));
        }

        return grouped;
    }

    private async Task ValidateShareOutputsAsync(
        IReadOnlyList<ShareOperationRecord> operations,
        CancellationToken cancellationToken)
    {
        foreach (var group in operations.GroupBy(value => (value.BatchJobId, value.AccountId, value.OutputOrdinal)))
        {
            await using var command = Command("""
                SELECT ShareEnabled,PlannedShareCount,CreatedShareOperationCount
                FROM BatchOutputAccounts output
                WHERE BatchJobId=$batch AND AccountId=$account AND OutputOrdinal=$ordinal;
                """);
            Add(command, "$batch", Id(group.Key.BatchJobId));
            Add(command, "$account", Id(group.Key.AccountId));
            Add(command, "$ordinal", group.Key.OutputOrdinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt64(0) != 1 ||
                reader.GetInt64(2) + group.Count() > reader.GetInt64(1))
                throw new InvalidOperationException("The confirmed output account cannot accept this share work.");
        }
    }

    private static T[] TakeBounded<T>(IEnumerable<T> source, int maximum, string parameterName)
    {
        var snapshot = source.Take(maximum + 1).ToArray();
        if (snapshot.Length > maximum)
            throw new ArgumentOutOfRangeException(parameterName, $"A single write chunk cannot exceed {maximum} records.");
        return snapshot;
    }

    private static void BindShareOperation(SqliteCommand command, ShareOperationRecord value, bool add)
    {
        Bind(command,"$id",Id(value.Id),add); Bind(command,"$transfer",Id(value.TransferOperationId),add);
        Bind(command,"$resource",Id(value.ResourceItemId),add); Bind(command,"$batch",Id(value.BatchJobId),add);
        Bind(command,"$account",Id(value.AccountId),add); Bind(command,"$ordinal",value.OutputOrdinal,add);
        Bind(command,"$status",value.Status,add); Bind(command,"$fingerprint",value.ExactItemSetFingerprint,add);
        Bind(command,"$settings",value.SettingsFingerprint,add); Bind(command,"$generation",value.Generation,add);
        Bind(command,"$descriptor",value.RequestDescriptorJson,add); Bind(command,"$request",value.AdapterRequestId,add);
        Bind(command,"$acceptance",value.SubmissionAcceptance,add); Bind(command,"$attempts",value.AttemptCount,add);
        Bind(command,"$leaseGeneration",value.LeaseGeneration,add); Bind(command,"$lease",NullableTime(value.LeaseExpiresAt),add);
        Bind(command,"$submitted",NullableTime(value.SubmitStartedAt),add); Bind(command,"$reconcile",NullableTime(value.NextReconcileAt),add);
        Bind(command,"$errorCode",value.LastErrorCode,add); Bind(command,"$errorMessage",value.LastErrorMessage,add);
        Bind(command,"$created",Time(value.CreatedAt),add); Bind(command,"$updated",Time(value.UpdatedAt),add);
    }

    private static void Bind(SqliteCommand command, string name, object? value, bool add)
    {
        if (add)
            Add(command, name, value);
        else
            command.Parameters[name].Value = value ?? DBNull.Value;
    }

    private static void BindOperationFilters(SqliteCommand command, OperationResultKeysetQuery query)
    {
        Add(command, "$job", Id(query.BatchJobId));
        if (query.AccountId is { } accountId) Add(command, "$account", Id(accountId));
        if (query.Status is not null) Add(command, "$status", query.Status);
        if (query.UpdatedFrom is { } from) Add(command, "$from", Time(from.ToUniversalTime()));
        if (query.UpdatedTo is { } to) Add(command, "$to", Time(to.ToUniversalTime()));
    }

    private async Task<long> CountAsync(string table, Guid batchJobId, CancellationToken cancellationToken)
    {
        var sql = table switch { "ResourceItems" => "SELECT COUNT(*) FROM ResourceItems WHERE BatchJobId=$job;", "TransferOperations" => "SELECT COUNT(*) FROM TransferOperations WHERE BatchJobId=$job;", "AccountAllocations" => "SELECT COUNT(*) FROM AccountAllocations WHERE BatchJobId=$job;", _ => throw new ArgumentOutOfRangeException(nameof(table)) };
        await using var c = Command(sql); Add(c,"$job",Id(batchJobId)); return Convert.ToInt64(await c.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static void AddPage(SqliteCommand c, Guid job, PageQuery query) { Add(c,"$job",Id(job)); Add(c,"$limit",query.PageSize); Add(c,"$offset",query.Offset); }
}
