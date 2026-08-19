using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Imports;

public sealed record BulkPrecheckRequest(Guid ImportSessionId, Guid AccountId, int PageSize = 100)
{
    public void Validate()
    {
        if (ImportSessionId == Guid.Empty) throw new ArgumentException("An import session is required.", nameof(ImportSessionId));
        if (AccountId == Guid.Empty) throw new ArgumentException("A precheck account is required.", nameof(AccountId));
        if (PageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(PageSize));
    }
}

public enum BulkPrecheckRunStatus
{
    Completed,
    PausedForLogin,
    PausedForVerification,
}

public sealed record BulkPrecheckResult(
    BulkPrecheckRunStatus Status,
    int ProcessedRows,
    BaiduErrorKind? PauseReason = null)
{
    public override string ToString() =>
        $"BulkPrecheckResult {{ Status = {Status}, ProcessedRows = {ProcessedRows}, PauseReason = {PauseReason} }}";
}

public sealed class BulkPrecheckService
{
    private static readonly object RunGatesSync = new();
    private static readonly Dictionary<Guid, SessionRunGate> RunGates = [];
    private readonly IUnitOfWorkFactory unitOfWorkFactory;
    private readonly IBaiduSessionStore sessionStore;
    private readonly IBaiduNetdiskAdapter adapter;
    private readonly TimeProvider timeProvider;

    public BulkPrecheckService(
        IUnitOfWorkFactory unitOfWorkFactory,
        IBaiduSessionStore sessionStore,
        IBaiduNetdiskAdapter adapter,
        TimeProvider? timeProvider = null)
    {
        this.unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BulkPrecheckResult> RunAsync(
        BulkPrecheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var gate = RentRunGate(request.ImportSessionId);
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return await RunCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (entered) gate.Semaphore.Release();
            ReturnRunGate(request.ImportSessionId, gate);
        }
    }

    internal static bool HasActiveRunGate(Guid importSessionId)
    {
        lock (RunGatesSync) return RunGates.ContainsKey(importSessionId);
    }

    private static SessionRunGate RentRunGate(Guid importSessionId)
    {
        lock (RunGatesSync)
        {
            if (!RunGates.TryGetValue(importSessionId, out var gate))
            {
                gate = new();
                RunGates.Add(importSessionId, gate);
            }
            gate.ReferenceCount++;
            return gate;
        }
    }

