using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class LocalTerminalHistoryServiceTests
{
    [Theory]
    [InlineData(LocalHistoryKind.Transfer)]
    [InlineData(LocalHistoryKind.StandaloneShare)]
    public async Task PreviewDelegatesKnownKindAndCancellationToken(LocalHistoryKind kind)
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new LocalHistoryCleanupPreview(kind, 7, HasConflicts: false, "opaque-snapshot");
        var store = new RecordingStore { PreviewResult = expected };

        var actual = await new LocalTerminalHistoryService(store)
            .PreviewAsync(kind, cancellation.Token);

        Assert.Same(expected, actual);
        Assert.Equal(kind, store.PreviewKind);
        Assert.Equal(cancellation.Token, store.PreviewToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task PreviewRejectsUnknownKindWithoutCallingStore(int value)
    {
        var store = new RecordingStore();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new LocalTerminalHistoryService(store).PreviewAsync((LocalHistoryKind)value));

        Assert.Equal(0, store.PreviewCalls);
    }

    [Fact]
    public async Task DeleteReturnsNothingToDeleteWithoutCallingDeleteForEmptyPreview()
    {
        var store = new RecordingStore
        {
            PreviewResult = new LocalHistoryCleanupPreview(
                LocalHistoryKind.Transfer, 0, HasConflicts: false, SnapshotToken: null),
        };

        var result = await new LocalTerminalHistoryService(store)
            .DeleteAsync(store.PreviewResult);

        Assert.Equal(new LocalHistoryCleanupResult(0, LocalHistoryCleanupDisposition.NothingToDelete), result);
        Assert.Equal(0, store.PreviewCalls);
        Assert.Equal(0, store.DeleteCalls);
    }

    [Fact]
    public async Task DeleteReturnsConflictWithoutCallingDeleteForConflictingPreview()
    {
        var store = new RecordingStore
        {
            PreviewResult = new LocalHistoryCleanupPreview(
                LocalHistoryKind.StandaloneShare, 3, HasConflicts: true, "conflicting-snapshot"),
        };

        var result = await new LocalTerminalHistoryService(store)
            .DeleteAsync(store.PreviewResult);

        Assert.Equal(new LocalHistoryCleanupResult(0, LocalHistoryCleanupDisposition.Conflict), result);
        Assert.Equal(0, store.PreviewCalls);
        Assert.Equal(0, store.DeleteCalls);
    }

    [Fact]
    public async Task DeleteDelegatesKnownNonEmptyPreviewAndReturnsStoreOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new LocalHistoryCleanupResult(4, LocalHistoryCleanupDisposition.Deleted);
        var store = new RecordingStore
        {
            PreviewResult = new LocalHistoryCleanupPreview(
                LocalHistoryKind.StandaloneShare, 4, HasConflicts: false, "confirmed-snapshot"),
            DeleteResult = expected,
        };

        var actual = await new LocalTerminalHistoryService(store)
            .DeleteAsync(store.PreviewResult, cancellation.Token);

        Assert.Same(expected, actual);
        Assert.Same(store.PreviewResult, store.DeletePreview);
        Assert.Equal(LocalHistoryKind.StandaloneShare, store.DeletePreview!.Kind);
        Assert.Equal(cancellation.Token, store.DeleteToken);
        Assert.Equal("confirmed-snapshot", store.DeleteSnapshotToken);
        Assert.Equal(0, store.PreviewCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    public void PreviewModelRejectsUnknownKind(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalHistoryCleanupPreview(
                (LocalHistoryKind)value, 0, HasConflicts: false, SnapshotToken: null));
    }

    [Fact]
    public async Task PreviewDoesNotSwallowStoreException()
    {
        var expected = new InvalidOperationException("preview failed");
        var store = new RecordingStore { PreviewException = expected };

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalTerminalHistoryService(store).PreviewAsync(LocalHistoryKind.Transfer));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task DeleteDoesNotSwallowStoreExceptionOrReportSuccess()
    {
        var expected = new InvalidOperationException("delete failed");
        var store = new RecordingStore
        {
            PreviewResult = new LocalHistoryCleanupPreview(
                LocalHistoryKind.Transfer, 1, HasConflicts: false, "opaque-snapshot"),
            DeleteException = expected,
        };

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalTerminalHistoryService(store).DeleteAsync(store.PreviewResult));

        Assert.Same(expected, actual);
        Assert.Equal(1, store.DeleteCalls);
    }

    [Fact]
    public async Task PreviewObservesPreCancellationBeforeCallingStore()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new RecordingStore();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalTerminalHistoryService(store)
                .PreviewAsync(LocalHistoryKind.Transfer, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, store.PreviewCalls);
    }

    [Fact]
    public async Task DeleteObservesPreCancellationBeforeCallingStore()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new RecordingStore
        {
            PreviewResult = new LocalHistoryCleanupPreview(
                LocalHistoryKind.Transfer, 1, HasConflicts: false, "opaque-snapshot"),
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalTerminalHistoryService(store)
                .DeleteAsync(store.PreviewResult, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, store.DeleteCalls);
    }

    [Fact]
    public async Task DeleteUsesCallerConfirmedSnapshotWithoutRepreviewing()
    {
        var confirmed = new LocalHistoryCleanupPreview(
            LocalHistoryKind.Transfer, 2, HasConflicts: false, "confirmed-snapshot-a");
        var store = new RecordingStore
        {
            PreviewResult = new LocalHistoryCleanupPreview(
                LocalHistoryKind.StandaloneShare, 3, HasConflicts: false, "later-snapshot-b"),
            DeleteResult = new LocalHistoryCleanupResult(2, LocalHistoryCleanupDisposition.Deleted),
        };

        await new LocalTerminalHistoryService(store)
            .DeleteAsync(confirmed);

        Assert.Equal(0, store.PreviewCalls);
        Assert.Same(confirmed, store.DeletePreview);
        Assert.Equal(LocalHistoryKind.Transfer, store.DeletePreview!.Kind);
        Assert.Equal("confirmed-snapshot-a", store.DeleteSnapshotToken);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task DeleteObservesPreCancellationBeforeEmptyOrConflictShortCircuit(
        int terminalBatchCount,
        bool hasConflicts)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var preview = new LocalHistoryCleanupPreview(
            LocalHistoryKind.Transfer,
            terminalBatchCount,
            hasConflicts,
            terminalBatchCount == 0 ? null : "opaque-snapshot");
        var store = new RecordingStore();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalTerminalHistoryService(store).DeleteAsync(preview, cancellation.Token));

        Assert.Equal(0, store.DeleteCalls);
    }

    [Fact]
    public async Task PreviewRejectsStoreResultForDifferentKind()
    {
        var store = new RecordingStore
        {
            PreviewResult = new LocalHistoryCleanupPreview(
                LocalHistoryKind.StandaloneShare, 0, HasConflicts: false, SnapshotToken: null),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalTerminalHistoryService(store).PreviewAsync(LocalHistoryKind.Transfer));
    }

    [Fact]
    public void PreviewRejectsNegativeTerminalBatchCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalHistoryCleanupPreview(
                LocalHistoryKind.Transfer, -1, HasConflicts: false, SnapshotToken: null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PreviewWithTerminalTargetsRequiresSnapshotToken(string? snapshotToken)
    {
        Assert.Throws<ArgumentException>(() =>
            new LocalHistoryCleanupPreview(
                LocalHistoryKind.Transfer, 1, HasConflicts: false, snapshotToken));
    }

    [Fact]
    public void PreviewRejectsOversizedSnapshotToken()
    {
        var token = new string('a', LocalHistoryCleanupPreview.MaximumSnapshotTokenLength + 1);

        Assert.Throws<ArgumentException>(() =>
            new LocalHistoryCleanupPreview(
                LocalHistoryKind.Transfer, 1, HasConflicts: false, token));
    }

    [Theory]
    [InlineData(0, LocalHistoryCleanupDisposition.Deleted)]
    [InlineData(-1, LocalHistoryCleanupDisposition.Deleted)]
    [InlineData(1, LocalHistoryCleanupDisposition.NothingToDelete)]
    [InlineData(1, LocalHistoryCleanupDisposition.Conflict)]
    [InlineData(0, LocalHistoryCleanupDisposition.Unspecified)]
    [InlineData(0, (LocalHistoryCleanupDisposition)4)]
    public void ResultRejectsInconsistentCountOrDisposition(
        int deletedBatchCount,
        LocalHistoryCleanupDisposition disposition)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new LocalHistoryCleanupResult(deletedBatchCount, disposition));
    }

    private sealed class RecordingStore : ILocalTerminalHistoryStore
    {
        public LocalHistoryCleanupPreview PreviewResult { get; init; } =
            new(LocalHistoryKind.Transfer, 0, HasConflicts: false, SnapshotToken: null);
        public LocalHistoryCleanupResult DeleteResult { get; init; } =
            new(0, LocalHistoryCleanupDisposition.NothingToDelete);
        public Exception? PreviewException { get; init; }
        public Exception? DeleteException { get; init; }
        public int PreviewCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public LocalHistoryKind? PreviewKind { get; private set; }
        public LocalHistoryCleanupPreview? DeletePreview { get; private set; }
        public string? DeleteSnapshotToken { get; private set; }
        public CancellationToken PreviewToken { get; private set; }
        public CancellationToken DeleteToken { get; private set; }

        public Task<LocalHistoryCleanupPreview> PreviewAsync(
            LocalHistoryKind kind,
            CancellationToken cancellationToken = default)
        {
            PreviewCalls++;
            PreviewKind = kind;
            PreviewToken = cancellationToken;
            return PreviewException is null
                ? Task.FromResult(PreviewResult)
                : Task.FromException<LocalHistoryCleanupPreview>(PreviewException);
        }

        public Task<LocalHistoryCleanupResult> DeleteAsync(
            LocalHistoryCleanupPreview confirmedPreview,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            DeletePreview = confirmedPreview;
            DeleteSnapshotToken = confirmedPreview.SnapshotToken;
            DeleteToken = cancellationToken;
            return DeleteException is null
                ? Task.FromResult(DeleteResult)
                : Task.FromException<LocalHistoryCleanupResult>(DeleteException);
        }
    }
}
