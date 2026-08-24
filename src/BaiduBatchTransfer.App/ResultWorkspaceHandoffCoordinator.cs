namespace BaiduBatchTransfer.App;

using BaiduBatchTransfer.App.Features.Jobs;

public interface IResultWorkspaceHandoffOperations
{
    Task<Guid?> FindPendingBatchAsync(CancellationToken cancellationToken);
    Task InitializeAsync(Guid batchJobId, CancellationToken cancellationToken);
    Task ResolveExistingSharesAsync(Guid batchJobId, CancellationToken cancellationToken);
    Task NavigateToResultsAsync(Guid batchJobId, CancellationToken cancellationToken);
}

public delegate Task ResultWorkspaceHandoffDispatchAsync(
    Func<CancellationToken, Task> operation,
    CancellationToken cancellationToken);

public sealed class ResultWorkspaceHandoffCoordinator : IDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly IResultWorkspaceHandoffOperations operations;
    private readonly ResultWorkspaceHandoffDispatchAsync dispatchAsync;
    private readonly Action<Exception>? reportException;
    private CancellationTokenSource? activeRequest;
    private Request? activeRestore;
    private Task? restoreTask;
    private long generation;
    private bool isClosed;

    public ResultWorkspaceHandoffCoordinator(
        IResultWorkspaceHandoffOperations operations,
        Action<Exception>? reportException = null)
        : this(operations, static (operation, cancellationToken) => operation(cancellationToken), reportException)
    {
    }

    public ResultWorkspaceHandoffCoordinator(
        IResultWorkspaceHandoffOperations operations,
        ResultWorkspaceHandoffDispatchAsync dispatchAsync,
        Action<Exception>? reportException = null)
    {
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.dispatchAsync = dispatchAsync ?? throw new ArgumentNullException(nameof(dispatchAsync));
        this.reportException = reportException;
    }

    public bool IsClosed
    {
        get
        {
            lock (sync) return isClosed;
        }
    }

    public Task ActivateJobAsync(Guid batchJobId)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        Request? request;
        lock (sync) request = BeginRequest();
        return request is null ? Task.CompletedTask : ActivateJobCoreAsync(request, batchJobId);
    }

    public Task RestorePendingAsync()
    {
        lock (sync)
        {
            if (isClosed) return Task.CompletedTask;
            if (activeRequest is not null && !ReferenceEquals(activeRestore?.Cancellation, activeRequest))
                return Task.CompletedTask;
            if (restoreTask is not null) return restoreTask;
            var request = BeginRequest();
            if (request is null) return Task.CompletedTask;
            activeRestore = request;
            restoreTask = RestorePendingCoreAsync(request);
            return restoreTask;
        }
    }

    public void Close()
    {
        lock (sync)
        {
            if (isClosed) return;
            isClosed = true;
            generation++;
            activeRequest?.Cancel();
            lifetime.Cancel();
        }
    }

    public void Dispose()
    {
        Close();
        lifetime.Dispose();
    }

    private Request? BeginRequest()
    {
        if (isClosed) return null;
        activeRequest?.Cancel();
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeRequest = requestCancellation;
        return new Request(++generation, requestCancellation);
    }

    private async Task ActivateJobCoreAsync(Request request, Guid batchJobId)
    {
        try
        {
            await gate.WaitAsync(request.Cancellation.Token).ConfigureAwait(false);
            try
            {
                if (!IsCurrent(request)) return;
                await InvokeOnDispatcherAsync(
                    request,
                    cancellationToken => operations.InitializeAsync(batchJobId, cancellationToken)).ConfigureAwait(false);
                await NavigateIfCurrentAsync(request, batchJobId).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Report(exception);
        }
        finally
        {
            Complete(request);
        }
    }

    private async Task RestorePendingCoreAsync(Request request)
    {
        await Task.Yield();
        try
        {
            await gate.WaitAsync(request.Cancellation.Token).ConfigureAwait(false);
            try
            {
                if (!IsCurrent(request)) return;
                Guid? batchJobId = null;
                await InvokeOnDispatcherAsync(
                    request,
                    async cancellationToken =>
                    {
                        batchJobId = await operations.FindPendingBatchAsync(cancellationToken).ConfigureAwait(true);
                    }).ConfigureAwait(false);
                if (batchJobId is not { } batch || !IsCurrent(request)) return;
                await InvokeOnDispatcherAsync(
                    request,
                    cancellationToken => operations.InitializeAsync(batch, cancellationToken)).ConfigureAwait(false);
                if (!IsCurrent(request)) return;
                await ResolveIfCurrentAsync(request, batch).ConfigureAwait(false);
                await NavigateIfCurrentAsync(request, batch).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Report(exception);
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(activeRestore, request))
                {
                    activeRestore = null;
                    restoreTask = null;
                }
            }
            Complete(request);
        }
    }

    private Task ResolveIfCurrentAsync(Request request, Guid batchJobId)
    {
        lock (sync)
        {
            if (!IsCurrentLocked(request)) return Task.CompletedTask;
        }
        return InvokeOnDispatcherAsync(
            request,
            cancellationToken => operations.ResolveExistingSharesAsync(batchJobId, cancellationToken));
    }

    private Task NavigateIfCurrentAsync(Request request, Guid batchJobId)
    {
        lock (sync)
        {
            if (!IsCurrentLocked(request)) return Task.CompletedTask;
        }
        return InvokeOnDispatcherAsync(
            request,
            cancellationToken => operations.NavigateToResultsAsync(batchJobId, cancellationToken));
    }

    private Task InvokeOnDispatcherAsync(
        Request request,
        Func<CancellationToken, Task> operation) =>
        dispatchAsync(
            cancellationToken => IsCurrent(request)
                ? operation(cancellationToken)
                : Task.CompletedTask,
            request.Cancellation.Token);

    private bool IsCurrent(Request request)
    {
        lock (sync) return IsCurrentLocked(request);
    }

    private bool IsCurrentLocked(Request request) =>
        !isClosed &&
        generation == request.Generation &&
        ReferenceEquals(activeRequest, request.Cancellation) &&
        !request.Cancellation.IsCancellationRequested;

    private void Complete(Request request)
    {
        lock (sync)
        {
            if (ReferenceEquals(activeRequest, request.Cancellation)) activeRequest = null;
        }
        request.Cancellation.Dispose();
    }

    private void Report(Exception exception)
    {
        try { reportException?.Invoke(exception); }
        catch { }
    }

    private sealed record Request(long Generation, CancellationTokenSource Cancellation);
}

internal sealed class JobResultsWorkspaceHandoffOperations(
    JobResultsViewModel resultsViewModel,
    Func<Guid, CancellationToken, Task> navigateToResults) : IResultWorkspaceHandoffOperations
{
    public Task<Guid?> FindPendingBatchAsync(CancellationToken cancellationToken) =>
        resultsViewModel.FindPendingExistingShareBatchAsync(cancellationToken);

    public Task InitializeAsync(Guid batchJobId, CancellationToken cancellationToken) =>
        resultsViewModel.InitializeAsync(batchJobId, cancellationToken);

    public Task ResolveExistingSharesAsync(Guid batchJobId, CancellationToken cancellationToken) =>
        resultsViewModel.ResolvePendingExistingSharesAsync(batchJobId, cancellationToken);

    public Task NavigateToResultsAsync(Guid batchJobId, CancellationToken cancellationToken) =>
        navigateToResults(batchJobId, cancellationToken);
}
