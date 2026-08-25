using System.Collections.Concurrent;
using System.Collections.Immutable;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed record AdapterWriteAuthorizationRequest(
    AdapterCapabilityVersion Capability,
    Guid AccountId,
    Guid OperationId,
    int Generation);

public interface IAdapterWriteAuthorizationGate
{
    ValueTask<bool> TryAuthorizeAsync(
        AdapterWriteAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

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

public class CoordinatedBaiduNetdiskAdapter : IBaiduNetdiskAdapter
{
    private readonly IBaiduNetdiskAdapter inner;
    private readonly BaiduAdapterConcurrencyGate gate;
    private readonly IAdapterWriteAuthorizationGate? writeGate;
    private readonly AdapterCapabilityVersion? capability;

    protected CoordinatedBaiduNetdiskAdapter(
        IBaiduNetdiskAdapter inner,
        BaiduAdapterConcurrencyGate gate,
        IAdapterWriteAuthorizationGate? writeGate = null,
        AdapterCapabilityVersion? capability = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.writeGate = writeGate;
        this.capability = capability;
        if ((writeGate is null) != (capability is null))
            throw new ArgumentException("A write gate and capability version must be supplied together.");
    }

    public static IBaiduNetdiskAdapter Wrap(
        IBaiduNetdiskAdapter inner,
        BaiduAdapterConcurrencyGate gate,
        IAdapterWriteAuthorizationGate writeGate,
        AdapterCapabilityVersion capability)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(writeGate);
        ArgumentNullException.ThrowIfNull(capability);
        return inner is IOperationBoundBaiduNetdiskAdapter operationBound
            ? new CoordinatedOperationBoundBaiduNetdiskAdapter(operationBound, gate, writeGate, capability)
            : new CoordinatedBaiduNetdiskAdapter(inner, gate, writeGate, capability);
    }

    public static IBaiduNetdiskAdapter Wrap(
        IBaiduNetdiskAdapter inner,
        BaiduAdapterConcurrencyGate gate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);
        return inner is IOperationBoundBaiduNetdiskAdapter operationBound
            ? new CoordinatedOperationBoundBaiduNetdiskAdapter(operationBound, gate)
            : new CoordinatedBaiduNetdiskAdapter(inner, gate);
    }

    public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.ValidateSessionAsync(session, token), cancellationToken);

    public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.GetCapacityAsync(session, token), cancellationToken);

    public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.ListDirectoriesAsync(session, path, token), cancellationToken);

    public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) =>
        RunWriteAsync(session, Guid.Empty, 0, token => inner.EnsureDirectoryAsync(session, path, token), cancellationToken);

    public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.InspectShareAsync(session, share, token), cancellationToken);

    public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) =>
        RunWriteAsync(session, Guid.Empty, 0, token => inner.SaveWholeShareAsync(session, share, destination, duplicatePolicy, token), cancellationToken);

    public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.ConfirmTransferAsync(session, query, token), cancellationToken);

    protected async Task<T> RunAsync<T>(AccountSession session, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var lease = await gate.AcquireAsync(session, cancellationToken).ConfigureAwait(false);
        return await action(cancellationToken).ConfigureAwait(false);
    }

    protected async Task<BaiduAdapterResult<T>> RunWriteAsync<T>(
        AccountSession session,
        Guid operationId,
        int generation,
        Func<CancellationToken, Task<BaiduAdapterResult<T>>> action,
        CancellationToken cancellationToken)
    {
        if (writeGate is not null)
        {
            var request = new AdapterWriteAuthorizationRequest(
                capability!, session.LocalAccountId ?? Guid.Empty, operationId, generation);
            if (!await writeGate.TryAuthorizeAsync(request, cancellationToken).ConfigureAwait(false))
                return NotValidated<T>();
        }
        return await RunAsync(session, action, cancellationToken).ConfigureAwait(false);
    }

    private static BaiduAdapterResult<T> NotValidated<T>() =>
        BaiduAdapterResult<T>.Failure(
            BaiduErrorKind.NotValidated,
            adapterDiagnostic: null,
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);
}

