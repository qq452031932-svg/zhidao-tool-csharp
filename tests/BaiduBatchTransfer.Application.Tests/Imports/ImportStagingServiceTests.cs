using System.Runtime.CompilerServices;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Imports;

public sealed class ImportStagingServiceTests
{
    [Fact]
    public void ImportRequestToStringRedactsSourceNameAndPath()
    {
        var request = new ImportRequest(Guid.NewGuid(), "Excel", @"C:\private\customer-list.xlsx");

        var text = request.ToString();

        Assert.DoesNotContain("private", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customer-list", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SourceName = [REDACTED]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanonicalRowsAreNormalizedAndNotesDoNotChangeDuplicateIdentity()
    {
        var store = new ImportStore();
        var service = CreateService(store, new SequenceResolver());
        var sessionId = Guid.NewGuid();
        var rows = ToAsync(
            new ImportSourceRow(1, "https://pan.baidu.com/s/same?pwd=code", Note: "first note"),
            new ImportSourceRow(2, "https://pan.baidu.com/s/same?pwd=code", Note: "different note"));

        await service.StageAsync(new ImportRequest(sessionId, "Text", "paste"), rows);

        Assert.Equal("Draft", store.Session!.Status);
        Assert.Equal(2, store.Session.TotalRows);
        Assert.Equal(1, store.Session.ReadyRows);
        Assert.Equal(1, store.Session.DuplicateRows);
        Assert.Equal("Ready", store.Rows[0].Disposition);
        Assert.Equal("Duplicate", store.Rows[1].Disposition);
        Assert.Equal(store.Rows[0].Id, store.Rows[1].DuplicateOfStagedRowId);
        Assert.Equal("different note", store.Rows[1].Note);
    }

    [Fact]
    public async Task PersistedStagingRecordToStringRedactsUserAndSecretFields()
    {
        var store = new ImportStore();
        var service = CreateService(store, new SequenceResolver());

        await service.StageAsync(
            new ImportRequest(Guid.NewGuid(), "Text", "paste"),
            ToAsync(new ImportSourceRow(
                1,
                "https://pan.baidu.com/s/secret-id?pwd=s3cr",
                Note: "private note")));

        var text = Assert.Single(store.Rows).ToString();
        Assert.DoesNotContain("secret-id", text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private note", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortLinkFailureRemainsNeedsResolutionAndRetryCanMakeItReady()
    {
        var store = new ImportStore();
        var resolver = new SequenceResolver(
            BaiduAdapterResult<ResolvedShareLink>.Failure(BaiduErrorKind.TransientService, "secret response"),
            BaiduAdapterResult<ResolvedShareLink>.Success(
                new ResolvedShareLink("https://pan.baidu.com/s/resolved", "resolved")));
        var service = CreateService(store, resolver);
        var sessionId = Guid.NewGuid();

        await service.StageAsync(
            new ImportRequest(sessionId, "Text", "paste"),
            ToAsync(new ImportSourceRow(1, "https://pan.baidu.com/scl/fi/short-token", "xy12")));

        Assert.Equal("NeedsResolution", Assert.Single(store.Rows).Disposition);
        Assert.DoesNotContain("secret", store.Rows[0].ValidationMessage, StringComparison.OrdinalIgnoreCase);

        await service.RetryNeedsResolutionAsync(sessionId);

        Assert.Equal("Ready", store.Rows[0].Disposition);
        Assert.Equal("resolved", store.Rows[0].NormalizedShareId);
        Assert.Equal(1, store.Session!.ReadyRows);
    }

    [Fact]
    public async Task ResolverTransportFailureIsStagedForRetryWithoutLeakingDiagnostics()
    {
        var store = new ImportStore();
        var service = CreateService(store, new ThrowingResolver());

        await service.StageAsync(
            new ImportRequest(Guid.NewGuid(), "Text", "paste"),
            ToAsync(new ImportSourceRow(1, "https://pan.baidu.com/scl/fi/short-token")));

        var row = Assert.Single(store.Rows);
        Assert.Equal("NeedsResolution", row.Disposition);
        Assert.Equal("The short link could not be resolved because the service is unavailable.", row.ValidationMessage);
        Assert.DoesNotContain("secret-token", row.ValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryPagesPastStillUnresolvedRows()
    {
        var unavailable = BaiduAdapterResult<ResolvedShareLink>.Failure(BaiduErrorKind.TransientService, null);
        var resolver = new SequenceResolver(
            unavailable, unavailable, unavailable,
            unavailable, unavailable,
            BaiduAdapterResult<ResolvedShareLink>.Success(new ResolvedShareLink("https://pan.baidu.com/s/third", "third")));
        var store = new ImportStore();
        var service = CreateService(store, resolver, chunkSize: 2);
        var sessionId = Guid.NewGuid();

        await service.StageAsync(
            new ImportRequest(sessionId, "Text", "paste"),
            ToAsync(
                new ImportSourceRow(1, "https://pan.baidu.com/scl/fi/one"),
                new ImportSourceRow(2, "https://pan.baidu.com/scl/fi/two"),
                new ImportSourceRow(3, "https://pan.baidu.com/scl/fi/three")));

        await service.RetryNeedsResolutionAsync(sessionId);

        Assert.Equal("NeedsResolution", store.Rows[0].Disposition);
        Assert.Equal("NeedsResolution", store.Rows[1].Disposition);
        Assert.Equal("Ready", store.Rows[2].Disposition);
    }

    [Fact]
    public async Task RetryClassifiesSamePageResolvedIdentityAsDuplicateOrConflict()
    {
        var unavailable = BaiduAdapterResult<ResolvedShareLink>.Failure(BaiduErrorKind.TransientService, null);
        var resolved = BaiduAdapterResult<ResolvedShareLink>.Success(
            new ResolvedShareLink("https://pan.baidu.com/s/shared", "shared"));
        var resolver = new SequenceResolver(unavailable, unavailable, unavailable, resolved, resolved, resolved);
        var store = new ImportStore();
        var service = CreateService(store, resolver, chunkSize: 3);
        var sessionId = Guid.NewGuid();

        await service.StageAsync(
            new ImportRequest(sessionId, "Text", "paste"),
            ToAsync(
                new ImportSourceRow(1, "https://pan.baidu.com/scl/fi/one", TargetPath: "/same"),
                new ImportSourceRow(2, "https://pan.baidu.com/scl/fi/two", TargetPath: "/same"),
                new ImportSourceRow(3, "https://pan.baidu.com/scl/fi/three", TargetPath: "/different")));

        await service.RetryNeedsResolutionAsync(sessionId);

        Assert.Equal("Ready", store.Rows[0].Disposition);
        Assert.Equal("Duplicate", store.Rows[1].Disposition);
        Assert.Equal(store.Rows[0].Id, store.Rows[1].DuplicateOfStagedRowId);
        Assert.Equal("Conflict", store.Rows[2].Disposition);
    }

    [Fact]
    public async Task LaterRetryPromotesEarliestSourceRowAndReclassifiesAllLaterRows()
    {
        var unavailable = BaiduAdapterResult<ResolvedShareLink>.Failure(BaiduErrorKind.TransientService, null);
        var resolved = BaiduAdapterResult<ResolvedShareLink>.Success(
            new ResolvedShareLink("https://pan.baidu.com/s/shared", "shared"));
        var resolver = new SequenceResolver(
            unavailable, unavailable, unavailable,
            unavailable, resolved, resolved,
            resolved);
        var store = new ImportStore();
        var service = CreateService(store, resolver, chunkSize: 1);
        var sessionId = Guid.NewGuid();

        await service.StageAsync(
            new ImportRequest(sessionId, "Text", "paste"),
            ToAsync(
                new ImportSourceRow(1, "https://pan.baidu.com/scl/fi/one", TargetPath: "/early"),
                new ImportSourceRow(2, "https://pan.baidu.com/scl/fi/two", TargetPath: "/late"),
                new ImportSourceRow(3, "https://pan.baidu.com/scl/fi/three", TargetPath: "/early")));

        await service.RetryNeedsResolutionAsync(sessionId);
        Assert.Equal(new[] { "NeedsResolution", "Ready", "Conflict" }, store.Rows.Select(row => row.Disposition));

        await service.RetryNeedsResolutionAsync(sessionId);

        Assert.Equal("Ready", store.Rows[0].Disposition);
        Assert.Equal("Conflict", store.Rows[1].Disposition);
        Assert.Equal("Duplicate", store.Rows[2].Disposition);
        Assert.Equal(store.Rows[0].Id, store.Rows[2].DuplicateOfStagedRowId);
        Assert.Equal(1, store.Session!.ReadyRows);
        Assert.Equal(1, store.Session.DuplicateRows);
        Assert.Equal(1, store.Session.InvalidRows);
    }

    [Fact]
    public async Task RetryReleasesReadUnitOfWorkBeforeWaitingForResolverNetwork()
    {
        var store = new ImportStore();
        var resolver = new BlockingRetryResolver();
        var service = CreateService(store, resolver);
        var sessionId = Guid.NewGuid();
        await service.StageAsync(
            new ImportRequest(sessionId, "Text", "paste"),
            ToAsync(new ImportSourceRow(1, "https://pan.baidu.com/scl/fi/one")));

        var retry = service.RetryNeedsResolutionAsync(sessionId);
        await resolver.RetryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(0, store.ActiveUnits);
            await using var checkpoint = await store.CreateUnitOfWorkAsync(CancellationToken.None);
            Assert.Equal(1, store.ActiveUnits);
        }
        finally
        {
            resolver.ReleaseRetry.TrySetResult();
        }

        await retry;
        Assert.Equal(0, store.ActiveUnits);
    }

    [Fact]
    public async Task StagingPersistsSessionFirstAndCommitsBoundedChunks()
    {
        var store = new ImportStore();
        var service = CreateService(store, new SequenceResolver(), chunkSize: 2);
        var rows = ToAsync(Enumerable.Range(1, 5)
            .Select(i => new ImportSourceRow(i, $"https://pan.baidu.com/s/id-{i}"))
            .ToArray());

        await service.StageAsync(new ImportRequest(Guid.NewGuid(), "Excel", "rows.xlsx"), rows);

        Assert.Equal("session", store.Events[0]);
        Assert.Equal(new[] { 2, 2, 1 }, store.AddBatchSizes);
        Assert.Equal(5, store.Session!.ReadyRows);
    }

    [Fact]
    public async Task SourceOverridesAndAccountFieldsAreCopiedAndDifferencesBecomeConflict()
    {
        var store = new ImportStore();
        var service = CreateService(store, new SequenceResolver());
        var accountId = Guid.NewGuid();

        await service.StageAsync(
            new ImportRequest(Guid.NewGuid(), "Excel", "rows.xlsx"),
            ToAsync(
                new ImportSourceRow(1, "https://pan.baidu.com/s/same", TargetPath: "/课程", CreateFolderName: "A", RequestedAccountName: "甲", MappedAccountId: accountId, Note: "one"),
                new ImportSourceRow(2, "https://pan.baidu.com/s/same", TargetPath: "/其他", CreateFolderName: "A", RequestedAccountName: "甲", MappedAccountId: accountId, Note: "two")));

        Assert.Equal("/课程", store.Rows[0].TargetPath);
        Assert.Equal("A", store.Rows[0].CreateFolderName);
        Assert.Equal("甲", store.Rows[0].RequestedAccountName);
        Assert.Equal(accountId, store.Rows[0].MappedAccountId);
        Assert.Equal("Conflict", store.Rows[1].Disposition);
        Assert.Equal(1, store.Session!.InvalidRows);
    }

    [Fact]
    public async Task InvalidTargetOverrideIsStagedInvalidInsteadOfThrowing()
    {
        var store = new ImportStore();
        var service = CreateService(store, new SequenceResolver());

        await service.StageAsync(
            new ImportRequest(Guid.NewGuid(), "Excel", "rows.xlsx"),
            ToAsync(new ImportSourceRow(1, "https://pan.baidu.com/s/id", TargetPath: "/bad:*")));

        Assert.Equal("Invalid", Assert.Single(store.Rows).Disposition);
        Assert.Equal("The target path is invalid.", store.Rows[0].ValidationMessage);
    }

    [Fact]
    public async Task CancellationLeavesCommittedProgressAsResumableDraft()
    {
        var store = new ImportStore();
        var service = CreateService(store, new SequenceResolver(), chunkSize: 2);
        using var cancellation = new CancellationTokenSource();

        async IAsyncEnumerable<ImportSourceRow> CancelAfterThree([EnumeratorCancellation] CancellationToken token = default)
        {
            for (var i = 1; i <= 5; i++)
            {
                if (i == 4) cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                yield return new ImportSourceRow(i, $"https://pan.baidu.com/s/id-{i}");
                await Task.Yield();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StageAsync(
            new ImportRequest(Guid.NewGuid(), "Excel", "rows.xlsx"),
            CancelAfterThree(cancellation.Token),
            cancellation.Token));

        Assert.NotNull(store.Session);
        Assert.Equal("Draft", store.Session!.Status);
        Assert.Equal(2, store.Session.TotalRows);
        Assert.Equal(2, store.Rows.Count);
    }

    [Fact]
    public async Task DraftCanResumeFromTheLastCommittedSourceRow()
    {
        var store = new ImportStore();
        var service = CreateService(store, new SequenceResolver(), chunkSize: 2);
        var sessionId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        async IAsyncEnumerable<ImportSourceRow> Interrupted([EnumeratorCancellation] CancellationToken token = default)
        {
            for (var i = 1; i <= 5; i++)
            {
                if (i == 4) cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                yield return new ImportSourceRow(i, $"https://pan.baidu.com/s/id-{i}");
                await Task.Yield();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StageAsync(
            new ImportRequest(sessionId, "Excel", "rows.xlsx"),
            Interrupted(cancellation.Token),
            cancellation.Token));

        await service.ResumeAsync(
            sessionId,
            ToAsync(Enumerable.Range(1, 5)
                .Select(i => new ImportSourceRow(i, $"https://pan.baidu.com/s/id-{i}"))
                .ToArray()));

        Assert.Equal(5, store.Rows.Count);
        Assert.Equal(5, store.Session!.TotalRows);
        Assert.Equal("Draft", store.Session.Status);
    }

    private static ImportStagingService CreateService(
        ImportStore store,
        IShareLinkResolver resolver,
        int chunkSize = 5_000) =>
        new(store.CreateUnitOfWorkAsync, resolver, chunkSize, maxResolutionConcurrency: 2);

    private static async IAsyncEnumerable<ImportSourceRow> ToAsync(params ImportSourceRow[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            await Task.Yield();
        }
    }

    private sealed class SequenceResolver(params BaiduAdapterResult<ResolvedShareLink>[] results) : IShareLinkResolver
    {
        private readonly Queue<BaiduAdapterResult<ResolvedShareLink>> results = new(results);

        public Task<BaiduAdapterResult<ResolvedShareLink>> ResolveAsync(string rawUrl, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(results.Count == 0
                ? BaiduAdapterResult<ResolvedShareLink>.Failure(BaiduErrorKind.TransientService, null)
                : results.Dequeue());
        }
    }

    private sealed class ThrowingResolver : IShareLinkResolver
    {
        public Task<BaiduAdapterResult<ResolvedShareLink>> ResolveAsync(string rawUrl, CancellationToken cancellationToken) =>
            throw new HttpRequestException("secret-token");
    }

    private sealed class BlockingRetryResolver : IShareLinkResolver
    {
        private int callCount;
        public TaskCompletionSource RetryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRetry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BaiduAdapterResult<ResolvedShareLink>> ResolveAsync(string rawUrl, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref callCount) == 1)
                return BaiduAdapterResult<ResolvedShareLink>.Failure(BaiduErrorKind.TransientService, null);
            RetryEntered.TrySetResult();
            await ReleaseRetry.Task.WaitAsync(cancellationToken);
            return BaiduAdapterResult<ResolvedShareLink>.Success(
                new ResolvedShareLink("https://pan.baidu.com/s/resolved", "resolved"));
        }
    }

    private sealed class ImportStore
    {
        public ImportSessionRecord? Session { get; set; }
        public List<StagedImportRowRecord> Rows { get; } = [];
        public List<string> Events { get; } = [];
        public List<int> AddBatchSizes { get; } = [];
        public int ActiveUnits { get; private set; }

        public Task<IUnitOfWork> CreateUnitOfWorkAsync(CancellationToken cancellationToken)
        {
            ActiveUnits++;
            return Task.FromResult<IUnitOfWork>(new Unit(this));
        }

        private sealed class Unit : IUnitOfWork
        {
            private readonly ImportStore store;
            private bool disposed;

            public Unit(ImportStore store)
            {
                this.store = store;
                Imports = new Repository(store);
            }

            public IImportRepository Imports { get; }
            public IJobRepository Jobs => throw new NotSupportedException();
            public IAccountRepository Accounts => throw new NotSupportedException();
            public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public ValueTask DisposeAsync()
            {
                if (!disposed)
                {
                    disposed = true;
                    store.ActiveUnits--;
                }
                return ValueTask.CompletedTask;
            }
        }

        private sealed class Repository(ImportStore store) : IImportRepository
        {
            public Task AddSessionAsync(ImportSessionRecord session, CancellationToken cancellationToken = default)
            {
                store.Session = session;
                store.Events.Add("session");
                return Task.CompletedTask;
            }

            public Task UpdateSessionAsync(ImportSessionRecord session, CancellationToken cancellationToken = default)
            {
                store.Session = session;
                return Task.CompletedTask;
            }

            public Task<ImportSessionRecord?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
                Task.FromResult(store.Session?.Id == id ? store.Session : null);

            public Task AddRowsAsync(IEnumerable<StagedImportRowRecord> rows, CancellationToken cancellationToken = default)
            {
                var batch = rows.ToArray();
                store.Rows.AddRange(batch);
                store.AddBatchSizes.Add(batch.Length);
                store.Events.Add("rows");
                return Task.CompletedTask;
            }

            public Task UpdateRowsAsync(IEnumerable<StagedImportRowRecord> rows, CancellationToken cancellationToken = default)
            {
                foreach (var row in rows)
                {
                    var index = store.Rows.FindIndex(existing => existing.Id == row.Id);
                    store.Rows[index] = row;
                }

                return Task.CompletedTask;
            }

            public Task<StagedImportRowRecord?> GetFirstByNormalizedShareIdAsync(Guid sessionId, string normalizedShareId, CancellationToken cancellationToken = default) =>
                Task.FromResult(store.Rows.FirstOrDefault(row =>
                    row.ImportSessionId == sessionId && row.NormalizedShareId == normalizedShareId));

            public Task<StagedImportRowRecord?> GetRowByIdAsync(Guid rowId, CancellationToken cancellationToken = default) =>
                Task.FromResult(store.Rows.FirstOrDefault(row => row.Id == rowId));

            public Task<int> GetLastSourceRowNumberAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
                Task.FromResult(store.Rows
                    .Where(row => row.ImportSessionId == sessionId)
                    .Select(row => row.SourceRowNumber)
                    .DefaultIfEmpty(0)
                    .Max());

            public Task<IReadOnlyList<StagedImportRowRecord>> GetRowsAfterSourceRowAsync(
                Guid sessionId,
                string disposition,
                int afterSourceRowNumber,
                int pageSize,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<StagedImportRowRecord>>(store.Rows
                    .Where(row => row.ImportSessionId == sessionId && row.Disposition == disposition && row.SourceRowNumber > afterSourceRowNumber)
                    .OrderBy(row => row.SourceRowNumber)
                    .Take(pageSize)
                    .ToArray());

            public Task<IReadOnlyList<StagedImportRowRecord>> GetRowsByNormalizedShareIdAfterSourceRowAsync(
                Guid sessionId,
                string normalizedShareId,
                int afterSourceRowNumber,
                int pageSize,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<StagedImportRowRecord>>(store.Rows
                    .Where(row => row.ImportSessionId == sessionId && row.NormalizedShareId == normalizedShareId && row.SourceRowNumber > afterSourceRowNumber)
                    .OrderBy(row => row.SourceRowNumber)
                    .Take(pageSize)
                    .ToArray());

            public Task<Page<StagedImportRowRecord>> GetRowsPageAsync(StagedImportRowQuery query, CancellationToken cancellationToken = default)
            {
                var filtered = store.Rows
                    .Where(row => row.ImportSessionId == query.ImportSessionId &&
                        (query.Disposition is null || row.Disposition == query.Disposition))
                    .OrderBy(row => row.SourceRowNumber)
                    .ToArray();
                var page = filtered.Skip(query.Offset).Take(query.PageSize).ToArray();
                return Task.FromResult(new Page<StagedImportRowRecord>(page, filtered.Length, query.PageNumber, query.PageSize));
            }
        }
    }
}
