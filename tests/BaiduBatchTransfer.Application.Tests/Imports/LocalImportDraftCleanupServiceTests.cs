using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Tests.Imports;

public sealed class LocalImportDraftCleanupServiceTests
{
    [Fact]
    public async Task EmptyImportSessionIdIsRejectedBeforeCallingStore()
    {
        var store = new RecordingStore();
        var service = new LocalImportDraftCleanupService(store);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync(Guid.Empty));

        Assert.Equal("importSessionId", exception.ParamName);
        Assert.Equal(0, store.DeleteCallCount);
    }

    [Fact]
    public async Task ValidImportSessionIdDelegatesExactlyOnce()
    {
        var expected = new LocalImportDraftCleanupResult(LocalImportDraftCleanupDisposition.Deleted, 7);
        var store = new RecordingStore { Result = expected };
        var service = new LocalImportDraftCleanupService(store);
        var importSessionId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        var actual = await service.DeleteAsync(importSessionId, cancellation.Token);

        Assert.Same(expected, actual);
        Assert.Equal(1, store.DeleteCallCount);
        Assert.Equal(importSessionId, store.ImportSessionId);
        Assert.Equal(cancellation.Token, store.CancellationToken);
    }

    [Fact]
    public async Task CancellationPropagatesFromStore()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new RecordingStore { CancelWhenCalled = true };
        var service = new LocalImportDraftCleanupService(store);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DeleteAsync(Guid.NewGuid(), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, store.DeleteCallCount);
        Assert.Equal(cancellation.Token, store.CancellationToken);
    }

    [Theory]
    [InlineData(LocalImportDraftCleanupDisposition.Deleted, 3)]
    [InlineData(LocalImportDraftCleanupDisposition.AlreadyAbsent, 0)]
    [InlineData(LocalImportDraftCleanupDisposition.BatchConflict, 0)]
    [InlineData(LocalImportDraftCleanupDisposition.LocalDependencyConflict, 0)]
    public async Task EveryDispositionRoundTrips(
        LocalImportDraftCleanupDisposition disposition,
        int deletedStagedRowCount)
    {
        var expected = new LocalImportDraftCleanupResult(disposition, deletedStagedRowCount);
        var service = new LocalImportDraftCleanupService(new RecordingStore { Result = expected });

        var actual = await service.DeleteAsync(Guid.NewGuid());

        Assert.Same(expected, actual);
        Assert.Equal(disposition, actual.Disposition);
        Assert.Equal(deletedStagedRowCount, actual.DeletedStagedRowCount);
    }

    [Fact]
    public void ResultModelIsClosedAndContainsOnlyNonSensitiveOutcomeData()
    {
        Assert.Equal(
            new[]
            {
                LocalImportDraftCleanupDisposition.Deleted,
                LocalImportDraftCleanupDisposition.AlreadyAbsent,
                LocalImportDraftCleanupDisposition.BatchConflict,
                LocalImportDraftCleanupDisposition.LocalDependencyConflict,
            },
            Enum.GetValues<LocalImportDraftCleanupDisposition>());
        Assert.Equal(
            new[] { "DeletedStagedRowCount", "Disposition" },
            typeof(LocalImportDraftCleanupResult)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name));

        var result = new LocalImportDraftCleanupResult(LocalImportDraftCleanupDisposition.Deleted, 2);

        Assert.Equal(
            "LocalImportDraftCleanupResult { Disposition = Deleted, DeletedStagedRowCount = 2 }",
            result.ToString());
    }

    [Theory]
    [InlineData(-1, LocalImportDraftCleanupDisposition.Deleted)]
    [InlineData(1, LocalImportDraftCleanupDisposition.AlreadyAbsent)]
    [InlineData(1, LocalImportDraftCleanupDisposition.BatchConflict)]
    [InlineData(1, LocalImportDraftCleanupDisposition.LocalDependencyConflict)]
    [InlineData(0, (LocalImportDraftCleanupDisposition)99)]
    public void ResultRejectsImpossibleValues(
        int deletedStagedRowCount,
        LocalImportDraftCleanupDisposition disposition)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalImportDraftCleanupResult(disposition, deletedStagedRowCount));
    }

    private sealed class RecordingStore : ILocalImportDraftCleanupStore
    {
        public LocalImportDraftCleanupResult Result { get; init; } =
            new(LocalImportDraftCleanupDisposition.AlreadyAbsent, 0);
        public bool CancelWhenCalled { get; init; }
        public int DeleteCallCount { get; private set; }
        public Guid ImportSessionId { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<LocalImportDraftCleanupResult> DeleteAsync(
            Guid importSessionId,
            CancellationToken cancellationToken = default)
        {
            DeleteCallCount++;
            ImportSessionId = importSessionId;
            CancellationToken = cancellationToken;
            return CancelWhenCalled
                ? Task.FromCanceled<LocalImportDraftCleanupResult>(cancellationToken)
                : Task.FromResult(Result);
        }
    }
}