public sealed class CoordinatedOperationBoundBaiduNetdiskAdapter(
    IOperationBoundBaiduNetdiskAdapter inner,
    BaiduAdapterConcurrencyGate gate,
    IAdapterWriteAuthorizationGate? writeGate = null,
    AdapterCapabilityVersion? capability = null) : CoordinatedBaiduNetdiskAdapter(inner, gate, writeGate, capability), IOperationBoundBaiduNetdiskAdapter
{
    private readonly IOperationBoundBaiduNetdiskAdapter inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken) =>
        RunWriteAsync(
            session,
            context.OperationId,
            context.Generation,
            token => inner.SaveWholeShareForOperationAsync(
                context, session, share, destination, duplicatePolicy, token),
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
            token => inner.PrepareWholeShareForOperationAsync(
                context, session, share, destination, duplicatePolicy, token),
            cancellationToken);

    public Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken) =>
        RunAsync(
            session,
            token => inner.ConfirmExactTransferForOperationAsync(context, session, query, token),
            cancellationToken);
}

public class CoordinatedBaiduShareAdapter : IBaiduShareAdapter
{
    private readonly IBaiduShareAdapter inner;
    private readonly BaiduAdapterConcurrencyGate gate;
    private readonly IAdapterWriteAuthorizationGate? writeGate;
    private readonly AdapterCapabilityVersion? capability;

    public CoordinatedBaiduShareAdapter(
        IBaiduShareAdapter inner,
        BaiduAdapterConcurrencyGate gate,
        IAdapterWriteAuthorizationGate? writeGate = null,
        AdapterCapabilityVersion? capability = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.writeGate = writeGate;
        this.capability = capability;
        if ((writeGate is null) != (capability is null))
            throw new ArgumentException("A write gate and capability version must be supplied together.");
    }

    public static IBaiduShareAdapter Wrap(
        IBaiduShareAdapter inner,
        BaiduAdapterConcurrencyGate gate,
        IAdapterWriteAuthorizationGate writeGate,
        AdapterCapabilityVersion capability)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(writeGate);
        ArgumentNullException.ThrowIfNull(capability);
        return inner is IOperationBoundBaiduShareAdapter operationBound
            ? new CoordinatedOperationBoundBaiduShareAdapter(operationBound, gate, writeGate, capability)
            : new CoordinatedBaiduShareAdapter(inner, gate, writeGate, capability);
    }

    public static IBaiduShareAdapter Wrap(
        IBaiduShareAdapter inner,
        BaiduAdapterConcurrencyGate gate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);
        return inner is IOperationBoundBaiduShareAdapter operationBound
            ? new CoordinatedOperationBoundBaiduShareAdapter(operationBound, gate)
            : new CoordinatedBaiduShareAdapter(inner, gate);
    }

    public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) =>
        RunWriteAsync(session, Guid.Empty, 0, token => inner.CreatePermanentShareAsync(session, request, token), cancellationToken);

    public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken) =>
        RunAsync(session, token => inner.ListSharesAsync(session, cursor, token), cancellationToken);

    protected async Task<T> RunAsync<T>(AccountSession session, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var lease = await gate.AcquireAsync(session, cancellationToken).ConfigureAwait(false);
        return await action(cancellationToken).ConfigureAwait(false);
    }

    protected async Task<BaiduAdapterResult<T>> RunWriteAsync<T>(
        AccountSession session,
        Guid operationId,
        int generation,
        Func<CancellationToken, Task<BaiduAdapterResult<T>>> action,
        CancellationToken cancellationToken)
    {
        if (writeGate is not null)
        {
            var request = new AdapterWriteAuthorizationRequest(
                capability!, session.LocalAccountId ?? Guid.Empty, operationId, generation);
            if (!await writeGate.TryAuthorizeAsync(request, cancellationToken).ConfigureAwait(false))
                return BaiduAdapterResult<T>.Failure(
                    BaiduErrorKind.NotValidated,
                    adapterDiagnostic: null,
                    submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);
        }
        return await RunAsync(session, action, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class CoordinatedOperationBoundBaiduShareAdapter(
    IOperationBoundBaiduShareAdapter inner,
    BaiduAdapterConcurrencyGate gate,
    IAdapterWriteAuthorizationGate? writeGate = null,
    AdapterCapabilityVersion? capability = null) : CoordinatedBaiduShareAdapter(inner, gate, writeGate, capability), IOperationBoundBaiduShareAdapter
{
    private readonly IOperationBoundBaiduShareAdapter inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(
        ShareOperationAutomationContext context,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(
            session,
            token => inner.PreparePermanentShareForOperationAsync(context, session, request, token),
            cancellationToken);

    public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(
        ShareOperationAutomationContext context,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken) =>
        RunWriteAsync(
            session,
            context.OperationId,
            context.Generation,
            token => inner.SubmitPermanentShareForOperationAsync(context, session, request, token),
            cancellationToken);

    public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(
        ShareOperationAutomationContext context,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(
            session,
            token => inner.ConfirmExactShareForOperationAsync(context, session, request, token),
            cancellationToken);
}
