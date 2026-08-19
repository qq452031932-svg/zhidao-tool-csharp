using System.Collections.Immutable;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class OperationExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SchedulerBoundaryModelsRedactPathsAndConfirmationDescriptors()
    {
        var snapshot = Snapshot("private-folder");
        var command = new SubmissionBoundaryCommand(
            snapshot.OperationId, snapshot.AccountId, "{\"source\":\"private-share\"}", Now.AddMinutes(5));

        Assert.DoesNotContain("/base", snapshot.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-folder", snapshot.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-share", command.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-share", new SubmissionBoundaryPersistence(command, "2026-08-19", Now).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsuresCompleteSnapshotPathAndPassesReturnedDirectoryIdentityToSave()
    {
        var snapshot = Snapshot(createFolderName: "outer");
        var timeline = new List<string>();
        var store = new RecordingExecutionStore(snapshot, timeline);
        var adapter = new RecordingAdapter(timeline);
        var executor = Executor(store, adapter);

        await executor.ExecuteAsync(snapshot.OperationId);

        Assert.Equal("/base/outer", adapter.EnsuredPath?.Value);
        Assert.Equal("directory-id", adapter.SavedDestination?.RemoteId);
        Assert.Equal(["Preparing", "Boundary", "Save", "Identifiers", "Succeeded"], timeline);
    }

    [Fact]
    public async Task DirectoryFailurePausesAccountWithoutCallingSaveOrCrossingSubmitBoundary()
    {
        var snapshot = Snapshot(createFolderName: "outer");
        var store = new RecordingExecutionStore(snapshot);
        var adapter = new RecordingAdapter
        {
            EnsureResults = new Queue<BaiduAdapterResult<RemoteDirectory>>(
                [BaiduAdapterResult<RemoteDirectory>.Failure(BaiduErrorKind.InvalidTargetPath, "cookie=secret")]),
        };

        await Executor(store, adapter).ExecuteAsync(snapshot.OperationId);

        Assert.Equal(0, adapter.SaveCalls);
        Assert.DoesNotContain(store.Events, item => item.Name == "Boundary");
        Assert.True(store.AccountPaused);
        Assert.Equal("InvalidTargetPath", store.AccountPauseCode);
        Assert.Equal("The target path is invalid.", store.AccountPauseMessage);
        Assert.DoesNotContain("secret", store.AccountPauseMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransientDirectoryFailureUsesThreeRetriesAndControlledDelays()
    {
        var snapshot = Snapshot(createFolderName: null);
        var store = new RecordingExecutionStore(snapshot);
        var failure = BaiduAdapterResult<RemoteDirectory>.Failure(BaiduErrorKind.TransientService, "private");
        var adapter = new RecordingAdapter
        {
            EnsureResults = new Queue<BaiduAdapterResult<RemoteDirectory>>(
                [failure, failure, failure, BaiduAdapterResult<RemoteDirectory>.Success(new RemoteDirectory("directory-id", new RemotePath("/base")))]),
        };
        var delay = new RecordingDelay();

        await Executor(store, adapter, delay: delay).ExecuteAsync(snapshot.OperationId);

        Assert.Equal(4, adapter.EnsureCalls);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delay.Delays);
        Assert.Contains(store.Events, item => item.Name == "Succeeded");
    }

    [Fact]
    public async Task TypedServerRetryDelayFlowsFromAdapterResultIntoActualDelay()
    {
        var snapshot = Snapshot(createFolderName: null);
        var store = new RecordingExecutionStore(snapshot);
        var adapter = new RecordingAdapter
        {
            EnsureResults = new Queue<BaiduAdapterResult<RemoteDirectory>>(
            [
                BaiduAdapterResult<RemoteDirectory>.Failure(
                    BaiduErrorKind.TransientService,
                    "untrusted response text 99",
                    TimeSpan.FromSeconds(23)),
                BaiduAdapterResult<RemoteDirectory>.Success(new RemoteDirectory("directory-id", new RemotePath("/base"))),
            ]),
        };
        var delay = new RecordingDelay();

        await Executor(store, adapter, delay: delay).ExecuteAsync(snapshot.OperationId);

        Assert.Equal([TimeSpan.FromSeconds(23)], delay.Delays);
        Assert.True(store.Succeeded);
    }

    [Fact]
    public async Task ResponseIdentifiersArePersistedBeforeTerminalResult()
    {
        var snapshot = Snapshot(createFolderName: null);
        var store = new RecordingExecutionStore(snapshot);
        var adapter = new RecordingAdapter();

        await Executor(store, adapter).ExecuteAsync(snapshot.OperationId);

        Assert.True(store.IdentifierOrder < store.SucceededOrder);
        Assert.Equal("request-1", store.AdapterRequestId);
        Assert.Equal(["remote-1"], store.RemoteItemIds);
    }

    [Fact]
    public async Task UncertainSaveFailureBecomesResultPendingAndRetainsSlot()
    {
        var snapshot = Snapshot(createFolderName: null);
        var store = new RecordingExecutionStore(snapshot);
        var adapter = new RecordingAdapter
        {
            SaveResult = BaiduAdapterResult<TransferReceipt>.Failure(BaiduErrorKind.TransientService, "response body secret"),
        };

        await Executor(store, adapter).ExecuteAsync(snapshot.OperationId);

        Assert.True(store.ResultPending);
        Assert.False(store.Succeeded);
        Assert.Equal("TransientService", store.FailureCode);
    }

    [Fact]
    public async Task AccountFailureReturnedAfterSubmitPausesAccountForReconciliation()
    {
        var snapshot = Snapshot(createFolderName: null);
        var store = new RecordingExecutionStore(snapshot);
        var adapter = new RecordingAdapter
        {
            SaveResult = BaiduAdapterResult<TransferReceipt>.Failure(
                BaiduErrorKind.InsufficientCapacity,
                "private response"),
        };

        await Executor(store, adapter).ExecuteAsync(snapshot.OperationId);

        Assert.Contains(store.Events, item => item.Name == "Boundary");
        Assert.True(store.AccountPaused);
        Assert.Equal("InsufficientCapacity", store.AccountPauseCode);
        Assert.Null(store.FailureCode);
    }

    [Fact]
    public async Task InvalidSnapshottedOuterDirectoryPausesAccountWithSafePathReason()
    {
        var snapshot = Snapshot(createFolderName: "../private");
        var store = new RecordingExecutionStore(snapshot);
        var adapter = new RecordingAdapter();

        await Executor(store, adapter).ExecuteAsync(snapshot.OperationId);

        Assert.Equal(0, adapter.EnsureCalls);
        Assert.Equal(0, adapter.SaveCalls);
        Assert.True(store.AccountPaused);
        Assert.Equal("InvalidTargetPath", store.AccountPauseCode);
        Assert.Equal("The target path is invalid.", store.AccountPauseMessage);
    }

    [Fact]
    public async Task ArgumentExceptionWhileBuildingSnapshottedPathCannotStrandPreparingWorker()
    {
        var snapshot = Snapshot(createFolderName: "invalid\0folder");
        var store = new RecordingExecutionStore(snapshot);
        var adapter = new RecordingAdapter();

        await Executor(store, adapter).ExecuteAsync(snapshot.OperationId);

        Assert.Equal(0, adapter.EnsureCalls);
        Assert.Equal(0, adapter.SaveCalls);
        Assert.True(store.AccountPaused);
        Assert.Equal("InvalidTargetPath", store.AccountPauseCode);
        Assert.Equal("The target path is invalid.", store.AccountPauseMessage);
    }

    [Fact]
    public async Task InvalidPersistedBasePathCannotCrashWorkerOrRetainItsReservation()
    {
        var snapshot = Snapshot(createFolderName: null);
        var store = new RecordingExecutionStore(snapshot)
        {
            LoadException = new ArgumentException("untrusted persisted path"),
        };
        var adapter = new RecordingAdapter();

        await Executor(store, adapter).ExecuteAsync(snapshot.OperationId);

        Assert.DoesNotContain(store.Events, item => item.Name == "Preparing");
        Assert.Equal(0, adapter.EnsureCalls);
        Assert.True(store.AccountPaused);
        Assert.Equal("InvalidTargetPath", store.AccountPauseCode);
        Assert.Equal("The target path is invalid.", store.AccountPauseMessage);
        Assert.DoesNotContain("untrusted", store.AccountPauseMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArgumentExceptionWhileEnsuringDirectoryCannotCrashOrStrandPreparingWorker()
    {
        var snapshot = Snapshot(createFolderName: null);
        var store = new RecordingExecutionStore(snapshot);
        var adapter = new RecordingAdapter
        {
            EnsureException = new ArgumentException("untrusted invalid directory response"),
        };

        await Executor(store, adapter).ExecuteAsync(snapshot.OperationId);

        Assert.Equal(1, adapter.EnsureCalls);
        Assert.Equal(0, adapter.SaveCalls);
        Assert.True(store.AccountPaused);
        Assert.Equal("InvalidTargetPath", store.AccountPauseCode);
        Assert.Equal("The target path is invalid.", store.AccountPauseMessage);
        Assert.DoesNotContain("untrusted", store.AccountPauseMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(BaiduErrorKind.ExpiredLogin)]
    [InlineData(BaiduErrorKind.InsufficientCapacity)]
    public async Task AccountScopedFailurePausesAccountWithoutConsumingRemainingWork(BaiduErrorKind errorKind)
    {
        var snapshot = Snapshot(createFolderName: null);
        var store = new RecordingExecutionStore(snapshot);
        var adapter = new RecordingAdapter
        {
            EnsureResults = new Queue<BaiduAdapterResult<RemoteDirectory>>(
                [BaiduAdapterResult<RemoteDirectory>.Failure(errorKind, "private-cookie")]),
        };

        await Executor(store, adapter).ExecuteAsync(snapshot.OperationId);

        Assert.True(store.AccountPaused);
        Assert.Equal(errorKind.ToString(), store.AccountPauseCode);
        Assert.Null(store.FailureCode);
        Assert.Equal(0, adapter.SaveCalls);
    }

    private static OperationExecutor Executor(
        RecordingExecutionStore store,
        RecordingAdapter adapter,
        IAsyncDelay? delay = null)
    {
        var sessions = new MemorySessionStore(store.Snapshot.AccountId);
        return new OperationExecutor(
            adapter,
            sessions,
            store,
            new AccountDailyLimitService(store, new FixedTimeProvider(Now)),
            RetryPolicy.Default,
            delay ?? new RecordingDelay(),
            new FixedTimeProvider(Now));
    }

    private static OperationExecutionSnapshot Snapshot(string? createFolderName) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ShareReference("share-id", "https://pan.baidu.com/s/abc", "a1b2"),
            new RemotePath("/base"),
            createFolderName,
            DuplicatePolicy.Skip,
            "operation-fingerprint");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingDelay : IAsyncDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySessionStore(Guid accountId) : IBaiduSessionStore
    {
        private readonly AccountSession session = new(
            accountId, "baidu-user", [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)], Now);
        public Task<AccountSession?> LoadAsync(string id, CancellationToken cancellationToken) => Task.FromResult<AccountSession?>(session);
        public Task RemoveAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(AccountSession value, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingExecutionStore(OperationExecutionSnapshot snapshot, List<string>? timeline = null)
        : IOperationExecutionStore, ISubmissionBoundaryStore
    {
        private int order;
        public OperationExecutionSnapshot Snapshot { get; } = snapshot;
        public Exception? LoadException { get; init; }
        public List<(int Order, string Name)> Events { get; } = [];
        public string? FailureCode { get; private set; }
        public string? FailureMessage { get; private set; }
        public string? AdapterRequestId { get; private set; }
        public IReadOnlyList<string> RemoteItemIds { get; private set; } = [];
        public int IdentifierOrder { get; private set; }
        public int SucceededOrder { get; private set; }
        public bool Succeeded { get; private set; }
        public bool ResultPending { get; private set; }
        public bool AccountPaused { get; private set; }
        public string? AccountPauseCode { get; private set; }
        public string? AccountPauseMessage { get; private set; }

        public Task<OperationExecutionSnapshot?> LoadAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            if (LoadException is not null) throw LoadException;
            return Task.FromResult<OperationExecutionSnapshot?>(Snapshot);
        }

        public Task MarkPreparingAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Events.Add((++order, "Preparing"));
            timeline?.Add("Preparing");
            return Task.CompletedTask;
        }

        public Task<SubmissionBoundaryResult> PersistAsync(SubmissionBoundaryPersistence persistence, CancellationToken cancellationToken = default)
        {
            Events.Add((++order, "Boundary"));
            timeline?.Add("Boundary");
            return Task.FromResult(new SubmissionBoundaryResult(true, true, 1));
        }

        public Task RecordSubmissionIdentifiersAsync(Guid operationId, string? requestId, IReadOnlyList<string> remoteItemIds, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            IdentifierOrder = ++order;
            Events.Add((order, "Identifiers"));
            timeline?.Add("Identifiers");
            AdapterRequestId = requestId;
            RemoteItemIds = remoteItemIds;
            return Task.CompletedTask;
        }

        public Task MarkSucceededAsync(Guid operationId, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Succeeded = true;
            SucceededOrder = ++order;
            Events.Add((order, "Succeeded"));
            timeline?.Add("Succeeded");
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid operationId, string code, string safeMessage, bool globallyInvalid, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            FailureCode = code;
            FailureMessage = safeMessage;
            Events.Add((++order, "Failed"));
            return Task.CompletedTask;
        }

        public Task MarkResultPendingAsync(Guid operationId, string code, string safeMessage, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            ResultPending = true;
            FailureCode = code;
            FailureMessage = safeMessage;
            Events.Add((++order, "ResultPending"));
            return Task.CompletedTask;
        }

        public Task MarkAccountUnavailableAsync(Guid operationId, string code, string safeMessage, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            AccountPaused = true;
            AccountPauseCode = code;
            AccountPauseMessage = safeMessage;
            Events.Add((++order, "AccountPaused"));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAdapter(List<string>? timeline = null) : IBaiduNetdiskAdapter
    {
        public Queue<BaiduAdapterResult<RemoteDirectory>> EnsureResults { get; init; } = [];
        public Exception? EnsureException { get; init; }
        public BaiduAdapterResult<TransferReceipt> SaveResult { get; init; } = BaiduAdapterResult<TransferReceipt>.Success(
            new TransferReceipt(Now, "request-1", ["remote-1"]));
        public RemotePath? EnsuredPath { get; private set; }
        public RemoteDirectory? SavedDestination { get; private set; }
        public int EnsureCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public List<(int Order, string Name)> Events { get; } = [];

        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken)
        {
            EnsureCalls++;
            EnsuredPath = path;
            if (EnsureException is not null) throw EnsureException;
            if (EnsureResults.TryDequeue(out var result)) return Task.FromResult(result);
            return Task.FromResult(BaiduAdapterResult<RemoteDirectory>.Success(new RemoteDirectory("directory-id", path)));
        }

        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken)
        {
            SaveCalls++;
            SavedDestination = destination;
            Events.Add((3, "Save"));
            timeline?.Add("Save");
            return Task.FromResult(SaveResult);
        }

        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
