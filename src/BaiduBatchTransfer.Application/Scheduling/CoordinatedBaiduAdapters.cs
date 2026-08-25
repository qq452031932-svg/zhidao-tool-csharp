using System.Collections.Concurrent;
using System.Collections.Immutable;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed class BaiduAdapterConcurrencyGate
{
    private readonly SemaphoreSlim global = new(TransferCoordinator.MaximumGlobalConcurrency,
        TransferCoordinator.MaximumGlobalConcurrency);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> accounts = new(StringComparer.Ordinal);

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        AccountSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var account = accounts.GetOrAdd(session.AccountId, static _ => new SemaphoreSlim(1, 1));
        await account.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await global.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(global, account);
        }
        catch
        {
            account.Release();
            throw;
        }
    }

    private sealed class Lease(SemaphoreSlim global, SemaphoreSlim account) : IAsyncDisposable
    {
        private int released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                global.Release();
                account.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class CoordinatedBaiduNetdiskAdapter(
    IBaiduNetdiskAdapter inner,
    BaiduAdapterConcurrencyGate gate) : IOperationBoundBaiduNetdiskAdapter
{
    private readonly IBaiduNetdiskAdapter inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly BaiduAdapterConcurrencyGate gate = gate ?? throw new ArgumentNullException(nameof(gate));

    public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.ValidateSessionAsync(session, token), cancellationToken);

    public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.GetCapacityAsync(session, token), cancellationToken);

    public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.ListDirectoriesAsync(session, path, token), cancellationToken);

    public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.EnsureDirectoryAsync(session, path, token), cancellationToken);

    public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.InspectShareAsync(session, share, token), cancellationToken);

    public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.SaveWholeShareAsync(session, share, destination, duplicatePolicy, token), cancellationToken);

    public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.ConfirmTransferAsync(session, query, token), cancellationToken);

    public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken) =>
        RunAsync(
            session,
            token => inner is IOperationBoundBaiduNetdiskAdapter operationBound
                ? operationBound.SaveWholeShareForOperationAsync(
                    context, session, share, destination, duplicatePolicy, token)
                : inner.SaveWholeShareAsync(session, share, destination, duplicatePolicy, token),
            cancellationToken);

    public Task<BaiduAdapterResult<TransferPreparationReceipt>> PrepareWholeShareForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken) =>
        RunAsync(
            session,
            token => inner is IOperationBoundBaiduNetdiskAdapter operationBound
                ? operationBound.PrepareWholeShareForOperationAsync(
                    context, session, share, destination, duplicatePolicy, token)
                : Task.FromResult(BaiduAdapterResult<TransferPreparationReceipt>.Success(TransferPreparationReceipt.Ready)),
            cancellationToken);

    public Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken) =>
        RunAsync(
            session,
            token => inner is IOperationBoundBaiduNetdiskAdapter operationBound
                ? operationBound.ConfirmExactTransferForOperationAsync(context, session, query, token)
                : inner.ConfirmExactTransferAsync(session, query, token),
            cancellationToken);

    private async Task<T> RunAsync<T>(AccountSession session, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var lease = await gate.AcquireAsync(session, cancellationToken).ConfigureAwait(false);
        return await action(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class CoordinatedBaiduShareAdapter(
    IBaiduShareAdapter inner,
    BaiduAdapterConcurrencyGate gate) : IBaiduShareAdapter
{
    private readonly IBaiduShareAdapter inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly BaiduAdapterConcurrencyGate gate = gate ?? throw new ArgumentNullException(nameof(gate));

    public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.CreatePermanentShareAsync(session, request, token), cancellationToken);

    public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.ListSharesAsync(session, cursor, token), cancellationToken);

    private async Task<T> RunAsync<T>(AccountSession session, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var lease = await gate.AcquireAsync(session, cancellationToken).ConfigureAwait(false);
        return await action(cancellationToken).ConfigureAwait(false);
    }
}
