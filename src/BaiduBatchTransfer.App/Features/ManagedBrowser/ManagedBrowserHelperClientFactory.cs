namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal interface IManagedBrowserHelperConnection : IAsyncDisposable
{
    Task<ManagedBrowserResponse> SendAsync(
        ManagedBrowserRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ManagedBrowserOperationHelper : IAsyncDisposable
{
    private IManagedBrowserHelperConnection? connection;
    private readonly Action released;

    internal ManagedBrowserOperationHelper(
        IManagedBrowserHelperConnection connection,
        Action released)
    {
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        this.released = released ?? throw new ArgumentNullException(nameof(released));
    }

    internal Task<ManagedBrowserResponse> SendAsync(
        ManagedBrowserRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = Volatile.Read(ref connection) ??
            throw new ObjectDisposedException(nameof(ManagedBrowserOperationHelper));
        return current.SendAsync(request, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        var owned = Interlocked.Exchange(ref connection, null);
        if (owned is null) return;
        try
        {
            await owned.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            released();
        }
    }
}

/// <summary>
/// Starts one helper only when an exact operation begins. Resolving this factory is process-free;
/// the returned operation owner is the sole lifetime owner of its helper connection.
/// </summary>
internal sealed class ManagedBrowserHelperClientFactory : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Func<CancellationToken, Task<IManagedBrowserHelperConnection>> start;
    private readonly HashSet<Guid> activeOperationIds = [];
    private readonly Dictionary<Guid, ManagedBrowserOperationHelper> activeHelpers = [];
    private bool disposed;

    internal ManagedBrowserHelperClientFactory(string helperRoot)
        : this(async cancellationToken => new ClientConnection(
            await ManagedBrowserHelperClient.StartAsync(helperRoot, cancellationToken)
                .ConfigureAwait(false)))
    {
        if (string.IsNullOrWhiteSpace(helperRoot))
            throw new ArgumentException("A helper package root is required.", nameof(helperRoot));
    }

    internal ManagedBrowserHelperClientFactory(
        Func<CancellationToken, Task<IManagedBrowserHelperConnection>> start) =>
        this.start = start ?? throw new ArgumentNullException(nameof(start));

    internal async ValueTask<ManagedBrowserOperationHelper> CreateAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!activeOperationIds.Add(operationId))
                throw new InvalidOperationException("This operation already owns a helper.");
        }

        IManagedBrowserHelperConnection? connection = null;
        try
        {
            connection = await start(cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException("The helper connection was not created.");
            ManagedBrowserOperationHelper? helper = null;
            helper = new(connection, () => Release(operationId, helper!));
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(ManagedBrowserHelperClientFactory));
                activeHelpers.Add(operationId, helper);
            }
            connection = null;
            return helper;
        }
        catch
        {
            lock (sync) activeOperationIds.Remove(operationId);
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        ManagedBrowserOperationHelper[] helpers;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            helpers = activeHelpers.Values.ToArray();
        }
        foreach (var helper in helpers)
            await helper.DisposeAsync().ConfigureAwait(false);
    }

    private void Release(Guid operationId, ManagedBrowserOperationHelper helper)
    {
        lock (sync)
        {
            if (activeHelpers.TryGetValue(operationId, out var current) && ReferenceEquals(current, helper))
                activeHelpers.Remove(operationId);
            activeOperationIds.Remove(operationId);
        }
    }

    private sealed class ClientConnection(ManagedBrowserHelperClient client) : IManagedBrowserHelperConnection
    {
        private ManagedBrowserHelperClient? client = client ?? throw new ArgumentNullException(nameof(client));

        public Task<ManagedBrowserResponse> SendAsync(
            ManagedBrowserRequest request,
            CancellationToken cancellationToken)
        {
            var current = Volatile.Read(ref client) ??
                throw new ObjectDisposedException(nameof(ClientConnection));
            return current.SendAsync(request, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            var owned = Interlocked.Exchange(ref client, null);
            return owned is null ? ValueTask.CompletedTask : owned.DisposeAsync();
        }
    }
}
