using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class CreateBatchServiceTests
{
    [Fact]
    public async Task MissingSelectionRejectsBeforeOpeningUnitOfWork()
    {
        var factory = new RejectingUnitOfWorkFactory();
        var probe = new ReplicationPreflightEstimatorTests.StubDiskSpaceProbe(long.MaxValue);
        var service = new CreateBatchService(factory, new BatchDraftValidator(),
            new ReplicationPreflightEstimator(probe, "jobs.db"));
        var draft = Draft(Guid.NewGuid(), TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(Guid.NewGuid(), 0, "/target", 1, 0, BatchAccountSelectionDecision.Unspecified)]);

        await Assert.ThrowsAsync<BatchDraftValidationException>(() => service.PreviewAsync(draft));

        Assert.Equal(0, factory.BeginCount);
        Assert.Null(probe.LastPath);
    }

    [Fact]
    public async Task MissingExplicitSelectionDecisionRejectsBeforeDiskProbeAndWrites()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var probe = new ReplicationPreflightEstimatorTests.StubDiskSpaceProbe(long.MaxValue);
        var service = new CreateBatchService(database.UnitOfWorkFactory, new BatchDraftValidator(),
            new ReplicationPreflightEstimator(probe, database.DatabasePath));
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Unspecified)]);

        await Assert.ThrowsAsync<BatchDraftValidationException>(() => service.PreviewAsync(draft));

        Assert.Null(probe.LastPath);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public async Task AllAccountsExplicitlyNotSelectedRejectsWithZeroWrites()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.NotSelected)]);
        var probe = new ReplicationPreflightEstimatorTests.StubDiskSpaceProbe(long.MaxValue);
        var service = new CreateBatchService(database.UnitOfWorkFactory, new BatchDraftValidator(),
            new ReplicationPreflightEstimator(probe, database.DatabasePath));

        await Assert.ThrowsAsync<BatchDraftValidationException>(() => service.PreviewAsync(draft));

        Assert.Null(probe.LastPath);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public async Task NonselectedAccountQuotaAndRowMappingCannotAuthorizeWork()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var selected = Guid.NewGuid();
        var notSelected = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(selected, "a"), BatchTestData.Account(notSelected, "b")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1, mappedAccountId: notSelected)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [
                new BatchAccountDraft(selected, 0, "/a", 1, 0, BatchAccountSelectionDecision.Selected),
                new BatchAccountDraft(notSelected, 1, "/b", 999, 999, BatchAccountSelectionDecision.NotSelected),
            ]);
        var probe = new ReplicationPreflightEstimatorTests.StubDiskSpaceProbe(long.MaxValue);
        var service = new CreateBatchService(database.UnitOfWorkFactory, new BatchDraftValidator(),
            new ReplicationPreflightEstimator(probe, database.DatabasePath));

        await Assert.ThrowsAsync<BatchDraftValidationException>(() => service.PreviewAsync(draft));

        Assert.Null(probe.LastPath);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public void WorkloadPlannerUsesOnlySelectedAccountForTwoHundredThousandRows()
    {
        // Product-scale cardinality is proven here without constructing validator persistence snapshots.
        var accounts = Enumerable.Range(0, 10)
            .Select(index => new BatchWorkloadAccountInput(
                Guid.NewGuid(), index, $"账号{index + 1}",
                index == 0 ? BatchAccountSelectionDecision.Selected : BatchAccountSelectionDecision.NotSelected,
                true, index == 0 ? 200_000 : 999_999))
            .ToArray();

        var plan = new BatchWorkloadPlanner().Plan(200_000, TransferMode.Distribution, accounts);

        var output = Assert.Single(plan.Accounts);
        Assert.Equal(200_000, plan.TransferCount);
        Assert.Equal(200_000, plan.ShareCount);
        Assert.Equal(200_000, output.PlannedTransferCount);
        Assert.Equal(200_000, output.PlannedShareCount);
    }

    [Fact]
    public async Task ValidatorInstrumentationProvesLinearPassesAtRepresentativeScaleWithOneThousandAccounts()
    {
        // The planner tests above cover 200k cardinality. This representative fixture proves the
        // validator's asymptotic row/account passes without retaining a competing 200k object graph.
        const int rowCount = 10_000;
        const int accountCount = 1_000;
        var sessionId = Guid.NewGuid();
        var accountIds = Enumerable.Range(0, accountCount).Select(_ => Guid.NewGuid()).ToArray();
        var rows = Enumerable.Range(1, rowCount).Select(row => BatchTestData.ReadyRow(
            sessionId,
            row,
            mappedAccountId: row <= rowCount / 2 ? accountIds[(row - 1) % accountCount] : null)).ToArray();
        var accountRecords = accountIds.Select((id, index) => BatchTestData.Account(id, index.ToString())).ToArray();
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            accountIds.Select((id, index) => new BatchAccountDraft(
                id, index, $"/账号{index}", rowCount / accountCount, 0,
                BatchAccountSelectionDecision.Selected, shareEnabled: index % 2 == 0)).ToArray());
        var observer = new BatchDraftValidationComplexityObserver();
        var unit = new InMemoryValidationUnitOfWork(
            BatchTestData.ReadySession(sessionId, rowCount, rowCount), rows, accountRecords);

        var validated = await new BatchDraftValidator(observer).ValidateAsync(draft, unit);

        Assert.Equal(rowCount, validated.OperationCount);
        Assert.Equal(rowCount / 2, validated.ShareOperationCount);
        Assert.Equal(rowCount, validated.Rows.Length);
        Assert.All(validated.Accounts, account => Assert.Equal(rowCount / accountCount / 2, account.LockedItemCount));
        Assert.Equal(rowCount, observer.ConstraintRowVisits);
        Assert.Equal(accountCount * 2L, observer.ConstraintAccountLookups);
        Assert.Equal(1, observer.RuleResolverPreparationCount);
        Assert.Equal(accountCount, observer.RuleResolverAccountPreparations);
        Assert.Equal(rowCount, observer.RuleResolutionCalls);
        Assert.Equal(rowCount, unit.ImportsStore.ReadyRowReads);
        Assert.Equal(accountCount, unit.AccountsStore.AccountReads);
        Assert.Equal(accountCount, unit.AccountsStore.RulePageReads);
    }

    [Fact]
    public async Task SqlitePreviewPreservesMixedMappedAndUnmappedDistributionSemantics()
    {
        const int rowCount = 200;
        const int accountCount = 10;
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accountIds = Enumerable.Range(0, accountCount).Select(_ => Guid.NewGuid()).ToArray();
        var rows = Enumerable.Range(1, rowCount).Select(row => BatchTestData.ReadyRow(
            sessionId,
            row,
            mappedAccountId: row <= rowCount / 2 ? accountIds[(row - 1) % accountCount] : null));
        await database.SeedAsync(
            accountIds.Select((id, index) => BatchTestData.Account(id, index.ToString())),
            BatchTestData.ReadySession(sessionId, rowCount, rowCount),
            rows);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            accountIds.Select((id, index) => new BatchAccountDraft(
                id, index, $"/账号{index}", rowCount / accountCount, 0,
                BatchAccountSelectionDecision.Selected, shareEnabled: index % 2 == 0)).ToArray());

        var preview = await Service(database, long.MaxValue).PreviewAsync(draft);

        Assert.Equal(rowCount, preview.TransferCount);
        Assert.Equal(rowCount / 2, preview.ShareCount);
        Assert.All(preview.Accounts, account => Assert.Equal(rowCount / accountCount / 2, account.LockedItemCount));
        Assert.Equal(rowCount, preview.Accounts.Sum(account => account.PlannedTransferCount));
    }

    [Fact]
    public void WorkloadPlannerDistributesAndReplicatesWithoutUsingLoggedAccountCount()
    {
        var accounts = new[]
        {
            WorkloadAccount(0, 50_000),
            WorkloadAccount(1, 100_000),
            WorkloadAccount(2, 50_000),
        };
        var planner = new BatchWorkloadPlanner();

        var distribution = planner.Plan(200_000, TransferMode.Distribution, accounts);
        var replication = planner.Plan(200_000, TransferMode.Replication, accounts);

        Assert.Equal(200_000, distribution.TransferCount);
        Assert.Equal(200_000, distribution.ShareCount);
        Assert.Equal([50_000L, 100_000L, 50_000L], distribution.Accounts.Select(x => x.PlannedTransferCount));
        Assert.Equal(600_000, replication.TransferCount);
        Assert.Equal(600_000, replication.ShareCount);
        Assert.All(replication.Accounts, account => Assert.Equal(200_000, account.PlannedTransferCount));
    }

    [Fact]
    public void WorkloadPlannerKeepsTransfersWhenSharingIsDisabledAndHonorsMappedAccount()
    {
        var first = WorkloadAccount(0, 0, shareEnabled: false);
        var second = WorkloadAccount(1, 0);
        var disabled = new BatchWorkloadPlanner().Plan(
            200_000,
            TransferMode.Distribution,
            [WorkloadAccount(0, 200_000, shareEnabled: false)]);
        var plan = new BatchWorkloadPlanner().Plan(
            1,
            TransferMode.Distribution,
            [first, second],
            new Dictionary<Guid, long> { [second.AccountId] = 1 });

        Assert.Equal(200_000, disabled.TransferCount);
        Assert.Equal(0, disabled.ShareCount);
        Assert.Equal(1, plan.TransferCount);
        Assert.Equal(1, plan.ShareCount);
        Assert.Equal(0, plan.Accounts.Single(x => x.AccountId == first.AccountId).PlannedShareCount);
        Assert.Equal(1, plan.Accounts.Single(x => x.AccountId == second.AccountId).PlannedTransferCount);
    }

    [Fact]
    public void WorkloadPlannerRejectsNoSelectionAndDuplicateOrUnavailableSelection()
    {
        var id = Guid.NewGuid();
        var planner = new BatchWorkloadPlanner();

        Assert.Throws<BatchDraftValidationException>(() => planner.Plan(1, TransferMode.Distribution,
            [new(id, 0, "A", BatchAccountSelectionDecision.Unspecified, true, 1)]));
        Assert.Throws<BatchDraftValidationException>(() => planner.Plan(1, TransferMode.Distribution,
            [new(id, 0, "A", BatchAccountSelectionDecision.NotSelected, true, 1)]));
        Assert.Throws<BatchDraftValidationException>(() => planner.Plan(1, TransferMode.Replication,
            [new(id, 0, "A", BatchAccountSelectionDecision.Selected, true, 1), new(id, 1, "B", BatchAccountSelectionDecision.Selected, true, 1)]));
        Assert.Throws<BatchDraftValidationException>(() => planner.Plan(1, TransferMode.Replication,
            [new(id, 0, "A", BatchAccountSelectionDecision.Selected, true, 1, IsAvailable: false)]));
    }

    [Fact]
    public void ResultLayoutEnforcesExcelCapacityAndCollisionSuffixesDoNotChangeCount()
    {
        var builder = new ResultColumnLayoutBuilder();
        var accounts = new[] { WorkloadAccount(0, 1), WorkloadAccount(1, 1) };
        var source = Enumerable.Range(0, 16_376).Select(index => $"原始列{index}").ToArray();

        var layout = builder.Build(source, accounts);

        Assert.Equal(16_384, layout.ColumnCount);
        Assert.Throws<BatchDraftValidationException>(() =>
            builder.Build(source.Append("额外原始列").ToArray(), accounts));
        var collision = builder.Build(
            ["转存结果_解析后标题", "转存结果_原分享链接"],
            [new(Guid.NewGuid(), 0, "同名", BatchAccountSelectionDecision.Selected, true, 1), new(Guid.NewGuid(), 1, "同名", BatchAccountSelectionDecision.Selected, true, 1)]);
        Assert.Equal(10, collision.ColumnCount);
        Assert.Equal("转存结果_解析后标题（系统2）", collision.ResolvedTitleHeader);
        Assert.Equal("转存结果_原分享链接（系统2）", collision.OriginalShareUrlHeader);
        Assert.Equal(["同名（1）", "同名（2）"], collision.Accounts.Select(x => x.OutputLabel));
    }

    [Fact]
    public void ResultLayoutFingerprintPreservesHeaderCollectionBoundariesAndUnusualUnicode()
    {
        var builder = new ResultColumnLayoutBuilder();
        var account = new[] { WorkloadAccount(0, 1) };

        var single = builder.Build(["a\nb", "😀\0𠀀"], account);
        var split = builder.Build(["a", "b", "😀\0𠀀"], account);
        var replay = builder.Build(["a\nb", "😀\0𠀀"], account);
        var nullHeader = builder.Build([null], account);
        var emptyHeader = builder.Build([string.Empty], account);

        Assert.NotEqual(single.Fingerprint, split.Fingerprint);
        Assert.Equal(single.Fingerprint, replay.Fingerprint);
        Assert.NotEqual(nullHeader.Fingerprint, emptyHeader.Fingerprint);
        Assert.Throws<ArgumentException>(() => builder.Build(["broken\uD800"], account));
    }

    [Fact]
    public void ResultLayoutNeverUsesIdsPathsUrlsControlsOrSecretShapedAccountLabels()
    {
        var sensitive = new[]
        {
            Guid.NewGuid().ToString("N"),
            @"C:\Users\person\profile",
            "https://pan.baidu.com/private",
            "token=private-value",
            "双向\u202E文本",
            "to\u200Bken=obfuscated-value",
        };
        var accounts = sensitive.Select((name, index) =>
            new BatchWorkloadAccountInput(Guid.NewGuid(), index, name, BatchAccountSelectionDecision.Selected, true, 1)).ToArray();

        var layout = new ResultColumnLayoutBuilder().Build([], accounts);
        var allHeaders = layout.Accounts.SelectMany(account => new[]
        {
            account.OutputLabel, account.ShareUrlHeader, account.ExtractionCodeHeader, account.ShareStatusHeader,
        }).ToArray();

        Assert.Equal(["账号1", "账号2", "账号3", "账号4", "双向文本", "账号6"], layout.Accounts.Select(x => x.OutputLabel));
        Assert.All(sensitive, value => Assert.DoesNotContain(allHeaders, header => header.Contains(value, StringComparison.Ordinal)));
    }

    [Fact]
    public void ResultLayoutAccountLabelsAreGloballyCollisionFreeAcrossOrdinalShapedNames()
    {
        var accounts = new[] { "同名", "同名（1）", "同名" }
            .Select((name, index) => new BatchWorkloadAccountInput(
                Guid.NewGuid(), index, name, BatchAccountSelectionDecision.Selected, true, 1))
            .ToArray();

        var labels = new ResultColumnLayoutBuilder().Build([], accounts).Accounts.Select(x => x.OutputLabel).ToArray();

        Assert.Equal(3, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["同名（1）", "同名(1)", "同名（2）"], labels);
    }

    [Theory]
    [InlineData("用户原标题", "用户原标题")]
    [InlineData("  用户原标题  ", "  用户原标题  ")]
    public void ResolvedTitleKeepsNonblankUserTitleExactly(string sourceTitle, string expected)
    {
        Assert.Equal(expected, ResolvedOutputTitleBuilder.Resolve(sourceTitle, ["网盘显示名称"]));
    }

    [Fact]
    public void ResolvedTitleUsesStableSafeDisplayedNameFallbackWithoutMutatingInput()
    {
        string? sourceTitle = "   ";
        var names = new[] { "第一个资源名称", "第二个资源名称", "第三个资源名称" };

        var resolved = ResolvedOutputTitleBuilder.Resolve(sourceTitle, names);
        var unsafeResolved = ResolvedOutputTitleBuilder.Resolve(null, ["https://pan.baidu.com/s/secret"]);

        Assert.Equal("第一个资源名称 等 3 项", resolved);
        Assert.Equal("未命名资源", unsafeResolved);
        Assert.Equal("   ", sourceTitle);
        Assert.Equal("第一个资源名称", names[0]);
    }

    [Fact]
    public async Task ConfirmationTokenBindsShareSettingsAndShareEnabledWithZeroWrites()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var original = new BatchDraft(
            Guid.NewGuid(), sessionId, "Batch", TransferMode.Distribution, QuotaBasis.TaskCount,
            ShortagePolicy.ProportionalReduction,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected, shareEnabled: true)],
            "/", "None", DuplicatePolicy.Skip, "Immediate", 1, true);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(original);
        var changed = new BatchDraft(
            original.BatchJobId, sessionId, original.Name, original.TransferMode, original.QuotaBasis,
            original.ShortagePolicy,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected, shareEnabled: false)],
            original.DefaultTargetPath, original.FolderCreationMode, original.DuplicatePolicy,
            original.ValidationMode, original.QueueOrder, original.IgnoreMappedAccountsForReplication,
            extractionCodeBehavior: ShareExtractionCodeBehavior.Fixed,
            fixedExtractionCode: "a1b2",
            existingSharePolicy: "Regenerate");

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(changed, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(original.BatchJobId));
    }

    [Fact]
    public async Task ConfirmationTokenBindsMappingRawColumnsAndSafeAccountLabels()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a") with { DisplayName = "原账号" }],
            BatchTestData.ReadySession(sessionId, 1, 1) with { ColumnMappingFingerprint = "mapping-a" },
            [BatchTestData.ReadyRow(sessionId, 1)]);
        await using (var seed = await database.UnitOfWorkFactory.BeginAsync())
        {
            await seed.Imports.SetColumnDefinitionsAsync(sessionId,
                [new(sessionId, 0, "原链接", "ShareUrl", true)]);
            await seed.CommitAsync();
        }
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        await using (var update = await database.UnitOfWorkFactory.BeginAsync())
        {
            await update.Accounts.UpsertAsync(BatchTestData.Account(account, "a") with { DisplayName = "新账号" });
            await update.Imports.SetColumnDefinitionsAsync(sessionId,
                [new(sessionId, 0, "改名后的原链接", "ShareUrl", true)]);
            await update.CommitAsync();
        }

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(draft, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public async Task ConfirmationTokenSeparatesDelimiterShapedDraftsAndReplaysExactly()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(accountId, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1, note: "换行\nNUL\0😀")]);
        var account = new BatchAccountDraft(
            accountId, 0, "/目标", 1, 0, BatchAccountSelectionDecision.Selected);
        var left = new BatchDraft(
            batchId, sessionId, "任务😀\0", TransferMode.Distribution, QuotaBasis.TaskCount,
            ShortagePolicy.ProportionalReduction, [account], "/", "x|Skip",
            DuplicatePolicy.Skip, "y\n", 1, true);
        var right = new BatchDraft(
            batchId, sessionId, "任务😀\0", TransferMode.Distribution, QuotaBasis.TaskCount,
            ShortagePolicy.ProportionalReduction, [account], "/", "x",
            DuplicatePolicy.Skip, "Skip|y\n", 1, true);
        var service = Service(database, long.MaxValue);

        var leftPreview = await service.PreviewAsync(left);
        var rightPreview = await service.PreviewAsync(right);
        var replay = await service.PreviewAsync(left);

        Assert.NotEqual(leftPreview.ConfirmationToken, rightPreview.ConfirmationToken);
        Assert.Equal(leftPreview.ConfirmationToken, replay.ConfirmationToken);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("nonselected-candidate")]
    [InlineData("order")]
    [InlineData("allocation")]
    [InlineData("share-enabled")]
    [InlineData("code-mode")]
    [InlineData("fixed-code")]
    [InlineData("existing-policy")]
    [InlineData("mapping-fingerprint")]
    [InlineData("raw-column-layout")]
    [InlineData("generated-label")]
    public async Task ConfirmationTokenVNextRejectsEachChangedAuthorityFieldWithZeroWrites(string mutation)
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accounts = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var session = BatchTestData.ReadySession(sessionId, 1, 1) with { ColumnMappingFingerprint = "mapping-a" };
        await database.SeedAsync(
            [
                BatchTestData.Account(accounts[0], "a") with { DisplayName = "账号A" },
                BatchTestData.Account(accounts[1], "b") with { DisplayName = "账号B" },
            ],
            session,
            [BatchTestData.ReadyRow(sessionId, 1)]);
        await using (var seed = await database.UnitOfWorkFactory.BeginAsync())
        {
            await seed.Imports.SetColumnDefinitionsAsync(sessionId,
                [new(sessionId, 0, "原链接", "ShareUrl", true)]);
            await seed.CommitAsync();
        }
        var fixedBaseline = mutation == "fixed-code";
        var original = TokenDraft(
            Guid.NewGuid(), sessionId, accounts,
            selectSecond: mutation != "nonselected-candidate",
            extractionCodeBehavior: fixedBaseline ? ShareExtractionCodeBehavior.Fixed : ShareExtractionCodeBehavior.Random,
            fixedExtractionCode: fixedBaseline ? "a1b2" : null);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(original);
        var changed = mutation switch
        {
            "selection" => TokenDraft(original.BatchJobId, sessionId, accounts, selectSecond: false),
            "nonselected-candidate" => TokenDraft(
                original.BatchJobId, sessionId, [accounts[0], Guid.NewGuid()], selectSecond: false),
            "order" => TokenDraft(original.BatchJobId, sessionId, accounts, reverseOrder: true),
            "allocation" => TokenDraft(original.BatchJobId, sessionId, accounts, allocateToSecond: true),
            "share-enabled" => TokenDraft(original.BatchJobId, sessionId, accounts, firstShareEnabled: false),
            "code-mode" => TokenDraft(original.BatchJobId, sessionId, accounts,
                extractionCodeBehavior: ShareExtractionCodeBehavior.Fixed, fixedExtractionCode: "a1b2"),
            "fixed-code" => TokenDraft(original.BatchJobId, sessionId, accounts,
                extractionCodeBehavior: ShareExtractionCodeBehavior.Fixed, fixedExtractionCode: "b2c3"),
            "existing-policy" => TokenDraft(original.BatchJobId, sessionId, accounts, existingSharePolicy: "Regenerate"),
            _ => original,
        };
        if (mutation is "mapping-fingerprint" or "raw-column-layout" or "generated-label")
        {
            await using var update = await database.UnitOfWorkFactory.BeginAsync();
            if (mutation == "mapping-fingerprint")
                await update.Imports.UpdateSessionAsync(session with { ColumnMappingFingerprint = "mapping-b" });
            else if (mutation == "raw-column-layout")
                await update.Imports.SetColumnDefinitionsAsync(sessionId,
                    [new(sessionId, 0, "原链接改名", "ShareUrl", true)]);
            else
                await update.Accounts.UpsertAsync(BatchTestData.Account(accounts[0], "a") with { DisplayName = "账号A改名" });
            await update.CommitAsync();
        }

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(changed, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(original.BatchJobId));
    }

    [Fact]
    public async Task ResultColumnOverflowRejectsBeforeDiskProbeOrBatchWrite()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        await using (var seed = await database.UnitOfWorkFactory.BeginAsync())
        {
            await seed.Imports.SetColumnDefinitionsAsync(sessionId,
                Enumerable.Range(0, 16_380).Select(index =>
                    new ImportColumnDefinitionRecord(sessionId, index, $"列{index}", index == 0 ? "ShareUrl" : "Preserve", index == 0)));
            await seed.CommitAsync();
        }
        var probe = new ReplicationPreflightEstimatorTests.StubDiskSpaceProbe(long.MaxValue);
        var service = new CreateBatchService(
            database.UnitOfWorkFactory,
            new BatchDraftValidator(),
            new ReplicationPreflightEstimator(probe, database.DatabasePath));
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected)]);

        await Assert.ThrowsAsync<BatchDraftValidationException>(() => service.PreviewAsync(draft));

        Assert.Null(probe.LastPath);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public async Task ImmediateValidationCanFinalizeDraftWhenOptionalPrecheckWasSkipped()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1) with { Status = "Draft" },
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected)]);
        var service = Service(database, long.MaxValue);

        var preview = await service.PreviewAsync(draft);
        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.NotNull(await read.Jobs.GetAsync(draft.BatchJobId));
        Assert.Equal("BatchCreated", (await read.Imports.GetSessionAsync(sessionId))!.Status);
    }

    [Fact]
    public async Task MidTransactionOperationFailureRollsBackBatchResourcesAllocationsAuditAndImportCommit()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Replication, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 1, BatchAccountSelectionDecision.Selected)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        await using (var connection = await database.ConnectionFactory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER FailTask11Operation BEFORE INSERT ON TransferOperations BEGIN SELECT RAISE(ABORT, 'forced task11 rollback'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => service.ConfirmAsync(draft, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
        Assert.Empty(await AllResourcesAsync(read.Jobs, draft.BatchJobId));
        Assert.Empty((await read.Jobs.GetAllocationsPageAsync(draft.BatchJobId, new PageQuery(1, 10))).Items);
        Assert.Equal("ReadyToConfirm", (await read.Imports.GetSessionAsync(sessionId))!.Status);
        await using var verify = await database.ConnectionFactory.OpenConnectionAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM ExecutionLogs WHERE BatchJobId=$id;";
        count.Parameters.AddWithValue("$id", draft.BatchJobId.ToString("D"));
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ConfirmationAtomicallyPersistsSharePipelineSettingsLayoutTitlesAndCounts()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accounts = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var rows = Enumerable.Range(1, 3).Select(row => BatchTestData.ReadyRow(sessionId, row)).ToArray();
        await database.SeedAsync(
            [BatchTestData.Account(accounts[0], "first") with { DisplayName = "同名" },
             BatchTestData.Account(accounts[1], "second") with { DisplayName = "同名" }],
            BatchTestData.ReadySession(sessionId, 3, 3) with { ColumnMappingFingerprint = new string('a', 64) },
            rows,
            payloads: rows.Select((row, index) => new StagedImportRowPayloadRecord(
                row.Id, $"source-{index}", $"resolved-{index}", row.ShareUrl, "[]")));
        await using (var seed = await database.UnitOfWorkFactory.BeginAsync())
        {
            await seed.Imports.SetColumnDefinitionsAsync(sessionId,
                [new(sessionId, 0, "原链接", "ShareUrl", true), new(sessionId, 1, "标题", "Title", false)]);
            await seed.CommitAsync();
        }
        var draft = new BatchDraft(
            Guid.NewGuid(), sessionId, "Batch", TransferMode.Distribution, QuotaBasis.TaskCount,
            ShortagePolicy.ProportionalReduction,
            [new BatchAccountDraft(accounts[0], 0, "/a", 1, 0, BatchAccountSelectionDecision.Selected, shareEnabled: true),
             new BatchAccountDraft(accounts[1], 1, "/b", 2, 0, BatchAccountSelectionDecision.Selected, shareEnabled: false)],
            "/", "None", DuplicatePolicy.Skip, "Immediate", 1, true,
            extractionCodeBehavior: ShareExtractionCodeBehavior.Fixed,
            fixedExtractionCode: "a1b2", existingSharePolicy: "Ask");
        var service = Service(database, long.MaxValue);

        var preview = await service.PreviewAsync(draft);
        var created = await service.ConfirmAsync(draft, preview.ConfirmationToken);

        Assert.Equal(draft.BatchJobId, created.BatchJobId);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var job = Assert.IsType<BatchJobRecord>(await read.Jobs.GetAsync(draft.BatchJobId));
        Assert.Equal(3, job.OperationCount);
        Assert.Equal(1, job.ShareOperationCount);
        Assert.Equal(["resolved-0", "resolved-1", "resolved-2"],
            (await AllResourcesAsync(read.Jobs, draft.BatchJobId)).Select(resource => resource.ResolvedOutputTitle));
        var settings = Assert.IsType<BatchShareSettingsRecord>(await read.Jobs.GetShareSettingsAsync(draft.BatchJobId));
        Assert.True(settings.IsPermanent);
        Assert.True(settings.IsAccessUnlimited);
        Assert.Equal("Fixed", settings.ExtractionCodeMode);
        Assert.Equal("a1b2", settings.FixedExtractionCode);
        Assert.Equal("Ask", settings.ExistingSharePolicy);
        Assert.Equal(draft.ShareSettingsFingerprint, settings.SettingsFingerprint);
        var outputs = await read.Jobs.GetOutputAccountsAsync(draft.BatchJobId);
        Assert.Equal(new[] { 1, 2 }, outputs.Select(output => output.OutputOrdinal));
        Assert.Equal(new long[] { 1, 2 }, outputs.Select(output => (long)output.PlannedTransferCount));
        Assert.Equal(new long[] { 1, 0 }, outputs.Select(output => (long)output.PlannedShareCount));

        await using var connection = await database.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Status,s.ExactItemSetFingerprint,s.TransferOperationId,s.ResourceItemId,s.AccountId,
                   t.Id,t.ResourceItemId,t.AssignedAccountId,
                   (SELECT COUNT(*) FROM ShareOperationRemoteItems WHERE ShareOperationId=s.Id),
                   (SELECT COUNT(*) FROM BatchSourceColumns WHERE BatchJobId=s.BatchJobId),
                   (SELECT COUNT(*) FROM BatchCreationReceipts WHERE BatchJobId=s.BatchJobId)
            FROM ShareOperations s JOIN TransferOperations t ON t.Id=s.TransferOperationId
            WHERE s.BatchJobId=$batch;
            """;
        command.Parameters.AddWithValue("$batch", draft.BatchJobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("WaitingTransfer", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(reader.GetString(2), reader.GetString(5));
        Assert.Equal(reader.GetString(3), reader.GetString(6));
        Assert.Equal(reader.GetString(4), reader.GetString(7));
        Assert.Equal(0, reader.GetInt64(8));
        Assert.Equal(2, reader.GetInt64(9));
        Assert.Equal(1, reader.GetInt64(10));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task FailureAfterPendingShareInsertRollsBackEntireCreationGraphAndSession()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var row = BatchTestData.ReadyRow(sessionId, 1);
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")], BatchTestData.ReadySession(sessionId, 1, 1), [row],
            payloads: [new(row.Id, "source", "resolved", row.ShareUrl, "[]")]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected, shareEnabled: true)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        await using (var connection = await database.ConnectionFactory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER FailAfterPendingShare BEFORE INSERT ON AccountAllocations BEGIN SELECT RAISE(ABORT, 'forced post-share rollback'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => service.ConfirmAsync(draft, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
        Assert.Empty(await AllResourcesAsync(read.Jobs, draft.BatchJobId));
        Assert.Null(await read.Jobs.GetShareSettingsAsync(draft.BatchJobId));
        Assert.Empty(await read.Jobs.GetOutputAccountsAsync(draft.BatchJobId));
        Assert.Equal("ReadyToConfirm", (await read.Imports.GetSessionAsync(sessionId))!.Status);
        await using var verify = await database.ConnectionFactory.OpenConnectionAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT (SELECT COUNT(*) FROM TransferOperations WHERE BatchJobId=$batch) + (SELECT COUNT(*) FROM ShareOperations WHERE BatchJobId=$batch) + (SELECT COUNT(*) FROM BatchSourceColumns WHERE BatchJobId=$batch) + (SELECT COUNT(*) FROM BatchCreationReceipts WHERE BatchJobId=$batch) + (SELECT COUNT(*) FROM ExecutionLogs WHERE BatchJobId=$batch);";
        count.Parameters.AddWithValue("$batch", draft.BatchJobId.ToString("D"));
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ShareEnabledConfirmationRejectsBlankResolvedTitleWithoutWrites()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var row = BatchTestData.ReadyRow(sessionId, 1);
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")], BatchTestData.ReadySession(sessionId, 1, 1), [row],
            payloads: [new(row.Id, null, "  ", row.ShareUrl, "[]")]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected, shareEnabled: true)]);
        var service = Service(database, long.MaxValue);

        await Assert.ThrowsAsync<BatchDraftValidationException>(() => service.PreviewAsync(draft));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public async Task ExactConfirmationReplayReturnsExistingJobWithoutDuplicatingGraph()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var row = BatchTestData.ReadyRow(sessionId, 1);
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")], BatchTestData.ReadySession(sessionId, 1, 1), [row],
            payloads: [new(row.Id, "source", "resolved", row.ShareUrl, "[]")]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected, shareEnabled: true)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);

        var first = await service.ConfirmAsync(draft, preview.ConfirmationToken);
        var replay = await service.ConfirmAsync(draft, preview.ConfirmationToken);

        Assert.Equal(first.BatchJobId, replay.BatchJobId);
        Assert.Equal(first.Confirmation.ConfirmationToken, replay.Confirmation.ConfirmationToken);
        Assert.Equal(first.Confirmation.ResourceCount, replay.Confirmation.ResourceCount);
        Assert.Equal(first.Confirmation.OperationCount, replay.Confirmation.OperationCount);
        Assert.Equal(first.Confirmation.ResultColumnLayout!.Fingerprint,
            replay.Confirmation.ResultColumnLayout!.Fingerprint);
        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(draft, new string('0', 64)));
        await using var connection = await database.ConnectionFactory.OpenConnectionAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT (SELECT COUNT(*) FROM BatchJobs WHERE Id=$batch), (SELECT COUNT(*) FROM TransferOperations WHERE BatchJobId=$batch), (SELECT COUNT(*) FROM ShareOperations WHERE BatchJobId=$batch), (SELECT COUNT(*) FROM BatchCreationReceipts WHERE BatchJobId=$batch);";
        count.Parameters.AddWithValue("$batch", draft.BatchJobId.ToString("D"));
        await using var reader = await count.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
        Assert.Equal(1, reader.GetInt64(3));
    }

    [Fact]
    public async Task RestartReplayReturnsTheExactPersistedConfirmationSummaryWhenDiskStateChanges()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accounts = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var rows = new[]
        {
            BatchTestData.ReadyRow(sessionId, 1, mappedAccountId: accounts[1]),
            BatchTestData.ReadyRow(sessionId, 2),
        };
        await database.SeedAsync(
            [BatchTestData.Account(accounts[0], "a") with { DisplayName = "同名" },
             BatchTestData.Account(accounts[1], "b") with { DisplayName = "同名" }],
            BatchTestData.ReadySession(sessionId, 2, 2) with { ColumnMappingFingerprint = new string('a', 64) },
            rows);
        await using (var seed = await database.UnitOfWorkFactory.BeginAsync())
        {
            await seed.Imports.SetColumnDefinitionsAsync(sessionId,
                [new(sessionId, 0, "原链接", "ShareUrl", true), new(sessionId, 1, "标题", "Title", false)]);
            await seed.CommitAsync();
        }
        var draft = new BatchDraft(
            Guid.NewGuid(), sessionId, "Batch", TransferMode.Distribution, QuotaBasis.TaskCount,
            ShortagePolicy.ProportionalReduction,
            [new BatchAccountDraft(accounts[0], 0, "/a", 1, 0, BatchAccountSelectionDecision.Selected, shareEnabled: true),
             new BatchAccountDraft(accounts[1], 1, "/b", 1, 0, BatchAccountSelectionDecision.Selected, shareEnabled: false)],
            "/", "None", DuplicatePolicy.Skip, "Immediate", 1, true);
        var firstService = Service(database, long.MaxValue);
        var preview = await firstService.PreviewAsync(draft);
        var first = await firstService.ConfirmAsync(draft, preview.ConfirmationToken);

        await using (var connection = await database.ConnectionFactory.OpenConnectionAsync())
        await using (var mutate = connection.CreateCommand())
        {
            mutate.CommandText = "UPDATE ImportSessions SET ColumnMappingFingerprint=$mapping WHERE Id=$id;";
            mutate.Parameters.AddWithValue("$mapping", new string('b', 64));
            mutate.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            Assert.Equal(1, await mutate.ExecuteNonQueryAsync());
        }

        var restartedService = Service(database, 123_456_789);
        var replay = await restartedService.ConfirmAsync(draft, preview.ConfirmationToken);

        AssertConfirmationEqual(first.Confirmation, replay.Confirmation);
        await using var receiptConnection = await database.ConnectionFactory.OpenConnectionAsync();
        await using var receipt = receiptConnection.CreateCommand();
        receipt.CommandText = "SELECT ColumnMappingFingerprint FROM BatchCreationReceipts WHERE BatchJobId=$batch;";
        receipt.Parameters.AddWithValue("$batch", draft.BatchJobId.ToString("D"));
        Assert.Equal(new string('a', 64), await receipt.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SelectedZeroWorkAccountPersistsAsEffectiveShareDisabledWithoutPlaceholderWork()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accounts = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await database.SeedAsync(
            accounts.Select((id, index) => BatchTestData.Account(id, index.ToString())),
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(accounts[0], 0, "/a", 1, 0, BatchAccountSelectionDecision.Selected, shareEnabled: true),
             new BatchAccountDraft(accounts[1], 1, "/b", 0, 0, BatchAccountSelectionDecision.Selected, shareEnabled: true)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);

        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var outputs = await read.Jobs.GetOutputAccountsAsync(draft.BatchJobId);
        Assert.True(outputs.Single(output => output.AccountId == accounts[0]).ShareEnabled);
        var zero = outputs.Single(output => output.AccountId == accounts[1]);
        Assert.Equal(0, zero.PlannedTransferCount);
        Assert.Equal(0, zero.PlannedShareCount);
        Assert.False(zero.ShareEnabled);
        await using var connection = await database.ConnectionFactory.OpenConnectionAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM ShareOperations WHERE BatchJobId=$batch;";
        count.Parameters.AddWithValue("$batch", draft.BatchJobId.ToString("D"));
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ConfirmationTokenBecomesStaleWhenImportChangesAndWritesNothing()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        await using (var update = await database.UnitOfWorkFactory.BeginAsync())
        {
            var session = (await update.Imports.GetSessionAsync(sessionId))!;
            await update.Imports.UpdateSessionAsync(session with { UpdatedAt = session.UpdatedAt.AddSeconds(1) });
            await update.CommitAsync();
        }

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(draft, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Theory]
    [InlineData("source-content")]
    [InlineData("header-row")]
    [InlineData("precheck-account")]
    [InlineData("source-type")]
    [InlineData("source-name")]
    [InlineData("created-at")]
    public async Task ConfirmationTokenRejectsSameTimestampImportAuthorityChangesWithoutWrites(string mutation)
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var session = BatchTestData.ReadySession(sessionId, 1, 1);
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            session,
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        var (column, value) = mutation switch
        {
            "source-content" => ("SourceContentFingerprint", (object)new string('b', 64)),
            "header-row" => ("HeaderRowNumber", 2),
            "precheck-account" => ("PrecheckAccountId", account.ToString("D")),
            "source-type" => ("SourceType", "Text"),
            "source-name" => ("SourceName", "changed.xlsx"),
            "created-at" => ("CreatedAt", session.CreatedAt.AddMinutes(-1).ToString("O")),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        await using (var connection = await database.ConnectionFactory.OpenConnectionAsync())
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = $"UPDATE ImportSessions SET {column}=$value WHERE Id=$id;";
            update.Parameters.AddWithValue("$value", value);
            update.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(draft, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public async Task ConfirmationTokenBecomesStaleWhenRowChangesEvenIfSessionTimestampDoesNot()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var row = BatchTestData.ReadyRow(sessionId, 1, note: "before");
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [row]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        await using (var update = await database.UnitOfWorkFactory.BeginAsync())
        {
            await update.Imports.UpdateRowsAsync([row with { Note = "after", UpdatedAt = row.UpdatedAt.AddSeconds(1) }]);
            await update.CommitAsync();
        }

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(draft, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public async Task ConfirmationTokenCannotBeReusedWithDifferentImmutableSettings()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        var changed = new BatchDraft(
            draft.BatchJobId, draft.ImportSessionId, draft.Name, draft.TransferMode, draft.QuotaBasis,
            draft.ShortagePolicy, draft.Accounts, draft.DefaultTargetPath, draft.FolderCreationMode,
            DuplicatePolicy.Rename, draft.ValidationMode, draft.QueueOrder, draft.IgnoreMappedAccountsForReplication);

        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(changed, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public async Task AuditUsesFixedSanitizedTextOnly()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var sensitiveUrl = "https://pan.baidu.com/s/private-resource";
        var sensitiveCode = "p4ss";
        var sensitiveNote = "customer-secret-note";
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1, note: sensitiveNote) with { ShareUrl = sensitiveUrl, ExtractionCode = sensitiveCode }]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using var connection = await database.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EventCode,SanitizedMessage FROM ExecutionLogs WHERE BatchJobId=$id;";
        command.Parameters.AddWithValue("$id", draft.BatchJobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var text = $"{reader.GetString(0)} {reader.GetString(1)}";
        Assert.Equal("BATCH_CREATED Batch creation was confirmed.", text);
        Assert.DoesNotContain(sensitiveUrl, text, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveCode, text, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveNote, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateConstrainedToRemovedZeroTargetAccountIsRejected()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var positive = Guid.NewGuid();
        var zero = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(positive, "positive"), BatchTestData.Account(zero, "zero")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1, mappedAccountId: zero)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.SuccessTarget,
            [new BatchAccountDraft(positive, 0, "/positive", 0, 1, BatchAccountSelectionDecision.Selected), new BatchAccountDraft(zero, 1, "/zero", 0, 0, BatchAccountSelectionDecision.NotSelected)]);

        await Assert.ThrowsAsync<BatchDraftValidationException>(() => Service(database, long.MaxValue).PreviewAsync(draft));
    }

    [Fact]
    public async Task ConfirmedReplicationCreatesOneThousandResourcesAndThreeThousandUniqueOperations()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accountIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await database.SeedAsync(
            accountIds.Select((id, index) => BatchTestData.Account(id, index.ToString())),
            BatchTestData.ReadySession(sessionId, 1_000, 1_000),
            Enumerable.Range(1, 1_000).Select(row => BatchTestData.ReadyRow(
                sessionId, row, targetPath: row == 1 ? "/row override" : null,
                folder: row == 1 ? "outer" : null, note: row == 1 ? "private note" : null)));
        var draft = Draft(
            sessionId,
            TransferMode.Replication,
            QuotaBasis.TaskCount,
            accountIds.Select((id, index) => new BatchAccountDraft(
                id, index, $"/account-{index}", index == 0 ? 0 : index * 17,
                index == 0 ? 0 : index * 123, BatchAccountSelectionDecision.Selected)).ToArray());
        var service = Service(database, long.MaxValue);

        var preview = await service.PreviewAsync(draft);
        Assert.Equal(1_000, preview.ResourceCount);
        Assert.Equal(3_000, preview.OperationCount);
        Assert.All(preview.Accounts, account =>
        {
            Assert.Equal(1_000, account.TargetItemCount);
            Assert.Equal(1_000, account.TargetSuccessCount);
            Assert.Equal(1_000, account.EffectiveTargetSuccessCount);
            Assert.Equal(1_000, account.ReachableTargetSuccessCount);
        });

        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var job = Assert.IsType<BatchJobRecord>(await read.Jobs.GetAsync(draft.BatchJobId));
        var resources = await AllResourcesAsync(read.Jobs, draft.BatchJobId);
        var operations = await AllOperationsAsync(read.Jobs, draft.BatchJobId);
        var allocations = (await read.Jobs.GetAllocationsPageAsync(draft.BatchJobId, new PageQuery(1, 10))).Items;
        Assert.Equal(1_000, job.ResourceCount);
        Assert.Equal(3_000, job.OperationCount);
        Assert.Equal(1_000, resources.Count);
        Assert.Equal(3_000, operations.Count);
        Assert.Equal(3_000, operations.Select(operation => operation.OperationFingerprint).Distinct().Count());
        Assert.Equal(accountIds, allocations.Select(allocation => allocation.AccountId));
        Assert.Equal(new[] { "/account-0", "/account-1", "/account-2" }, allocations.Select(allocation => allocation.DefaultTargetPath));
        Assert.All(allocations, allocation =>
        {
            Assert.Equal(1_000, allocation.TargetItemCount);
            Assert.Equal(1_000, allocation.TargetSuccessCount);
            Assert.Equal(1_000, allocation.EffectiveTargetSuccessCount);
        });
        var overridden = resources.Single(resource => resource.CandidateSequence == 1);
        Assert.Equal("private note", overridden.Note);
        Assert.Equal("/row override", overridden.TargetPathOverride);
        Assert.All(operations.Where(operation => operation.ResourceItemId == overridden.Id), operation =>
        {
            Assert.Equal("/row override", operation.TargetPath);
            Assert.Equal("outer", operation.CreateFolderName);
        });
        var committed = await read.Imports.GetSessionAsync(sessionId);
        Assert.Equal("BatchCreated", committed!.Status);
        Assert.Equal(draft.BatchJobId, committed.CreatedBatchJobId);
    }

    [Fact]
    public async Task InsufficientDiskRejectsBeforeAnyBatchWrite()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Replication, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 1, BatchAccountSelectionDecision.Selected)]);
        var service = Service(database, 0);

        var preview = await service.PreviewAsync(draft);
        Assert.False(preview.StorageEstimate.HasSufficientSpace);
        await Assert.ThrowsAsync<StorageCapacityException>(() =>
            service.ConfirmAsync(draft, preview.ConfirmationToken));

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
        Assert.Equal("ReadyToConfirm", (await read.Imports.GetSessionAsync(sessionId))!.Status);
    }

    [Fact]
    public async Task TaskQuotaDistributionHonorsLockedAssignmentsSnapshotsAndRemovesZeroTargets()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accounts = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var rows = Enumerable.Range(1, 6)
            .Select(row => BatchTestData.ReadyRow(
                sessionId, row, mappedAccountId: row == 1 ? accounts[1] : null,
                targetPath: row == 1 ? "/locked-path" : null,
                folder: row == 1 ? "locked-folder" : null,
                note: row == 1 ? "retain me" : null))
            .ToArray();
        await database.SeedAsync(
            accounts.Select((id, index) => BatchTestData.Account(id, index.ToString())),
            BatchTestData.ReadySession(sessionId, 6, 6), rows);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [
                new BatchAccountDraft(accounts[0], 0, "/a", 3, 0, BatchAccountSelectionDecision.Selected),
                new BatchAccountDraft(accounts[1], 1, "/b", 3, 0, BatchAccountSelectionDecision.Selected),
                new BatchAccountDraft(accounts[2], 2, "/removed", 0, 0, BatchAccountSelectionDecision.NotSelected),
            ]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);

        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var allocations = (await read.Jobs.GetAllocationsPageAsync(draft.BatchJobId, new PageQuery(1, 10))).Items;
        var resources = await AllResourcesAsync(read.Jobs, draft.BatchJobId);
        var operations = await AllOperationsAsync(read.Jobs, draft.BatchJobId);
        Assert.Equal(6, resources.Count);
        Assert.Equal(6, operations.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, resources.Select(resource => resource.CandidateSequence));
        Assert.Equal(2, allocations.Count);
        Assert.Equal(6, allocations.Sum(allocation => allocation.TargetItemCount));
        Assert.Equal(1, allocations.Single(allocation => allocation.AccountId == accounts[1]).LockedItemCount);
        var lockedResource = resources.Single(resource => resource.StagedImportRowId == rows[0].Id);
        var lockedOperation = operations.Single(operation => operation.ResourceItemId == lockedResource.Id);
        Assert.Equal(accounts[1], lockedOperation.AssignedAccountId);
        Assert.Equal("/locked-path", lockedOperation.TargetPath);
        Assert.Equal("locked-folder", lockedOperation.CreateFolderName);
        Assert.Equal("retain me", lockedResource.Note);
    }

    [Fact]
    public async Task SuccessTargetDistributionPersistsExclusiveCandidatesAndDefersOperations()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accounts = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await database.SeedAsync(
            accounts.Select((id, index) => BatchTestData.Account(id, index.ToString())),
            BatchTestData.ReadySession(sessionId, 5, 5),
            Enumerable.Range(1, 5).Select(row => BatchTestData.ReadyRow(
                sessionId, row, mappedAccountId: row <= 2 ? accounts[0] : null)));
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.SuccessTarget,
            [
                new BatchAccountDraft(accounts[0], 0, "/a", 0, 2, BatchAccountSelectionDecision.Selected),
                new BatchAccountDraft(accounts[1], 1, "/b", 0, 1, BatchAccountSelectionDecision.Selected),
                new BatchAccountDraft(accounts[2], 2, "/removed", 0, 0, BatchAccountSelectionDecision.NotSelected),
            ]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);

        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var resources = await AllResourcesAsync(read.Jobs, draft.BatchJobId);
        var operations = await AllOperationsAsync(read.Jobs, draft.BatchJobId);
        var allocations = (await read.Jobs.GetAllocationsPageAsync(draft.BatchJobId, new PageQuery(1, 10))).Items;
        Assert.Equal(5, preview.OperationCount);
        Assert.Equal(5, preview.StorageEstimate.OperationCount);
        Assert.Empty(operations);
        Assert.Equal(2, resources.Count(resource => resource.CandidateConstraintAccountId == accounts[0]));
        Assert.Equal(2, allocations.Single(allocation => allocation.AccountId == accounts[0]).ExclusiveCandidateCount);
        Assert.DoesNotContain(allocations, allocation => allocation.AccountId == accounts[2]);
    }

    [Fact]
    public async Task SuccessTargetReservationAtomicallyCreatesItsBoundPendingShareOperation()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.SuccessTarget,
            [new BatchAccountDraft(account, 0, "/target", 0, 1,
                BatchAccountSelectionDecision.Selected, shareEnabled: true)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);

        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using (var read = await database.UnitOfWorkFactory.BeginAsync())
        {
            var created = Assert.IsType<BatchJobRecord>(await read.Jobs.GetAsync(draft.BatchJobId));
            Assert.Equal(0, created.OperationCount);
            Assert.Equal(0, created.ShareOperationCount);
        }
        var scheduler = new SqliteTransferSchedulingStore(database.ConnectionFactory);
        Assert.Equal(draft.BatchJobId, await scheduler.TryActivateNextBatchAsync(BatchTestData.Now));
        var reserved = Assert.IsType<BaiduBatchTransfer.Application.Scheduling.ScheduledOperation>(
            await scheduler.TryReserveNextAsync(
                draft.BatchJobId, BatchTestData.Now, BatchTestData.Now.AddMinutes(5)));

        await using var connection = await database.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job.OperationCount,job.ShareOperationCount,share.TransferOperationId,
              share.ResourceItemId,share.BatchJobId,share.AccountId,share.Status,
              share.ExactItemSetFingerprint,share.OutputOrdinal,output.CreatedShareOperationCount
            FROM BatchJobs job
            JOIN TransferOperations transfer ON transfer.BatchJobId=job.Id
            JOIN ShareOperations share ON share.TransferOperationId=transfer.Id
            JOIN BatchOutputAccounts output ON output.BatchJobId=share.BatchJobId
              AND output.AccountId=share.AccountId AND output.OutputOrdinal=share.OutputOrdinal
            WHERE job.Id=$batch;
            """;
        command.Parameters.AddWithValue("$batch", draft.BatchJobId.ToString("D"));
        await using var row = await command.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal(1, row.GetInt64(0));
        Assert.Equal(1, row.GetInt64(1));
        Assert.Equal(reserved.OperationId.ToString("D"), row.GetString(2));
        Assert.False(row.IsDBNull(3));
        Assert.Equal(draft.BatchJobId.ToString("D"), row.GetString(4));
        Assert.Equal(account.ToString("D"), row.GetString(5));
        Assert.Equal("WaitingTransfer", row.GetString(6));
        Assert.True(row.IsDBNull(7));
        Assert.Equal(1, row.GetInt32(8));
        Assert.Equal(1, row.GetInt64(9));
        Assert.False(await row.ReadAsync());
    }

    [Fact]
    public async Task SuccessTargetFailureAfterPendingShareInsertRollsBackDynamicReservationGraph()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 1, 1),
            [BatchTestData.ReadyRow(sessionId, 1)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.SuccessTarget,
            [new BatchAccountDraft(account, 0, "/target", 0, 1,
                BatchAccountSelectionDecision.Selected, shareEnabled: true)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        await service.ConfirmAsync(draft, preview.ConfirmationToken);
        var scheduler = new SqliteTransferSchedulingStore(database.ConnectionFactory);
        Assert.Equal(draft.BatchJobId, await scheduler.TryActivateNextBatchAsync(BatchTestData.Now));
        await using (var connection = await database.ConnectionFactory.OpenConnectionAsync())
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER FailDynamicReservationAfterPendingShare
                BEFORE UPDATE ON AccountAllocations
                WHEN EXISTS(
                  SELECT 1 FROM ShareOperations share
                  JOIN TransferOperations transfer ON transfer.Id=share.TransferOperationId
                  WHERE transfer.BatchJobId=NEW.BatchJobId)
                BEGIN SELECT RAISE(ABORT, 'forced dynamic share rollback'); END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => scheduler.TryReserveNextAsync(
            draft.BatchJobId, BatchTestData.Now, BatchTestData.Now.AddMinutes(5)));

        await using var verify = await database.ConnectionFactory.OpenConnectionAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = """
            SELECT job.OperationCount,job.ShareOperationCount,
              (SELECT COUNT(*) FROM TransferOperations WHERE BatchJobId=job.Id),
              (SELECT COUNT(*) FROM ShareOperations WHERE BatchJobId=job.Id),
              resource.CandidateStatus,allocation.AssignedItemCount,allocation.ReservedSlotCount,
              output.CreatedShareOperationCount
            FROM BatchJobs job
            JOIN ResourceItems resource ON resource.BatchJobId=job.Id
            JOIN AccountAllocations allocation ON allocation.BatchJobId=job.Id
            JOIN BatchOutputAccounts output ON output.BatchJobId=job.Id
            WHERE job.Id=$batch;
            """;
        command.Parameters.AddWithValue("$batch", draft.BatchJobId.ToString("D"));
        await using var row = await command.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal(0, row.GetInt64(0));
        Assert.Equal(0, row.GetInt64(1));
        Assert.Equal(0, row.GetInt64(2));
        Assert.Equal(0, row.GetInt64(3));
        Assert.Equal("Unallocated", row.GetString(4));
        Assert.Equal(0, row.GetInt64(5));
        Assert.Equal(0, row.GetInt64(6));
        Assert.Equal(0, row.GetInt64(7));
    }

    [Fact]
    public async Task FailedSuccessTargetAttemptReleasesPendingShareCapacityForSameAccountRetry()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accounts = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await database.SeedAsync(
            accounts.Select((id, index) => BatchTestData.Account(id, index.ToString())),
            BatchTestData.ReadySession(sessionId, 2, 2),
            [BatchTestData.ReadyRow(sessionId, 1), BatchTestData.ReadyRow(sessionId, 2)]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.SuccessTarget,
            [new BatchAccountDraft(accounts[0], 0, "/a", 0, 1, BatchAccountSelectionDecision.Selected, shareEnabled: true),
             new BatchAccountDraft(accounts[1], 1, "/b", 0, 1, BatchAccountSelectionDecision.Selected, shareEnabled: true)]);
        var service = Service(database, long.MaxValue);
        var preview = await service.PreviewAsync(draft);
        await service.ConfirmAsync(draft, preview.ConfirmationToken);
        var scheduler = new SqliteTransferSchedulingStore(database.ConnectionFactory);
        Assert.Equal(draft.BatchJobId, await scheduler.TryActivateNextBatchAsync(BatchTestData.Now));
        var first = Assert.IsType<BaiduBatchTransfer.Application.Scheduling.ScheduledOperation>(
            await scheduler.TryReserveNextAsync(draft.BatchJobId, BatchTestData.Now, BatchTestData.Now.AddMinutes(5)));
        Assert.Equal(accounts[0], first.AccountId);

        await scheduler.MarkFailedAsync(first.OperationId, "TransientService",
            "The service is temporarily unavailable.", globallyInvalid: false, BatchTestData.Now.AddSeconds(1));

        var retry = Assert.IsType<BaiduBatchTransfer.Application.Scheduling.ScheduledOperation>(
            await scheduler.TryReserveNextAsync(draft.BatchJobId, BatchTestData.Now.AddSeconds(2), BatchTestData.Now.AddMinutes(5)));
        Assert.Equal(accounts[0], retry.AccountId);
        await using var connection = await database.ConnectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job.ShareOperationCount,output.CreatedShareOperationCount,
              (SELECT COUNT(*) FROM ShareOperations share WHERE share.BatchJobId=job.Id),
              (SELECT COUNT(*) FROM ShareOperations share WHERE share.TransferOperationId=$failed),
              (SELECT COUNT(*) FROM ShareOperations share WHERE share.TransferOperationId=$retry)
            FROM BatchJobs job JOIN BatchOutputAccounts output
              ON output.BatchJobId=job.Id AND output.AccountId=$account
            WHERE job.Id=$batch;
            """;
        command.Parameters.AddWithValue("$failed", first.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$retry", retry.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$account", accounts[0].ToString("D"));
        command.Parameters.AddWithValue("$batch", draft.BatchJobId.ToString("D"));
        await using var row = await command.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal(1, row.GetInt64(0));
        Assert.Equal(1, row.GetInt64(1));
        Assert.Equal(1, row.GetInt64(2));
        Assert.Equal(0, row.GetInt64(3));
        Assert.Equal(1, row.GetInt64(4));
    }

    [Fact]
    public async Task SuccessTargetPreviewShowsDeterministicEffectiveTargetsUnderExclusiveShortage()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var accounts = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await database.SeedAsync(
            accounts.Select((id, index) => BatchTestData.Account(id, index.ToString())),
            BatchTestData.ReadySession(sessionId, 4, 4),
            Enumerable.Range(1, 4).Select(row => BatchTestData.ReadyRow(sessionId, row, mappedAccountId: accounts[0])));
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.SuccessTarget,
            [new BatchAccountDraft(accounts[0], 0, "/a", 0, 2, BatchAccountSelectionDecision.Selected), new BatchAccountDraft(accounts[1], 1, "/b", 0, 2, BatchAccountSelectionDecision.Selected)]);

        var preview = await Service(database, long.MaxValue).PreviewAsync(draft);

        Assert.Equal(2, preview.Accounts.Single(account => account.AccountId == accounts[0]).EffectiveTargetSuccessCount);
        Assert.Equal(0, preview.Accounts.Single(account => account.AccountId == accounts[1]).EffectiveTargetSuccessCount);
        Assert.Equal(2, preview.Accounts.Single(account => account.AccountId == accounts[0]).ReachableTargetSuccessCount);
        Assert.Equal(0, preview.Accounts.Single(account => account.AccountId == accounts[1]).ReachableTargetSuccessCount);
    }

    [Theory]
    [InlineData("Conflict")]
    [InlineData("NeedsResolution")]
    public async Task ConflictOrNeedsResolutionRowsBlockFinalizationWithoutLeakingContent(string disposition)
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var invalidCount = disposition is "Invalid" or "Conflict" ? 1 : 0;
        var session = BatchTestData.ReadySession(sessionId, 1, 0, invalidRows: invalidCount);
        var sensitiveUrl = "https://pan.baidu.com/s/do-not-leak?pwd=secret";
        var row = BatchTestData.ReadyRow(sessionId, 1) with
        {
            ShareUrl = sensitiveUrl,
            ExtractionCode = "secret",
            NormalizedShareId = disposition == "NeedsResolution" ? null : "sensitive-id",
            Disposition = disposition,
            ValidationMessage = "sensitive validation text",
        };
        await database.SeedAsync([BatchTestData.Account(account, "a")], session, [row]);
        var service = Service(database, long.MaxValue);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 0, 0, BatchAccountSelectionDecision.Selected)]);

        var error = await Assert.ThrowsAsync<BatchDraftValidationException>(() => service.PreviewAsync(draft));

        Assert.DoesNotContain(sensitiveUrl, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Jobs.GetAsync(draft.BatchJobId));
    }

    [Fact]
    public async Task ClassifiedInvalidRowsAreExcludedWithoutBlockingValidResources()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var invalid = BatchTestData.ReadyRow(sessionId, 1) with
        {
            Disposition = "Invalid",
            NormalizedShareId = null,
            ValidationMessage = "malformed input",
        };
        var valid = BatchTestData.ReadyRow(sessionId, 2);
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 2, 1, invalidRows: 1),
            [invalid, valid]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected)]);
        var service = Service(database, long.MaxValue);

        var preview = await service.PreviewAsync(draft);
        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        Assert.Equal(1, preview.ResourceCount);
        Assert.Equal(1, preview.OperationCount);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var resource = Assert.Single(await AllResourcesAsync(read.Jobs, draft.BatchJobId));
        Assert.Equal(valid.Id, resource.StagedImportRowId);
    }

    [Fact]
    public async Task ExcludedDuplicateAndFailedPrecheckRowsDoNotBecomeResources()
    {
        await using var database = await BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var ready = BatchTestData.ReadyRow(sessionId, 1);
        var failedPrecheck = BatchTestData.ReadyRow(sessionId, 2) with { PrecheckStatus = "Failed", PrecheckErrorCode = "INVALID", PrecheckedAt = BatchTestData.Now };
        var duplicate = BatchTestData.ReadyRow(sessionId, 3) with { Disposition = "Duplicate", DuplicateOfStagedRowId = ready.Id };
        var excluded = BatchTestData.ReadyRow(sessionId, 4) with { Disposition = "Excluded" };
        await database.SeedAsync(
            [BatchTestData.Account(account, "a")],
            BatchTestData.ReadySession(sessionId, 4, 2, duplicateRows: 1, excludedRows: 1),
            [ready, failedPrecheck, duplicate, excluded]);
        var draft = Draft(sessionId, TransferMode.Distribution, QuotaBasis.TaskCount,
            [new BatchAccountDraft(account, 0, "/target", 1, 0, BatchAccountSelectionDecision.Selected)]);
        var service = Service(database, long.MaxValue);

        var preview = await service.PreviewAsync(draft);
        await service.ConfirmAsync(draft, preview.ConfirmationToken);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Single(await AllResourcesAsync(read.Jobs, draft.BatchJobId));
    }

    private static CreateBatchService Service(BatchTestDatabase database, long availableBytes) =>
        new(database.UnitOfWorkFactory, new BatchDraftValidator(),
            new ReplicationPreflightEstimator(
                new ReplicationPreflightEstimatorTests.StubDiskSpaceProbe(availableBytes),
                database.DatabasePath));

    private static BatchDraft Draft(
        Guid importSessionId,
        TransferMode mode,
        QuotaBasis quota,
        IReadOnlyList<BatchAccountDraft> accounts) =>
        new(Guid.NewGuid(), importSessionId, "Batch", mode, quota, ShortagePolicy.ProportionalReduction,
            accounts, "/", "None", DuplicatePolicy.Skip, "Immediate", 1, true);

    private static BatchWorkloadAccountInput WorkloadAccount(
        int priority,
        long distributionCount,
        bool shareEnabled = true) =>
        new(Guid.NewGuid(), priority, $"账号{priority + 1}", BatchAccountSelectionDecision.Selected, shareEnabled, distributionCount);

    private static BatchDraft TokenDraft(
        Guid batchJobId,
        Guid sessionId,
        IReadOnlyList<Guid> accounts,
        bool selectSecond = true,
        bool reverseOrder = false,
        bool allocateToSecond = false,
        bool firstShareEnabled = true,
        ShareExtractionCodeBehavior extractionCodeBehavior = ShareExtractionCodeBehavior.Random,
        string? fixedExtractionCode = null,
        string existingSharePolicy = "Ask") =>
        new(
            batchJobId,
            sessionId,
            "Batch",
            TransferMode.Distribution,
            QuotaBasis.TaskCount,
            ShortagePolicy.ProportionalReduction,
            [
                new BatchAccountDraft(
                    accounts[0], reverseOrder ? 1 : 0, "/a", allocateToSecond ? 0 : 1, 0,
                    BatchAccountSelectionDecision.Selected, shareEnabled: firstShareEnabled),
                new BatchAccountDraft(
                    accounts[1], reverseOrder ? 0 : 1, "/b", allocateToSecond ? 1 : 0, 0,
                    selectSecond ? BatchAccountSelectionDecision.Selected : BatchAccountSelectionDecision.NotSelected,
                    shareEnabled: true),
            ],
            "/",
            "None",
            DuplicatePolicy.Skip,
            "Immediate",
            1,
            true,
            extractionCodeBehavior: extractionCodeBehavior,
            fixedExtractionCode: fixedExtractionCode,
            existingSharePolicy: existingSharePolicy);

    internal static async Task<IReadOnlyList<ResourceItemRecord>> AllResourcesAsync(IJobRepository jobs, Guid batchJobId)
    {
        var result = new List<ResourceItemRecord>();
        for (var page = 1; ; page++)
        {
            var current = await jobs.GetResourcesPageAsync(batchJobId, new PageQuery(page, 1_000));
            result.AddRange(current.Items);
            if (result.Count >= current.TotalCount) return result;
        }
    }

    internal static async Task<IReadOnlyList<TransferOperationRecord>> AllOperationsAsync(IJobRepository jobs, Guid batchJobId)
    {
        var result = new List<TransferOperationRecord>();
        for (var page = 1; ; page++)
        {
            var current = await jobs.GetOperationsPageAsync(batchJobId, new PageQuery(page, 1_000));
            result.AddRange(current.Items);
            if (result.Count >= current.TotalCount) return result;
        }
    }

    private static void AssertConfirmationEqual(BatchConfirmationSummary expected, BatchConfirmationSummary actual)
    {
        Assert.Equal(expected.ImportSessionId, actual.ImportSessionId);
        Assert.Equal(expected.ImportUpdatedAt, actual.ImportUpdatedAt);
        Assert.Equal(expected.ResourceCount, actual.ResourceCount);
        Assert.Equal(expected.OperationCount, actual.OperationCount);
        Assert.Equal(expected.TransferCount, actual.TransferCount);
        Assert.Equal(expected.ShareCount, actual.ShareCount);
        Assert.Equal(expected.TotalActionCount, actual.TotalActionCount);
        Assert.Equal(expected.ConfirmationToken, actual.ConfirmationToken);
        Assert.Equal(expected.StorageEstimate, actual.StorageEstimate);
        Assert.Equal(expected.ResultColumnCount, actual.ResultColumnCount);
        Assert.NotNull(expected.ResultColumnLayout);
        Assert.NotNull(actual.ResultColumnLayout);
        Assert.Equal(expected.ResultColumnLayout!.Fingerprint, actual.ResultColumnLayout!.Fingerprint);
        Assert.Equal(expected.ResultColumnLayout.SourceHeaders.ToArray(), actual.ResultColumnLayout.SourceHeaders.ToArray());
        Assert.Equal(expected.ResultColumnLayout.ResolvedTitleHeader, actual.ResultColumnLayout.ResolvedTitleHeader);
        Assert.Equal(expected.ResultColumnLayout.OriginalShareUrlHeader, actual.ResultColumnLayout.OriginalShareUrlHeader);
        Assert.Equal(expected.ResultColumnLayout.Accounts.ToArray(), actual.ResultColumnLayout.Accounts.ToArray());
        Assert.Equal(expected.Accounts.Length, actual.Accounts.Length);
        for (var index = 0; index < expected.Accounts.Length; index++)
        {
            var left = expected.Accounts[index];
            var right = actual.Accounts[index];
            Assert.Equal(left.AccountId, right.AccountId);
            Assert.Equal(left.PriorityOrder, right.PriorityOrder);
            Assert.Equal(left.DefaultTargetPath, right.DefaultTargetPath);
            Assert.Equal(left.NormalizedDefaultTargetPath, right.NormalizedDefaultTargetPath);
            Assert.Equal(left.TargetItemCount, right.TargetItemCount);
            Assert.Equal(left.TargetSuccessCount, right.TargetSuccessCount);
            Assert.Equal(left.EffectiveTargetSuccessCount, right.EffectiveTargetSuccessCount);
            Assert.Equal(left.ReachableTargetSuccessCount, right.ReachableTargetSuccessCount);
            Assert.Equal(left.FulfilledCount, right.FulfilledCount);
            Assert.Equal(left.ReservedSlotCount, right.ReservedSlotCount);
            Assert.Equal(left.LockedItemCount, right.LockedItemCount);
            Assert.Equal(left.ExclusiveCandidateCount, right.ExclusiveCandidateCount);
            Assert.Equal(left.UnusedCandidateCount, right.UnusedCandidateCount);
            Assert.Equal(left.NewExclusiveCandidateCount, right.NewExclusiveCandidateCount);
            Assert.Equal(left.PlannedTransferCount, right.PlannedTransferCount);
            Assert.Equal(left.PlannedShareCount, right.PlannedShareCount);
            Assert.Equal(left.ShareEnabled, right.ShareEnabled);
            Assert.Equal(left.OutputOrdinal, right.OutputOrdinal);
            Assert.Equal(left.OutputLabel, right.OutputLabel);
        }
    }

    private sealed class InMemoryValidationUnitOfWork(
        ImportSessionRecord session,
        IReadOnlyList<StagedImportRowRecord> rows,
        IReadOnlyList<AccountRecord> accounts) : IUnitOfWork
    {
        public InMemoryImportRepository ImportsStore { get; } = new(session, rows);
        public InMemoryAccountRepository AccountsStore { get; } = new(accounts);
        public IImportRepository Imports => ImportsStore;
        public IAccountRepository Accounts => AccountsStore;
        public IJobRepository Jobs => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InMemoryImportRepository(
        ImportSessionRecord session,
        IReadOnlyList<StagedImportRowRecord> rows) : IImportRepository
    {
        private readonly StagedImportRowRecord[] readyRows = rows.ToArray();

        public long ReadyRowReads { get; private set; }

        public Task<ImportSessionRecord?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<ImportSessionRecord?>(id == session.Id ? session : null);

        public Task<IReadOnlyList<StagedImportRowRecord>> GetRowsAfterSourceRowAsync(
            Guid sessionId,
            string disposition,
            int afterSourceRowNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            if (sessionId != session.Id || disposition != "Ready" || afterSourceRowNumber >= readyRows.Length)
                return Task.FromResult<IReadOnlyList<StagedImportRowRecord>>([]);
            var start = Math.Max(0, afterSourceRowNumber);
            var count = Math.Min(pageSize, readyRows.Length - start);
            var page = readyRows.AsSpan(start, count).ToArray();
            ReadyRowReads = checked(ReadyRowReads + page.Length);
            return Task.FromResult<IReadOnlyList<StagedImportRowRecord>>(page);
        }

        public Task<IReadOnlyList<ImportColumnDefinitionRecord>> GetColumnDefinitionsAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ImportColumnDefinitionRecord>>([]);

        public Task<IReadOnlyList<StagedImportRowPayloadRecord>> GetRowPayloadsAsync(
            IEnumerable<Guid> stagedImportRowIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StagedImportRowPayloadRecord>>(stagedImportRowIds.Select(id =>
                new StagedImportRowPayloadRecord(id, "source", "resolved", "[REDACTED]", "[]")).ToArray());

        public Task AddSessionAsync(ImportSessionRecord value, CancellationToken cancellationToken = default) => Unsupported();
        public Task UpdateSessionAsync(ImportSessionRecord value, CancellationToken cancellationToken = default) => Unsupported();
        public Task AddRowsAsync(IEnumerable<StagedImportRowRecord> values, CancellationToken cancellationToken = default) => Unsupported();
        public Task UpdateRowsAsync(IEnumerable<StagedImportRowRecord> values, CancellationToken cancellationToken = default) => Unsupported();
        public Task<StagedImportRowRecord?> GetRowByIdAsync(Guid rowId, CancellationToken cancellationToken = default) => Unsupported<StagedImportRowRecord?>();
        public Task<StagedImportRowRecord?> GetFirstByNormalizedShareIdAsync(Guid sessionId, string normalizedShareId, CancellationToken cancellationToken = default) => Unsupported<StagedImportRowRecord?>();
        public Task<int> GetLastSourceRowNumberAsync(Guid sessionId, CancellationToken cancellationToken = default) => Unsupported<int>();
        public Task<IReadOnlyList<StagedImportRowRecord>> GetRowsByNormalizedShareIdAfterSourceRowAsync(Guid sessionId, string normalizedShareId, int afterSourceRowNumber, int pageSize, CancellationToken cancellationToken = default) => Unsupported<IReadOnlyList<StagedImportRowRecord>>();
        public Task<Page<StagedImportRowRecord>> GetRowsPageAsync(StagedImportRowQuery query, CancellationToken cancellationToken = default) => Unsupported<Page<StagedImportRowRecord>>();
        public Task<KeysetPage<StagedImportRowRecord>> GetRowsKeysetPageAsync(StagedImportKeysetQuery query, CancellationToken cancellationToken = default) => Unsupported<KeysetPage<StagedImportRowRecord>>();

        private static Task Unsupported() => throw new NotSupportedException();
        private static Task<T> Unsupported<T>() => throw new NotSupportedException();
    }

    private sealed class InMemoryAccountRepository(IReadOnlyList<AccountRecord> accounts) : IAccountRepository
    {
        private readonly IReadOnlyDictionary<Guid, AccountRecord> records = accounts.ToDictionary(account => account.Id);

        public long AccountReads { get; private set; }
        public long RulePageReads { get; private set; }

        public Task<AccountRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            AccountReads++;
            return Task.FromResult(records.TryGetValue(id, out var account) ? account : null);
        }

        public Task<Page<AccountRuleRecord>> GetRulesPageAsync(
            Guid accountId,
            PageQuery query,
            CancellationToken cancellationToken = default)
        {
            RulePageReads++;
            return Task.FromResult(new Page<AccountRuleRecord>([], 0, query.PageNumber, query.PageSize));
        }

        public Task UpsertAsync(AccountRecord account, CancellationToken cancellationToken = default) => Unsupported();
        public Task<AccountRecord?> GetByBaiduUserIdAsync(string baiduUserId, CancellationToken cancellationToken = default) => Unsupported<AccountRecord?>();
        public Task SetEncryptedSessionAsync(Guid id, byte[]? encryptedSession, CancellationToken cancellationToken = default) => Unsupported();
        public Task<Page<AccountRecord>> GetPageAsync(PageQuery query, CancellationToken cancellationToken = default) => Unsupported<Page<AccountRecord>>();
        public Task UpsertRuleAsync(AccountRuleRecord rule, CancellationToken cancellationToken = default) => Unsupported();

        private static Task Unsupported() => throw new NotSupportedException();
        private static Task<T> Unsupported<T>() => throw new NotSupportedException();
    }

    private sealed class RejectingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        public int BeginCount { get; private set; }

        public Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken = default)
        {
            BeginCount++;
            throw new InvalidOperationException("选择快照校验失败时不得打开工作单元。");
        }
    }
}
