using System.Diagnostics;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Database.Repositories;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class RepositoryTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-repository-{Guid.NewGuid():N}");

    [Fact]
    public async Task UnitOfWorkCommitsAllRepositoriesAtomically()
    {
        var factory = await CreateFactoryAsync();
        var account = AccountRecord.Create(Guid.NewGuid(), "user-1", "Alpha");
        var session = ImportSessionRecord.Create(Guid.NewGuid(), "Text", "paste");
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account);
            await unit.Imports.AddSessionAsync(session);
            await unit.CommitAsync();
        }

        await using var verify = await SqliteUnitOfWork.BeginAsync(factory);
        Assert.Equal(account, await verify.Accounts.GetByIdAsync(account.Id));
        Assert.Equal(session, await verify.Imports.GetSessionAsync(session.Id));
    }

    [Fact]
    public async Task DisposedUncommittedUnitOfWorkRollsBack()
    {
        var factory = await CreateFactoryAsync();
        var account = AccountRecord.Create(Guid.NewGuid(), "user-rollback", "Beta");
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory)) await unit.Accounts.UpsertAsync(account);

        await using var verify = await SqliteUnitOfWork.BeginAsync(factory);
        Assert.Null(await verify.Accounts.GetByIdAsync(account.Id));
    }

    [Fact]
    public async Task ImportRowsArePagedByStableSourceOrderAndDisposition()
    {
        var factory = await CreateFactoryAsync();
        var session = ImportSessionRecord.Create(Guid.NewGuid(), "Excel", "large.xlsx");
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Imports.AddSessionAsync(session);
            await unit.Imports.AddRowsAsync(Enumerable.Range(1, 25).Select(i => StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, i, $"url-{i}", i % 2 == 0 ? "Ready" : "Invalid")));
            await unit.CommitAsync();
        }

        await using var read = await SqliteUnitOfWork.BeginAsync(factory);
        var page = await read.Imports.GetRowsPageAsync(new StagedImportRowQuery(session.Id, "Ready", 2, 5));
        Assert.Equal(12, page.TotalCount);
        Assert.Equal(new[] { 12, 14, 16, 18, 20 }, page.Items.Select(x => x.SourceRowNumber));
    }

    [Fact]
    public async Task NormalizedIdentityQueriesNeverReturnExcludedRows()
    {
        var factory = await CreateFactoryAsync();
        var session = ImportSessionRecord.Create(Guid.NewGuid(), "Text", "paste");
        var rows = new[]
        {
            StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, 1, "url-1", "Excluded") with { NormalizedShareId = "shared", Note = "keep-first" },
            StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, 2, "url-2", "Ready") with { NormalizedShareId = "shared" },
            StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, 3, "url-3", "Excluded") with { NormalizedShareId = "shared", Note = "keep-later" },
            StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, 4, "url-4", "Duplicate") with { NormalizedShareId = "shared" },
            StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, 5, "url-5", "Excluded") with { NormalizedShareId = "excluded-only" },
        };
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Imports.AddSessionAsync(session);
            await unit.Imports.AddRowsAsync(rows);
            await unit.CommitAsync();
        }

        await using var read = await SqliteUnitOfWork.BeginAsync(factory);
        Assert.Equal(rows[1].Id, (await read.Imports.GetFirstByNormalizedShareIdAsync(session.Id, "shared"))!.Id);
        Assert.Equal(
            new[] { rows[3].Id },
            (await read.Imports.GetRowsByNormalizedShareIdAfterSourceRowAsync(session.Id, "shared", 2, 10)).Select(row => row.Id));
        Assert.Null(await read.Imports.GetFirstByNormalizedShareIdAsync(session.Id, "excluded-only"));
    }

    [Fact]
    public async Task ImportColumnDefinitionsAndPositionalRawPayloadRoundTripWithoutRewritingValues()
    {
        var factory = await CreateFactoryAsync();
        var session = ImportSessionRecord.Create(Guid.NewGuid(), "Excel", "source.xlsx") with
        {
            ColumnMappingFingerprint = "mapping-fingerprint",
            SourceContentFingerprint = new string('a', 64),
            HeaderRowNumber = 1,
        };
        var row = StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, 1, "normalized-url", "Ready");
        var definitions = new[]
        {
            new ImportColumnDefinitionRecord(session.Id, 0, "标题", "Title", false),
            new ImportColumnDefinitionRecord(session.Id, 1, "链接", "ShareUrl", true),
            new ImportColumnDefinitionRecord(session.Id, 2, null, "Preserve", false),
        };
        const string rawJson = "[\"  title  \",\"https://pan.baidu.com/s/private 提取码 abcd\",\"=SUM(A1:A2)\"]";
        var payload = new StagedImportRowPayloadRecord(
            row.Id, "  title  ", "  title  ", "https://pan.baidu.com/s/private", rawJson);

        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Imports.AddSessionAsync(session);
            await unit.Imports.SetColumnDefinitionsAsync(session.Id, definitions);
            await unit.Imports.AddRowsAsync([row]);
            await unit.Imports.AddRowPayloadsAsync([payload]);
            await unit.CommitAsync();
        }

        await using var read = await SqliteUnitOfWork.BeginAsync(factory);
        var persistedSession = (await read.Imports.GetSessionAsync(session.Id))!;
        Assert.Equal(session.ColumnMappingFingerprint, persistedSession.ColumnMappingFingerprint);
        Assert.Equal(session.SourceContentFingerprint, persistedSession.SourceContentFingerprint);
        Assert.Equal(session.HeaderRowNumber, persistedSession.HeaderRowNumber);
        Assert.Equal(definitions, await read.Imports.GetColumnDefinitionsAsync(session.Id));
        Assert.Equal(payload, await read.Imports.GetRowPayloadAsync(row.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => read.Imports.AddRowPayloadsAsync([
            payload with { RawValuesJson = "[{\"not\":\"a raw cell\"}]" }]));
    }

    [Fact]
    public async Task LatestResumableExcelSessionUsesDurableParsingStateAndDeterministicOrder()
    {
        var factory = await CreateFactoryAsync();
        var now = DateTimeOffset.UtcNow;
        var older = DurableExcelSession(Guid.NewGuid(), "Parsing", now.AddMinutes(-1));
        var latest = DurableExcelSession(Guid.NewGuid(), "Parsing", now);
        var completed = DurableExcelSession(Guid.NewGuid(), "Draft", now.AddMinutes(1));
        var text = ImportSessionRecord.Create(Guid.NewGuid(), "Text", "paste") with
        {
            Status = "Parsing",
            UpdatedAt = now.AddMinutes(2),
        };

        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            foreach (var session in new[] { older, latest, completed })
            {
                await unit.Imports.AddSessionAsync(session);
                await unit.Imports.SetColumnDefinitionsAsync(session.Id,
                [
                    new(session.Id, 0, "链接", "ShareUrl", true),
                ]);
            }
            await unit.Imports.AddSessionAsync(text);
            await unit.CommitAsync();
        }

        await using var read = await SqliteUnitOfWork.BeginAsync(factory);
        Assert.Equal(latest.Id, (await read.Imports.GetLatestResumableExcelSessionAsync())!.Id);
    }

    private static ImportSessionRecord DurableExcelSession(Guid id, string status, DateTimeOffset updatedAt) =>
        ImportSessionRecord.Create(id, "Excel", "source.xlsx") with
        {
            Status = status,
            ColumnMappingFingerprint = new string('a', 64),
            SourceContentFingerprint = new string('b', 64),
            HeaderRowNumber = 1,
            UpdatedAt = updatedAt,
        };

    [Fact]
    public void SharePersistenceModelsRedactIdentifiersCursorsAndRawUserContent()
    {
        var markerId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        const string marker = "secret-cursor-url-code-fingerprint-raw";
        var values = new object[]
        {
            new StringKeysetPage<string>([marker], marker),
            new CursorKeysetPage<string, SecretCursor>([marker], new(marker)),
            new ShareResultKeysetQuery(markerId, 10, new(4, 1, markerId)),
            new ImportColumnDefinitionRecord(markerId, 0, marker, "Preserve", false),
            new StagedImportRowPayloadRecord(markerId, marker, marker, marker, marker),
            new BatchShareSettingsRecord(markerId, true, true, "Fixed", marker, "Ask", DateTimeOffset.UnixEpoch, marker),
            new BatchOutputAccountRecord(markerId, markerId, 1, marker, 1, 1, true),
            new ShareOperationRecord(markerId, markerId, markerId, markerId, markerId, 1, "WaitingShare", marker,
                marker, 1, marker, marker, "Unknown", 0, null, null, null, marker, marker,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new ShareOperationRemoteItemRecord(markerId, 0, marker, marker),
            new ShareDiscoveryBaselineRecord(markerId, marker),
            new ShareDiscoveryBaselineStateRecord(markerId, 1, marker, 1, true, DateTimeOffset.UnixEpoch),
            new BatchShareDecisionSnapshotRecord(markerId, markerId, marker, "Cancel", marker, 1, 0, 0,
                marker, DateTimeOffset.UnixEpoch),
            new BatchShareDiscoverySnapshotRecord(markerId, markerId, marker, marker, 1, 0, 0,
                marker, DateTimeOffset.UnixEpoch, 1),
            new BaiduShareResultRecord(markerId, markerId, markerId, markerId, markerId, marker, marker, 1,
                marker, marker, marker, true, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new ShareResultCursor(4, 1, markerId),
            new AdapterCapabilityAcceptanceRecord("BaiduShare", "v1", "build", DateTimeOffset.UnixEpoch, marker),
        };

        foreach (var value in values)
        {
            var rendered = value.ToString()!;
            Assert.DoesNotContain(marker, rendered);
            Assert.DoesNotContain(markerId.ToString("D"), rendered);
        }
    }

    [Fact]
    public async Task ColumnDefinitionValidationRequiresExactlyOneShareUrlAndUniqueMappedRoles()
    {
        var factory = await CreateFactoryAsync();
        var session = ImportSessionRecord.Create(Guid.NewGuid(), "Excel", "source.xlsx");
        await using var unit = await SqliteUnitOfWork.BeginAsync(factory);
        await unit.Imports.AddSessionAsync(session);
        await Assert.ThrowsAsync<ArgumentException>(() => unit.Imports.SetColumnDefinitionsAsync(session.Id,
            [new(session.Id, 0, "标题", "Title", false)]));
        await Assert.ThrowsAsync<ArgumentException>(() => unit.Imports.SetColumnDefinitionsAsync(session.Id,
            [new(session.Id, 0, "链接1", "ShareUrl", true), new(session.Id, 1, "链接2", "ShareUrl", true)]));
    }

    [Fact]
    public async Task RepositoryOperationsHonorPreCancelledToken()
    {
        var factory = await CreateFactoryAsync();
        await using var unit = await SqliteUnitOfWork.BeginAsync(factory);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unit.Accounts.GetPageAsync(new PageQuery(1, 10), cancellation.Token));
    }

    [Fact]
    public async Task AccountRulesRoundTripInPriorityOrder()
    {
        var factory = await CreateFactoryAsync();
        var account = AccountRecord.Create(Guid.NewGuid(), "rules-user", "Rules");
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account);
            await unit.Accounts.UpsertRuleAsync(new AccountRuleRecord(Guid.NewGuid(), account.Id, "PathPrefix", "/b", 2, true));
            await unit.Accounts.UpsertRuleAsync(new AccountRuleRecord(Guid.NewGuid(), account.Id, "PathPrefix", "/a", 1, true));
            await unit.CommitAsync();
        }
        await using var read = await SqliteUnitOfWork.BeginAsync(factory);
        var page = await read.Accounts.GetRulesPageAsync(account.Id, new PageQuery(1, 10));
        Assert.Equal(new[] { 1, 2 }, page.Items.Select(x => x.Priority));
    }

    [Fact]
    public async Task JobGraphRepositoriesRoundTripUsingPages()
    {
        var factory = await CreateFactoryAsync(); var now = DateTimeOffset.UtcNow;
        var account = AccountRecord.Create(Guid.NewGuid(), "job-user", "Worker");
        var job = new BatchJobRecord(Guid.NewGuid(), "Job", "Text", "Distribution", "TaskCount", "ProportionalReduction", 1, 1, "/", "None", "Skip", "Immediate", "Queued", now, null, null, null, 1);
        var resource = new ResourceItemRecord(Guid.NewGuid(), job.Id, null, 1, "url", "share", null, null, null, null, null, "None", "Unknown", null, "Unallocated", null, null, now, now);
        var operation = new TransferOperationRecord(Guid.NewGuid(), resource.Id, job.Id, 1, 1, "/", "/", "", account.Id, "Rule", "Waiting", null, 0, "fingerprint", "{}", null, null, "NotPrepared", "Unoccupied", null, null, null, null, null, null, now, now);
        var allocation = new AccountAllocationRecord(Guid.NewGuid(), job.Id, account.Id, 1, "/", "/", 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account); await unit.Jobs.AddAsync(job); await unit.Jobs.AddResourcesAsync([resource]);
            await unit.Jobs.AddOperationsAsync([operation]); await unit.Jobs.AddAllocationsAsync([allocation]); await unit.CommitAsync();
        }
        await using var read = await SqliteUnitOfWork.BeginAsync(factory);
        Assert.Equal(job, await read.Jobs.GetAsync(job.Id));
        Assert.Equal(resource, Assert.Single((await read.Jobs.GetResourcesPageAsync(job.Id, new PageQuery(1, 1))).Items));
        Assert.Equal(operation, Assert.Single((await read.Jobs.GetOperationsPageAsync(job.Id, new PageQuery(1, 1))).Items));
        Assert.Equal(allocation, Assert.Single((await read.Jobs.GetAllocationsPageAsync(job.Id, new PageQuery(1, 1))).Items));
    }

    [Fact]
    public async Task ImportBatchCreationBoundaryUsesSnapshotCompareAndSwap()
    {
        var factory = await CreateFactoryAsync();
        var now = new DateTimeOffset(2026, 8, 21, 1, 2, 3, TimeSpan.Zero);
        var session = new ImportSessionRecord(
            Guid.NewGuid(), "Excel", "source.xlsx", "ReadyToConfirm", 1, 1, 0, 0, 0,
            null, null, null, now, now, new string('a', 64));
        await using (var seed = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await seed.Imports.AddSessionAsync(session);
            await seed.CommitAsync();
        }

        await using (var stale = await SqliteUnitOfWork.BeginAsync(factory))
        {
            Assert.False(await stale.Imports.TryFinalizeBatchCreationAsync(
                session with { UpdatedAt = now.AddSeconds(-1) }, Guid.NewGuid(), now.AddSeconds(1)));
            await stale.CommitAsync();
        }
        await using (var current = await SqliteUnitOfWork.BeginAsync(factory))
        {
            var batchId = Guid.NewGuid();
            await current.Jobs.AddAsync(new BatchJobRecord(
                batchId, "Batch", "Excel", "Distribution", "TaskCount", "ProportionalReduction",
                0, 0, "/", "None", "Skip", "Immediate", "Queued", now, null, null, null, 1));
            Assert.True(await current.Imports.TryFinalizeBatchCreationAsync(session, batchId, now.AddSeconds(1)));
            var saved = Assert.IsType<ImportSessionRecord>(await current.Imports.GetSessionAsync(session.Id));
            Assert.Equal("BatchCreated", saved.Status);
            Assert.Equal(batchId, saved.CreatedBatchJobId);
            await current.RollbackAsync();
        }
    }

    [Theory]
    [InlineData("source-content")]
    [InlineData("header-row")]
    [InlineData("precheck-account")]
    [InlineData("source-type")]
    [InlineData("source-name")]
    [InlineData("created-at")]
    public async Task ImportBatchCreationBoundaryRejectsEverySameTimestampAuthorityChange(string mutation)
    {
        var factory = await CreateFactoryAsync();
        var now = new DateTimeOffset(2026, 8, 21, 1, 2, 3, TimeSpan.Zero);
        var account = AccountRecord.Create(Guid.NewGuid(), "cas-user", "CAS");
        var session = new ImportSessionRecord(
            Guid.NewGuid(), "Excel", "source.xlsx", "ReadyToConfirm", 1, 1, 0, 0, 0,
            null, null, null, now, now, new string('a', 64), new string('b', 64), 1);
        var batchId = Guid.NewGuid();
        await using (var seed = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await seed.Accounts.UpsertAsync(account);
            await seed.Imports.AddSessionAsync(session);
            await seed.Jobs.AddAsync(new BatchJobRecord(
                batchId, "Batch", "Excel", "Distribution", "TaskCount", "ProportionalReduction",
                0, 0, "/", "None", "Skip", "Immediate", "Queued", now, null, null, null, 1));
            await seed.CommitAsync();
        }
        var (column, value) = mutation switch
        {
            "source-content" => ("SourceContentFingerprint", (object)new string('c', 64)),
            "header-row" => ("HeaderRowNumber", 2),
            "precheck-account" => ("PrecheckAccountId", account.Id.ToString("D")),
            "source-type" => ("SourceType", "Text"),
            "source-name" => ("SourceName", "changed.xlsx"),
            "created-at" => ("CreatedAt", now.AddMinutes(-1).ToString("O")),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        await using (var connection = await factory.OpenConnectionAsync())
        await using (var mutate = connection.CreateCommand())
        {
            mutate.CommandText = $"UPDATE ImportSessions SET {column}=$value WHERE Id=$id;";
            mutate.Parameters.AddWithValue("$value", value);
            mutate.Parameters.AddWithValue("$id", session.Id.ToString("D"));
            Assert.Equal(1, await mutate.ExecuteNonQueryAsync());
        }
        await using var stale = await SqliteUnitOfWork.BeginAsync(factory);
        Assert.False(await stale.Imports.TryFinalizeBatchCreationAsync(session, batchId, now.AddSeconds(1)));
        Assert.Equal("ReadyToConfirm", (await stale.Imports.GetSessionAsync(session.Id))!.Status);
        await stale.RollbackAsync();
    }

    [Fact]
    public async Task ShareConfirmationSnapshotsAndShareProjectionFieldsRoundTripAndAreImmutable()
    {
        var factory = await CreateFactoryAsync();
        var now = new DateTimeOffset(2026, 8, 21, 1, 2, 3, TimeSpan.Zero);
        var first = AccountRecord.Create(Guid.NewGuid(), "first-user", "First");
        var second = AccountRecord.Create(Guid.NewGuid(), "second-user", "Second");
        var job = new BatchJobRecord(Guid.NewGuid(), "Job", "Excel", "Distribution", "TaskCount",
            "ProportionalReduction", 1, 1, "/", "None", "Skip", "Immediate", "Queued",
            now, null, null, null, 1, 3);
        var resource = new ResourceItemRecord(Guid.NewGuid(), job.Id, null, 1, "url", "share", null, null,
            null, null, null, "None", "Valid", null, "Expanded", null, null, now, now, "Resolved title");
        var settingsFingerprint = ShareSettingsFingerprint.Compute(
            true, true, ShareExtractionCodeBehavior.Random, null);
        var settings = new BatchShareSettingsRecord(job.Id, true, true, "Random", null, "Ask", now, settingsFingerprint);
        var outputs = new[]
        {
            new BatchOutputAccountRecord(job.Id, second.Id, 2, "Second label", 4, 0, false),
            new BatchOutputAccountRecord(job.Id, first.Id, 1, "First label", 7, 3, true),
        };

        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(first);
            await unit.Accounts.UpsertAsync(second);
            await unit.Jobs.AddAsync(job);
            await unit.Jobs.AddResourcesAsync([resource]);
            await unit.Jobs.SetShareSettingsAsync(settings);
            await unit.Jobs.AddOutputAccountsAsync(outputs);
            await unit.CommitAsync();
        }

        await using (var read = await SqliteUnitOfWork.BeginAsync(factory))
        {
            Assert.Equal(3, (await read.Jobs.GetAsync(job.Id))!.ShareOperationCount);
            Assert.Equal("Resolved title", Assert.Single((await read.Jobs.GetResourcesPageAsync(
                job.Id, new PageQuery(1, 10))).Items).ResolvedOutputTitle);
            Assert.Equal(settings, await read.Jobs.GetShareSettingsAsync(job.Id));
            Assert.Equal(outputs.OrderBy(value => value.OutputOrdinal), await read.Jobs.GetOutputAccountsAsync(job.Id));
        }

        await using (var replay = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await replay.Jobs.SetShareSettingsAsync(settings);
            await replay.Jobs.AddOutputAccountsAsync(outputs.Reverse());
            await replay.CommitAsync();
        }

        await using (var changed = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => changed.Jobs.SetShareSettingsAsync(
                settings with
                {
                    ExtractionCodeMode = "Fixed", FixedExtractionCode = " abcd ",
                    SettingsFingerprint = ShareSettingsFingerprint.Compute(
                        true, true, ShareExtractionCodeBehavior.Fixed, "abcd")
                }));
            await Assert.ThrowsAsync<ArgumentException>(() => changed.Jobs.SetShareSettingsAsync(
                settings with { SettingsFingerprint = new string('0', 64) }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => changed.Jobs.SetShareSettingsAsync(
                settings with { ExistingSharePolicy = "Regenerate" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => changed.Jobs.AddOutputAccountsAsync(
                outputs.Select(value => value with { OutputOrdinal = 3 - value.OutputOrdinal })));
        }

        await using var verify = await SqliteUnitOfWork.BeginAsync(factory);
        Assert.Equal(settings, await verify.Jobs.GetShareSettingsAsync(job.Id));
        Assert.Equal(outputs.OrderBy(value => value.OutputOrdinal), await verify.Jobs.GetOutputAccountsAsync(job.Id));
    }

    [Fact]
    public async Task ShareOperationInsertIsBoundedAndRejectsDisabledOrInvalidPlannedOutputBeforeWriting()
    {
        var graph = await CreateShareInsertGraphAsync(501, shareEnabled: true, plannedShareCount: 501);
        await using (var oversized = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => oversized.Jobs.AddShareOperationsAsync(
                graph.Operations, graph.RemoteItems));
        }
        Assert.Equal(0, await CountTableAsync(graph.Factory, "ShareOperations"));

        var disabled = await CreateShareInsertGraphAsync(1, shareEnabled: false, plannedShareCount: 0);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(disabled.Factory))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => unit.Jobs.AddShareOperationsAsync(
                disabled.Operations, disabled.RemoteItems));
        }
        Assert.Equal(0, await CountTableAsync(disabled.Factory, "ShareOperations"));

        await using var connection = await disabled.Factory.OpenConnectionAsync();
        await using var direct = connection.CreateCommand();
        direct.CommandText = """
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,
              ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,AttemptCount,CreatedAt,UpdatedAt)
            VALUES($id,$transfer,$resource,$batch,$account,1,'WaitingShare',$items,$settings,1,'Unknown',0,$now,$now);
            """;
        var operation = Assert.Single(disabled.Operations);
        direct.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
        direct.Parameters.AddWithValue("$transfer", operation.TransferOperationId.ToString("D"));
        direct.Parameters.AddWithValue("$resource", operation.ResourceItemId.ToString("D"));
        direct.Parameters.AddWithValue("$batch", operation.BatchJobId.ToString("D"));
        direct.Parameters.AddWithValue("$account", operation.AccountId.ToString("D"));
        direct.Parameters.AddWithValue("$items", operation.ExactItemSetFingerprint);
        direct.Parameters.AddWithValue("$settings", operation.SettingsFingerprint);
        direct.Parameters.AddWithValue("$now", operation.CreatedAt.ToString("O"));
        await Assert.ThrowsAsync<SqliteException>(() => direct.ExecuteNonQueryAsync());
        Assert.Equal(0, await CountTableAsync(disabled.Factory, "ShareOperations"));
    }

    [Fact]
    public async Task PendingShareCreationKeepsExactEvidenceNullAndRejectsPrematureRemoteItems()
    {
        var graph = await CreateShareInsertGraphAsync(1, shareEnabled: true, plannedShareCount: 1);
        var exact = Assert.Single(graph.Operations);
        var pending = new PendingShareOperationRecord(
            exact.Id, exact.TransferOperationId, exact.ResourceItemId, exact.BatchJobId,
            exact.AccountId, exact.OutputOrdinal, exact.SettingsFingerprint, exact.CreatedAt);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await unit.Jobs.AddPendingShareOperationsAsync([pending]);
            await unit.CommitAsync();
        }

        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = "SELECT Status,ExactItemSetFingerprint,(SELECT COUNT(*) FROM ShareOperationGenerations WHERE ShareOperationId=ShareOperations.Id) FROM ShareOperations WHERE Id=$id;";
            inspect.Parameters.AddWithValue("$id", pending.Id.ToString("D"));
            await using var reader = await inspect.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("WaitingTransfer", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(0, reader.GetInt64(2));
        }
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO ShareOperationRemoteItems(ShareOperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName) VALUES($operation,0,'premature',X'01','premature');";
        insert.Parameters.AddWithValue("$operation", pending.Id.ToString("D"));
        await Assert.ThrowsAsync<SqliteException>(() => insert.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task OneShareOperationStreamsMoreThanOneThousandExactItemsAndRoundTrips()
    {
        var graph = await CreateShareInsertGraphAsync(1, shareEnabled: true, plannedShareCount: 1);
        var identities = Enumerable.Range(0, 1_501)
            .Select(index => new RemoteItemIdentity($"large-remote-{index:D4}", $"Item {index:D4}"))
            .ToArray();
        var operation = Assert.Single(graph.Operations) with
        {
            ExactItemSetFingerprint = new RemoteItemIdentitySet(identities).Fingerprint
        };
        IEnumerable<ShareOperationRemoteItemRecord> StreamItems()
        {
            foreach (var (identity, ordinal) in identities.Select((value, index) => (value, index)))
                yield return new(operation.Id, ordinal, identity.ExactId, $"Item {ordinal:D4}");
        }

        var before = GC.GetTotalMemory(forceFullCollection: true);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await unit.Jobs.AddShareOperationsAsync([operation], StreamItems());
            await unit.CommitAsync();
        }
        var growth = Math.Max(0, GC.GetTotalMemory(forceFullCollection: true) - before);
        Assert.True(growth < 16L * 1024 * 1024, $"Managed growth was {growth:N0} bytes.");

        var store = new SqliteShareSchedulingStore(graph.Factory);
        var first = await store.GetRemoteItemsKeysetPageAsync(operation.Id, null, 1_000);
        var second = await store.GetRemoteItemsKeysetPageAsync(operation.Id, first.NextCursor, 1_000);
        Assert.Equal(1_501, first.Items.Count + second.Items.Count);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task ShareOperationFingerprintUsesCanonicalExactIdsInsteadOfCallerOrdinalsOrSqliteTextOrder()
    {
        var graph = await CreateShareInsertGraphAsync(1, shareEnabled: true, plannedShareCount: 1);
        var operation = Assert.Single(graph.Operations);
        var supplementary = "\U00010000";
        var privateUseBmp = "\uE000";
        var canonical = new RemoteItemIdentitySet([
            new(privateUseBmp, "BMP"),
            new(supplementary, "Supplementary"),
        ]);
        operation = operation with { ExactItemSetFingerprint = canonical.Fingerprint };

        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await unit.Jobs.AddShareOperationsAsync([operation], [
                new(operation.Id, 0, privateUseBmp, "BMP"),
                new(operation.Id, 1, supplementary, "Supplementary"),
            ]);
            await unit.CommitAsync();
        }

        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RemoteItemId FROM ShareOperationRemoteItems WHERE ShareOperationId=$id ORDER BY CanonicalSortKey;";
        command.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
        var persisted = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) persisted.Add(reader.GetString(0));
        Assert.Equal([supplementary, privateUseBmp], persisted);

        var store = new SqliteShareSchedulingStore(graph.Factory);
        var first = await store.GetRemoteItemsKeysetPageAsync(operation.Id, null, 1);
        var second = await store.GetRemoteItemsKeysetPageAsync(operation.Id, first.NextCursor, 1);
        Assert.Equal(supplementary, Assert.Single(first.Items).RemoteItemId);
        Assert.Equal(privateUseBmp, Assert.Single(second.Items).RemoteItemId);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task InvalidStreamingExactItemsRollbackInsertedOperationsBeforeCallerCommits()
    {
        var graph = await CreateShareInsertGraphAsync(1, shareEnabled: true, plannedShareCount: 1);
        var operation = Assert.Single(graph.Operations);
        var valid = Assert.Single(graph.RemoteItems);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(graph.Factory))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => unit.Jobs.AddShareOperationsAsync([operation], []));
            await Assert.ThrowsAsync<SqliteException>(() => unit.Jobs.AddShareOperationsAsync(
                [operation], [valid, valid with { ItemOrdinal = 1 }]));
            await Assert.ThrowsAsync<ArgumentException>(() => unit.Jobs.AddShareOperationsAsync(
                [operation], [valid, valid with { ShareOperationId = Guid.NewGuid() }]));
            await unit.CommitAsync();
        }

        Assert.Equal(0, await CountTableAsync(graph.Factory, "ShareOperations"));
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT CreatedShareOperationCount FROM BatchOutputAccounts;";
        Assert.Equal(0, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task JobUpdatesAllocationUpdatesAndSanitizedAuditShareOneTransaction()
    {
        var factory = await CreateFactoryAsync();
        var now = DateTimeOffset.UtcNow;
        var account = AccountRecord.Create(Guid.NewGuid(), "update-user", "Update");
        var job = new BatchJobRecord(Guid.NewGuid(), "Job", "Text", "Distribution", "SuccessTarget",
            "WaitForCandidates", 1, 0, "/", "None", "Skip", "Immediate", "AwaitingCandidates",
            now, null, null, null, 1);
        var allocation = new AccountAllocationRecord(Guid.NewGuid(), job.Id, account.Id, 1, "/", "/",
            0, 10, 10, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account);
            await unit.Jobs.AddAsync(job);
            await unit.Jobs.AddAllocationsAsync([allocation]);
            await unit.CommitAsync();
        }

        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Jobs.UpdateAsync(job with { ResourceCount = 2, Status = "PausedReady" });
            await unit.Jobs.UpdateAllocationsAsync([allocation with { ExclusiveCandidateCount = 2 }]);
            await unit.Jobs.WriteAuditAsync(new AuditEventRecord(
                Guid.NewGuid(), job.Id, null, null, null, AuditEventKind.CandidatesAppended, now));
            await unit.CommitAsync();
        }

        await using (var read = await SqliteUnitOfWork.BeginAsync(factory))
        {
            Assert.Equal("PausedReady", (await read.Jobs.GetAsync(job.Id))!.Status);
            Assert.Equal(2, Assert.Single((await read.Jobs.GetAllocationsPageAsync(job.Id, new PageQuery(1, 10))).Items).ExclusiveCandidateCount);
        }
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EventCode,SanitizedMessage FROM ExecutionLogs WHERE BatchJobId=$id;";
        command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("CANDIDATES_APPENDED", reader.GetString(0));
        Assert.Equal("Candidate append was confirmed.", reader.GetString(1));
    }

    [Fact]
    public async Task AuditBoundaryRejectsUnknownEventsAndNeverAcceptsUserMessageContent()
    {
        var factory = await CreateFactoryAsync();
        var now = DateTimeOffset.UtcNow;
        var job = new BatchJobRecord(Guid.NewGuid(), "https://private/?pwd=code", "Text", "Distribution",
            "SuccessTarget", "WaitForCandidates", 0, 0, "/private/cookie/profile", "None", "Skip",
            "Immediate", "AwaitingCandidates", now, null, null, null, 1);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Jobs.AddAsync(job);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => unit.Jobs.WriteAuditAsync(
                new AuditEventRecord(Guid.NewGuid(), job.Id, null, null, null, (AuditEventKind)999, now)));
            await unit.Jobs.WriteAuditAsync(new AuditEventRecord(
                Guid.NewGuid(), job.Id, null, null, null, AuditEventKind.CandidatesAppended, now));
            await unit.CommitAsync();
        }

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EventCode,SanitizedMessage FROM ExecutionLogs WHERE BatchJobId=$id;";
        command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var persisted = $"{reader.GetString(0)} {reader.GetString(1)}";
        Assert.Equal("CANDIDATES_APPENDED Candidate append was confirmed.", persisted);
        Assert.DoesNotContain("https://", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwd=", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profile", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/private", persisted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreparedStreamingWritesHandleTwoHundredThousandResourcesAndThreeReplicasWithinBoundedMemory()
    {
        const int resourceCount = 200_000;
        const int accountCount = 3;
        var factory = await CreateFactoryAsync();
        var now = DateTimeOffset.UtcNow;
        var accounts = Enumerable.Range(0, accountCount)
            .Select(index => AccountRecord.Create(StableGuid(index + 1), $"scale-{index}", $"Scale {index}"))
            .ToArray();
        var job = new BatchJobRecord(StableGuid(900_001), "Scale", "Text", "Replication", "TaskCount",
            "ProportionalReduction", resourceCount, resourceCount * accountCount, "/", "None", "Skip",
            "Immediate", "Queued", now, null, null, null, 1);
        var beforeBytes = GC.GetTotalMemory(forceFullCollection: true);
        var peakBytes = beforeBytes;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                var current = GC.GetTotalMemory(forceFullCollection: false);
                UpdateMaximum(ref peakBytes, current);
                await Task.Delay(10);
            }
        });
        var timer = Stopwatch.StartNew();
        try
        {
            await using var unit = await SqliteUnitOfWork.BeginAsync(factory);
            foreach (var account in accounts) await unit.Accounts.UpsertAsync(account);
            await unit.Jobs.AddAsync(job);
            await unit.Jobs.AddResourcesAsync(ScaleResources(job.Id, resourceCount, now));
            await unit.Jobs.AddOperationsAsync(ScaleOperations(job.Id, accounts, resourceCount, now));
            await unit.CommitAsync();
        }
        finally
        {
            timer.Stop();
            sampling.Cancel();
            await sampler;
        }

        await using var connection = await factory.OpenConnectionAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT (SELECT COUNT(*) FROM ResourceItems), (SELECT COUNT(*) FROM TransferOperations);";
        await using var reader = await count.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(resourceCount, reader.GetInt32(0));
        Assert.Equal(resourceCount * accountCount, reader.GetInt32(1));
        var peakGrowth = Math.Max(0, Volatile.Read(ref peakBytes) - beforeBytes);
        output.WriteLine($"scale resources={resourceCount}, operations={resourceCount * accountCount}, elapsed={timer.Elapsed}, peak-managed-growth={peakGrowth:N0} bytes");
        Assert.True(peakGrowth < 384L * 1024 * 1024, $"Peak managed growth was {peakGrowth:N0} bytes.");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), $"Bulk insert took {timer.Elapsed}.");
    }

    [Fact]
    public async Task ShareWritesStreamSixHundredThousandOperationsInBoundedChunksAndMemory()
    {
        const int resourceCount = 200_000;
        const int accountCount = 3;
        const int chunkSize = 500;
        var factory = await CreateFactoryAsync();
        var now = DateTimeOffset.UtcNow;
        var accounts = Enumerable.Range(0, accountCount)
            .Select(index => AccountRecord.Create(StableGuid(index + 1), $"share-scale-{index}", $"Share Scale {index}"))
            .ToArray();
        var job = new BatchJobRecord(StableGuid(900_002), "Share Scale", "Text", "Replication", "TaskCount",
            "ProportionalReduction", resourceCount, resourceCount * accountCount, "/", "None", "Skip",
            "Immediate", "Active", now, now, null, null, 1, resourceCount * accountCount);
        var settingsFingerprint = ShareSettingsFingerprint.Compute(
            true, true, ShareExtractionCodeBehavior.Random, null);
        var beforeBytes = GC.GetTotalMemory(forceFullCollection: true);
        var peakBytes = beforeBytes;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                UpdateMaximum(ref peakBytes, GC.GetTotalMemory(forceFullCollection: false));
                await Task.Delay(10);
            }
        });
        var timer = Stopwatch.StartNew();
        try
        {
            await using var unit = await SqliteUnitOfWork.BeginAsync(factory);
            foreach (var account in accounts) await unit.Accounts.UpsertAsync(account);
            await unit.Jobs.AddAsync(job);
            await unit.Jobs.AddResourcesAsync(ScaleResources(job.Id, resourceCount, now));
            await unit.Jobs.AddOperationsAsync(ScaleOperations(job.Id, accounts, resourceCount, now));
            await unit.Jobs.SetShareSettingsAsync(new(
                job.Id, true, true, "Random", null, "Ask", now, settingsFingerprint));
            await unit.Jobs.AddOutputAccountsAsync(accounts.Select((account, index) =>
                new BatchOutputAccountRecord(job.Id, account.Id, index + 1, $"Output {index + 1}",
                    resourceCount, resourceCount, true)));

            for (var start = 0; start < resourceCount * accountCount; start += chunkSize)
            {
                var operations = new List<ShareOperationRecord>(chunkSize);
                var remoteItems = new List<ShareOperationRemoteItemRecord>(chunkSize);
                for (var offset = 0; offset < chunkSize; offset++)
                {
                    var ordinal = start + offset + 1;
                    var resourceIndex = (ordinal - 1) / accountCount + 1;
                    var accountIndex = (ordinal - 1) % accountCount;
                    var shareId = StableGuid(3_000_000 + ordinal);
                    var remoteId = $"remote-{ordinal}";
                    var itemFingerprint = new RemoteItemIdentitySet([new(remoteId, remoteId)]).Fingerprint;
                    operations.Add(new(shareId, StableGuid(2_000_000 + ordinal), StableGuid(1_000_000 + resourceIndex),
                        job.Id, accounts[accountIndex].Id, accountIndex + 1, "WaitingShare", itemFingerprint,
                        settingsFingerprint, 1, null, null, "Unknown", 0, null, null, null, null, null, now, now));
                    remoteItems.Add(new(shareId, 0, remoteId, remoteId));
                }
                await unit.Jobs.AddShareOperationsAsync(operations, remoteItems);
            }
            await unit.CommitAsync();
        }
        finally
        {
            timer.Stop();
            sampling.Cancel();
            await sampler;
        }

        await using var connection = await factory.OpenConnectionAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*),(SELECT COUNT(*) FROM ShareOperationRemoteItems) FROM ShareOperations;";
        await using var reader = await count.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(resourceCount * accountCount, reader.GetInt32(0));
        Assert.Equal(resourceCount * accountCount, reader.GetInt32(1));
        var claimTimer = Stopwatch.StartNew();
        var store = new SqliteShareSchedulingStore(factory);
        for (var index = 0; index < 200; index++)
            Assert.NotNull(await store.TryReserveNextAsync(now, TimeSpan.FromMinutes(5)));
        claimTimer.Stop();
        var peakGrowth = Math.Max(0, Volatile.Read(ref peakBytes) - beforeBytes);
        output.WriteLine($"share scale resources={resourceCount}, share-operations={resourceCount * accountCount}, elapsed={timer.Elapsed}, repeated-claims={claimTimer.Elapsed}, peak-managed-growth={peakGrowth:N0} bytes");
        Assert.True(peakGrowth < 384L * 1024 * 1024, $"Peak managed growth was {peakGrowth:N0} bytes.");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(120), $"Share pipeline insert took {timer.Elapsed}.");
        Assert.True(claimTimer.Elapsed < TimeSpan.FromSeconds(10), $"Repeated indexed claims took {claimTimer.Elapsed}.");
    }

    [Fact]
    public async Task OperationSubmitLifecyclePersistsDescriptorBeforeResponseIdentifiers()
    {
        var factory = await CreateFactoryAsync(); var now = DateTimeOffset.UtcNow;
        var account = AccountRecord.Create(Guid.NewGuid(), "submit-user", "Submit");
        var job = new BatchJobRecord(Guid.NewGuid(), "Job", "Text", "Distribution", "TaskCount", "ProportionalReduction", 1, 1, "/", "None", "Skip", "Immediate", "Queued", now, null, null, null, 1);
        var resource = new ResourceItemRecord(Guid.NewGuid(), job.Id, null, 1, "url", "share", null, null, null, null, null, "None", "Unknown", null, "Unallocated", null, null, now, now);
        var operation = new TransferOperationRecord(Guid.NewGuid(), resource.Id, job.Id, 1, 1, "/", "/", "", account.Id, "Rule", "Preparing", null, 1, "submit-fingerprint", null, null, null, "PreparingToSubmit", "Reserved", now, null, null, null, null, null, now, now);
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account); await unit.Jobs.AddAsync(job); await unit.Jobs.AddResourcesAsync([resource]); await unit.Jobs.AddOperationsAsync([operation]);
            await unit.Jobs.PrepareSubmissionAsync(new OperationSubmissionPreparation(operation.Id, "{\"share\":\"share\"}", now.AddMinutes(1), now.AddSeconds(1)));
            await Assert.ThrowsAsync<ArgumentException>(() => unit.Jobs.RecordSubmissionResponseAsync(new OperationSubmissionResponse(operation.Id, null, null, "NotPrepared", "Reserved", now.AddSeconds(2))));
            await unit.Jobs.RecordSubmissionResponseAsync(new OperationSubmissionResponse(operation.Id, "request-1", "[\"remote-1\"]", "ResultReceived", "ResultPending", now.AddSeconds(2)));
            await unit.CommitAsync();
        }
        await using var read = await SqliteUnitOfWork.BeginAsync(factory);
        var saved = Assert.Single((await read.Jobs.GetOperationsPageAsync(job.Id, new PageQuery(1, 1))).Items);
        Assert.Equal("{\"share\":\"share\"}", saved.ConfirmationDescriptorJson);
        Assert.Equal("request-1", saved.AdapterRequestId); Assert.Equal("[\"remote-1\"]", saved.RemoteItemIdsJson);
    }

    [Fact]
    public async Task WalReaderGetsStableSnapshotWhileAnotherTransactionIsWriting()
    {
        var factory = await CreateFactoryAsync();
        await using var writer = await SqliteUnitOfWork.BeginAsync(factory);
        await writer.Accounts.UpsertAsync(AccountRecord.Create(Guid.NewGuid(), "uncommitted", "Writer"));
        await using var reader = await SqliteUnitOfWork.BeginAsync(factory).WaitAsync(TimeSpan.FromSeconds(2));
        var page = await reader.Accounts.GetPageAsync(new PageQuery(1, 10));
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task UnitOfWorkCleanupAttemptsEveryStepAndAggregatesFailures()
    {
        var calls = new List<string>();
        static ValueTask Fail(List<string> calls, string step)
        {
            calls.Add(step);
            return ValueTask.FromException(new IOException(step));
        }

        var error = await Assert.ThrowsAsync<AggregateException>(async () =>
            await UnitOfWorkCleanup.DisposeAsync(
                rollbackRequired: true,
                () => Fail(calls, "rollback"),
                () => Fail(calls, "transaction"),
                () => Fail(calls, "connection")));

        Assert.Equal(new[] { "rollback", "transaction", "connection" }, calls);
        Assert.Equal(3, error.InnerExceptions.Count);
    }

    [Fact]
    public async Task FactoryTracksRawCloseAndReopenLifecycle()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"), busyTimeoutMilliseconds: 4321);
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "1.0").MigrateAsync();
        await using var connection = await factory.OpenConnectionAsync();
        Assert.Equal(1, factory.ActiveConnectionCount);
        connection.Close();
        Assert.Equal(0, factory.ActiveConnectionCount);
        connection.Open();
        Assert.Equal(1, factory.ActiveConnectionCount);
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, await foreignKeys.ExecuteScalarAsync());
        }
        await using (var timeout = connection.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout;";
            Assert.Equal(4321L, await timeout.ExecuteScalarAsync());
        }
        await connection.DisposeAsync();
        Assert.Equal(0, factory.ActiveConnectionCount);
    }

    private async Task<SqliteConnectionFactory> CreateFactoryAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        return factory;
    }

    private async Task<ShareInsertGraph> CreateShareInsertGraphAsync(
        int count,
        bool shareEnabled,
        int plannedShareCount)
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, $"{Guid.NewGuid():N}.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        var now = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);
        var account = AccountRecord.Create(Guid.NewGuid(), $"bounded-{Guid.NewGuid():N}", "Bounded");
        var batch = new BatchJobRecord(Guid.NewGuid(), "Bounded", "Excel", "Distribution", "TaskCount",
            "ProportionalReduction", count, count, "/", "None", "Skip", "Immediate", "Active", now,
            now, null, null, 1, count);
        var settingsFingerprint = ShareSettingsFingerprint.Compute(
            true, true, ShareExtractionCodeBehavior.Random, null);
        var operations = new List<ShareOperationRecord>(count);
        var remoteItems = new List<ShareOperationRemoteItemRecord>(count);
        var resources = new List<ResourceItemRecord>(count);
        var transfers = new List<TransferOperationRecord>(count);
        for (var index = 1; index <= count; index++)
        {
            var resourceId = Guid.NewGuid();
            var transferId = Guid.NewGuid();
            var shareId = Guid.NewGuid();
            var remoteId = $"remote-{index}";
            var itemFingerprint = new RemoteItemIdentitySet([new(remoteId, remoteId)]).Fingerprint;
            resources.Add(new(resourceId, batch.Id, null, index, $"url-{index}", $"share-{index}", null, null,
                null, null, null, "None", "Valid", null, "Expanded", null, null, now, now));
            transfers.Add(new(transferId, resourceId, batch.Id, index, index, "/", "/", "", account.Id,
                "Rule", "Succeeded", "NewTransfer", 1, $"transfer-{index}", "{}", "request", $"[\"{remoteId}\"]",
                "ResultReceived", "Fulfilled", now, now, now, null, null, null, now, now));
            operations.Add(new(shareId, transferId, resourceId, batch.Id, account.Id, 1, "WaitingShare",
                itemFingerprint, settingsFingerprint, 1, null, null, "Unknown", 0, null, null, null, null, null, now, now));
            remoteItems.Add(new(shareId, 0, remoteId, remoteId));
        }

        await using var unit = await SqliteUnitOfWork.BeginAsync(factory);
        await unit.Accounts.UpsertAsync(account);
        await unit.Jobs.AddAsync(batch);
        await unit.Jobs.AddResourcesAsync(resources);
        await unit.Jobs.AddOperationsAsync(transfers);
        await unit.Jobs.SetShareSettingsAsync(new(batch.Id, true, true, "Random", null, "Ask", now, settingsFingerprint));
        await unit.Jobs.AddOutputAccountsAsync([
            new(batch.Id, account.Id, 1, "Bounded", count, plannedShareCount, shareEnabled)]);
        await unit.CommitAsync();
        return new(factory, operations, remoteItems);
    }

    private static async Task<int> CountTableAsync(SqliteConnectionFactory factory, string table)
    {
        var sql = table switch
        {
            "ShareOperations" => "SELECT COUNT(*) FROM ShareOperations;",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static IEnumerable<ResourceItemRecord> ScaleResources(
        Guid batchJobId,
        int count,
        DateTimeOffset now)
    {
        for (var index = 1; index <= count; index++)
        {
            yield return new ResourceItemRecord(
                StableGuid(1_000_000 + index), batchJobId, null, index,
                $"https://pan.baidu.com/s/scale-{index}", $"scale-{index}", null, null, null, null,
                null, "None", "Unknown", null, "Expanded", null, null, now, now);
        }
    }

    private static IEnumerable<TransferOperationRecord> ScaleOperations(
        Guid batchJobId,
        IReadOnlyList<AccountRecord> accounts,
        int resourceCount,
        DateTimeOffset now)
    {
        var ordinal = 0;
        for (var resourceIndex = 1; resourceIndex <= resourceCount; resourceIndex++)
        {
            var resourceId = StableGuid(1_000_000 + resourceIndex);
            foreach (var account in accounts)
            {
                ordinal++;
                yield return new TransferOperationRecord(
                    StableGuid(2_000_000 + ordinal), resourceId, batchJobId, resourceIndex, ordinal,
                    "/", "/", string.Empty, account.Id, "Replication", "Waiting", null, 0,
                    $"scale-{resourceIndex}-{account.Id:N}", null, null, null, "NotPrepared", "Unoccupied",
                    null, null, null, null, null, null, now, now);
            }
        }
    }

    private static Guid StableGuid(int value) => new(value, 0, 0, new byte[8]);

    private static void UpdateMaximum(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private sealed record SecretCursor(string Value);
    private sealed record ShareInsertGraph(
        SqliteConnectionFactory Factory,
        IReadOnlyList<ShareOperationRecord> Operations,
        IReadOnlyList<ShareOperationRemoteItemRecord> RemoteItems);

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
