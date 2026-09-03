namespace BaiduBatchTransfer.App.Updates;

public sealed class ApplicationUpdateService : IApplicationUpdateService, IDisposable
{
    private readonly object sync = new();
    private readonly IApplicationUpdateRuntime runtime;
    private ApplicationUpdateSnapshot snapshot;
    private OperationSlot? activeOperation;
    private int disposed;

    public ApplicationUpdateService(IApplicationUpdateRuntime runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        snapshot = runtime.Snapshot;
        runtime.SnapshotChanged += OnRuntimeSnapshotChanged;
    }

    public ApplicationUpdateSnapshot Snapshot
    {
        get
        {
            lock (sync) return snapshot;
        }
    }

    public event EventHandler<ApplicationUpdateSnapshot>? SnapshotChanged;

    public Task<ApplicationUpdateSnapshot> CheckAsync(
        bool manual,
        CancellationToken cancellationToken) =>
        RunOperationAsync(UpdateOperationKind.Check, manual, cancellationToken);

    public Task<ApplicationUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken) =>
        RunOperationAsync(UpdateOperationKind.Download, manual: false, cancellationToken);

    public bool TryPrepareRestart()
    {
        ThrowIfDisposed();
        lock (sync)
        {
            if (activeOperation is not null)
                return false;
        }

        return runtime.TryPrepareRestart();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        OperationSlot? operation;
        lock (sync)
        {
            operation = activeOperation;
            if (operation is not null)
            {
                operation.CancelInProgress = true;
                if (IsTransient(snapshot.Status, operation.Kind))
                    snapshot = operation.StableSnapshot;
            }
        }

        runtime.SnapshotChanged -= OnRuntimeSnapshotChanged;
        if (operation is not null)
            CancelOperation(operation);
    }

    private async Task<ApplicationUpdateSnapshot> RunOperationAsync(
        UpdateOperationKind kind,
        bool manual,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        OperationSlot operation;
        var ownsOperation = false;
        lock (sync)
        {
            ThrowIfDisposed();
            if (activeOperation is null)
            {
                operation = new OperationSlot(kind, snapshot);
                activeOperation = operation;
                ownsOperation = true;
            }
            else if (activeOperation.Kind == kind && activeOperation.AcceptingWaiters)
            {
                operation = activeOperation;
            }
            else
            {
                return BusySnapshot(snapshot);
            }

            operation.WaiterCount++;
            if (kind == UpdateOperationKind.Check && manual)
                operation.ManualWaiterCount++;
        }

        if (ownsOperation)
            StartOperation(operation);

        try
        {
            var result = await operation.Completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return ProjectForCaller(result, kind, manual);
        }
        finally
        {
            ReleaseWaiter(operation, manual);
        }
    }

    private void StartOperation(OperationSlot operation)
    {
        Task<ApplicationUpdateSnapshot> runtimeOperation;
        try
        {
            runtimeOperation = operation.Kind == UpdateOperationKind.Check
                ? runtime.CheckAsync(operation.Cancellation.Token)
                : runtime.DownloadAsync(operation.Cancellation.Token);
        }
        catch (Exception exception)
        {
            runtimeOperation = Task.FromException<ApplicationUpdateSnapshot>(exception);
        }

        _ = runtimeOperation.ContinueWith(
            static (completed, state) =>
            {
                var completion = (OperationCompletion)state!;
                completion.Service.FinishOperation(completion.Operation, completed);
            },
            new OperationCompletion(this, operation),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void FinishOperation(
        OperationSlot operation,
        Task<ApplicationUpdateSnapshot> completed)
    {
        ApplicationUpdateSnapshot effectiveResult;
        ApplicationUpdateSnapshot? notification = null;
        var cancelled = completed.IsCanceled ||
            completed.Exception?.GetBaseException() is OperationCanceledException;

        lock (sync)
        {
            if (!ReferenceEquals(activeOperation, operation))
            {
                effectiveResult = snapshot;
            }
            else if (cancelled)
            {
                activeOperation = null;
                effectiveResult = operation.StableSnapshot;
                if (disposed == 0 && IsTransient(snapshot.Status, operation.Kind))
                {
                    snapshot = effectiveResult;
                    notification = effectiveResult;
                }
            }
            else
            {
                var returned = completed.IsCompletedSuccessfully
                    ? completed.Result
                    : ApplicationUpdateFailureClassifier.Classify(
                        completed.Exception!.GetBaseException(),
                        snapshot);
                var projected = ProjectForAllWaiters(returned, operation);
                activeOperation = null;

                if (disposed == 0 &&
                    (!operation.SawRuntimeEvent || IsTransient(snapshot.Status, operation.Kind)))
                {
                    snapshot = projected;
                    effectiveResult = projected;
                    notification = projected;
                }
                else
                {
                    effectiveResult = snapshot;
                }
            }
        }

        if (notification is not null)
            RaiseSnapshotChanged(notification);

        if (cancelled)
            operation.Completion.TrySetCanceled();
        else
            operation.Completion.TrySetResult(effectiveResult);
        TryDisposeCancellation(operation);
    }

    private void ReleaseWaiter(OperationSlot operation, bool manual)
    {
        ApplicationUpdateSnapshot? notification = null;
        var cancelOperation = false;
        lock (sync)
        {
            operation.WaiterCount--;
            if (operation.Kind == UpdateOperationKind.Check && manual)
                operation.ManualWaiterCount--;

            if (ReferenceEquals(activeOperation, operation) &&
                operation.WaiterCount == 0 &&
                !operation.Completion.Task.IsCompleted)
            {
                operation.CancelInProgress = true;
                operation.AcceptingWaiters = false;
                cancelOperation = true;
                if (IsTransient(snapshot.Status, operation.Kind))
                {
                    snapshot = operation.StableSnapshot;
                    notification = snapshot;
                }
            }
        }

        if (notification is not null)
            RaiseSnapshotChanged(notification);
        if (cancelOperation)
            CancelOperation(operation);
        else
            TryDisposeCancellation(operation);
    }

    private void CancelOperation(OperationSlot operation)
    {
        try
        {
            operation.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion won the race; there is no operation left to cancel.
        }
        finally
        {
            lock (sync) operation.CancelInProgress = false;
            TryDisposeCancellation(operation);
        }
    }

    private void TryDisposeCancellation(OperationSlot operation)
    {
        var shouldDispose = false;
        lock (sync)
        {
            if (!operation.CancellationDisposed &&
                !operation.CancelInProgress &&
                !ReferenceEquals(activeOperation, operation) &&
                operation.WaiterCount == 0)
            {
                operation.CancellationDisposed = true;
                shouldDispose = true;
            }
        }

        if (shouldDispose)
            operation.Cancellation.Dispose();
    }

    private void OnRuntimeSnapshotChanged(object? sender, ApplicationUpdateSnapshot value)
    {
        ApplicationUpdateSnapshot projected;
        lock (sync)
        {
            if (disposed != 0)
                return;

            if (activeOperation is not null)
                activeOperation.SawRuntimeEvent = true;
            projected = activeOperation is null
                ? value
                : ProjectForAllWaiters(value, activeOperation);
            snapshot = projected;
        }

        RaiseSnapshotChanged(projected);
    }

    private void RaiseSnapshotChanged(ApplicationUpdateSnapshot value)
    {
        var handlers = SnapshotChanged;
        if (handlers is null)
            return;

        foreach (EventHandler<ApplicationUpdateSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, value);
            }
            catch
            {
                // UI observers cannot corrupt the update state machine.
            }
        }
    }

    private static ApplicationUpdateSnapshot ProjectForCaller(
        ApplicationUpdateSnapshot value,
        UpdateOperationKind kind,
        bool manual) =>
        kind == UpdateOperationKind.Check &&
        value.Status == ApplicationUpdateStatus.NetworkUnavailable
            ? value with
            {
                UserMessage = manual
                    ? ApplicationUpdateMessages.NetworkUnavailable
                    : string.Empty
            }
            : value;

    private static ApplicationUpdateSnapshot ProjectForAllWaiters(
        ApplicationUpdateSnapshot value,
        OperationSlot operation) =>
        operation.Kind == UpdateOperationKind.Check &&
        value.Status == ApplicationUpdateStatus.NetworkUnavailable
            ? value with
            {
                UserMessage = operation.ManualWaiterCount > 0
                    ? ApplicationUpdateMessages.NetworkUnavailable
                    : string.Empty
            }
            : value;

    private static ApplicationUpdateSnapshot BusySnapshot(ApplicationUpdateSnapshot current) =>
        current with
        {
            Status = ApplicationUpdateStatus.Busy,
            UserMessage = ApplicationUpdateMessages.Busy
        };

    private static bool IsTransient(
        ApplicationUpdateStatus status,
        UpdateOperationKind kind) =>
        kind == UpdateOperationKind.Check
            ? status == ApplicationUpdateStatus.Checking
            : status == ApplicationUpdateStatus.Downloading;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private enum UpdateOperationKind
    {
        Check,
        Download
    }

    private sealed class OperationSlot
    {
        public OperationSlot(
            UpdateOperationKind kind,
            ApplicationUpdateSnapshot stableSnapshot)
        {
            Kind = kind;
            StableSnapshot = stableSnapshot;
        }

        public UpdateOperationKind Kind { get; }

        public ApplicationUpdateSnapshot StableSnapshot { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource<ApplicationUpdateSnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WaiterCount { get; set; }

        public int ManualWaiterCount { get; set; }

        public bool SawRuntimeEvent { get; set; }

        public bool AcceptingWaiters { get; set; } = true;

        public bool CancelInProgress { get; set; }

        public bool CancellationDisposed { get; set; }
    }

    private sealed record OperationCompletion(
        ApplicationUpdateService Service,
        OperationSlot Operation);
}
