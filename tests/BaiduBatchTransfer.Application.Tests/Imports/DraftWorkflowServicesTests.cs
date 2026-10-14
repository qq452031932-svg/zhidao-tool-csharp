using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace BaiduBatchTransfer.Application.Tests.Imports;

public sealed class DraftWorkflowServicesTests
{
    [Fact]
    public async Task ModeSwitchReclassifiesAtomicallyAndPreservesExcludedAndInvalidRows()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        var first = Row(sessionId, 1, "same", requested: "甲", mapped: account);
        var same = Row(sessionId, 2, "same", requested: "甲", mapped: account);
        var excluded = Row(sessionId, 3, "same") with { Disposition = "Excluded", ValidationMessage = "用户排除" };
        var invalid = Row(sessionId, 4, null) with { Disposition = "Invalid", ValidationMessage = "DRAFT_INVALID_LINK" };
        await database.SeedAsync(
            [Jobs.BatchTestData.Account(account, "a")],
            Session(sessionId, 4, ready: 1, invalid: 1, duplicate: 1, excluded: 1),
            [first, same with { Disposition = "Duplicate", DuplicateOfStagedRowId = first.Id }, excluded, invalid]);

        var service = new TransferModeReclassificationService(database.UnitOfWorkFactory);
        var result = await service.SwitchAsync(sessionId, TransferMode.Replication);

