using System.Collections.Concurrent;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public sealed class WebAutomationBatchCancellation(
    IWebAutomationBatchCancellationStore store,
    TimeProvider timeProvider) : IWebAutomationBatchCancellation, IDisposable
{
    private readonly ConcurrentDictionary<BatchKey, CancellationTokenSource> signals = [];
    private int disposed;

    public async Task<bool> CancelAsync(
        WebAutomationRequestIdentity identity,
        long expectedAttemptRevision,
        WebAutomationAttemptState expectedResumeStage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        ThrowIfDisposed();
        var disposition = await store.CancelAsync(
            identity.OperationId,
            identity.BatchId,
            identity.AccountId,
            identity.Generation,
            identity.OwnerKind,
            expectedAttemptRevision,
            expectedResumeStage,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        switch (disposition)
        {
            case WebAutomationBatchCancellationDisposition.Cancelled:
            case WebAutomationBatchCancellationDisposition.AlreadyCancelled:
                Signal(identity.BatchId, identity.OwnerKind);
                return true;
            case WebAutomationBatchCancellationDisposition.StaleHandoff:
                return false;
            case WebAutomationBatchCancellationDisposition.Terminal:
                throw new InvalidOperationException("批次已经结束，不能再取消。");
            case WebAutomationBatchCancellationDisposition.OwnershipMismatch:
                throw new InvalidOperationException("浏览器操作不属于要取消的批次。");
            default:
                throw new InvalidOperationException("未知的批次取消结果。");
        }
    }

    public async Task<bool> IsCancelledAsync(
        Guid batchId,
        WebAutomationOwnerKind ownerKind,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var cancelled = await store.IsCancelledAsync(batchId, ownerKind, cancellationToken)
            .ConfigureAwait(false);
        if (cancelled) Signal(batchId, ownerKind);
        return cancelled;
    }

    public IDisposable Register(
        Guid batchId,
        WebAutomationOwnerKind ownerKind,
        Action cancellationRequested)
    {
        if (batchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchId));
        if (!Enum.IsDefined(ownerKind)) throw new ArgumentOutOfRangeException(nameof(ownerKind));
        ArgumentNullException.ThrowIfNull(cancellationRequested);
        ThrowIfDisposed();
        var signal = signals.GetOrAdd(new(batchId, Normalize(ownerKind)), _ => new());
        return signal.Token.Register(cancellationRequested);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var signal in signals.Values) signal.Dispose();
        signals.Clear();
    }

    private void Signal(Guid batchId, WebAutomationOwnerKind ownerKind)
    {
        var signal = signals.GetOrAdd(new(batchId, Normalize(ownerKind)), _ => new());
        if (!signal.IsCancellationRequested) signal.Cancel();
    }

    private static WebAutomationOwnerKind Normalize(WebAutomationOwnerKind ownerKind) =>
        ownerKind == WebAutomationOwnerKind.StandaloneShare
            ? WebAutomationOwnerKind.StandaloneShare
            : WebAutomationOwnerKind.Transfer;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private readonly record struct BatchKey(Guid BatchId, WebAutomationOwnerKind OwnerKind);
}
