using System.Runtime.CompilerServices;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Application.Tests.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using System.Text.Json;

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
    public async Task RetryPreservesExcludedEarliestRowAndChoosesNextNonExcludedPrimary()
    {
        var unavailable = BaiduAdapterResult<ResolvedShareLink>.Failure(BaiduErrorKind.TransientService, null);
        var resolved = BaiduAdapterResult<ResolvedShareLink>.Success(
            new ResolvedShareLink("https://pan.baidu.com/s/shared", "shared"));
        var store = new ImportStore();
        var service = CreateService(store, new SequenceResolver(unavailable, resolved), chunkSize: 1);
        var sessionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        await service.StageAsync(
            new ImportRequest(sessionId, "Text", "paste"),
            ToAsync(
                new ImportSourceRow(1, "https://pan.baidu.com/s/shared", "a1b2", "/keep", "outer", "account", accountId, "keep-note"),
                new ImportSourceRow(2, "https://pan.baidu.com/scl/fi/later", TargetPath: "/later")));
        var excluded = ExcludeRow(store, 0, "user excluded earliest");

        await service.RetryNeedsResolutionAsync(sessionId);

        Assert.Equal(excluded, store.Rows[0]);
        Assert.Equal("Ready", store.Rows[1].Disposition);
        Assert.Equal(1, store.Session!.ReadyRows);
        Assert.Equal(1, store.Session.ExcludedRows);
    }

    [Fact]
    public async Task RetryPreservesExcludedLaterRowAndItsUserFields()
    {
        var unavailable = BaiduAdapterResult<ResolvedShareLink>.Failure(BaiduErrorKind.TransientService, null);
        var resolved = BaiduAdapterResult<ResolvedShareLink>.Success(
            new ResolvedShareLink("https://pan.baidu.com/s/shared", "shared"));
        var store = new ImportStore();
        var service = CreateService(store, new SequenceResolver(unavailable, resolved), chunkSize: 1);
        var sessionId = Guid.NewGuid();

        await service.StageAsync(
            new ImportRequest(sessionId, "Text", "paste"),
            ToAsync(
                new ImportSourceRow(1, "https://pan.baidu.com/s/shared", TargetPath: "/same"),
                new ImportSourceRow(2, "https://pan.baidu.com/s/shared", "c0de", "/same", "keep-folder", "keep-account", Note: "keep-later-note"),
                new ImportSourceRow(3, "https://pan.baidu.com/scl/fi/trigger", TargetPath: "/same")));
        var excluded = ExcludeRow(store, 1, "user excluded later");

        await service.RetryNeedsResolutionAsync(sessionId);

        Assert.Equal(excluded, store.Rows[1]);
        Assert.Equal("Duplicate", store.Rows[2].Disposition);
        Assert.Equal(1, store.Session!.ReadyRows);
        Assert.Equal(1, store.Session.DuplicateRows);
        Assert.Equal(1, store.Session.ExcludedRows);
    }

    [Fact]
    public async Task RetryNeverReactivatesAnExistingGroupWhoseEveryRowIsExcluded()
    {
        var unavailable = BaiduAdapterResult<ResolvedShareLink>.Failure(BaiduErrorKind.TransientService, null);
        var resolved = BaiduAdapterResult<ResolvedShareLink>.Success(
            new ResolvedShareLink("https://pan.baidu.com/s/shared", "shared"));
        var store = new ImportStore();
        var service = CreateService(store, new SequenceResolver(unavailable, resolved), chunkSize: 1);
        var sessionId = Guid.NewGuid();

        await service.StageAsync(
            new ImportRequest(sessionId, "Text", "paste"),
            ToAsync(
                new ImportSourceRow(1, "https://pan.baidu.com/s/shared", Note: "excluded-one"),
                new ImportSourceRow(2, "https://pan.baidu.com/s/shared", Note: "excluded-two"),
                new ImportSourceRow(3, "https://pan.baidu.com/scl/fi/trigger")));
        var first = ExcludeRow(store, 0, "user excluded one");
        var second = ExcludeRow(store, 1, "user excluded two");

        await service.RetryNeedsResolutionAsync(sessionId);

        Assert.Equal(first, store.Rows[0]);
        Assert.Equal(second, store.Rows[1]);
        Assert.Equal("Ready", store.Rows[2].Disposition);
        Assert.Equal(1, store.Session!.ReadyRows);
        Assert.Equal(2, store.Session.ExcludedRows);
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
    public async Task ExcelStagingPersistsConfirmedMappingAndRawPayloadWithTheSameRowIdentity()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var service = new ImportStagingService(
            database.UnitOfWorkFactory.BeginAsync,
            new SequenceResolver(),
            chunkSize: 2,
            maxResolutionConcurrency: 2);
        var sessionId = Guid.NewGuid();
        var mapping = new ConfirmedImportColumnMapping(1,
        [
            new(0, TabularImportColumnRole.SourceTitle),
            new(1, TabularImportColumnRole.ShareUrl),
            new(2, TabularImportColumnRole.ExtractionCode),
            new(3, TabularImportColumnRole.Preserve),
            new(4, TabularImportColumnRole.Preserve),
        ]);
        var raw = new[] { "  标题  ", "说明 https://pan.baidu.com/s/id", " a1b2 ", "\t=RAW()  ", "" };

        await service.StageAsync(
            new ImportRequest(
                sessionId,
                "Excel",
                "rows.xlsx",
                ColumnMapping: mapping,
                SourceColumns:
                [
                    new(0, "标题"), new(1, "链接"), new(2, "提取码"), new(3, "原字段"), new(4, "空字段"),
                ]),
            ToAsync(new ImportSourceRow(
                2,
                "https://pan.baidu.com/s/id",
                "a1b2",
                SourceTitle: "标题",
                OriginalExtractedUrl: "https://pan.baidu.com/s/id",
                RawValues: raw)));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var session = await read.Imports.GetSessionAsync(sessionId);
        var definitions = await read.Imports.GetColumnDefinitionsAsync(sessionId);
        var staged = Assert.Single((await read.Imports.GetRowsPageAsync(new(sessionId, null, 1, 10))).Items);
        var payload = await read.Imports.GetRowPayloadAsync(staged.Id);

        Assert.Equal(mapping.Fingerprint, session!.ColumnMappingFingerprint);
        Assert.Equal(new[] { "Title", "ShareUrl", "ExtractionCode", "Preserve", "Preserve" }, definitions.Select(x => x.MappedRole));
        Assert.NotNull(payload);
        Assert.Equal(staged.Id, payload!.StagedImportRowId);
        Assert.Equal("标题", payload.SourceTitle);
        Assert.Equal("标题", payload.ResolvedOutputTitle);
        Assert.Equal("https://pan.baidu.com/s/id", payload.OriginalShareUrl);
        Assert.Equal(raw, JsonSerializer.Deserialize<string[]>(payload.RawValuesJson));
    }

    [Fact]
    public async Task ProgressReportsOnlyTheSessionAndChunksWhoseTransactionsCommitted()
    {
        var store = new ImportStore { FailCommitAttempt = 3 };
        var service = CreateService(store, new SequenceResolver(), chunkSize: 3);
        var progress = new List<(ImportCommittedProgress Progress, int CommitCount)>();
        var rows = ToAsync(Enumerable.Range(1, 7)
            .Select(i => new ImportSourceRow(i, $"https://pan.baidu.com/s/id-{i}"))
            .ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StageAsync(
            new ImportRequest(Guid.NewGuid(), "Excel", "rows.xlsx"),
            rows,
            new InlineProgress<ImportCommittedProgress>(value => progress.Add((value, store.CommitCount))),
            CancellationToken.None));

        Assert.Equal(
            [
                (new ImportCommittedProgress(ImportProgressKind.SessionCreated, 0), 1),
                (new ImportCommittedProgress(ImportProgressKind.ChunkCommitted, 3), 2),
            ],
            progress);
    }

    [Fact]
    public async Task LegacyPositionalDefaultCancellationCallsRemainUnambiguous()
    {
        var store = new ImportStore();
        var service = CreateService(store, new SequenceResolver());
        var sessionId = Guid.NewGuid();

        await service.StageAsync(
            new ImportRequest(sessionId, "Text", "paste"),
            ToAsync(new ImportSourceRow(1, "https://pan.baidu.com/s/id-1")),
            default);
        await service.ResumeAsync(
            sessionId,
            ToAsync(),
            TransferMode.Distribution,
            default);

        Assert.Equal("Draft", store.Session!.Status);
    }

    [Fact]
    public async Task ProgressObserverFailureCannotTurnCommittedWorkIntoImportFailure()
    {
        var store = new ImportStore();
        var service = CreateService(store, new SequenceResolver(), chunkSize: 2);

        await service.StageAsync(
            new ImportRequest(Guid.NewGuid(), "Text", "paste"),
            ToAsync(
                new ImportSourceRow(1, "https://pan.baidu.com/s/id-1"),
                new ImportSourceRow(2, "https://pan.baidu.com/s/id-2")),
            new InlineProgress<ImportCommittedProgress>(_ =>
                throw new InvalidOperationException("Observer failed.")),
            CancellationToken.None);

        Assert.Equal("Draft", store.Session!.Status);
        Assert.Equal(2, store.Session.TotalRows);
        Assert.Equal(2, store.Rows.Count);
    }

    [Fact]
    public async Task FailedSqliteChunkRollsBackRowsAndCountersAndStopsProgressAtLastCommit()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var failingFactory = new FailingCommitFactory(database.UnitOfWorkFactory, failCommitAttempt: 3);
        var service = new ImportStagingService(
            failingFactory.BeginAsync,
            new SequenceResolver(),
            chunkSize: 3,
            maxResolutionConcurrency: 2);
        var sessionId = Guid.NewGuid();
        var progress = new List<ImportCommittedProgress>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StageAsync(
            new ImportRequest(sessionId, "Excel", "rows.xlsx"),
            ToAsync(Enumerable.Range(1, 7)
                .Select(i => new ImportSourceRow(i, $"https://pan.baidu.com/s/id-{i}"))
                .ToArray()),
            new InlineProgress<ImportCommittedProgress>(progress.Add),
            CancellationToken.None));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var session = await read.Imports.GetSessionAsync(sessionId);
        var rows = await read.Imports.GetRowsPageAsync(new(sessionId, null, 1, 10));
        Assert.Equal(3, session!.TotalRows);
        Assert.Equal(3, session.ReadyRows);
        Assert.Equal(3, rows.TotalCount);
        foreach (var row in rows.Items) Assert.NotNull(await read.Imports.GetRowPayloadAsync(row.Id));
        Assert.Equal(
            [
                new ImportCommittedProgress(ImportProgressKind.SessionCreated, 0),
                new ImportCommittedProgress(ImportProgressKind.ChunkCommitted, 3),
            ],
            progress);
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

    private static StagedImportRowRecord ExcludeRow(ImportStore store, int index, string reason)
    {
        var excluded = store.Rows[index] with
        {
            Disposition = "Excluded",
            DuplicateOfStagedRowId = null,
            ValidationMessage = reason,
            UpdatedAt = store.Rows[index].UpdatedAt.AddTicks(1),
        };
        store.Rows[index] = excluded;
        store.Session = store.Session! with
        {
            ReadyRows = store.Rows.Count(row => row.Disposition == "Ready"),
            InvalidRows = store.Rows.Count(row => row.Disposition is "Invalid" or "Conflict"),
            DuplicateRows = store.Rows.Count(row => row.Disposition == "Duplicate"),
            ExcludedRows = store.Rows.Count(row => row.Disposition == "Excluded"),
        };
        return excluded;
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FailingCommitFactory(IUnitOfWorkFactory inner, int failCommitAttempt)
    {
        private int commitAttempts;

        public async Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken) =>
            new FailingCommitUnit(
                await inner.BeginAsync(cancellationToken),
                this,
                failCommitAttempt);

        private sealed class FailingCommitUnit(
            IUnitOfWork inner,
            FailingCommitFactory owner,
            int failCommitAttempt) : IUnitOfWork
        {
            public IImportRepository Imports => inner.Imports;
            public IJobRepository Jobs => inner.Jobs;
            public IAccountRepository Accounts => inner.Accounts;

            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                if (Interlocked.Increment(ref owner.commitAttempts) == failCommitAttempt)
                    throw new InvalidOperationException("Injected SQLite commit failure.");
                await inner.CommitAsync(cancellationToken);
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default) =>
                inner.RollbackAsync(cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class ImportStore
    {
        public ImportSessionRecord? Session { get; set; }
        public List<StagedImportRowRecord> Rows { get; } = [];
        public Dictionary<Guid, StagedImportRowPayloadRecord> Payloads { get; } = [];
        public List<string> Events { get; } = [];
        public List<int> AddBatchSizes { get; } = [];
        public int ActiveUnits { get; private set; }
        public int? FailCommitAttempt { get; init; }
        public int CommitAttempts { get; private set; }
        public int CommitCount { get; private set; }

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
            public Task CommitAsync(CancellationToken cancellationToken = default)
            {
                store.CommitAttempts++;
                if (store.CommitAttempts == store.FailCommitAttempt)
                    throw new InvalidOperationException("Injected commit failure.");
                store.CommitCount++;
                return Task.CompletedTask;
            }
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
                    row.ImportSessionId == sessionId && row.NormalizedShareId == normalizedShareId && row.Disposition != "Excluded"));

            public Task<StagedImportRowRecord?> GetRowByIdAsync(Guid rowId, CancellationToken cancellationToken = default) =>
                Task.FromResult(store.Rows.FirstOrDefault(row => row.Id == rowId));

            public Task<int> GetLastSourceRowNumberAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
                Task.FromResult(store.Rows
                    .Where(row => row.ImportSessionId == sessionId && store.Payloads.ContainsKey(row.Id))
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
                    .Where(row => row.ImportSessionId == sessionId && row.NormalizedShareId == normalizedShareId && row.Disposition != "Excluded" && row.SourceRowNumber > afterSourceRowNumber)
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

            public Task<KeysetPage<StagedImportRowRecord>> GetRowsKeysetPageAsync(
                StagedImportKeysetQuery query,
                CancellationToken cancellationToken = default)
            {
                var filtered = store.Rows
                    .Where(row => row.ImportSessionId == query.ImportSessionId &&
                        (query.Disposition is null || row.Disposition == query.Disposition) &&
                        (query.PrecheckStatus is null ||
                         (query.PrecheckStatus == "Pending" && row.PrecheckStatus is null) ||
                         row.PrecheckStatus == query.PrecheckStatus))
                    .OrderBy(row => row.SourceRowNumber)
                    .ToArray();
                var candidates = filtered
                    .Where(row => row.SourceRowNumber > query.AfterSourceRowNumber)
                    .Take(query.PageSize + 1)
                    .ToList();
                var hasMore = candidates.Count > query.PageSize;
                if (hasMore) candidates.RemoveAt(candidates.Count - 1);
                return Task.FromResult(new KeysetPage<StagedImportRowRecord>(
                    candidates,
                    filtered.LongLength,
                    hasMore ? candidates[^1].SourceRowNumber : null));
            }

            public Task AddRowPayloadsAsync(
                IEnumerable<StagedImportRowPayloadRecord> payloads,
                CancellationToken cancellationToken = default)
            {
                foreach (var payload in payloads) store.Payloads.Add(payload.StagedImportRowId, payload);
                return Task.CompletedTask;
            }

            public Task<StagedImportRowPayloadRecord?> GetRowPayloadAsync(
                Guid stagedImportRowId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(store.Payloads.GetValueOrDefault(stagedImportRowId));

            public Task UpdateRowPayloadAsync(
                StagedImportRowPayloadRecord payload,
                CancellationToken cancellationToken = default)
            {
                store.Payloads[payload.StagedImportRowId] = payload;
                return Task.CompletedTask;
            }
        }
    }
}
