using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Tests.Domain;

public sealed class StateTransitionTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = CreatedAt.AddMinutes(1);

    [Fact]
    public void Batch_CandidatesMustBecomeReadyBeforeReactivation()
    {
        var batch = CreateBatch();
        batch.Activate(Later);
        batch.WaitForCandidates();

        Assert.Throws<InvalidOperationException>(() => batch.Activate(Later.AddMinutes(1)));

        batch.MarkCandidatesReady();
        batch.Activate(Later.AddMinutes(2));

        Assert.Equal(BatchStatus.Active, batch.Status);
    }

    [Fact]
    public void Batch_CannotSkipFromQueuedToPausedReady()
    {
        var batch = CreateBatch();

        Assert.Throws<InvalidOperationException>(batch.MarkCandidatesReady);
        Assert.Equal(BatchStatus.Queued, batch.Status);
    }

    [Fact]
    public void CompletedWithShortfall_RequiresExplicitConfirmationBeforeReopening()
    {
        var batch = CreateBatch();
        batch.Activate(Later);
        batch.Complete(Later.AddMinutes(1), hasShortfall: true);

        Assert.Throws<InvalidOperationException>(
            () => batch.ReopenAfterShortfall(userConfirmed: false, Later.AddMinutes(2)));
        Assert.Equal(BatchStatus.CompletedWithShortfall, batch.Status);

        batch.ReopenAfterShortfall(userConfirmed: true, Later.AddMinutes(2));

        Assert.Equal(BatchStatus.PausedReady, batch.Status);
        batch.Activate(Later.AddMinutes(3));
        Assert.Equal(BatchStatus.Active, batch.Status);
    }

    [Fact]
    public void ReopenAfterShortfall_RejectsEveryOtherBatchStatus()
    {
        var queued = CreateBatch();
        Assert.Throws<InvalidOperationException>(
            () => queued.ReopenAfterShortfall(userConfirmed: true, Later));

        var completed = CreateBatch();
        completed.Activate(Later);
        completed.Complete(Later.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(
            () => completed.ReopenAfterShortfall(userConfirmed: true, Later.AddMinutes(2)));
    }

    [Fact]
    public void ReactivationTimestamp_CannotPrecedeOriginalStart()
    {
        var batch = CreateBatch();
        var originalStart = Later.AddMinutes(5);
        batch.Activate(originalStart);
        batch.Pause();

        Assert.Throws<ArgumentOutOfRangeException>(() => batch.Activate(Later));
        Assert.Equal(BatchStatus.Paused, batch.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparingOrSubmitting_CanBecomeResultPending(bool markSubmitting)
    {
        var operation = CreateReservedOperation();
        operation.MarkPreparing(Later);
        if (markSubmitting)
        {
            operation.MarkSubmitting(Later.AddSeconds(1));
        }

        operation.MarkResultPending("response timeout", Later.AddSeconds(2));

        Assert.Equal(OperationStatus.ResultPending, operation.Status);
        Assert.Equal(TargetSlotState.Reserved, operation.TargetSlotState);
    }

    [Fact]
    public void Operation_CanBeCancelledBeforeSubmissionBoundary()
    {
        var operation = CreateReservedOperation();
        operation.MarkPreparing(Later);

        operation.Cancel(Later.AddSeconds(1));

        Assert.Equal(OperationStatus.Cancelled, operation.Status);
        Assert.Equal(TargetSlotState.Released, operation.TargetSlotState);
    }

    [Fact]
    public void Operation_CannotBeCancelledOnceSubmissionMayHaveOccurred()
    {
        var operation = CreateReservedOperation();
        operation.MarkPreparing(Later);
        operation.MarkSubmitting(Later.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() => operation.Cancel(Later.AddSeconds(2)));

        operation.MarkResultPending("response timeout", Later.AddSeconds(3));
        Assert.Throws<InvalidOperationException>(() => operation.Cancel(Later.AddSeconds(4)));
    }

    [Fact]
    public void SubmittedDefinitiveFailure_RetryReusesIdentityAndRemainsNonCancellable()
    {
        var operation = CreateReservedOperation();
        var operationId = operation.Id;
        operation.MarkPreparing(Later);
        operation.MarkSubmitting(Later.AddSeconds(1));
        operation.MarkDefinitivelyFailed("quota", "Quota exceeded", Later.AddSeconds(2));

        operation.ResetForRetry(Later.AddSeconds(3));

        Assert.Equal(operationId, operation.Id);
        Assert.Equal(OperationStatus.Waiting, operation.Status);
        Assert.Equal(TargetSlotState.Released, operation.TargetSlotState);
        Assert.True(operation.SubmissionMayHaveOccurred);
        Assert.Throws<InvalidOperationException>(() => operation.Cancel(Later.AddSeconds(4)));
    }

    [Fact]
    public void IllegalOperationTransition_IsRejected()
    {
        var operation = CreateReservedOperation();

        Assert.Throws<InvalidOperationException>(() => operation.MarkSubmitting(Later));
        Assert.Equal(OperationStatus.Reserved, operation.Status);
    }

    [Fact]
    public void TombstoningAccount_RemovesSensitiveReferencesButKeepsHistoricalIdentity()
    {
        var accountId = Guid.Parse("90000000-0000-0000-0000-000000000001");
        var account = new BaiduAccount(
            accountId,
            "baidu-user-1",
            "Historical Display Name",
            "encrypted-session-reference",
            "profile-directory-reference",
            CreatedAt);

        account.Tombstone(Later);

        Assert.Equal(accountId, account.Id);
        Assert.Equal("baidu-user-1", account.BaiduUserId);
        Assert.Equal("Historical Display Name", account.DisplayName);
        Assert.Null(account.EncryptedSession);
        Assert.Null(account.WebView2ProfileDirectory);
        Assert.True(account.IsDeleted);
        Assert.Equal(Later, account.DeletedAt);
    }

    [Fact]
    public void DisabledAccount_RequiresExplicitReactivationBeforeActiveMutation()
    {
        var account = CreateAccount();
        account.Disable(Later);

        Assert.Equal(AccountStatus.Disabled, account.Status);
        Assert.Throws<InvalidOperationException>(
            () => account.Rename("Should not apply", Later.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(
            () => account.UpdateSessionReferences("new-session", "new-profile", Later.AddSeconds(1)));

        account.Reactivate(Later.AddSeconds(2));
        account.Rename("Reactivated", Later.AddSeconds(3));

        Assert.Equal(AccountStatus.Active, account.Status);
        Assert.Equal("Reactivated", account.DisplayName);
    }

    [Fact]
    public void InvalidAccount_RequiresExplicitReactivationAndRejectsInvalidSkips()
    {
        var account = CreateAccount();
        account.MarkInvalid(Later);

        Assert.Equal(AccountStatus.Invalid, account.Status);
        Assert.Throws<InvalidOperationException>(() => account.Disable(Later.AddSeconds(1)));

        account.Reactivate(Later.AddSeconds(2));

        Assert.Equal(AccountStatus.Active, account.Status);
        Assert.Throws<InvalidOperationException>(() => account.Reactivate(Later.AddSeconds(3)));
    }

    [Fact]
    public void UnavailableAccount_CanStillBeTombstoned()
    {
        var account = CreateAccount();
        account.Disable(Later);

        account.Tombstone(Later.AddSeconds(1));

        Assert.Equal(AccountStatus.Deleted, account.Status);
        Assert.Null(account.EncryptedSession);
        Assert.Null(account.WebView2ProfileDirectory);
    }

    [Theory]
    [InlineData(StagedImportDisposition.Ready)]
    [InlineData(StagedImportDisposition.Invalid)]
    [InlineData(StagedImportDisposition.Conflict)]
    [InlineData(StagedImportDisposition.NeedsResolution)]
    [InlineData(StagedImportDisposition.Excluded)]
    public void LeavingDuplicate_ClearsDuplicateMetadata(StagedImportDisposition nextDisposition)
    {
        var row = CreateStagedRow();
        row.MarkDuplicate(
            Guid.Parse("50000000-0000-0000-0000-000000000099"),
            Later);

        switch (nextDisposition)
        {
            case StagedImportDisposition.Ready:
                row.MarkReady("share-id", "1234", Later.AddSeconds(1));
                break;
            case StagedImportDisposition.Invalid:
                row.MarkInvalid("invalid", Later.AddSeconds(1));
                break;
            case StagedImportDisposition.Conflict:
                row.MarkConflict("conflict", Later.AddSeconds(1));
                break;
            case StagedImportDisposition.NeedsResolution:
                row.MarkNeedsResolution("resolve again", Later.AddSeconds(1));
                break;
            case StagedImportDisposition.Excluded:
                row.Exclude("excluded", Later.AddSeconds(1));
                break;
            default:
                throw new InvalidOperationException("Unexpected test disposition.");
        }

        Assert.Equal(nextDisposition, row.Disposition);
        Assert.Null(row.DuplicateOfStagedRowId);
    }

    [Fact]
    public void MarkDuplicate_ClearsValidationAndPrecheckMetadata()
    {
        var precheckedRow = CreateStagedRow();
        precheckedRow.MarkReady("share-id", "1234", Later);
        precheckedRow.RecordPrecheck(
            StagedRowPrecheckStatus.Failed,
            "PRECHECK_FAILED",
            Later.AddSeconds(1));
        precheckedRow.MarkDuplicate(
            Guid.Parse("50000000-0000-0000-0000-000000000099"),
            Later.AddSeconds(2));

        var invalidRow = CreateStagedRow();
        invalidRow.MarkInvalid("invalid row", Later);
        invalidRow.MarkDuplicate(
            Guid.Parse("50000000-0000-0000-0000-000000000099"),
            Later.AddSeconds(1));

        Assert.Null(invalidRow.ValidationMessage);
        Assert.Null(precheckedRow.PrecheckStatus);
        Assert.Null(precheckedRow.PrecheckErrorCode);
        Assert.Null(precheckedRow.PrecheckedAt);
    }

    [Fact]
    public void CorrectingDisposition_ClearsStalePrecheckMetadata()
    {
        var row = CreateStagedRow();
        row.MarkReady("share-id", "1234", Later);
        row.RecordPrecheck(
            StagedRowPrecheckStatus.Succeeded,
            errorCode: null,
            Later.AddSeconds(1));

        row.MarkNeedsResolution("short link changed", Later.AddSeconds(2));

        Assert.Equal(StagedImportDisposition.NeedsResolution, row.Disposition);
        Assert.Null(row.PrecheckStatus);
        Assert.Null(row.PrecheckErrorCode);
        Assert.Null(row.PrecheckedAt);
    }

    [Fact]
    public void ImportSessionCounterOverflow_IsRejectedWithoutPartialMutation()
    {
        var counters = new ImportSessionCounters(
            totalRows: int.MaxValue,
            readyRows: int.MaxValue,
            invalidRows: 0,
            duplicateRows: 0,
            excludedRows: 0);
        var session = new ImportSession(
            Guid.Parse("60000000-0000-0000-0000-000000000001"),
            "xlsx",
            "input.xlsx",
            CreatedAt,
            appendToBatchJobId: null,
            counters);

        Assert.Throws<OverflowException>(
            () => session.RecordRow(StagedImportDisposition.Ready, Later));
        Assert.Equal(int.MaxValue, session.TotalRows);
        Assert.Equal(int.MaxValue, session.ReadyRows);
    }

    [Fact]
    public void AttemptCounterOverflow_IsRejectedWithoutPartialMutation()
    {
        var operation = TransferOperation.CreateReserved(
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("90000000-0000-0000-0000-000000000001"),
            CreatedAt,
            attemptCount: int.MaxValue);

        Assert.Throws<OverflowException>(() => operation.MarkPreparing(Later));
        Assert.Equal(OperationStatus.Reserved, operation.Status);
        Assert.Equal(int.MaxValue, operation.AttemptCount);
    }

    [Fact]
    public void ImportSession_FollowsConfirmationLifecycleAndRejectsSkips()
    {
        var session = CreateImportSession();
        Assert.Throws<InvalidOperationException>(() => session.MarkReadyToConfirm(Later));

        session.RecordRow(StagedImportDisposition.Ready, Later);
        session.MarkDraft(Later.AddSeconds(1));
        session.BeginPrecheck(
            Guid.Parse("90000000-0000-0000-0000-000000000001"),
            Later.AddSeconds(2));
        session.MarkReadyToConfirm(Later.AddSeconds(3));
        session.MarkBatchCreated(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Later.AddSeconds(4));

        Assert.Equal(ImportSessionStatus.BatchCreated, session.Status);
        Assert.Equal(1, session.TotalRows);
        Assert.Equal(1, session.ReadyRows);
        Assert.Throws<InvalidOperationException>(() => session.Cancel(Later.AddSeconds(5)));
    }

    [Fact]
    public void CancelledImportSession_IsTerminal()
    {
        var session = CreateImportSession();

        session.Cancel(Later);

        Assert.Equal(ImportSessionStatus.Cancelled, session.Status);
        Assert.Throws<InvalidOperationException>(() => session.MarkDraft(Later.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(
            () => session.RecordRow(StagedImportDisposition.Ready, Later.AddSeconds(1)));
    }

    [Fact]
    public void ResourceItem_CanReleaseAndRereserveBeforeExpansion()
    {
        var resource = CreateResourceItem();
        var accountId = Guid.Parse("90000000-0000-0000-0000-000000000001");
        resource.Reserve(accountId, Later.AddMinutes(2), Later);
        resource.ReleaseAsUnused(Later.AddSeconds(1));
        resource.Reserve(accountId, Later.AddMinutes(3), Later.AddSeconds(2));
        resource.MarkExpanded(Later.AddSeconds(3));

        Assert.Equal(CandidateStatus.Expanded, resource.CandidateStatus);
        Assert.Equal(accountId, resource.ReservedAccountId);
        Assert.Null(resource.ReservationExpiresAt);
    }

    [Fact]
    public void ResourceItem_RejectsIllegalAndTerminalTransitions()
    {
        var resource = CreateResourceItem();
        Assert.Throws<InvalidOperationException>(() => resource.MarkExpanded(Later));
        Assert.Throws<InvalidOperationException>(() => resource.ReleaseAsUnused(Later));

        resource.MarkGloballyInvalid(Later);

        Assert.Throws<InvalidOperationException>(
            () => resource.Reserve(
                Guid.Parse("90000000-0000-0000-0000-000000000001"),
                Later.AddMinutes(2),
                Later.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(
            () => resource.MarkGloballyInvalid(Later.AddSeconds(1)));
    }

    [Fact]
    public void Precheck_IsRejectedUnlessStagedRowIsReady()
    {
        var row = CreateStagedRow();

        Assert.Throws<InvalidOperationException>(
            () => row.RecordPrecheck(
                StagedRowPrecheckStatus.Succeeded,
                errorCode: null,
                Later));
    }

    [Fact]
    public void ChangingPrecheckedStagedInputs_ClearsPrecheckMetadata()
    {
        var mappedRow = CreateStagedRow();
        mappedRow.MarkReady("share-id", "1234", Later);
        mappedRow.RecordPrecheck(
            StagedRowPrecheckStatus.Succeeded,
            errorCode: null,
            Later.AddSeconds(1));

        mappedRow.MapAccount(
            Guid.Parse("90000000-0000-0000-0000-000000000001"),
            Later.AddSeconds(2));

        var overriddenRow = CreateStagedRow();
        overriddenRow.MarkReady("share-id", "1234", Later);
        overriddenRow.RecordPrecheck(
            StagedRowPrecheckStatus.Succeeded,
            errorCode: null,
            Later.AddSeconds(1));
        overriddenRow.SetOverrides(
            "/new-target",
            "folder",
            "account",
            "note",
            Later.AddSeconds(2));

        Assert.Null(mappedRow.PrecheckStatus);
        Assert.Null(mappedRow.PrecheckedAt);
        Assert.Null(overriddenRow.PrecheckStatus);
        Assert.Null(overriddenRow.PrecheckedAt);
    }

    [Fact]
    public void ExcludedStagedRow_IsTerminal()
    {
        var row = CreateStagedRow();
        row.Exclude("user excluded", Later);

        Assert.Throws<InvalidOperationException>(
            () => row.MarkReady("share-id", "1234", Later.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(
            () => row.MarkInvalid("invalid", Later.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(
            () => row.MarkDuplicate(
                Guid.Parse("50000000-0000-0000-0000-000000000099"),
                Later.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(
            () => row.MapAccount(
                Guid.Parse("90000000-0000-0000-0000-000000000001"),
                Later.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(
            () => row.SetOverrides(
                "/target",
                "folder",
                "account",
                "note",
                Later.AddSeconds(1)));
        Assert.Equal(StagedImportDisposition.Excluded, row.Disposition);
        Assert.Null(row.NormalizedShareId);
        Assert.Null(row.MappedAccountId);
        Assert.Null(row.TargetPath);
    }

    private static BatchJob CreateBatch() => new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        "Batch 1",
        TransferMode.Distribution,
        QuotaBasis.SuccessTarget,
        ShortagePolicy.WaitForCandidates,
        CreatedAt);

    private static TransferOperation CreateReservedOperation() => TransferOperation.CreateReserved(
        Guid.Parse("20000000-0000-0000-0000-000000000001"),
        Guid.Parse("30000000-0000-0000-0000-000000000001"),
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("90000000-0000-0000-0000-000000000001"),
        CreatedAt);

    private static StagedImportRow CreateStagedRow() => new(
        Guid.Parse("50000000-0000-0000-0000-000000000001"),
        Guid.Parse("60000000-0000-0000-0000-000000000001"),
        1,
        "https://pan.baidu.com/s/example",
        CreatedAt);

    private static BaiduAccount CreateAccount() => new(
        Guid.Parse("90000000-0000-0000-0000-000000000001"),
        "baidu-user-1",
        "Display Name",
        "encrypted-session-reference",
        "profile-directory-reference",
        CreatedAt);

    private static ImportSession CreateImportSession() => new(
        Guid.Parse("60000000-0000-0000-0000-000000000001"),
        "xlsx",
        "input.xlsx",
        CreatedAt);

    private static ResourceItem CreateResourceItem() => new(
        Guid.Parse("30000000-0000-0000-0000-000000000001"),
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("50000000-0000-0000-0000-000000000001"),
        1,
        "https://pan.baidu.com/s/example",
        "share-id",
        CreatedAt);
}
