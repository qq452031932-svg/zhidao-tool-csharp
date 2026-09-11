using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Database.Repositories;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.Sqlite;
using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class BatchResultProjectionRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "batch-result-projection-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DefinitionUsesSourceSnapshotAndOrdersZeroBasedColumnsAndAccounts()
    {
        var graph = await CreateGraphAsync();
        await graph.ExecuteAsync("""
            INSERT INTO BatchSourceColumns(BatchJobId,ColumnOrdinal,SourceHeader,MappedRole,IsRequired)
            VALUES($batch,4,'备注','Note',0),($batch,0,'标题','Title',0),($batch,2,'链接','ShareUrl',1);
            INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled)
            VALUES($batch,$account2,2,'账号乙',1,0,0),($batch,$account1,1,'账号甲',1,1,1);
            """);

        var definition = await graph.Repository.GetDefinitionAsync(graph.BatchId);

        Assert.NotNull(definition);
        Assert.Equal("原文件.xlsx", definition.SourceFileName);
        Assert.Equal([(0, "标题"), (2, "链接"), (4, "备注")],
            definition.OriginalColumns.Select(value => (value.ColumnOrdinal, value.Header)));
        Assert.Equal([(graph.Account1, 1, "账号甲", true), (graph.Account2, 2, "账号乙", false)],
            definition.Accounts.Select(value => (value.AccountId, value.OutputOrdinal, value.DisplayName, value.ShareEnabled)));
    }

    [Fact]
    public async Task PastedTextBatchWithoutSourceColumnsProjectsItsFixedFields()
    {
        var graph = await CreateGraphAsync();
        await graph.ExecuteAsync("""
            UPDATE ImportSessions SET SourceType='Text',SourceName='[PASTED]' WHERE Id=$session;
            UPDATE BatchJobs SET SourceType='Text' WHERE Id=$batch;
            """);
        var resourceId = await graph.AddResourceAsync(
            1, "粘贴标题", "[]", "https://pan.baidu.com/s/original");

        var definition = await graph.Repository.GetDefinitionAsync(graph.BatchId);
        var page = await graph.Repository.GetProjectionPageAsync(graph.BatchId, 0, 10);

        Assert.NotNull(definition);
        Assert.Equal("[PASTED]", definition.SourceFileName);
        Assert.Empty(definition.OriginalColumns);
        var resource = Assert.Single(page.Resources);
        Assert.Equal(resourceId, resource.ResourceItemId);
        Assert.Empty(resource.OriginalValues);
        Assert.Equal("粘贴标题", resource.ResolvedTitle);
        Assert.Equal("https://pan.baidu.com/s/original", resource.OriginalShareUrl);
    }

    [Fact]
    public async Task ExcelBatchWithoutSourceColumnsStillFailsClosed()
    {
        var graph = await CreateGraphAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            graph.Repository.GetDefinitionAsync(graph.BatchId));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            graph.Repository.GetProjectionPageAsync(graph.BatchId, 0, 10));
    }

    [Fact]
    public async Task ResourcePagesUseStrictCandidateKeysetAndPersistedPayloadValues()
    {
        var graph = await CreateGraphAsync();
        await graph.ExecuteAsync("""
            INSERT INTO BatchSourceColumns(BatchJobId,ColumnOrdinal,SourceHeader,MappedRole,IsRequired)
            VALUES($batch,4,'备注','Note',0),($batch,0,'标题','Title',0),($batch,2,'链接','ShareUrl',1);
            """);
        var first = await graph.AddResourceAsync(1, "资源标题一", "[\"原始标题一\",\"忽略一\",\"原始链接文本一\",\"忽略二\",\"备注一\"]", "https://original/1");
        var second = await graph.AddResourceAsync(2, "资源标题二", "[\"原始标题二\",\"忽略一\",\"原始链接文本二\",\"忽略二\",\"备注二\"]", "https://original/2");

        var page1 = await graph.Repository.GetProjectionPageAsync(graph.BatchId, 0, 1);
        var page2 = await graph.Repository.GetProjectionPageAsync(graph.BatchId, page1.NextCandidateSequence!.Value, 1);

        var projectedFirst = Assert.Single(page1.Resources);
        Assert.Equal(first, projectedFirst.ResourceItemId);
        Assert.Equal(1, projectedFirst.CandidateSequence);
        Assert.Equal("资源标题一", projectedFirst.ResolvedTitle);
        Assert.Equal("https://original/1", projectedFirst.OriginalShareUrl);
        Assert.Equal([(0, "原始标题一"), (2, "原始链接文本一"), (4, "备注一")],
            projectedFirst.OriginalValues.Select(value => (value.ColumnOrdinal, value.Text)));
        Assert.Equal(1, page1.NextCandidateSequence);
        Assert.Equal(second, Assert.Single(page2.Resources).ResourceItemId);
        Assert.Null(page2.NextCandidateSequence);
        var empty = await graph.Repository.GetProjectionPageAsync(graph.BatchId, 2, 1);
        Assert.Empty(empty.Resources);
        Assert.Empty(empty.AccountResults);
    }

    [Theory]
    [InlineData("missing-payload")]
    [InlineData("blank-title")]
    [InlineData("malformed-json")]
    [InlineData("wrong-json-kind")]
    [InlineData("ordinal-out-of-range")]
    public async Task ResourceProjectionFailsClosedForIncompleteOrInvalidPersistedPayload(string corruption)
    {
        var graph = await CreateGraphAsync();
        await graph.ExecuteAsync("""
            INSERT INTO BatchSourceColumns(BatchJobId,ColumnOrdinal,SourceHeader,MappedRole,IsRequired)
            VALUES($batch,0,'标题','Title',0),($batch,1,'链接','ShareUrl',1);
            """);
        var resource = await graph.AddResourceAsync(1, "稳定标题", "[\"标题\",\"链接\"]", "https://original/1");
        var sql = corruption switch
        {
            "missing-payload" => "DELETE FROM StagedImportRowPayloads;",
            "blank-title" => "UPDATE ResourceItems SET ResolvedOutputTitle='   ' WHERE Id=$resource;",
            "malformed-json" => "UPDATE StagedImportRowPayloads SET RawValuesJson='not-json';",
            "wrong-json-kind" => "UPDATE StagedImportRowPayloads SET RawValuesJson='{\"cell\":\"value\"}';",
            "ordinal-out-of-range" => "UPDATE StagedImportRowPayloads SET RawValuesJson='[\"only-one\"]';",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };
        await graph.ExecuteAsync(sql, ("$resource", resource));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            graph.Repository.GetProjectionPageAsync(graph.BatchId, 0, 10));
    }

    [Fact]
    public async Task AccountRangeSelectsCanonicalTransferAndOnlyCurrentAcceptedShareResult()
    {
        var graph = await CreateGraphAsync();
        await graph.ExecuteAsync("""
            INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled)
            VALUES($batch,$account1,1,'账号甲',2,2,1),($batch,$account2,2,'账号乙',2,0,0);
            INSERT INTO BatchSourceColumns(BatchJobId,ColumnOrdinal,SourceHeader,MappedRole,IsRequired)
            VALUES($batch,0,'标题','Title',0);
            INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,FixedExtractionCode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
            VALUES($batch,1,1,'Random',NULL,'Ask','settings',$now);
            """);
        var first = await graph.AddResourceAsync(1, "一", "[\"一\"]", "https://original/1");
        var second = await graph.AddResourceAsync(2, "二", "[\"二\"]", "https://original/2");
        var cancelled = Guid.NewGuid();
        var active = Guid.NewGuid();
        await graph.AddTransferAsync(cancelled, first, graph.Account1, 1, 1, "/cancelled", "Cancelled", "OLD");
        await graph.AddTransferAsync(active, first, graph.Account1, 2, 2, "/active", "Succeeded", null);
        await graph.AddTransferAsync(Guid.NewGuid(), second, graph.Account2, 3, 3, "/second", "Failed", "SECOND_FAILED");
        var share = Guid.NewGuid();
        await graph.ExecuteAsync("""
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($share,$transfer,$resource,$batch,$account1,1,'Succeeded','exact','settings',1,'Unknown',$now,$now,
              'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account1,'-','')),NULL,NULL);
            INSERT INTO BaiduShareResults(Id,ShareOperationId,BatchJobId,ResourceItemId,AccountId,CandidateSequence,OutputOrdinal,ExactItemSetFingerprint,SettingsFingerprint,Generation,ShareId,ShareUrl,ExtractionCode,IsAccepted,IsCurrent,SharedAt,UpdatedAt)
            VALUES($oldResult,$share,$batch,$resource,$account1,1,1,'exact','settings',1,'old-share','https://old','old0',1,0,$now,$now),
                  ($rejectedResult,$share,$batch,$resource,$account1,1,1,'exact','settings',1,'rejected-share','https://rejected-current','bad0',0,1,$now,$now),
                  ($currentResult,$share,$batch,$resource,$account1,1,1,'exact','settings',1,'new-share','https://current','new1',1,1,$now,$now);
            """,
            ("$share", share), ("$transfer", active), ("$resource", first),
            ("$oldResult", Guid.NewGuid()), ("$rejectedResult", Guid.NewGuid()), ("$currentResult", Guid.NewGuid()));

        var page = await graph.Repository.GetProjectionPageAsync(graph.BatchId, 0, 1);
        var outcomes = page.AccountResults;

        Assert.Equal(first, Assert.Single(page.Resources).ResourceItemId);
        Assert.Equal(1, page.NextCandidateSequence);
        var outcome = Assert.Single(outcomes);
        Assert.Equal(first, outcome.ResourceItemId);
        Assert.Equal(graph.Account1, outcome.AccountId);
        Assert.Equal(1, outcome.OutputOrdinal);
        Assert.Equal("Succeeded", outcome.TransferStatus);
        Assert.Equal(ShareOperationStatus.Succeeded, outcome.ShareStatus);
        Assert.Equal("https://current", outcome.ShareUrl);
        Assert.Equal("new1", outcome.ExtractionCode);
        Assert.Null(outcome.StableErrorCode);
        var secondPage = await graph.Repository.GetProjectionPageAsync(graph.BatchId, 1, 1);
        Assert.Equal(second, Assert.Single(secondPage.Resources).ResourceItemId);
        Assert.Equal(graph.Account2, Assert.Single(secondPage.AccountResults).AccountId);
        Assert.Null(secondPage.NextCandidateSequence);
    }

    [Fact]
    public async Task AccountRangeDoesNotExposeUnacceptedOrNoncurrentLinksAndDoesNotCreateDistributionRows()
    {
        var graph = await CreateGraphAsync();
        await graph.ExecuteAsync("""
            INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled)
            VALUES($batch,$account1,1,'账号甲',1,1,1),($batch,$account2,2,'账号乙',1,0,0);
            INSERT INTO BatchSourceColumns(BatchJobId,ColumnOrdinal,SourceHeader,MappedRole,IsRequired)
            VALUES($batch,0,'标题','Title',0);
            INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,FixedExtractionCode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
            VALUES($batch,1,1,'Random',NULL,'Ask','settings',$now);
            """);
        var first = await graph.AddResourceAsync(1, "一", "[\"一\"]", "https://original/1");
        _ = await graph.AddResourceAsync(2, "二", "[\"二\"]", "https://original/2");
        var transfer = Guid.NewGuid();
        await graph.AddTransferAsync(transfer, first, graph.Account1, 1, 1, "/active", "Failed", "TRANSFER_FAILED");

        var page = await graph.Repository.GetProjectionPageAsync(graph.BatchId, 0, 10);
        var outcomes = page.AccountResults;

        Assert.Equal(2, page.Resources.Count);
        var failed = Assert.Single(outcomes);
        Assert.Equal("Failed", failed.TransferStatus);
        Assert.Equal("TRANSFER_FAILED", failed.StableErrorCode);
        Assert.Null(failed.ShareStatus);
        Assert.Null(failed.ShareUrl);
        Assert.Null(failed.ExtractionCode);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("ReusedExisting")]
    public async Task SuccessfulShareStatusWithoutExactlyOneAcceptedCurrentResultFailsClosed(string shareStatus)
    {
        var graph = await CreateGraphAsync();
        await graph.ExecuteAsync("""
            INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled)
            VALUES($batch,$account1,1,'账号甲',1,1,1);
            INSERT INTO BatchSourceColumns(BatchJobId,ColumnOrdinal,SourceHeader,MappedRole,IsRequired)
            VALUES($batch,0,'标题','Title',0);
            INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,FixedExtractionCode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
            VALUES($batch,1,1,'Random',NULL,'Ask','settings',$now);
            """);
        var resource = await graph.AddResourceAsync(1, "资源", "[\"资源\"]", "https://original/1");
        var transfer = Guid.NewGuid();
        await graph.AddTransferAsync(transfer, resource, graph.Account1, 1, 1, "/target", "Succeeded", null);
        await graph.ExecuteAsync("""
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($share,$transfer,$resource,$batch,$account1,1,$status,'exact','settings',1,'Unknown',$now,$now,
              'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account1,'-','')),NULL,NULL);
            """,
            ("$share", Guid.NewGuid()), ("$transfer", transfer), ("$resource", resource), ("$status", shareStatus));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            graph.Repository.GetProjectionPageAsync(graph.BatchId, 0, 10));
    }

    [Fact]
    public async Task CurrentShareResultIsResolvedByAccountExactSetAndSettingsAcrossOperations()
    {
        var graph = await CreateGraphAsync();
        await graph.ExecuteAsync("""
            INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled)
            VALUES($batch,$account1,1,'账号甲',2,2,1);
            INSERT INTO BatchSourceColumns(BatchJobId,ColumnOrdinal,SourceHeader,MappedRole,IsRequired)
            VALUES($batch,0,'标题','Title',0);
            INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,FixedExtractionCode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
            VALUES($batch,1,1,'Random',NULL,'Ask','settings',$now);
            """);
        var first = await graph.AddResourceAsync(1, "资源一", "[\"资源一\"]", "https://original/1");
        var second = await graph.AddResourceAsync(2, "资源二", "[\"资源二\"]", "https://original/2");
        var firstTransfer = Guid.NewGuid();
        var secondTransfer = Guid.NewGuid();
        var firstShare = Guid.NewGuid();
        var secondShare = Guid.NewGuid();
        await graph.AddTransferAsync(firstTransfer, first, graph.Account1, 1, 1, "/first", "Succeeded", null);
        await graph.AddTransferAsync(secondTransfer, second, graph.Account1, 2, 2, "/second", "Succeeded", null);
        await graph.ExecuteAsync("""
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($firstShare,$firstTransfer,$first,$batch,$account1,1,'Succeeded','same-set','settings',1,'Unknown',$now,$now,
                    'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account1,'-','')),NULL,NULL),
                  ($secondShare,$secondTransfer,$second,$batch,$account1,1,'ReusedExisting','same-set','settings',1,'Unknown',$now,$now,
                    'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account1,'-','')),NULL,NULL);
            INSERT INTO BaiduShareResults(Id,ShareOperationId,BatchJobId,ResourceItemId,AccountId,CandidateSequence,OutputOrdinal,ExactItemSetFingerprint,SettingsFingerprint,Generation,ShareId,ShareUrl,ExtractionCode,IsAccepted,IsCurrent,SharedAt,UpdatedAt)
            VALUES($oldResult,$firstShare,$batch,$first,$account1,1,1,'same-set','settings',1,'old-share','https://old','old0',1,0,$now,$now),
                  ($currentResult,$secondShare,$batch,$second,$account1,2,1,'same-set','settings',1,'current-share','https://current','new1',1,1,$now,$now);
            """,
            ("$firstShare", firstShare), ("$firstTransfer", firstTransfer), ("$first", first),
            ("$secondShare", secondShare), ("$secondTransfer", secondTransfer), ("$second", second),
            ("$oldResult", Guid.NewGuid()), ("$currentResult", Guid.NewGuid()));

        var page = await graph.Repository.GetProjectionPageAsync(graph.BatchId, 0, 10);

        Assert.Equal(2, page.AccountResults.Count);
        Assert.All(page.AccountResults, outcome =>
        {
            Assert.Equal("https://current", outcome.ShareUrl);
            Assert.Equal("new1", outcome.ExtractionCode);
        });
    }

    [Fact]
    public async Task RealSqliteProjectionPagesRollsOverPublishesAndReopensReplacementRows()
    {
        var graph = await CreateGraphAsync();
        await graph.ExecuteAsync("""
            INSERT INTO BatchSourceColumns(BatchJobId,ColumnOrdinal,SourceHeader,MappedRole,IsRequired)
            VALUES($batch,0,'原标题','Title',0),($batch,1,'原链接字段','ShareUrl',1);
            INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled)
            VALUES($batch,$account1,1,'账号甲',1,1,1);
            INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,FixedExtractionCode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
            VALUES($batch,1,1,'Random',NULL,'Ask','settings',$now);
            """);
        var resource = await graph.AddResourceAsync(
            1,
            "持久化的缺失标题回填 等 2 项",
            "[null,\"源表链接原文\"]",
            "https://pan.baidu.com/s/original");
        _ = await graph.AddResourceAsync(2, "第二个标题", "[\"源标题二\",\"源链接二\"]", "https://pan.baidu.com/s/original-2");
        _ = await graph.AddResourceAsync(3, "第三个标题", "[\"源标题三\",\"源链接三\"]", "https://pan.baidu.com/s/original-3");
        var transfer = Guid.NewGuid();
        var share = Guid.NewGuid();
        await graph.AddTransferAsync(transfer, resource, graph.Account1, 1, 1, "/target", "Succeeded", null);
        await graph.ExecuteAsync("""
            INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,SubmissionAcceptance,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($share,$transfer,$resource,$batch,$account1,1,'Succeeded','exact','settings',1,'Unknown',$now,$now,
              'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account1,'-','')),NULL,NULL);
            INSERT INTO BaiduShareResults(Id,ShareOperationId,BatchJobId,ResourceItemId,AccountId,CandidateSequence,OutputOrdinal,ExactItemSetFingerprint,SettingsFingerprint,Generation,ShareId,ShareUrl,ExtractionCode,IsAccepted,IsCurrent,SharedAt,UpdatedAt)
            VALUES($result,$share,$batch,$resource,$account1,1,1,'exact','settings',1,'share-id','https://pan.baidu.com/s/replacement','r4nd',1,1,$now,$now);
            """,
            ("$share", share), ("$transfer", transfer), ("$resource", resource), ("$result", Guid.NewGuid()));

        var service = new ReplacementWorkbookExportService(
            graph.Repository,
            new ReplacementWorkbookFileNameBuilder(),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 23, 9, 10, 11, TimeSpan.Zero)));
        var receipt = await service.ExportToDirectoryAsync(
            new(graph.BatchId, PageSize: 1),
            root,
            new OpenXmlReplacementWorkbookFilePublisher(new OpenXmlReplacementResultWriter(maxRowsPerSheet: 3)));

        Assert.Equal(3, receipt.ExportedRows);
        Assert.Equal(2, receipt.SheetCount);
        Assert.Equal("原文件_转存分享结果_20260823091011.xlsx", Path.GetFileName(receipt.FinalPath));
        using var workbook = SpreadsheetDocument.Open(receipt.FinalPath, false);
        var workbookPart = workbook.WorkbookPart!;
        var sheets = workbookPart.Workbook!.Sheets!.Elements<Sheet>().ToArray();
        Assert.Equal(2, sheets.Length);
        var rows = sheets.SelectMany(sheet =>
                ((WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet!
                    .GetFirstChild<SheetData>()!.Elements<Row>().Skip(1))
            .ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(
            new[]
            {
                "", "源表链接原文",
                "持久化的缺失标题回填 等 2 项", "https://pan.baidu.com/s/original",
                "账号甲", "转存成功",
                "https://pan.baidu.com/s/replacement", "r4nd", "分享成功",
            },
            rows[0].Elements<Cell>().Select(cell => cell.InlineString!.Text!.Text));
        Assert.Equal(
            new[] { "持久化的缺失标题回填 等 2 项", "第二个标题", "第三个标题" },
            rows.Select(row => row.Elements<Cell>().ElementAt(2).InlineString!.Text!.Text));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task TwoHundredThousandRowsUseRealSqliteKeysetsAndRealReplacementWriterWithinBoundedMemory()
    {
        const int resourceCount = 200_000;
        var graph = await CreateGraphAsync();
        await graph.ExecuteAsync("""
            INSERT INTO BatchSourceColumns(BatchJobId,ColumnOrdinal,SourceHeader,MappedRole,IsRequired)
            VALUES($batch,0,'原标题','Title',0),($batch,1,'原链接字段','ShareUrl',1);
            INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled)
            VALUES($batch,$account1,1,'规模账号',200000,0,0);
            UPDATE BatchJobs SET ResourceCount=200000,OperationCount=200000 WHERE Id=$batch;
            WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<200000)
            INSERT INTO StagedImportRows(Id,ImportSessionId,SourceRowNumber,ShareUrl,Disposition,UpdatedAt)
            SELECT printf('30000000-0000-0000-0000-%012d',x),$session,x,
                   'https://pan.baidu.com/s/original-'||x,'Ready',$now FROM seq;
            WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<200000)
            INSERT INTO StagedImportRowPayloads(StagedImportRowId,SourceTitle,ResolvedOutputTitle,OriginalShareUrl,RawValuesJson)
            SELECT printf('30000000-0000-0000-0000-%012d',x),NULL,'标题-'||x,
                   'https://pan.baidu.com/s/original-'||x,
                   json_array('原标题-'||x,'https://source/'||x) FROM seq;
            WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<200000)
            INSERT INTO ResourceItems(Id,BatchJobId,StagedImportRowId,CandidateSequence,ShareUrl,NormalizedShareId,FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt,ResolvedOutputTitle)
            SELECT printf('40000000-0000-0000-0000-%012d',x),$batch,
                   printf('30000000-0000-0000-0000-%012d',x),x,
                   'https://mutable/'||x,'normalized-'||x,'None','Unknown','Expanded',$now,$now,'标题-'||x FROM seq;
            WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<200000)
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,SubmitState,TargetSlotState,LastErrorCode,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            SELECT printf('50000000-0000-0000-0000-%012d',x),
                   printf('40000000-0000-0000-0000-%012d',x),$batch,x,x,'/target','/target','',$account1,
                   'Weighted','Failed','Failed',1,'fingerprint-'||x,'{}','ResultReceived','Fulfilled','SCALE_FAILURE',$now,$now,
                   'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account1,'-','')),NULL,NULL FROM seq;
            """);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(true);
        var trackingWriter = new ManagedMemoryTrackingWriter(new OpenXmlReplacementResultWriter(), baseline);
        var service = new ReplacementWorkbookExportService(
            graph.Repository,
            new ReplacementWorkbookFileNameBuilder(),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 23, 9, 10, 11, TimeSpan.Zero)));

        var receipt = await service.ExportToDirectoryAsync(
            new(graph.BatchId, PageSize: 1_000),
            root,
            new OpenXmlReplacementWorkbookFilePublisher(trackingWriter));

        Assert.Equal(resourceCount, receipt.ExportedRows);
        Assert.True(trackingWriter.MaximumManagedBytes - baseline < 128L * 1024 * 1024,
            $"Managed memory grew by {trackingWriter.MaximumManagedBytes - baseline:N0} bytes.");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private async Task<Graph> CreateGraphAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, $"{Guid.NewGuid():N}.db"));
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        var graph = new Graph(factory, new SqliteBatchResultProjectionRepository(factory));
        await graph.ExecuteAsync("""
            INSERT INTO ImportSessions(Id,SourceType,SourceName,Status,CreatedAt,UpdatedAt)
            VALUES($session,'Excel','原文件.xlsx','BatchCreated',$now,$now);
            INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
            VALUES($account1,'user-1','账号甲','WebView2','Active',0,0,1,1,'2026-08-23',0),
                  ($account2,'user-2','账号乙','WebView2','Active',0,0,2,1,'2026-08-23',0);
            INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,ImportSessionId,QueueOrder)
            VALUES($batch,'批次','Excel','Distribution','TaskCount','AccountPriority',2,2,'/','None','Skip','Strict','Active',$now,$session,1);
            """);
        return graph;
    }

    private sealed class Graph(SqliteConnectionFactory factory, SqliteBatchResultProjectionRepository repository)
    {
        private static readonly string Now = new DateTimeOffset(2026, 8, 23, 1, 2, 3, TimeSpan.Zero).ToString("O");
        public Guid SessionId { get; } = Guid.NewGuid();
        public Guid BatchId { get; } = Guid.NewGuid();
        public Guid Account1 { get; } = Guid.NewGuid();
        public Guid Account2 { get; } = Guid.NewGuid();
        public SqliteBatchResultProjectionRepository Repository { get; } = repository;

        public async Task<Guid> AddResourceAsync(long candidateSequence, string title, string rawJson, string originalUrl)
        {
            var row = Guid.NewGuid();
            var resource = Guid.NewGuid();
            await ExecuteAsync("""
                INSERT INTO StagedImportRows(Id,ImportSessionId,SourceRowNumber,ShareUrl,Disposition,UpdatedAt)
                VALUES($row,$session,$sourceRow,$original,'Ready',$now);
                INSERT INTO StagedImportRowPayloads(StagedImportRowId,SourceTitle,ResolvedOutputTitle,OriginalShareUrl,RawValuesJson)
                VALUES($row,NULL,'payload-title',$original,$raw);
                INSERT INTO ResourceItems(Id,BatchJobId,StagedImportRowId,CandidateSequence,ShareUrl,NormalizedShareId,FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt,ResolvedOutputTitle)
                VALUES($resource,$batch,$row,$sequence,'https://mutable/resource','normalized','None','Unknown','Expanded',$now,$now,$title);
                """,
                ("$row", row), ("$sourceRow", candidateSequence), ("$original", originalUrl),
                ("$raw", rawJson), ("$resource", resource), ("$sequence", candidateSequence), ("$title", title));
            return resource;
        }

        public Task AddTransferAsync(Guid operationId, Guid resourceId, Guid accountId, long sequenceNumber,
            long allocationOrdinal, string targetPath, string status, string? errorCode) => ExecuteAsync("""
                INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,SubmitState,TargetSlotState,LastErrorCode,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
                VALUES($operation,$resource,$batch,$sequence,$ordinal,$path,$path,'',$account,'Weighted',$status,NULL,1,$fingerprint,'{}','ResultReceived','Fulfilled',$error,$now,$now,
                  'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account,'-','')),NULL,NULL);
                """,
                ("$operation", operationId), ("$resource", resourceId), ("$account", accountId),
                ("$sequence", sequenceNumber), ("$ordinal", allocationOrdinal), ("$path", targetPath),
                ("$status", status), ("$fingerprint", $"fingerprint-{operationId:N}"), ("$error", errorCode));

        public async Task ExecuteAsync(string sql, params (string Name, object? Value)[] values)
        {
            await using var connection = await factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in BaseValues().Concat(values).GroupBy(value => value.Name).Select(group => group.Last()))
                command.Parameters.AddWithValue(name, value is Guid id ? id.ToString("D") : value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        private IEnumerable<(string Name, object? Value)> BaseValues()
        {
            yield return ("$session", SessionId.ToString("D"));
            yield return ("$batch", BatchId.ToString("D"));
            yield return ("$account1", Account1.ToString("D"));
            yield return ("$account2", Account2.ToString("D"));
            yield return ("$now", Now);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ManagedMemoryTrackingWriter(IReplacementWorkbookWriter inner, long baseline)
        : IReplacementWorkbookWriter
    {
        public long MaximumManagedBytes { get; private set; } = baseline;

        public Task WriteAsync(
            Stream output,
            ReplacementWorkbookLayout layout,
            IAsyncEnumerable<ReplacementWorkbookRow> rows,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(output, layout, TrackAsync(rows, cancellationToken), cancellationToken);

        private async IAsyncEnumerable<ReplacementWorkbookRow> TrackAsync(
            IAsyncEnumerable<ReplacementWorkbookRow> rows,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var count = 0;
            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                if (++count % 10_000 == 0)
                    MaximumManagedBytes = Math.Max(MaximumManagedBytes, GC.GetTotalMemory(forceFullCollection: true));
                yield return row;
            }
            MaximumManagedBytes = Math.Max(MaximumManagedBytes, GC.GetTotalMemory(forceFullCollection: true));
        }
    }
}
