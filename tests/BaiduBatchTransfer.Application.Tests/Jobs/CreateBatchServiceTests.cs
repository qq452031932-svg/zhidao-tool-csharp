using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using Xunit.Abstractions;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class CreateBatchServiceTests(ITestOutputHelper output)
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
    public async Task PreviewCountsMixedMappingsLinearlyForTwoHundredThousandRowsAndOneThousandAccounts()
    {
        const int rowCount = 200_000;
        const int accountCount = 1_000;
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
        var observer = new BatchDraftValidationComplexityObserver();
        var service = new CreateBatchService(
            database.UnitOfWorkFactory,
            new BatchDraftValidator(observer),
            new ReplicationPreflightEstimator(
                new ReplicationPreflightEstimatorTests.StubDiskSpaceProbe(long.MaxValue),
                database.DatabasePath));
        var stopwatch = Stopwatch.StartNew();

        var preview = await service.PreviewAsync(draft);

        stopwatch.Stop();
        output.WriteLine($"200k/1000 preview diagnostic: {stopwatch.Elapsed}.");
        Assert.Equal(rowCount, preview.TransferCount);
        Assert.Equal(rowCount / 2, preview.ShareCount);
        Assert.Equal(rowCount, observer.ConstraintRowVisits);
        Assert.InRange(observer.ConstraintAccountLookups, accountCount, accountCount * 2L);
        Assert.Equal(1, observer.RuleResolverPreparationCount);
        Assert.Equal(accountCount, observer.RuleResolverAccountPreparations);
        Assert.Equal(rowCount, observer.RuleResolutionCalls);
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
