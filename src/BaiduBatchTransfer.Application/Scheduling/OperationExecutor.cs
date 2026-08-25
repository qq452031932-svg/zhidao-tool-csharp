using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed record OperationExecutionSnapshot(
    Guid OperationId,
    Guid AccountId,
    ShareReference Share,
    RemotePath TargetPath,
    string? CreateFolderName,
    DuplicatePolicy DuplicatePolicy,
    string OperationFingerprint,
    bool RequiresExactShareEvidence = false,
    int AttemptCount = 0,
    bool AttemptGenerationPrepared = false,
    Guid BatchId = default)
{
    public override string ToString() =>
        $"OperationExecutionSnapshot {{ EntityIds = [REDACTED], Share = [REDACTED], Destination = [REDACTED], DuplicatePolicy = {DuplicatePolicy}, OperationFingerprint = [REDACTED] }}";
}

public sealed class OperationSnapshotValidationException : ArgumentException
{
    public OperationSnapshotValidationException(int expectedAttemptCount, Exception innerException)
        : base("The persisted operation snapshot is invalid.", innerException)
    {
        if (expectedAttemptCount < 0) throw new ArgumentOutOfRangeException(nameof(expectedAttemptCount));
        ExpectedAttemptCount = expectedAttemptCount;
    }

    public int ExpectedAttemptCount { get; }
}

public enum TransferPauseReason
{
    AccountSessionUnavailable,
    AccountVerificationRequired,
    UserInterventionRequired,
}

public interface IOperationExecutionStore
{
    Task<OperationExecutionSnapshot?> LoadAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task MarkPreparingAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null);
    Task RecordSubmissionIdentifiersAsync(
        Guid operationId,
        string? requestId,
        IReadOnlyList<string> remoteItemIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null);
    Task MarkSucceededAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null);
    Task CompleteSucceededWithEvidenceAsync(
        Guid operationId,
        string? requestId,
        RemoteItemIdentitySet exactRemoteItems,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null);
    Task MarkFailedAsync(
        Guid operationId,
        string code,
        string safeMessage,
        bool globallyInvalid,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null);
    Task MarkResultPendingAsync(
        Guid operationId,
        string code,
        string safeMessage,
        SubmissionAcceptance submissionAcceptance,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null);
    Task PauseAccountBeforeSubmissionAsync(
        Guid operationId,
        string code,
        string safeMessage,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null);
    Task PauseForAccountInterventionAsync(
        Guid operationId,
        TransferPauseReason reason,
        SubmissionAcceptance submissionAcceptance,
        DateTimeOffset? observedSessionCapturedAtUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null);
    Task MarkDefiniteAccountFailureAsync(
        Guid operationId,
        string code,
        string safeMessage,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null);

    Task ReturnToWaitingForDailyLimitAsync(
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        int? expectedAttemptCount = null) =>
        throw new NotSupportedException("The execution store does not support daily-limit release.");
}

public interface IAsyncDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class TimeProviderDelay(TimeProvider timeProvider) : IAsyncDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, timeProvider, cancellationToken);
}

public sealed class OperationExecutor : ITransferOperationExecutor
{
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromMinutes(10);
    private readonly IBaiduNetdiskAdapter adapter;
    private readonly IBaiduSessionStore sessionStore;
    private readonly IOperationExecutionStore store;
    private readonly AccountDailyLimitService dailyLimit;
    private readonly RetryPolicy retryPolicy;
    private readonly IAsyncDelay delay;
    private readonly TimeProvider timeProvider;

    public OperationExecutor(
        IBaiduNetdiskAdapter adapter,
        IBaiduSessionStore sessionStore,
        IOperationExecutionStore store,
        AccountDailyLimitService dailyLimit,
        RetryPolicy? retryPolicy = null,
        IAsyncDelay? delay = null,
        TimeProvider? timeProvider = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.dailyLimit = dailyLimit ?? throw new ArgumentNullException(nameof(dailyLimit));
        this.retryPolicy = retryPolicy ?? RetryPolicy.Default;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.delay = delay ?? new TimeProviderDelay(this.timeProvider);
    }