    private static void ReturnRunGate(Guid importSessionId, SessionRunGate gate)
    {
        lock (RunGatesSync)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount != 0) return;
            RunGates.Remove(importSessionId);
            gate.Semaphore.Dispose();
        }
    }

    private async Task<BulkPrecheckResult> RunCoreAsync(
        BulkPrecheckRequest request,
        CancellationToken cancellationToken)
    {
        var account = await StartOrResumeAsync(request, cancellationToken).ConfigureAwait(false);
        var accountSession = await sessionStore.LoadAsync(request.AccountId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        if (accountSession is null)
        {
            return new(BulkPrecheckRunStatus.PausedForLogin, 0, BaiduErrorKind.ExpiredLogin);
        }
        if (!accountSession.IsBoundTo(account.Id, account.BaiduUserId))
        {
            throw new InvalidOperationException("The stored session does not match the selected precheck account.");
        }

        var processed = 0;
        var cursor = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KeysetPage<StagedImportRowRecord> page;
            await using (var readUnit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false))
            {
                page = await readUnit.Imports.GetRowsKeysetPageAsync(
                    new(request.ImportSessionId, "Ready", "Pending", request.PageSize, cursor, IncludeTotalCount: false),
                    cancellationToken).ConfigureAwait(false);
            }

            if (page.Items.Count == 0)
            {
                if (cursor != 0)
                {
                    cursor = 0;
                    continue;
                }
                if (await TryCompleteAsync(request, cancellationToken).ConfigureAwait(false))
                    return new(BulkPrecheckRunStatus.Completed, processed);
                continue;
            }
            foreach (var row in page.Items)
            {
                cursor = row.SourceRowNumber;
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(row.NormalizedShareId))
                {
                    if (await PersistOutcomeAsync(row, "Failed", "FORMAT_ERROR", cancellationToken).ConfigureAwait(false))
                        processed++;
                    continue;
                }

                BaiduAdapterResult<ShareMetadata> outcome;
                try
                {
                    outcome = await adapter.InspectShareAsync(
                        accountSession,
                        new ShareReference(row.NormalizedShareId, row.ShareUrl, row.ExtractionCode),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException)
                {
                    if (await PersistOutcomeAsync(row, "Failed", "TEMPORARY_UNAVAILABLE", cancellationToken)
                        .ConfigureAwait(false))
                        processed++;
                    continue;
                }

                if (!outcome.IsSuccess && outcome.ErrorKind is BaiduErrorKind.ExpiredLogin or BaiduErrorKind.VerificationRequired)
                {
                    return new(
                        outcome.ErrorKind == BaiduErrorKind.ExpiredLogin
                            ? BulkPrecheckRunStatus.PausedForLogin
                            : BulkPrecheckRunStatus.PausedForVerification,
                        processed,
                        outcome.ErrorKind);
                }

                if (await PersistOutcomeAsync(
                    row,
                    outcome.IsSuccess ? "Succeeded" : "Failed",
                    outcome.IsSuccess ? null : ErrorCode(outcome.ErrorKind),
                    cancellationToken).ConfigureAwait(false))
                    processed++;
            }
        }

    }

    private async Task<AccountRecord> StartOrResumeAsync(
        BulkPrecheckRequest request,
        CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var import = await unit.Imports.GetSessionAsync(request.ImportSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The import session does not exist.");
        if (import.Status == "Draft")
        {
            if (import.PrecheckAccountId is { } precheckAccountId && precheckAccountId != request.AccountId)
                throw new InvalidOperationException("Persisted precheck results cannot be mixed across accounts.");
            import = import with
            {
                Status = "Prechecking",
                PrecheckAccountId = request.AccountId,
                UpdatedAt = timeProvider.GetUtcNow(),
            };
            await unit.Imports.UpdateSessionAsync(import, cancellationToken).ConfigureAwait(false);
        }
        else if (import.Status == "Prechecking")
        {
            if (import.PrecheckAccountId != request.AccountId)
                throw new InvalidOperationException("A running precheck cannot silently switch accounts.");
        }
        else if (import.Status != "ReadyToConfirm" || import.PrecheckAccountId != request.AccountId)
        {
            throw new InvalidOperationException("Only a draft or matching paused precheck can be processed.");
        }

        var account = await unit.Accounts.GetByIdAsync(request.AccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected precheck account does not exist.");
        if (account.IsDeleted || !account.IsEnabled || !string.Equals(account.Status, "Active", StringComparison.Ordinal))
            throw new InvalidOperationException("The selected precheck account is not active.");
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return account;
    }

    private async Task<bool> PersistOutcomeAsync(
        StagedImportRowRecord snapshot,
        string status,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var current = await unit.Imports.GetRowByIdAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Disposition != "Ready" || current.PrecheckStatus is not null ||
            current.UpdatedAt != snapshot.UpdatedAt)
        {
            await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var now = timeProvider.GetUtcNow();
        await unit.Imports.UpdateRowsAsync(
            [current with
            {
                PrecheckStatus = status,
                PrecheckErrorCode = errorCode,
                PrecheckedAt = now,
                UpdatedAt = now,
            }],
            cancellationToken).ConfigureAwait(false);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryCompleteAsync(BulkPrecheckRequest request, CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var import = await unit.Imports.GetSessionAsync(request.ImportSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The import session does not exist.");
        if (import.Status == "ReadyToConfirm" && import.PrecheckAccountId == request.AccountId)
        {
            await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (import.Status != "Prechecking" || import.PrecheckAccountId != request.AccountId)
            throw new InvalidOperationException("The precheck state changed while it was running.");
        var pending = await unit.Imports.GetRowsKeysetPageAsync(
            new(request.ImportSessionId, "Ready", "Pending", 1, IncludeTotalCount: false),
            cancellationToken).ConfigureAwait(false);
        if (pending.Items.Count != 0)
        {
            await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await unit.Imports.UpdateSessionAsync(
            import with { Status = "ReadyToConfirm", UpdatedAt = timeProvider.GetUtcNow() },
            cancellationToken).ConfigureAwait(false);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string ErrorCode(BaiduErrorKind? errorKind) => errorKind switch
    {
        BaiduErrorKind.InvalidLink => "INVALID_LINK",
        BaiduErrorKind.InvalidExtractionCode => "INVALID_EXTRACTION_CODE",
        BaiduErrorKind.TransientService => "TEMPORARY_UNAVAILABLE",
        BaiduErrorKind.UnknownResponse => "UNKNOWN_RESPONSE",
        BaiduErrorKind.InsufficientCapacity => "ACCOUNT_CAPACITY",
        BaiduErrorKind.InvalidTargetPath => "INVALID_TARGET_PATH",
        _ => "UNKNOWN_RESPONSE",
    };

    private sealed class SessionRunGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }
}
