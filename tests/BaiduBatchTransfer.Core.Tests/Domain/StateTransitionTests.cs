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
}
