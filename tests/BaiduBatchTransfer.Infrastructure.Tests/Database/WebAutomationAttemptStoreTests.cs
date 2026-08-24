using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class WebAutomationAttemptStoreTests : IDisposable
{
    // Ownership note: browser attempts duplicate no TransferOperations/ShareOperations result facts.
    // They persist only the UI phase, sanitized observation fingerprint, and exact pre-submit
    // remote directory set needed to recover one account-bound generation.
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-web-attempt-{Guid.NewGuid():N}");

    [Fact]
    public async Task TransferBaseline_RoundTripsExactPreSubmitDirectorySet()
    {
        var graph = await CreateAsync();
        var attempt = WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now);

        Assert.True(await graph.Store.BeginAsync(attempt));
        Assert.True(await graph.Store.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, 2, 0,
            ExactBaseline("before-2", "before-1"), Fingerprint('a'), graph.Now.AddSeconds(1)));

        var restored = await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 2);

        Assert.NotNull(restored);
        Assert.Equal(WebAutomationAttemptState.BaselinePersisted, restored.State);
        Assert.Equal(
            new[] { "before-1", "before-2" },
            restored.BaselineRemoteItems.Select(item => item.ExactId).Order());
    }

    [Fact]
    public async Task BaselineReadPagesPastTheOldPerQueryBoundaryWithoutTruncation()
    {
        var graph = await CreateAsync(baselineReadPageSize: 2);
        var ids = Enumerable.Range(1, 5).Select(index => $"before-{index}").ToArray();
        await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now));
        Assert.True(await graph.Store.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, 1, 0,
            ExactBaseline(ids), Fingerprint('d'), graph.Now.AddSeconds(1)));

        var reopened = new SqliteWebAutomationAttemptStore(
            graph.Factory, maximumBaselineItemCount: 100, baselineReadPageSize: 2);
        var restored = await reopened.GetAsync(graph.OperationId, graph.AccountId, 1);

        Assert.Equal(ids, restored!.BaselineRemoteItems.Select(item => item.ExactId));
        Assert.Equal(5, restored.Baseline!.Items.Length);
    }

    [Fact]
    public async Task OversizedBaselineIsRejectedBeforeAnyEvidenceIsWritten()
    {
        var graph = await CreateAsync(maximumBaselineItemCount: 2);
        await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => graph.Store.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, 1, 0,
            ExactBaseline("one", "two", "three"), Fingerprint('e'), graph.Now.AddSeconds(1)));

        var unchanged = await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1);
        Assert.Equal(WebAutomationAttemptState.Preparing, unchanged!.State);
        Assert.Equal(0, unchanged.Revision);
        Assert.Null(unchanged.Baseline);
        Assert.Equal(0L, await ScalarAsync(graph.Factory,
            "SELECT COUNT(*) FROM WebAutomationAttemptRemoteItems;"));
    }

    [Fact]
    public async Task CorruptCanonicalSortKeyCannotMasqueradeAsAnExactBaseline()
    {
        var graph = await CreateAsync();
        await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now));
        Assert.True(await graph.Store.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, 1, 0,
            ExactBaseline("before"), Fingerprint('f'), graph.Now.AddSeconds(1)));
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE WebAutomationAttemptRemoteItems SET CanonicalSortKey=X'00';";
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SqliteWebAutomationAttemptStore(graph.Factory).GetAsync(
                graph.OperationId, graph.AccountId, 1));

        Assert.Equal("WEB_BASELINE_CANONICAL_SORT_KEY_MISMATCH", error.Message);
    }

    [Fact]
    public async Task BaselineSchemaAndModelDoNotRetainRemoteDisplayNames()
    {
        var graph = await CreateAsync();
        var baseline = ExactBaseline("private-item");
        await using var connection = await graph.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(WebAutomationAttemptRemoteItems);";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));

        Assert.DoesNotContain("DisplayName", columns, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            baseline.Items.Single().GetType().GetProperties(),
            property => property.Name.Contains("Display", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BeginAcceptsEveryOwnerKindOnlyForItsAuthoritativeBusinessRow()
    {
        var graph = await CreateAsync();

        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now)));
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.ShareOperationId, graph.AccountId, 1, WebAutomationOwnerKind.TransferShare, graph.Now)));
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.StandaloneOperationId, graph.AccountId, 1, WebAutomationOwnerKind.StandaloneShare, graph.Now)));
    }

    [Fact]
    public async Task RequestOwnershipRequiresTheExactAuthoritativeBatchAccountAndGeneration()
    {
        var graph = await CreateAsync();
        var ownership = new SqliteWebAutomationRequestOwnershipStore(graph.Factory);

        Assert.True(await ownership.IsBoundAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 7, WebAutomationOwnerKind.Transfer));
        Assert.True(await ownership.IsBoundAsync(
            graph.ShareOperationId, graph.BatchId, graph.AccountId, 1, WebAutomationOwnerKind.TransferShare));
        Assert.True(await ownership.IsBoundAsync(
            graph.StandaloneOperationId, graph.StandaloneBatchId, graph.AccountId, 1,
            WebAutomationOwnerKind.StandaloneShare));
        Assert.False(await ownership.IsBoundAsync(
            graph.OperationId, Guid.NewGuid(), graph.AccountId, 7, WebAutomationOwnerKind.Transfer));
        Assert.False(await ownership.IsBoundAsync(
            graph.ShareOperationId, graph.BatchId, graph.AccountId, 2, WebAutomationOwnerKind.TransferShare));
        Assert.False(await ownership.IsBoundAsync(
            graph.StandaloneOperationId, graph.StandaloneBatchId, graph.OtherAccountId, 1,
            WebAutomationOwnerKind.StandaloneShare));
    }

    [Theory]
    [InlineData(WebAutomationOwnerKind.Transfer)]
    [InlineData(WebAutomationOwnerKind.TransferShare)]
    [InlineData(WebAutomationOwnerKind.StandaloneShare)]
    public async Task BeginRejectsWrongAccountAndNonexistentBusinessOwner(WebAutomationOwnerKind ownerKind)
    {
        var graph = await CreateAsync();
        var operationId = ownerKind switch
        {
            WebAutomationOwnerKind.Transfer => graph.OperationId,
            WebAutomationOwnerKind.TransferShare => graph.ShareOperationId,
            WebAutomationOwnerKind.StandaloneShare => graph.StandaloneOperationId,
            _ => throw new InvalidOperationException(),
        };

        Assert.False(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            operationId, graph.OtherAccountId, 1, ownerKind, graph.Now)));
        Assert.False(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            Guid.NewGuid(), graph.AccountId, 1, ownerKind, graph.Now)));
    }

    [Theory]
    [InlineData(WebAutomationAttemptState.Preparing)]
    [InlineData(WebAutomationAttemptState.BaselinePersisted)]
    [InlineData(WebAutomationAttemptState.Submitting)]
    [InlineData(WebAutomationAttemptState.ResultPending)]
    public async Task UserHandoffAfterRestartCanResumeOnlyItsExactPriorStage(
        WebAutomationAttemptState priorStage)
    {
        var graph = await CreateAsync();
        var generation = (int)priorStage + 20;
        var beforeHandoff = await MoveToStageAsync(graph, generation, priorStage);
        Assert.True(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, generation, priorStage, beforeHandoff.Revision,
            WebAutomationAttemptState.UserHandoff, Fingerprint('7'), true,
            graph.Now.AddMinutes(1)));

        var reopened = new SqliteWebAutomationAttemptStore(graph.Factory);
        var handoff = (await reopened.GetAsync(graph.OperationId, graph.AccountId, generation))!;
        Assert.Equal(WebAutomationAttemptState.UserHandoff, handoff.State);
        Assert.Equal(priorStage, handoff.ResumeStage);
        Assert.Equal(beforeHandoff.Baseline?.Fingerprint, handoff.Baseline?.Fingerprint);
        var wrongStage = priorStage == WebAutomationAttemptState.Preparing
            ? WebAutomationAttemptState.BaselinePersisted
            : WebAutomationAttemptState.Preparing;
        Assert.False(await reopened.TryResumeFromUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation, handoff.Revision,
            wrongStage, Fingerprint('8'), graph.Now.AddMinutes(2)));
        Assert.False(await reopened.TryResumeFromUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation, handoff.Revision - 1,
            priorStage, Fingerprint('8'), graph.Now.AddMinutes(2)));
        Assert.False(await reopened.TryResumeFromUserHandoffAsync(
            graph.OperationId, graph.OtherAccountId, generation, handoff.Revision,
            priorStage, Fingerprint('8'), graph.Now.AddMinutes(2)));
        Assert.False(await reopened.TryResumeFromUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation + 1, handoff.Revision,
            priorStage, Fingerprint('8'), graph.Now.AddMinutes(2)));
        Assert.False(await reopened.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, generation, WebAutomationAttemptState.UserHandoff,
            handoff.Revision, WebAutomationAttemptState.Succeeded, Fingerprint('8'), false,
            graph.Now.AddMinutes(2)));

        Assert.True(await reopened.TryResumeFromUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation, handoff.Revision,
            priorStage, Fingerprint('8'), graph.Now.AddMinutes(2)));
        var resumed = (await reopened.GetAsync(graph.OperationId, graph.AccountId, generation))!;
        Assert.Equal(priorStage, resumed.State);
        Assert.Null(resumed.ResumeStage);
        Assert.False(resumed.IsWaitingForUser);
        Assert.Equal(beforeHandoff.Baseline?.Fingerprint, resumed.Baseline?.Fingerprint);
    }

    [Theory]
    [InlineData(WebAutomationAttemptState.Preparing, WebAutomationAttemptState.DefinitelyNotSubmitted)]
    [InlineData(WebAutomationAttemptState.BaselinePersisted, WebAutomationAttemptState.DefinitelyNotSubmitted)]
    [InlineData(WebAutomationAttemptState.Submitting, WebAutomationAttemptState.NeedsDecision)]
    [InlineData(WebAutomationAttemptState.ResultPending, WebAutomationAttemptState.NeedsDecision)]
    public async Task UserHandoffSkipAtomicallyPersistsOnlyItsSafeTerminalState(
        WebAutomationAttemptState priorStage,
        WebAutomationAttemptState terminalState)
    {
        var graph = await CreateAsync();
        var generation = (int)priorStage + 40;
        var beforeHandoff = await MoveToStageAsync(graph, generation, priorStage);
        Assert.True(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, generation, priorStage, beforeHandoff.Revision,
            WebAutomationAttemptState.UserHandoff, Fingerprint('9'), true,
            graph.Now.AddMinutes(1)));
        var handoff = (await graph.Store.GetAsync(
            graph.OperationId, graph.AccountId, generation))!;
        var unsafeTerminal = terminalState == WebAutomationAttemptState.NeedsDecision
            ? WebAutomationAttemptState.DefinitelyNotSubmitted
            : WebAutomationAttemptState.NeedsDecision;

        Assert.False(await graph.Store.TryResolveUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation, handoff.Revision,
            priorStage, unsafeTerminal, Fingerprint('a'), graph.Now.AddMinutes(2)));
        Assert.False(await graph.Store.TryResolveUserHandoffAsync(
            graph.OperationId, graph.OtherAccountId, generation, handoff.Revision,
            priorStage, terminalState, Fingerprint('a'), graph.Now.AddMinutes(2)));
        Assert.True(await graph.Store.TryResolveUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation, handoff.Revision,
            priorStage, terminalState, Fingerprint('a'), graph.Now.AddMinutes(2)));
        Assert.False(await graph.Store.TryResolveUserHandoffAsync(
            graph.OperationId, graph.AccountId, generation, handoff.Revision,
            priorStage, terminalState, Fingerprint('a'), graph.Now.AddMinutes(2)));

        var restored = await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, generation);
        Assert.Equal(terminalState, restored!.State);
        Assert.Null(restored.ResumeStage);
        Assert.False(restored.IsWaitingForUser);
    }

    [Fact]
    public async Task Advance_RejectsOldGenerationWrongAccountStaleRevisionAndInvalidTransition()
    {
        var graph = await CreateAsync();
        await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now));

        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 1, WebAutomationAttemptState.Preparing, 0,
            WebAutomationAttemptState.DefinitelyNotSubmitted, Fingerprint('b'), false, graph.Now));
        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, Guid.NewGuid(), 2, WebAutomationAttemptState.Preparing, 0,
            WebAutomationAttemptState.DefinitelyNotSubmitted, Fingerprint('b'), false, graph.Now));
        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 2, WebAutomationAttemptState.Preparing, 1,
            WebAutomationAttemptState.DefinitelyNotSubmitted, Fingerprint('b'), false, graph.Now));
        Assert.False(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 2, WebAutomationAttemptState.Preparing, 0,
            WebAutomationAttemptState.Succeeded, Fingerprint('b'), false, graph.Now));
    }

    [Fact]
    public async Task ResultCannotSucceedFromFingerprintWithoutExactPersistedBaseline()
    {
        var graph = await CreateAsync();
        await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.Transfer, graph.Now));

        var changed = await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 1, WebAutomationAttemptState.Preparing, 0,
            WebAutomationAttemptState.Succeeded, Fingerprint('c'), false, graph.Now.AddMinutes(1));

        Assert.False(changed);
        Assert.Equal(WebAutomationAttemptState.Preparing,
            (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, 1))!.State);
    }

    [Fact]
    public async Task RecoveryReadsAreBoundedAndModelsRedactEvidence()
    {
        var graph = await CreateAsync();
        for (var index = 0; index < 3; index++)
        {
            await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
                graph.OperationId, graph.AccountId, index + 1, WebAutomationOwnerKind.Transfer,
                graph.Now.AddSeconds(index)));
        }

        var page = await graph.Store.GetRecoveryPageAsync(new PageQuery(1, 2));

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(3, page.TotalCount);
        var text = page.Items[0].ToString();
        Assert.Contains("[REDACTED]", text);
        Assert.DoesNotContain(Fingerprint('a'), text, StringComparison.Ordinal);
    }

    private static WebAutomationBaseline ExactBaseline(params string[] ids) =>
        WebAutomationBaseline.Create(ids);

    private static async Task<WebAutomationAttemptRecord> MoveToStageAsync(
        Graph graph,
        int generation,
        WebAutomationAttemptState stage)
    {
        Assert.True(await graph.Store.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, generation, WebAutomationOwnerKind.Transfer, graph.Now)));
        if (stage == WebAutomationAttemptState.Preparing)
            return (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, generation))!;

        Assert.True(await graph.Store.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, generation, 0,
            ExactBaseline($"before-{generation}"), Fingerprint('6'), graph.Now.AddSeconds(1)));
        var current = (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, generation))!;
        if (stage == WebAutomationAttemptState.BaselinePersisted) return current;

        Assert.True(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, generation, WebAutomationAttemptState.BaselinePersisted,
            current.Revision, WebAutomationAttemptState.Submitting, Fingerprint('6'), false,
            graph.Now.AddSeconds(2)));
        current = (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, generation))!;
        if (stage == WebAutomationAttemptState.Submitting) return current;

        Assert.True(await graph.Store.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, generation, WebAutomationAttemptState.Submitting,
            current.Revision, WebAutomationAttemptState.ResultPending, Fingerprint('6'), false,
            graph.Now.AddSeconds(3)));
        return (await graph.Store.GetAsync(graph.OperationId, graph.AccountId, generation))!;
    }

    private static string Fingerprint(char value) => new(value, 64);

    private async Task<Graph> CreateAsync(
        int maximumBaselineItemCount = 100_000,
        int baselineReadPageSize = 1_000)
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var shareOperationId = Guid.NewGuid();
        var standaloneBatchId = Guid.NewGuid();
        var standaloneOperationId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        await SeedBusinessOwnersAsync(
            factory, accountId, otherAccountId, batchId, resourceId, operationId,
            shareOperationId, standaloneBatchId, standaloneOperationId, now);
        return new Graph(
            new SqliteWebAutomationAttemptStore(factory, maximumBaselineItemCount, baselineReadPageSize),
            factory,
            operationId, shareOperationId, standaloneOperationId, batchId, standaloneBatchId,
            accountId, otherAccountId, now);
    }

    private static async Task SeedBusinessOwnersAsync(
        SqliteConnectionFactory factory,
        Guid accountId,
        Guid otherAccountId,
        Guid batchId,
        Guid resourceId,
        Guid operationId,
        Guid shareOperationId,
        Guid standaloneBatchId,
        Guid standaloneOperationId,
        DateTimeOffset now)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,
              SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
            VALUES($account,'owner','owner','WebView2','Active',0,0,0,1,'2026-08-25',0),
                  ($other,'other','other','WebView2','Active',0,0,1,1,'2026-08-25',0);
            INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,
              OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,QueueOrder)
            VALUES($batch,'job','Text','Distribution','TaskCount','ProportionalReduction',1,1,'/','None','Skip',
              'Immediate','Queued',$now,1);
            INSERT INTO ResourceItems(Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,
              FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
            VALUES($resource,$batch,1,'https://pan.baidu.com/s/test','test','1111','None','Valid','Expanded',$now,$now);
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
              NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,
              OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt)
            VALUES($operation,$resource,$batch,1,1,'/','/','',$account,'Automatic','Waiting',0,'transfer-fp',
              'NotPrepared','Unoccupied',$now,$now);
            INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,
              PlannedShareCount,ShareEnabled,CreatedShareOperationCount)
            VALUES($batch,$account,1,'owner',1,1,1,0);
            INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,
              FixedExtractionCode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
            VALUES($batch,1,1,'Random',NULL,'Ask','settings-fp',$now);
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,
              Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,AttemptCount,
              LeaseGeneration,BaselineShareCount,CreatedAt,UpdatedAt)
            VALUES($share,$operation,$resource,$batch,$account,1,'WaitingTransfer',NULL,'settings-fp',1,'Unknown',0,0,0,$now,$now);
            INSERT INTO StandaloneShareBatches(Id,DraftFingerprint,ResultDirectoryReference,Status,OperationCount,
              Revision,CreatedAt,UpdatedAt)
            VALUES($standaloneBatch,'draft-fp',$directory,'Queued',1,0,$now,$now);
            INSERT INTO StandaloneShareOperations(Id,BatchId,AccountId,SequenceNumber,DisplayName,ResourceKind,
              ParentPath,ExactItemSetFingerprint,Generation,Status,SubmissionState,SubmissionAcceptance,
              LeaseGeneration,Revision,CreatedAt,UpdatedAt)
            VALUES($standaloneOperation,$standaloneBatch,$account,1,'item','File','/','exact-fp',1,'Queued',
              'NotStarted','Unknown',0,0,$now,$now);
            INSERT INTO StandaloneShareRemoteItems(OperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName)
            VALUES($standaloneOperation,0,'remote-id',X'01','item');
            """;
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$other", otherAccountId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$resource", resourceId.ToString("D"));
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$share", shareOperationId.ToString("D"));
        command.Parameters.AddWithValue("$standaloneBatch", standaloneBatchId.ToString("D"));
        command.Parameters.AddWithValue("$standaloneOperation", standaloneOperationId.ToString("D"));
        command.Parameters.AddWithValue("$directory", Path.Combine(Path.GetTempPath(), $"web-owner-{standaloneBatchId:N}"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record Graph(
        SqliteWebAutomationAttemptStore Store,
        SqliteConnectionFactory Factory,
        Guid OperationId,
        Guid ShareOperationId,
        Guid StandaloneOperationId,
        Guid BatchId,
        Guid StandaloneBatchId,
        Guid AccountId,
        Guid OtherAccountId,
        DateTimeOffset Now);
}