    public async Task ExecuteAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation ID is required.", nameof(operationId));
        OperationExecutionSnapshot? snapshot;
        try
        {
            snapshot = await store.LoadAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationSnapshotValidationException exception)
        {
            await FailAndPauseAccountAsync(
                    operationId, BaiduErrorKind.InvalidTargetPath, exception.ExpectedAttemptCount, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (snapshot is null) throw new InvalidOperationException("The scheduled operation no longer exists.");
        var session = await sessionStore.LoadAsync(snapshot.AccountId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            await PauseAccountBeforeSubmissionAsync(
                    snapshot.OperationId, BaiduErrorKind.ExpiredLogin, observedSessionCapturedAtUtc: null,
                    snapshot.AttemptCount, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var now = timeProvider.GetUtcNow();
        await store.MarkPreparingAsync(
            snapshot.OperationId, now, cancellationToken, snapshot.AttemptCount).ConfigureAwait(false);
        var attemptCount = snapshot.AttemptGenerationPrepared
            ? snapshot.AttemptCount
            : checked(snapshot.AttemptCount + 1);
        RemotePath completePath;
        try
        {
            completePath = CombinePath(snapshot.TargetPath, snapshot.CreateFolderName);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            await FailAndPauseAccountAsync(
                    snapshot.OperationId, BaiduErrorKind.InvalidTargetPath, attemptCount, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        BaiduAdapterResult<RemoteDirectory> directoryResult;
        try
        {
            directoryResult = await EnsureWithRetryAsync(session, completePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            await FailAndPauseAccountAsync(
                    snapshot.OperationId, BaiduErrorKind.InvalidTargetPath, attemptCount, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        if (!directoryResult.IsSuccess)
        {
            var error = directoryResult.ErrorKind!.Value;
            if (error is BaiduErrorKind.ExpiredLogin or BaiduErrorKind.VerificationRequired)
                await PauseAccountBeforeSubmissionAsync(
                    snapshot.OperationId, error, session.CapturedAtUtc, attemptCount, cancellationToken).ConfigureAwait(false);
            else if (IsAccountPausingFailure(error))
                await FailAndPauseAccountAsync(snapshot.OperationId, error, attemptCount, cancellationToken).ConfigureAwait(false);
            else
                await FailAsync(snapshot.OperationId, error, false, attemptCount, cancellationToken).ConfigureAwait(false);
            return;
        }

        TransferOperationAutomationContext? automationContext = null;
        if (adapter is IOperationBoundBaiduNetdiskAdapter operationBoundAdapter)
        {
            automationContext = new TransferOperationAutomationContext(
                snapshot.OperationId,
                snapshot.BatchId,
                snapshot.AccountId,
                attemptCount,
                snapshot.Share).Validate();
            BaiduAdapterResult<TransferPreparationReceipt> preparation;
            try
            {
                preparation = await operationBoundAdapter.PrepareWholeShareForOperationAsync(
                    automationContext,
                    session,
                    snapshot.Share,
                    directoryResult.Value!,
                    snapshot.DuplicatePolicy,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return;
            }
            if (!preparation.IsSuccess)
            {
                var error = preparation.ErrorKind!.Value;
                if (error is BaiduErrorKind.ExpiredLogin or BaiduErrorKind.VerificationRequired)
                    await PauseAccountBeforeSubmissionAsync(
                        snapshot.OperationId, error, session.CapturedAtUtc, attemptCount, cancellationToken).ConfigureAwait(false);
                else if (IsAccountPausingFailure(error))
                    await FailAndPauseAccountAsync(snapshot.OperationId, error, attemptCount, cancellationToken).ConfigureAwait(false);
                else
                    await FailAsync(snapshot.OperationId, error, false, attemptCount, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        now = timeProvider.GetUtcNow();
        var descriptor = SerializeDescriptor(snapshot, completePath, now);
        var boundary = await dailyLimit.PersistFirstSubmissionAsync(
            new SubmissionBoundaryCommand(
                snapshot.OperationId,
                snapshot.AccountId,
                descriptor,
                now.Add(ConfirmationWindow),
                attemptCount),
            cancellationToken).ConfigureAwait(false);
        if (!boundary.Accepted)
        {
            await store.ReturnToWaitingForDailyLimitAsync(
                    snapshot.OperationId, now, cancellationToken, attemptCount)
                .ConfigureAwait(false);
            return;
        }

        // There is deliberately no blind retry beyond this point. A transport error may hide
        // a completed remote transfer and therefore must be reconciled instead of resubmitted.
        BaiduAdapterResult<TransferReceipt> saveResult;
        try
        {
            saveResult = adapter is IOperationBoundBaiduNetdiskAdapter operationBound
                ? await operationBound.SaveWholeShareForOperationAsync(
                    automationContext!,
                    session,
                    snapshot.Share,
                    directoryResult.Value!,
                    snapshot.DuplicatePolicy,
                    cancellationToken).ConfigureAwait(false)
                : await adapter.SaveWholeShareAsync(
                    session,
                    snapshot.Share,
                    directoryResult.Value!,
                    snapshot.DuplicatePolicy,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await MarkUnknownSubmissionResultAsync(snapshot.OperationId, attemptCount).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await MarkUnknownSubmissionResultAsync(snapshot.OperationId, attemptCount).ConfigureAwait(false);
            return;
        }
        now = timeProvider.GetUtcNow();
        if (!saveResult.IsSuccess)
        {
            var errorKind = saveResult.ErrorKind!.Value;
            if (errorKind is BaiduErrorKind.ExpiredLogin or BaiduErrorKind.VerificationRequired)
            {
                await PauseForAccountInterventionAsync(
                    snapshot.OperationId,
                    errorKind,
                    saveResult.SubmissionAcceptance,
                    session.CapturedAtUtc,
                    attemptCount,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (saveResult.SubmissionAcceptance != SubmissionAcceptance.DefinitelyNotAccepted)
            {
                await store.MarkResultPendingAsync(
                    snapshot.OperationId,
                    errorKind.ToString(),
                    saveResult.SafeMessage!,
                    saveResult.SubmissionAcceptance,
                    now,
                    cancellationToken,
                    attemptCount).ConfigureAwait(false);
            }
            else if (IsAccountPausingFailure(errorKind))
            {
                await FailAndPauseAccountAsync(
                    snapshot.OperationId, errorKind, attemptCount, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await FailAsync(
                    snapshot.OperationId,
                    errorKind,
                    errorKind is BaiduErrorKind.InvalidLink or BaiduErrorKind.InvalidExtractionCode,
                    attemptCount,
                    cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var receipt = saveResult.Value!;
        if (receipt.ExactRemoteItems is not null)
        {
            await store.CompleteSucceededWithEvidenceAsync(
                snapshot.OperationId,
                receipt.AdapterRequestId,
                receipt.ExactRemoteItems,
                now,
                cancellationToken,
                attemptCount).ConfigureAwait(false);
            return;
        }
        await store.RecordSubmissionIdentifiersAsync(
            snapshot.OperationId,
            receipt.AdapterRequestId,
            receipt.RemoteItemIds,
            now,
            cancellationToken,
            attemptCount).ConfigureAwait(false);
        if (snapshot.RequiresExactShareEvidence)
        {
            var unknown = BaiduAdapterResult.Failure(BaiduErrorKind.UnknownResponse, null);
            await store.MarkResultPendingAsync(
                snapshot.OperationId,
                BaiduErrorKind.UnknownResponse.ToString(),
                unknown.SafeMessage!,
                SubmissionAcceptance.Unknown,
                now,
                cancellationToken,
                attemptCount).ConfigureAwait(false);
            return;
        }
        await store.MarkSucceededAsync(
            snapshot.OperationId, now, cancellationToken, attemptCount).ConfigureAwait(false);
    }

    private async Task<BaiduAdapterResult<RemoteDirectory>> EnsureWithRetryAsync(
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        var completedRetries = 0;
        while (true)
        {
            var result = await adapter.EnsureDirectoryAsync(session, path, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess) return result;
            var decision = retryPolicy.Decide(result.ErrorKind!.Value, completedRetries, result.RetryAfter);
            if (!decision.ShouldRetry) return result;
            completedRetries++;
            await delay.DelayAsync(decision.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task FailAsync(
        Guid operationId,
        BaiduErrorKind errorKind,
        bool globallyInvalid,
        int? expectedAttemptCount,
        CancellationToken cancellationToken)
    {
        var safe = BaiduAdapterResult.Failure(errorKind, null);
        return store.MarkFailedAsync(
            operationId,
            errorKind.ToString(),
            safe.SafeMessage!,
            globallyInvalid,
            timeProvider.GetUtcNow(),
            cancellationToken,
            expectedAttemptCount);
    }

    private Task FailAndPauseAccountAsync(
        Guid operationId,
        BaiduErrorKind errorKind,
        int? expectedAttemptCount,
        CancellationToken cancellationToken)
    {
        var safe = BaiduAdapterResult.Failure(errorKind, null);
        return store.MarkDefiniteAccountFailureAsync(
            operationId,
            errorKind.ToString(),
            safe.SafeMessage!,
            timeProvider.GetUtcNow(),
            cancellationToken,
            expectedAttemptCount);
    }

    private Task PauseAccountBeforeSubmissionAsync(
        Guid operationId,
        BaiduErrorKind errorKind,
        DateTimeOffset? observedSessionCapturedAtUtc,
        int? expectedAttemptCount,
        CancellationToken cancellationToken)
    {
        var reason = errorKind switch
        {
            BaiduErrorKind.ExpiredLogin => TransferPauseReason.AccountSessionUnavailable,
            BaiduErrorKind.VerificationRequired => TransferPauseReason.AccountVerificationRequired,
            _ => throw new ArgumentOutOfRangeException(nameof(errorKind)),
        };
        return store.PauseForAccountInterventionAsync(
            operationId,
            reason,
            SubmissionAcceptance.DefinitelyNotAccepted,
            observedSessionCapturedAtUtc,
            timeProvider.GetUtcNow(),
            cancellationToken,
            expectedAttemptCount);
    }

    private Task PauseForAccountInterventionAsync(
        Guid operationId,
        BaiduErrorKind errorKind,
        SubmissionAcceptance submissionAcceptance,
        DateTimeOffset? observedSessionCapturedAtUtc,
        int expectedAttemptCount,
        CancellationToken cancellationToken)
    {
        var reason = errorKind switch
        {
            BaiduErrorKind.ExpiredLogin => TransferPauseReason.AccountSessionUnavailable,
            BaiduErrorKind.VerificationRequired => TransferPauseReason.AccountVerificationRequired,
            _ => throw new ArgumentOutOfRangeException(nameof(errorKind)),
        };
        return store.PauseForAccountInterventionAsync(
            operationId,
            reason,
            submissionAcceptance,
            observedSessionCapturedAtUtc,
            timeProvider.GetUtcNow(),
            cancellationToken,
            expectedAttemptCount);
    }

    private Task MarkUnknownSubmissionResultAsync(Guid operationId, int expectedAttemptCount)
    {
        var failure = BaiduAdapterResult.Failure(BaiduErrorKind.UnknownResponse, null);
        return store.MarkResultPendingAsync(
            operationId,
            BaiduErrorKind.UnknownResponse.ToString(),
            failure.SafeMessage!,
            SubmissionAcceptance.Unknown,
            timeProvider.GetUtcNow(),
            CancellationToken.None,
            expectedAttemptCount);
    }

    private static bool IsAccountPausingFailure(BaiduErrorKind errorKind) =>
        errorKind is BaiduErrorKind.ExpiredLogin
            or BaiduErrorKind.VerificationRequired
            or BaiduErrorKind.InsufficientCapacity
            or BaiduErrorKind.InvalidTargetPath;

    private static RemotePath CombinePath(RemotePath basePath, string? outerDirectory)
    {
        if (string.IsNullOrWhiteSpace(outerDirectory)) return basePath;
        var outer = outerDirectory.Trim();
        if (outer.Contains('/') || outer.Contains('\\') || outer is "." or "..")
            throw new InvalidOperationException("The snapshotted outer directory name is invalid.");
        return new RemotePath($"{basePath.Value.TrimEnd('/')}/{outer}");
    }

    private static string SerializeDescriptor(
        OperationExecutionSnapshot snapshot,
        RemotePath completePath,
        DateTimeOffset now)
    {
        var outerFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            snapshot.CreateFolderName?.Trim() ?? string.Empty)));
        var descriptor = new OperationConfirmationDescriptor(
            snapshot.Share.StableShareId,
            completePath,
            outerFingerprint,
            snapshot.OperationFingerprint,
            now,
            now.Add(ConfirmationWindow));
        return JsonSerializer.Serialize(new
        {
            descriptor.SourceShareId,
            TargetPath = descriptor.TargetPath.Value,
            descriptor.ExpectedOuterNameFingerprint,
            descriptor.OperationFingerprint,
            descriptor.SubmitWindowStartUtc,
            descriptor.SubmitWindowEndUtc,
        });
    }
}