        Assert.Equal(0, result.ReadyRows);
        Assert.Equal(2, result.ConflictRows);
        Assert.Equal(1, result.InvalidRows);
        Assert.Equal(1, result.ExcludedRows);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var rows = await AllRowsAsync(read.Imports, sessionId);
        Assert.Equal("Excluded", rows[2].Disposition);
        Assert.Equal("用户排除", rows[2].ValidationMessage);
        Assert.Equal("Invalid", rows[3].Disposition);
        Assert.All(rows.Take(2), row => Assert.Equal("DRAFT_REPLICATION_ACCOUNT_COLUMN", row.ValidationMessage));
    }

    [Fact]
    public async Task CancelledModeSwitchRollsBackAndCanBeRetried()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var first = Row(sessionId, 1, "same");
        var second = Row(sessionId, 2, "same") with { Disposition = "Conflict", ValidationMessage = "old" };
        await database.SeedAsync([], Session(sessionId, 2, ready: 1, conflict: 1), [first, second]);
        var service = new TransferModeReclassificationService(database.UnitOfWorkFactory);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SwitchAsync(sessionId, TransferMode.Distribution, cancelled.Token));
        var result = await service.SwitchAsync(sessionId, TransferMode.Distribution);

        Assert.Equal(1, result.ReadyRows);
        Assert.Equal(1, result.DuplicateRows);
    }

    [Fact]
    public async Task MutationsAreRejectedWhilePrecheckOwnsTheDraft()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        await database.SeedAsync([], Session(sessionId, 1, ready: 1) with { Status = "Prechecking" }, [Row(sessionId, 1, "one")]);

        var error = await Assert.ThrowsAsync<DraftWorkflowException>(() =>
            new TransferModeReclassificationService(database.UnitOfWorkFactory)
                .SwitchAsync(sessionId, TransferMode.Replication));

        Assert.Equal("DRAFT_NOT_MUTABLE", error.Code);
    }

    [Fact]
    public async Task ModeSwitchNeverPromotesAnInvalidRowThatAlreadyHasANormalizedIdentity()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var invalid = Row(sessionId, 1, "same") with { Disposition = "Invalid", ValidationMessage = "DRAFT_INVALID_TARGET_PATH" };
        var ready = Row(sessionId, 2, "same");
        await database.SeedAsync([], Session(sessionId, 2, ready: 1, invalid: 1), [invalid, ready]);

        var result = await new TransferModeReclassificationService(database.UnitOfWorkFactory)
            .SwitchAsync(sessionId, TransferMode.Distribution);

        Assert.Equal(1, result.InvalidRows);
        Assert.Equal(1, result.ReadyRows);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Equal("Invalid", (await read.Imports.GetRowByIdAsync(invalid.Id))!.Disposition);
    }

    [Fact]
    public async Task RequestedAccountNamesAreAggregatedInDatabaseAndRequireExplicitResolution()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var unique = Guid.NewGuid();
        var ambiguous1 = Guid.NewGuid();
        var ambiguous2 = Guid.NewGuid();
        await database.SeedAsync(
            [
                Jobs.BatchTestData.Account(unique, "u") with { DisplayName = "唯一" },
                Jobs.BatchTestData.Account(ambiguous1, "a1") with { DisplayName = "重名" },
                Jobs.BatchTestData.Account(ambiguous2, "a2") with { DisplayName = "重名" },
            ],
            Session(sessionId, 4, ready: 4),
            [Row(sessionId, 1, "1", requested: "唯一"), Row(sessionId, 2, "2", requested: "唯一"),
             Row(sessionId, 3, "3", requested: "重名"), Row(sessionId, 4, "4", requested: "不存在")]);
        var service = new RequestedAccountMappingService(database.UnitOfWorkFactory);

        var page = await service.GetGroupsPageAsync(sessionId, 1, 100);

        Assert.Collection(page.Items.OrderBy(x => x.RequestedAccountName, StringComparer.Ordinal),
            item => { Assert.Equal("不存在", item.RequestedAccountName); Assert.Equal(AccountNameMatchStatus.Unmatched, item.Status); },
            item => { Assert.Equal("唯一", item.RequestedAccountName); Assert.Equal(2, item.RowCount); Assert.Equal(unique, Assert.Single(item.MatchingAccountIds)); },
            item => { Assert.Equal("重名", item.RequestedAccountName); Assert.Equal(AccountNameMatchStatus.Ambiguous, item.Status); Assert.Equal(2, item.MatchingAccountIds.Count); });

        await service.ApplyAsync(sessionId, [
            AccountMappingDecision.PreserveUnique("唯一"),
            AccountMappingDecision.Map("重名", ambiguous2),
            AccountMappingDecision.Ignore("不存在"),
        ], TransferMode.Distribution);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var rows = await AllRowsAsync(read.Imports, sessionId);
        Assert.Equal([unique, unique, ambiguous2, null], rows.Select(x => x.MappedAccountId).ToArray());
        Assert.Null(rows[3].RequestedAccountName);
    }

    [Fact]
    public async Task RequestedAccountColumnCanBeOverriddenOrExplicitlyIgnoredForTheWholeDraft()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        await database.SeedAsync([Jobs.BatchTestData.Account(account, "a")], Session(sessionId, 2, ready: 2),
            [Row(sessionId, 1, "1", requested: "旧账号"), Row(sessionId, 2, "2")]);
        var service = new RequestedAccountMappingService(database.UnitOfWorkFactory);

        await service.OverrideAllAsync(sessionId, account, TransferMode.Distribution);
        await using (var read = await database.UnitOfWorkFactory.BeginAsync())
        {
            var rows = await AllRowsAsync(read.Imports, sessionId);
            Assert.All(rows, row => Assert.Equal(account, row.MappedAccountId));
            Assert.All(rows, row => Assert.Null(row.RequestedAccountName));
        }

        await service.IgnoreAllAsync(sessionId, TransferMode.Distribution);
        await using var verify = await database.UnitOfWorkFactory.BeginAsync();
        Assert.All(await AllRowsAsync(verify.Imports, sessionId), row => Assert.Null(row.MappedAccountId));
    }

    [Fact]
    public async Task ChangingRequestedAccountNameCannotRetainAStaleMappedAccount()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var oldAccount = Guid.NewGuid();
        var row = Row(sessionId, 1, "one", requested: "甲", mapped: oldAccount);
        await database.SeedAsync([Jobs.BatchTestData.Account(oldAccount, "a")], Session(sessionId, 1, ready: 1), [row]);
        var service = new DraftRowEditingService(database.UnitOfWorkFactory);

        await service.EditAsync(new DraftRowEditRequest(sessionId, row.Id, requestedAccountName: "乙"), TransferMode.Distribution);
        await using (var read = await database.UnitOfWorkFactory.BeginAsync())
        {
            Assert.Null((await read.Imports.GetRowByIdAsync(row.Id))!.MappedAccountId);
        }

        await service.ApplyBulkOverridesAsync(sessionId, new DraftBulkOverride(RequestedAccountName: "丙"), TransferMode.Distribution);
        await using var verify = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null((await verify.Imports.GetRowByIdAsync(row.Id))!.MappedAccountId);
    }

    [Fact]
    public async Task DraftEditsInvalidatePrecheckMaintainCountersAndResolveConflictPrimary()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var first = Row(sessionId, 1, "same") with { ExtractionCode = "aaaa", PrecheckStatus = "Succeeded", PrecheckedAt = Jobs.BatchTestData.Now };
        var conflict = Row(sessionId, 2, "same") with { ExtractionCode = "bbbb", Disposition = "Conflict", ValidationMessage = "old" };
        await database.SeedAsync([], Session(sessionId, 2, ready: 1, conflict: 1), [first, conflict]);
        var service = new DraftRowEditingService(database.UnitOfWorkFactory);

        await service.EditAsync(new DraftRowEditRequest(sessionId, first.Id, extractionCode: "cccc", targetPath: "/新目录", createFolderName: "外层", note: "备注"), TransferMode.Distribution);
        await service.ExcludeAsync(sessionId, first.Id, TransferMode.Distribution);
        await service.RestoreAsync(sessionId, first.Id, TransferMode.Distribution);
        var result = await service.ResolveConflictAsync(sessionId, "same", conflict.Id, TransferMode.Distribution);

        Assert.Equal(1, result.ReadyRows);
        Assert.Equal(1, result.DuplicateRows);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var rows = await AllRowsAsync(read.Imports, sessionId);
        var edited = rows.Single(x => x.Id == first.Id);
        Assert.Null(edited.PrecheckStatus);
        Assert.Equal("备注", edited.Note);
        Assert.Equal("Duplicate", edited.Disposition);
        Assert.Equal(conflict.Id, edited.DuplicateOfStagedRowId);
    }

    [Fact]
    public async Task LinkAndCodeEditsRestoreUserTitleButClearUntitledFallback()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var titled = Row(sessionId, 1, "old") with
        {
            PrecheckStatus = "Succeeded",
            PrecheckedAt = Jobs.BatchTestData.Now,
        };
        var untitled = Row(sessionId, 2, "untitled") with
        {
            PrecheckStatus = "Succeeded",
            PrecheckedAt = Jobs.BatchTestData.Now,
        };
        await database.SeedAsync([], Session(sessionId, 2, ready: 2), [titled, untitled], seedDefaultPayloads: false);
        await using (var seed = await database.UnitOfWorkFactory.BeginAsync())
        {
            await seed.Imports.AddRowPayloadsAsync([
                new(titled.Id, "  原始标题  ", "解析后标题", "https://pan.baidu.com/s/old", "[\"  原始标题  \",\"https://pan.baidu.com/s/old\",\"\"]"),
                new(untitled.Id, null, "无标题回退", "https://pan.baidu.com/s/untitled", "[\"\",\"https://pan.baidu.com/s/untitled\"]"),
            ]);
            await seed.CommitAsync();
        }

        var service = new DraftRowEditingService(database.UnitOfWorkFactory);
        await service.EditAsync(
            new DraftRowEditRequest(
                sessionId,
                titled.Id,
                shareUrl: "https://pan.baidu.com/s/new",
                extractionCode: "n3w4"),
            TransferMode.Distribution);
        await service.EditAsync(
            new DraftRowEditRequest(
                sessionId,
                untitled.Id,
                extractionCode: "z9y8"),
            TransferMode.Distribution);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var edited = await read.Imports.GetRowByIdAsync(titled.Id);
        var titledPayload = await read.Imports.GetRowPayloadAsync(titled.Id);
        var untitledPayload = await read.Imports.GetRowPayloadAsync(untitled.Id);
        Assert.Null(edited!.PrecheckStatus);
        Assert.Equal("https://pan.baidu.com/s/new", edited.ShareUrl);
        Assert.Equal("  原始标题  ", titledPayload!.SourceTitle);
        Assert.Equal("  原始标题  ", titledPayload.ResolvedOutputTitle);
        Assert.Equal("https://pan.baidu.com/s/old", titledPayload.OriginalShareUrl);
        Assert.Equal("[\"  原始标题  \",\"https://pan.baidu.com/s/old\",\"\"]", titledPayload.RawValuesJson);
        Assert.Null(untitledPayload!.SourceTitle);
        Assert.Null(untitledPayload.ResolvedOutputTitle);
        Assert.Equal("https://pan.baidu.com/s/untitled", untitledPayload.OriginalShareUrl);
        Assert.Equal("[\"\",\"https://pan.baidu.com/s/untitled\"]", untitledPayload.RawValuesJson);
    }

    [Fact]
    public async Task BulkUnrelatedEditsPreserveTitledAndUntitledResolvedOutputTitles()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var titled = Row(sessionId, 1, "titled");
        var untitled = Row(sessionId, 2, "untitled");
        await database.SeedAsync([], Session(sessionId, 2, ready: 2), [titled, untitled], seedDefaultPayloads: false);
        await using (var seed = await database.UnitOfWorkFactory.BeginAsync())
        {
            await seed.Imports.AddRowPayloadsAsync([
                new(titled.Id, "源标题", "源标题", "https://pan.baidu.com/s/titled", "[\"源标题\"]"),
                new(untitled.Id, null, "已解析回退", "https://pan.baidu.com/s/untitled", "[\"\"]"),
            ]);
            await seed.CommitAsync();
        }

        await new DraftRowEditingService(database.UnitOfWorkFactory).ApplyBulkOverridesAsync(
            sessionId,
            new DraftBulkOverride(Note: "批量备注"),
            TransferMode.Distribution);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Equal("源标题", (await read.Imports.GetRowPayloadAsync(titled.Id))!.ResolvedOutputTitle);
        Assert.Equal("已解析回退", (await read.Imports.GetRowPayloadAsync(untitled.Id))!.ResolvedOutputTitle);
    }

    [Fact]
    public async Task ConflictResolutionPreservesInvalidAndExcludedRowsInTheSameIdentityGroup()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var selected = Row(sessionId, 1, "same") with { Disposition = "Conflict", ValidationMessage = "DRAFT_SOURCE_SETTINGS_CONFLICT" };
        var invalid = Row(sessionId, 2, "same") with { Disposition = "Invalid", ValidationMessage = "DRAFT_INVALID_TARGET_PATH" };
        var excluded = Row(sessionId, 3, "same") with { Disposition = "Excluded", ValidationMessage = "DRAFT_USER_EXCLUDED" };
        await database.SeedAsync([], Session(sessionId, 3, ready: 0, invalid: 2, excluded: 1), [selected, invalid, excluded]);

        await new DraftRowEditingService(database.UnitOfWorkFactory)
            .ResolveConflictAsync(sessionId, "same", selected.Id, TransferMode.Distribution);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Equal("Invalid", (await read.Imports.GetRowByIdAsync(invalid.Id))!.Disposition);
        Assert.Equal("Excluded", (await read.Imports.GetRowByIdAsync(excluded.Id))!.Disposition);
    }

    [Fact]
    public async Task ReplicationConflictResolutionCannotSilentlyFoldAPerRowAccount()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var selected = Row(sessionId, 1, "same") with { Disposition = "Conflict" };
        var accountRow = Row(sessionId, 2, "same", requested: "甲") with { Disposition = "Conflict" };
        await database.SeedAsync([], Session(sessionId, 2, ready: 0, invalid: 2), [selected, accountRow]);

        var error = await Assert.ThrowsAsync<DraftWorkflowException>(() =>
            new DraftRowEditingService(database.UnitOfWorkFactory)
                .ResolveConflictAsync(sessionId, "same", selected.Id, TransferMode.Replication));

        Assert.Equal("DRAFT_REPLICATION_ACCOUNT_COLUMN", error.Code);
    }

    [Fact]
    public async Task BulkFailedPrecheckExclusionAndOverridesStayPagedAndUpdateDraftStatus()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var rows = Enumerable.Range(1, 2_501).Select(i => Row(sessionId, i, $"s{i}") with
        {
            PrecheckStatus = i % 2 == 0 ? "Failed" : "Succeeded",
            PrecheckErrorCode = i % 2 == 0 ? "INVALID" : null,
            PrecheckedAt = Jobs.BatchTestData.Now,
        }).ToArray();
        await database.SeedAsync([], Session(sessionId, rows.Length, ready: rows.Length) with { Status = "ReadyToConfirm" }, rows);
        var service = new DraftRowEditingService(database.UnitOfWorkFactory);

        await service.ApplyBulkOverridesAsync(sessionId,
            new DraftBulkOverride(TargetPath: "/批量", CreateFolderName: "目录", Note: "统一"), TransferMode.Distribution);
        var result = await service.ExcludeFailedPrechecksAsync(sessionId, TransferMode.Distribution);

        Assert.Equal(1_251, result.ReadyRows);
        Assert.Equal(1_250, result.ExcludedRows);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var session = await read.Imports.GetSessionAsync(sessionId);
        Assert.Equal("Draft", session!.Status);
    }

    [Fact]
    public async Task RestoringAPrecheckFailedExclusionClearsTheFailureForImmediateValidation()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var row = Row(sessionId, 1, "one") with
        {
            PrecheckStatus = "Failed",
            PrecheckErrorCode = "INVALID",
            PrecheckedAt = Jobs.BatchTestData.Now,
        };
        await database.SeedAsync([], Session(sessionId, 1, ready: 1), [row]);
        var service = new DraftRowEditingService(database.UnitOfWorkFactory);
        await service.ExcludeFailedPrechecksAsync(sessionId, TransferMode.Distribution);

        await service.RestoreAsync(sessionId, row.Id, TransferMode.Distribution);

        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        var restored = await read.Imports.GetRowByIdAsync(row.Id);
        Assert.Equal("Ready", restored!.Disposition);
        Assert.Null(restored.PrecheckStatus);
        Assert.Null(restored.PrecheckErrorCode);
        Assert.Null(restored.PrecheckedAt);
    }

    [Fact]
    public async Task PreviewResolvesExplicitThenDirectoryThenPublicAndBindsResolutionIntoToken()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var explicitAccount = Guid.NewGuid();
        var directoryAccount = Guid.NewGuid();
        var publicAccount = Guid.NewGuid();
        var accounts = new[]
        {
            Jobs.BatchTestData.Account(explicitAccount, "explicit"),
            Jobs.BatchTestData.Account(directoryAccount, "directory"),
            Jobs.BatchTestData.Account(publicAccount, "public"),
        };
        var rows = new[]
        {
            Row(sessionId, 1, "1", mapped: explicitAccount) with { TargetPath = "/team/a" },
            Row(sessionId, 2, "2") with { TargetPath = "/team/b" },
            Row(sessionId, 3, "3") with { TargetPath = "/other" },
        };
        await database.SeedAsync(accounts, Session(sessionId, 3, ready: 3), rows);
        var teamRuleId = Guid.NewGuid();
        await using (var seed = await database.UnitOfWorkFactory.BeginAsync())
        {
            await seed.Accounts.UpsertRuleAsync(new AccountRuleRecord(teamRuleId, directoryAccount, "DirectoryPrefix", "/team", 0, true));
            await seed.CommitAsync();
        }
        var service = CreateService(database);
        var draft = Draft(sessionId, [
            new BatchAccountDraft(explicitAccount, 0, "/e", 1, 0, BatchAccountSelectionDecision.Selected),
            new BatchAccountDraft(directoryAccount, 1, "/d", 1, 0, BatchAccountSelectionDecision.Selected),
            new BatchAccountDraft(publicAccount, 2, "/p", 1, 0, BatchAccountSelectionDecision.Selected),
        ]);

        var preview = await service.PreviewAsync(draft);

        Assert.Equal(1, preview.Accounts.Single(x => x.AccountId == explicitAccount).LockedItemCount);
        Assert.Equal(1, preview.Accounts.Single(x => x.AccountId == directoryAccount).LockedItemCount);
        Assert.Equal(0, preview.Accounts.Single(x => x.AccountId == publicAccount).LockedItemCount);
        await using var mutate = await database.UnitOfWorkFactory.BeginAsync();
        await mutate.Accounts.UpsertRuleAsync(new AccountRuleRecord(teamRuleId, directoryAccount, "DirectoryPrefix", "/team/", 99, true));
        await mutate.CommitAsync();
        var changed = await service.PreviewAsync(draft);
        Assert.Equal(preview.Accounts.Select(x => x.LockedItemCount), changed.Accounts.Select(x => x.LockedItemCount));
        Assert.NotEqual(preview.ConfirmationToken, changed.ConfirmationToken);
        await Assert.ThrowsAsync<BatchConfirmationException>(() =>
            service.ConfirmAsync(draft, preview.ConfirmationToken));
    }

    [Fact]
    public async Task UnresolvedRequestedAccountCannotFallThroughToPublicAndErrorsStaySanitized()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var account = Guid.NewGuid();
        const string sensitiveName = "客户私密账号名";
        await database.SeedAsync([Jobs.BatchTestData.Account(account, "a")], Session(sessionId, 1, ready: 1),
            [Row(sessionId, 1, "one", requested: sensitiveName)]);
        var draft = Draft(sessionId, [new BatchAccountDraft(account, 0, "/", 1, 0, BatchAccountSelectionDecision.Selected)]);

        var error = await Assert.ThrowsAsync<BatchDraftValidationException>(() => CreateService(database).PreviewAsync(draft));

        Assert.DoesNotContain(sensitiveName, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task TwoHundredThousandRowsUseDatabaseAggregationAndBoundedPaging()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        await database.SeedAsync([], Session(sessionId, 200_000, ready: 200_000));
        await using (var connection = await database.ConnectionFactory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE seq(n) AS (
                  VALUES(1) UNION ALL SELECT n+1 FROM seq WHERE n<200000
                )
                INSERT INTO StagedImportRows(
                  Id,ImportSessionId,SourceRowNumber,ShareUrl,NormalizedShareId,ExtractionCode,
                  RequestedAccountName,Disposition,UpdatedAt)
                SELECT printf('%08x-0000-0000-0000-%012x',n,n),$session,n,
                  'https://pan.baidu.com/s/'||n,'share-'||n,'a1b2',
                  CASE n%3 WHEN 0 THEN '甲' WHEN 1 THEN '乙' ELSE '丙' END,
                  'Ready','2026-08-19T01:02:03.0000000+00:00'
                FROM seq;
                """;
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        var watch = Stopwatch.StartNew();

        var groups = await new RequestedAccountMappingService(database.UnitOfWorkFactory)
            .GetGroupsPageAsync(sessionId, 1, 100);
        var result = await new TransferModeReclassificationService(database.UnitOfWorkFactory)
            .SwitchAsync(sessionId, TransferMode.Distribution);

        watch.Stop();
        Assert.Equal(3, groups.TotalCount);
        Assert.Equal(200_000, groups.Items.Sum(x => x.RowCount));
        Assert.Equal(200_000, result.ReadyRows);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Large draft operations took {watch.Elapsed}.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task TwoHundredThousandUniqueRowsPlusOneDuplicateDoNotDegenerateIntoPerIdentityQueries()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        await database.SeedAsync([], Session(sessionId, 200_001, ready: 200_001));
        await using (var connection = await database.ConnectionFactory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE seq(n) AS (
                  VALUES(1) UNION ALL SELECT n+1 FROM seq WHERE n<200000
                )
                INSERT INTO StagedImportRows(
                  Id,ImportSessionId,SourceRowNumber,ShareUrl,NormalizedShareId,ExtractionCode,Disposition,UpdatedAt)
                SELECT printf('%08x-0000-0000-0000-%012x',n,n),$session,n,
                  'https://pan.baidu.com/s/'||n,'share-'||n,'a1b2','Ready','2026-08-19T01:02:03.0000000+00:00'
                FROM seq;
                INSERT INTO StagedImportRows(
                  Id,ImportSessionId,SourceRowNumber,ShareUrl,NormalizedShareId,ExtractionCode,Disposition,UpdatedAt)
                VALUES('ffffffff-0000-0000-0000-000000000001',$session,200001,
                  'https://pan.baidu.com/s/duplicate','share-1','a1b2','Ready','2026-08-19T01:02:03.0000000+00:00');
                """;
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var watch = Stopwatch.StartNew();

        var result = await new TransferModeReclassificationService(database.UnitOfWorkFactory)
            .SwitchAsync(sessionId, TransferMode.Distribution, timeout.Token);

        watch.Stop();
        Assert.Equal(200_000, result.ReadyRows);
        Assert.Equal(1, result.DuplicateRows);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"Duplicate-aware reclassification took {watch.Elapsed}.");
    }

    [Fact]
    public async Task DeletingARowRemovesItForGoodAndPromotesItsDuplicate()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var primary = Row(sessionId, 1, "same");
        var duplicate = Row(sessionId, 2, "same") with { Disposition = "Duplicate", DuplicateOfStagedRowId = primary.Id };
        var other = Row(sessionId, 3, "other");
        await database.SeedAsync([], Session(sessionId, 3, ready: 2, duplicate: 1), [primary, duplicate, other]);
        var service = new DraftRowEditingService(database.UnitOfWorkFactory);

        var result = await service.DeleteAsync(sessionId, primary.Id, TransferMode.Distribution);

        Assert.Equal(2, result.TotalRows);
        Assert.Equal(2, result.ReadyRows);
        Assert.Equal(0, result.DuplicateRows);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Null(await read.Imports.GetRowByIdAsync(primary.Id));
        var remaining = await AllRowsAsync(read.Imports, sessionId);
        Assert.Equal([duplicate.Id, other.Id], remaining.Select(row => row.Id));
        Assert.All(remaining, row =>
        {
            Assert.Equal("Ready", row.Disposition);
            Assert.Null(row.DuplicateOfStagedRowId);
        });
        // 来源行号不重排：被删的那一行留下空号，其余行原样。
        Assert.Equal([2, 3], remaining.Select(row => row.SourceRowNumber));
        var session = await read.Imports.GetSessionAsync(sessionId);
        Assert.Equal(2, session!.TotalRows);
    }

    [Fact]
    public async Task DeletingADuplicateLeavesItsPrimaryUntouched()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var primary = Row(sessionId, 1, "same");
        var duplicate = Row(sessionId, 2, "same") with { Disposition = "Duplicate", DuplicateOfStagedRowId = primary.Id };
        await database.SeedAsync([], Session(sessionId, 2, ready: 1, duplicate: 1), [primary, duplicate]);
        var service = new DraftRowEditingService(database.UnitOfWorkFactory);

        var result = await service.DeleteAsync(sessionId, duplicate.Id, TransferMode.Distribution);

        Assert.Equal((1L, 1L, 0L), (result.TotalRows, result.ReadyRows, result.DuplicateRows));
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.Equal("Ready", (await read.Imports.GetRowByIdAsync(primary.Id))!.Disposition);
    }

    [Theory]
    [InlineData("BatchCreated")]
    [InlineData("Cancelled")]
    public async Task RowsOfADraftThatAlreadyBecameATaskCannotBeDeleted(string status)
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var row = Row(sessionId, 1, "one");
        await database.SeedAsync([], Session(sessionId, 1, ready: 1) with { Status = status }, [row]);
        var service = new DraftRowEditingService(database.UnitOfWorkFactory);

        var error = await Assert.ThrowsAsync<DraftWorkflowException>(() =>
            service.DeleteAsync(sessionId, row.Id, TransferMode.Distribution));

        Assert.Equal("DRAFT_NOT_MUTABLE", error.Code);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.NotNull(await read.Imports.GetRowByIdAsync(row.Id));
    }

    [Fact]
    public async Task DeleteRefusesARowFromAnotherImportAndAMissingRow()
    {
        await using var database = await Jobs.BatchTestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var row = Row(sessionId, 1, "one");
        await database.SeedAsync([], Session(sessionId, 1, ready: 1), [row]);
        var otherSession = Guid.NewGuid();
        await using (var seed = await database.UnitOfWorkFactory.BeginAsync())
        {
            await seed.Imports.AddSessionAsync(Session(otherSession, 0, ready: 0));
            await seed.CommitAsync();
        }
        var service = new DraftRowEditingService(database.UnitOfWorkFactory);

        var mismatch = await Assert.ThrowsAsync<DraftWorkflowException>(() =>
            service.DeleteAsync(otherSession, row.Id, TransferMode.Distribution));
        var missing = await Assert.ThrowsAsync<DraftWorkflowException>(() =>
            service.DeleteAsync(sessionId, Guid.NewGuid(), TransferMode.Distribution));

        Assert.Equal("DRAFT_ROW_SESSION_MISMATCH", mismatch.Code);
        Assert.Equal("DRAFT_ROW_NOT_FOUND", missing.Code);
        await using var read = await database.UnitOfWorkFactory.BeginAsync();
        Assert.NotNull(await read.Imports.GetRowByIdAsync(row.Id));
    }

    private static CreateBatchService CreateService(Jobs.BatchTestDatabase database) =>
        new(database.UnitOfWorkFactory, new BatchDraftValidator(), new ReplicationPreflightEstimator(new UnlimitedDisk(), database.DatabasePath));

    private static BatchDraft Draft(Guid sessionId, IReadOnlyList<BatchAccountDraft> accounts) =>
        new(Guid.NewGuid(), sessionId, "批次", TransferMode.Distribution, QuotaBasis.TaskCount,
            ShortagePolicy.ProportionalReduction, accounts, "/", "None", DuplicatePolicy.Skip, "Immediate", 1, false);

    private static ImportSessionRecord Session(Guid id, int total, int ready, int invalid = 0, int duplicate = 0, int excluded = 0, int conflict = 0) =>
        new(id, "Excel", "[REDACTED]", "Draft", total, ready, invalid, duplicate, excluded,
            null, null, null, Jobs.BatchTestData.Now, Jobs.BatchTestData.Now);

    private static StagedImportRowRecord Row(Guid sessionId, int number, string? normalized, string? requested = null, Guid? mapped = null) =>
        new(Guid.NewGuid(), sessionId, number, $"https://pan.baidu.com/s/{number}", normalized,
            "a1b2", null, null, requested, mapped, null, normalized is null ? "Invalid" : "Ready",
            null, normalized is null ? "DRAFT_INVALID_LINK" : null, null, null, null, Jobs.BatchTestData.Now);

    private static async Task<IReadOnlyList<StagedImportRowRecord>> AllRowsAsync(IImportRepository repository, Guid sessionId)
    {
        var result = new List<StagedImportRowRecord>();
        long cursor = 0;
        while (true)
        {
            var page = await repository.GetRowsKeysetPageAsync(new(sessionId, null, null, 1_000, cursor, false));
            result.AddRange(page.Items);
            if (page.NextCursor is null) return result;
            cursor = page.NextCursor.Value;
        }
    }

    private sealed class UnlimitedDisk : IDiskSpaceProbe
    {
        public Task<long> GetAvailableBytesAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(long.MaxValue);
    }
}
