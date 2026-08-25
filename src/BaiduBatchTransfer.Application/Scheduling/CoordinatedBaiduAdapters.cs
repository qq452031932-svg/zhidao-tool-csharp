using System.Collections.Concurrent;
using System.Collections.Immutable;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed record AdapterWriteAuthorizationRequest(
    AdapterCapabilityVersion Capability,
    Guid AccountId,
    Guid OperationId,
    int Generation,
    Guid BatchId = default,
    AdapterWriteAction Action = AdapterWriteAction.SubmitTransfer,
    bool IsRecovery = false);

public enum AdapterWriteAction
{
    EnsureDirectory,
    SubmitTransfer,
    PrepareShare,
    SubmitShare,
}

public enum AdapterWriteAuthorizationSource
{
    ConsumedLease,
    ExactFormalRecord,
}

public sealed record AdapterWriteAuthorizationBinding(
    AdapterWriteAuthorizationSource Source,
    AdapterCapabilityVersion Capability,
    Guid AccountId,
    Guid OperationId,
    int Generation,
    Guid BatchId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsRecovery,
    string? FormalEvidenceFingerprint);

public interface IAdapterWriteAuthorizationGate
{
    ValueTask<bool> TryAuthorizeAsync(
        AdapterWriteAuthorizationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<AdapterWriteAuthorizationBinding?> TryCreateBindingAsync(
        AdapterWriteAuthorizationRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AdapterWriteAuthorizationBinding?>(null);

    ValueTask<bool> TryValidateBindingAsync(
        AdapterWriteAuthorizationRequest request,
        AdapterWriteAuthorizationBinding binding,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);
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
        CancellationToken cancellationToken,
        Guid batchId = default,
        AdapterWriteAction writeAction = AdapterWriteAction.SubmitTransfer,
        bool isRecovery = false)
    {
        if (writeGate is not null)
        {
            var request = new AdapterWriteAuthorizationRequest(
                capability!, session.LocalAccountId ?? Guid.Empty, operationId, generation,
                batchId, writeAction, isRecovery);
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

    public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken) =>
        RunWriteAsync(
            session,
            context.OperationId,
            context.Generation,
            token => inner.EnsureDirectoryForOperationAsync(context, session, path, token),
            cancellationToken,
            context.BatchId,
            AdapterWriteAction.EnsureDirectory,
            context.IsRecovery);

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
            cancellationToken,
            context.BatchId,
            AdapterWriteAction.SubmitTransfer,
            context.IsRecovery);

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
    protected bool HasWriteGate => writeGate is not null;

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
        CancellationToken cancellationToken,
        Guid batchId = default,
        AdapterWriteAction writeAction = AdapterWriteAction.SubmitShare,
        bool isRecovery = false)
    {
        if (writeGate is not null)
        {
            var request = new AdapterWriteAuthorizationRequest(
                capability!, session.LocalAccountId ?? Guid.Empty, operationId, generation,
                batchId, writeAction, isRecovery);
            if (!await writeGate.TryAuthorizeAsync(request, cancellationToken).ConfigureAwait(false))
                return BaiduAdapterResult<T>.Failure(
                    BaiduErrorKind.NotValidated,
                    adapterDiagnostic: null,
                    submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);
        }
        return await RunAsync(session, action, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<BaiduAdapterResult<T>> RunBindingCreateAsync<T>(
        ShareOperationAutomationContext context,
        AccountSession session,
        Func<ShareOperationAutomationContext, CancellationToken, Task<BaiduAdapterResult<T>>> action,
        CancellationToken cancellationToken,
        Func<ShareOperationAutomationContext, bool>? bind = null)
    {
        if (writeGate is null)
            return await RunAsync(session, token => action(context, token), cancellationToken).ConfigureAwait(false);
        return await RunAsync(
            session,
            async token =>
            {
                if (session.LocalAccountId != context.AccountId) return NotValidated<T>();
                var request = WriteRequest(context, session, AdapterWriteAction.PrepareShare);
                var binding = await writeGate.TryCreateBindingAsync(request, token).ConfigureAwait(false);
                if (binding is null) return NotValidated<T>();
                var boundContext = context with { AuthorizationBinding = binding };
                if (bind is not null && !bind(boundContext)) return NotValidated<T>();
                return await action(boundContext, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    protected async Task<BaiduAdapterResult<T>> RunBindingValidateAsync<T>(
        ShareOperationAutomationContext context,
        AccountSession session,
        Func<CancellationToken, Task<BaiduAdapterResult<T>>> action,
        CancellationToken cancellationToken)
    {
        if (writeGate is null)
            return await RunAsync(session, action, cancellationToken).ConfigureAwait(false);
        return await RunAsync(
            session,
            async token =>
            {
                if (session.LocalAccountId != context.AccountId ||
                    context.AuthorizationBinding is not { } binding)
                    return NotValidated<T>();
                var request = WriteRequest(context, session, AdapterWriteAction.SubmitShare);
                if (!await writeGate.TryValidateBindingAsync(request, binding, token).ConfigureAwait(false))
                    return NotValidated<T>();
                return await action(token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private AdapterWriteAuthorizationRequest WriteRequest(
        ShareOperationAutomationContext context,
        AccountSession session,
        AdapterWriteAction action) => new(
        capability!, session.LocalAccountId ?? Guid.Empty, context.OperationId, context.Generation,
        context.BatchId, action, context.IsRecovery);

    protected static BaiduAdapterResult<T> NotValidated<T>() =>
        BaiduAdapterResult<T>.Failure(
            BaiduErrorKind.NotValidated,
            adapterDiagnostic: null,
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);
}

public sealed class CoordinatedOperationBoundBaiduShareAdapter(
    IOperationBoundBaiduShareAdapter inner,
    BaiduAdapterConcurrencyGate gate,
    IAdapterWriteAuthorizationGate? writeGate = null,
    AdapterCapabilityVersion? capability = null) : CoordinatedBaiduShareAdapter(inner, gate, writeGate, capability), IOperationBoundBaiduShareAdapter
{
    private readonly IOperationBoundBaiduShareAdapter inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ConcurrentDictionary<ShareBindingKey, AdapterWriteAuthorizationBinding> bindings = [];

    public async Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(
        ShareOperationAutomationContext context,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        var key = ShareBindingKey.From(context);
        var keepBinding = false;
        try
        {
            var prepared = await RunBindingCreateAsync(
                context,
                session,
                (boundContext, token) => inner.PreparePermanentShareForOperationAsync(
                    boundContext, session, request, token),
                cancellationToken,
                boundContext => bindings.TryAdd(key, boundContext.AuthorizationBinding!))
                .ConfigureAwait(false);
            keepBinding = prepared.IsSuccess;
            return prepared;
        }
        finally
        {
            if (!keepBinding) bindings.TryRemove(key, out _);
        }
    }

    public async Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(
        ShareOperationAutomationContext context,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasWriteGate)
            return await RunBindingValidateAsync(
                context,
                session,
                token => inner.SubmitPermanentShareForOperationAsync(
                    context, session, request, token),
                cancellationToken).ConfigureAwait(false);
        var key = ShareBindingKey.From(context);
        if (!bindings.TryGetValue(key, out var binding) ||
            context.AuthorizationBinding is { } supplied && supplied != binding)
            return NotValidated<ShareCreationReceipt>();
        var boundContext = context with { AuthorizationBinding = binding };
        try
        {
            return await RunBindingValidateAsync(
                boundContext,
                session,
                token => inner.SubmitPermanentShareForOperationAsync(
                    boundContext, session, request, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bindings.TryRemove(key, out _);
        }
    }

    public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(
        ShareOperationAutomationContext context,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(
            session,
            token => inner.ConfirmExactShareForOperationAsync(context, session, request, token),
            cancellationToken);

    private readonly record struct ShareBindingKey(
        Guid AccountId,
        Guid OperationId,
        int Generation,
        Guid BatchId)
    {
        public static ShareBindingKey From(ShareOperationAutomationContext context) =>
            new(context.AccountId, context.OperationId, context.Generation, context.BatchId);
    }
}
