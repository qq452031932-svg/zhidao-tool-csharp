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
        var expected = new LocalHistoryCleanupPreview(7, HasConflicts: false);
        var store = new RecordingStore { PreviewResult = expected };

        var actual = await new LocalTerminalHistoryService(store)
            .PreviewAsync(kind, cancellation.Token);

        Assert.Same(expected, actual);
        Assert.Equal(kind, store.PreviewKind);
        Assert.Equal(cancellation.Token, store.PreviewToken);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
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
            PreviewResult = new LocalHistoryCleanupPreview(0, HasConflicts: false),
        };

        var result = await new LocalTerminalHistoryService(store)
            .DeleteAsync(LocalHistoryKind.Transfer);

        Assert.Equal(new LocalHistoryCleanupResult(0, LocalHistoryCleanupDisposition.NothingToDelete), result);
        Assert.Equal(0, store.DeleteCalls);
    }

    [Fact]
    public async Task DeleteReturnsConflictWithoutCallingDeleteForConflictingPreview()
    {
        var store = new RecordingStore
        {
            PreviewResult = new LocalHistoryCleanupPreview(3, HasConflicts: true),
        };

        var result = await new LocalTerminalHistoryService(store)
            .DeleteAsync(LocalHistoryKind.StandaloneShare);

        Assert.Equal(new LocalHistoryCleanupResult(0, LocalHistoryCleanupDisposition.Conflict), result);
        Assert.Equal(0, store.DeleteCalls);
    }

    [Fact]
    public async Task DeleteDelegatesKnownNonEmptyPreviewAndReturnsStoreOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new LocalHistoryCleanupResult(4, LocalHistoryCleanupDisposition.Deleted);
        var store = new RecordingStore
        {
            PreviewResult = new LocalHistoryCleanupPreview(4, HasConflicts: false),
            DeleteResult = expected,
        };

        var actual = await new LocalTerminalHistoryService(store)
            .DeleteAsync(LocalHistoryKind.StandaloneShare, cancellation.Token);

        Assert.Same(expected, actual);
        Assert.Equal(LocalHistoryKind.StandaloneShare, store.PreviewKind);
        Assert.Equal(LocalHistoryKind.StandaloneShare, store.DeleteKind);
        Assert.Equal(cancellation.Token, store.PreviewToken);
        Assert.Equal(cancellation.Token, store.DeleteToken);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task DeleteRejectsUnknownKindWithoutCallingStore(int value)
    {
        var store = new RecordingStore();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new LocalTerminalHistoryService(store).DeleteAsync((LocalHistoryKind)value));

        Assert.Equal(0, store.PreviewCalls);
        Assert.Equal(0, store.DeleteCalls);
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
            PreviewResult = new LocalHistoryCleanupPreview(1, HasConflicts: false),
            DeleteException = expected,
        };

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalTerminalHistoryService(store).DeleteAsync(LocalHistoryKind.Transfer));

        Assert.Same(expected, actual);
        Assert.Equal(1, store.DeleteCalls);
    }

    private sealed class RecordingStore : ILocalTerminalHistoryStore
    {
        public LocalHistoryCleanupPreview PreviewResult { get; init; } =
            new(0, HasConflicts: false);
        public LocalHistoryCleanupResult DeleteResult { get; init; } =
            new(0, LocalHistoryCleanupDisposition.NothingToDelete);
        public Exception? PreviewException { get; init; }
        public Exception? DeleteException { get; init; }
        public int PreviewCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public LocalHistoryKind? PreviewKind { get; private set; }
        public LocalHistoryKind? DeleteKind { get; private set; }
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
            LocalHistoryKind kind,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            DeleteKind = kind;
            DeleteToken = cancellationToken;
            return DeleteException is null
                ? Task.FromResult(DeleteResult)
                : Task.FromException<LocalHistoryCleanupResult>(DeleteException);
        }
    }
}
