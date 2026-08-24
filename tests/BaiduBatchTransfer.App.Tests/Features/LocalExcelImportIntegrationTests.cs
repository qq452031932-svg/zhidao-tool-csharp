using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.DependencyInjection;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LocalExcelImportIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "BaiduBatchTransfer-LocalExcelImportIntegrationTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GeneratedTemplatePreservesOperationalFieldsAndRawCellsThroughConfirmedStaging()
    {
        Directory.CreateDirectory(root);
        var workbookPath = Path.Combine(root, "generated-template.xlsx");
        await using (var output = File.Create(workbookPath))
            await new OpenXmlTemplateWriter().WriteAsync(output);
        AppendTemplateRow(
            workbookPath,
            ["  源标题  ", "https://pan.baidu.com/s/template", " a1B2 ", " /课程 ", " 外层 ", " 账号甲 ", " 备注文本 "]);
        var presenter = new ConfirmingMappingPresenter();
        using var host = AppBootstrap.CreateHost(root, services =>
            services.AddSingleton<IExcelColumnMappingPresenter>(presenter));
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var sessionId = Guid.NewGuid();

        await host.Services.GetRequiredService<IImportWorkflow>()
            .ImportExcelAsync(sessionId, workbookPath, CancellationToken.None);

        await using var unit = await host.Services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync();
        var definitions = await unit.Imports.GetColumnDefinitionsAsync(sessionId);
        Assert.Equal(
            new[] { "Title", "ShareUrl", "ExtractionCode", "TargetPath", "OuterFolderName", "RequestedAccountName", "Note" },
            definitions.Select(definition => definition.MappedRole));
        var row = Assert.Single((await unit.Imports.GetRowsPageAsync(new(sessionId, null, 1, 10))).Items);
        Assert.Equal("a1B2", row.ExtractionCode);
        Assert.Equal("/课程", row.TargetPath);
        Assert.Equal("外层", row.CreateFolderName);
        Assert.Equal("账号甲", row.RequestedAccountName);
        Assert.Equal("备注文本", row.Note);
        var payload = await unit.Imports.GetRowPayloadAsync(row.Id);
        Assert.NotNull(payload);
        Assert.Equal("源标题", payload.SourceTitle);
        Assert.Equal(
            new[] { "  源标题  ", "https://pan.baidu.com/s/template", " a1B2 ", " /课程 ", " 外层 ", " 账号甲 ", " 备注文本 " },
            System.Text.Json.JsonSerializer.Deserialize<string[]>(payload.RawValuesJson));
    }

    [Fact]
    public async Task WorkbookReplacedDuringMappingDialogIsRejectedBeforeCreatingSession()
    {
        Directory.CreateDirectory(root);
        var workbookPath = Path.Combine(root, "replace-during-dialog.xlsx");
        WriteWorkbook(workbookPath, [["https://pan.baidu.com/s/original", "a1b2"]]);
        var presenter = new MutatingMappingPresenter(() =>
        {
            File.Delete(workbookPath);
            WriteWorkbook(workbookPath, [["https://pan.baidu.com/s/replaced", "a1b2"]]);
        });
        using var host = AppBootstrap.CreateHost(root, services =>
            services.AddSingleton<IExcelColumnMappingPresenter>(presenter));
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();

        await Assert.ThrowsAsync<ImportWorkflowException>(() =>
            host.Services.GetRequiredService<IImportWorkflow>()
                .ImportExcelAsync(Guid.NewGuid(), workbookPath, CancellationToken.None));

        await using var connection = await host.Services.GetRequiredService<SqliteConnectionFactory>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ImportSessions;";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task NewViewModelKeepsDurableResumeReachableAfterWrongWorkbookAndClearsAfterSuccess()
    {
        Directory.CreateDirectory(root);
        var workbookPath = Path.Combine(root, "restart-source.xlsx");
        WriteWorkbook(
            workbookPath,
            [
                ["https://pan.baidu.com/s/restart-one", "a1b2"],
                ["https://pan.baidu.com/s/restart-two", "c3d4"],
            ]);
        var sessionId = Guid.NewGuid();
        ConfirmedImportColumnMapping mapping;
        string digest;
        await using (var stream = File.OpenRead(workbookPath))
        {
            var inspection = await new OpenXmlWorkbookInspector().InspectAsync(stream);
            var suggestion = new ExcelColumnSuggestionEngine().Suggest(inspection);
            mapping = new ConfirmedImportColumnMapping(suggestion.HeaderRowNumber, suggestion.Columns, inspection.Columns);
        }
        await using (var stream = File.OpenRead(workbookPath))
            digest = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream)).ToLowerInvariant();

        using (var firstHost = AppBootstrap.CreateHost(root))
        {
            await firstHost.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
            var firstRow = new ImportSourceRow(
                2,
                "https://pan.baidu.com/s/restart-one",
                "a1b2",
                OriginalExtractedUrl: "https://pan.baidu.com/s/restart-one",
                RawValues: ["https://pan.baidu.com/s/restart-one", "a1b2"]);
            await firstHost.Services.GetRequiredService<ImportStagingService>().StageAsync(
                new ImportRequest(
                    sessionId,
                    "Excel",
                    Path.GetFileName(workbookPath),
                    ColumnMapping: mapping,
                    SourceColumns: mapping.SourceColumns,
                    SourceContentFingerprint: digest),
                OneRow(firstRow));
            await using var interrupted = await firstHost.Services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync();
            var session = (await interrupted.Imports.GetSessionAsync(sessionId))!;
            await interrupted.Imports.UpdateSessionAsync(session with { Status = "Parsing" });
            await interrupted.CommitAsync();
        }

        var wrongPath = Path.Combine(root, "wrong-reselection.xlsx");
        WriteWorkbook(wrongPath, [["https://pan.baidu.com/s/wrong", "zzzz"]]);
        using var resumedHost = AppBootstrap.CreateHost(root, services =>
            services.AddSingleton<IDialogService>(new SequenceDialogService(wrongPath, workbookPath)));
        var vm = new ImportViewModel(
            resumedHost.Services.GetRequiredService<IImportWorkflow>(),
            resumedHost.Services.GetRequiredService<IImportPreviewPageSource>(),
            resumedHost.Services.GetRequiredService<IDialogService>());
        await vm.InitializeAsync();
        Assert.Equal(sessionId, vm.SessionId);
        Assert.True(vm.CanResumeImport);

        await vm.ResumeImportAsync();
        Assert.True(vm.CanResumeImport);
        Assert.DoesNotContain(wrongPath, vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        await vm.ResumeImportAsync();

        Assert.False(vm.CanResumeImport);
        Assert.Equal(2, vm.TotalRows);
        await using var unit = await resumedHost.Services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync();
        Assert.Equal("Draft", (await unit.Imports.GetSessionAsync(sessionId))!.Status);
        Assert.Equal(2, (await unit.Imports.GetRowsPageAsync(new(sessionId, null, 1, 10))).TotalCount);
    }

    [Fact]
    public async Task NewViewModelDiscoversLatestDurableExcelResumeWithoutReplayPath()
    {
        using var firstHost = AppBootstrap.CreateHost(root);
        await firstHost.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var older = Guid.NewGuid();
        var latest = Guid.NewGuid();
        var completed = Guid.NewGuid();
        await using (var unit = await firstHost.Services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync())
        {
            foreach (var (id, status, updatedAt) in new[]
            {
                (older, "Parsing", now.AddMinutes(-1)),
                (latest, "Parsing", now),
                (completed, "Draft", now.AddMinutes(1)),
            })
            {
                var columns = new[] { new TabularInspectionColumn(0, "链接") };
                var mapping = new ConfirmedImportColumnMapping(
                    1,
                    [new(0, TabularImportColumnRole.ShareUrl)],
                    columns);
                await unit.Imports.AddSessionAsync(ImportSessionRecord.Create(id, "Excel", $"{id:N}.xlsx") with
                {
                    Status = status,
                    ColumnMappingFingerprint = mapping.Fingerprint,
                    SourceContentFingerprint = new string('a', 64),
                    HeaderRowNumber = 1,
                    UpdatedAt = updatedAt,
                });
                await unit.Imports.SetColumnDefinitionsAsync(id,
                [
                    new(id, 0, "链接", "ShareUrl", true),
                ]);
            }
            await unit.Imports.AddSessionAsync(ImportSessionRecord.Create(Guid.NewGuid(), "Text", "paste") with
            {
                Status = "Parsing",
                UpdatedAt = now.AddMinutes(2),
            });
            await unit.CommitAsync();
        }

        using var restartedHost = AppBootstrap.CreateHost(root);
        var vm = await restartedHost.Services.GetRequiredService<IImportWorkspaceFactory>()
            .CreateAsync();

        Assert.Equal(latest, vm.SessionId);
        Assert.True(vm.CanResumeImport);
        Assert.DoesNotContain(".xlsx", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealWorkbookImportsThroughTheMigratedHostWhileTransfersStayUnavailable()
    {
        Directory.CreateDirectory(root);
        var workbookPath = Path.Combine(root, "private-user-workbook.xlsx");
        WriteWorkbook(
            workbookPath,
            [
                ["https://pan.baidu.com/s/integration-alpha", "a1b2"],
                ["https://pan.baidu.com/s/integration-beta", "c3d4"],
            ]);
        var presenter = new ConfirmingMappingPresenter();
        using var host = AppBootstrap.CreateHost(root, services =>
            services.AddSingleton<IExcelColumnMappingPresenter>(presenter));
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var availability = host.Services.GetRequiredService<IAdapterAvailability>();
        var vm = new ImportViewModel(
            host.Services.GetRequiredService<IImportWorkflow>(),
            host.Services.GetRequiredService<IImportPreviewPageSource>(),
            new FakeDialogService(workbookPath),
            availability);
        var statuses = new List<string>();
        ImportDraftReady? draft = null;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ImportViewModel.StatusMessage))
                statuses.Add(vm.StatusMessage);
        };
        vm.DraftReady += (_, value) => draft = value;

        await vm.SelectAndImportExcelCommand.ExecuteAsync(null);

        Assert.False(availability.CanStartTransfers);
        var completed = Assert.IsType<ImportDraftReady>(draft);
        Assert.Equal(2, completed.Statistics.Total);
        Assert.Equal(2, completed.Statistics.Ready);
        Assert.Equal(0, completed.Statistics.Invalid);
        Assert.Equal(0, completed.Statistics.Duplicate);

        await using (var unit = await host.Services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync())
        {
            var persisted = await unit.Imports.GetSessionAsync(completed.SessionId);
            Assert.NotNull(persisted);
            Assert.Equal("Excel", persisted.SourceType);
            Assert.Equal("private-user-workbook.xlsx", persisted.SourceName);
            Assert.NotNull(persisted.ColumnMappingFingerprint);
            Assert.Equal(2, persisted.TotalRows);
            Assert.Equal(2, persisted.ReadyRows);
            Assert.Equal(2, (await unit.Imports.GetColumnDefinitionsAsync(completed.SessionId)).Count);
            var staged = (await unit.Imports.GetRowsPageAsync(new(completed.SessionId, null, 1, 10))).Items;
            foreach (var row in staged) Assert.NotNull(await unit.Imports.GetRowPayloadAsync(row.Id));
            Assert.Equal(presenter.LastMapping!.Fingerprint, persisted.ColumnMappingFingerprint);
            await unit.RollbackAsync();
        }

        await using (var connection = await host.Services
            .GetRequiredService<SqliteConnectionFactory>()
            .OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM ImportSessions;";
            var sessionCount = Convert.ToInt64(await command.ExecuteScalarAsync());
            Assert.Equal(1L, sessionCount);
        }

        var query = host.Services.GetRequiredService<ImportPageQuery>();
        const int pageSize = 1;
        Assert.InRange(pageSize, 1, 1_000);
        var first = await query.ExecuteAsync(new(completed.SessionId, null, null, pageSize));
        var firstRow = Assert.Single(first.Items);
        Assert.Equal("https://pan.baidu.com/s/integration-alpha", firstRow.ShareUrl);
        Assert.NotNull(first.NextSourceRowNumber);
        var second = await query.ExecuteAsync(new(
            completed.SessionId,
            null,
            null,
            pageSize,
            first.NextSourceRowNumber));
        var secondRow = Assert.Single(second.Items);
        Assert.Equal("https://pan.baidu.com/s/integration-beta", secondRow.ShareUrl);

        Assert.Contains("请选择 Excel 文件", statuses);
        Assert.Contains("已选择 Excel 文件，正在检查", statuses);
        Assert.Contains(statuses, status => status.Contains("正在解析并保存数据", StringComparison.Ordinal));
        Assert.All(statuses, status => Assert.DoesNotContain("草稿", status, StringComparison.Ordinal));
        Assert.Contains("导入完成，请分页检查异常、重复和账号映射。", statuses);
        Assert.All(statuses, status => Assert.DoesNotContain(workbookPath, status, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, presenter.ConfirmationCount);
    }

    [Fact]
    public async Task LinkEditKeepsResolvedExcelAccountMappingOutOfTheUnresolvedSnapshot()
    {
        using var host = AppBootstrap.CreateHost(root);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var sessionId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await using (var unit = await host.Services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync())
        {
            await unit.Accounts.UpsertAsync(AccountRecord.Create(accountId, "mapped-user", "账号甲"));
            await unit.Imports.AddSessionAsync(ImportSessionRecord.Create(sessionId, "Excel", "mapped.xlsx") with
            {
                Status = "Draft",
                TotalRows = 1,
                ReadyRows = 1,
            });
            await unit.Imports.AddRowsAsync(
            [
                StagedImportRowRecord.Create(
                    rowId,
                    sessionId,
                    2,
                    "https://pan.baidu.com/s/mapped-before",
                    "Ready") with
                {
                    NormalizedShareId = "mapped-before",
                    ExtractionCode = "a1b2",
                    RequestedAccountName = "账号甲",
                    MappedAccountId = accountId,
                },
            ]);
            await unit.CommitAsync();
        }

        var result = await host.Services.GetRequiredService<IImportWorkflow>().UpdateLinkAsync(
            rowId,
            "https://pan.baidu.com/s/mapped-after",
            "a1b2",
            CancellationToken.None);

        Assert.True(result.Statistics.HasExcelAccountColumn);
        Assert.Equal(0, result.Statistics.UnresolvedAccountMappings);
    }

    [Fact]
    public async Task InvalidRealWorkbookReturnsSafeChineseErrorWithoutLeakingItsPath()
    {
        Directory.CreateDirectory(root);
        var workbookPath = Path.Combine(root, "secret-token-invalid.xlsx");
        await File.WriteAllTextAsync(workbookPath, "not an Open XML workbook");
        var presenter = new ConfirmingMappingPresenter();
        using var host = AppBootstrap.CreateHost(root, services =>
            services.AddSingleton<IExcelColumnMappingPresenter>(presenter));
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var vm = new ImportViewModel(
            host.Services.GetRequiredService<IImportWorkflow>(),
            host.Services.GetRequiredService<IImportPreviewPageSource>(),
            new FakeDialogService(workbookPath),
            host.Services.GetRequiredService<IAdapterAvailability>());

        await vm.SelectAndImportExcelCommand.ExecuteAsync(null);

        Assert.False(vm.CanResumeImport);
        Assert.Equal("Excel 文件无法读取，请确认文件未损坏且格式为 .xlsx。", vm.StatusMessage);
        Assert.DoesNotContain(workbookPath, vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenXml", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, presenter.ConfirmationCount);
        await using var connection = await host.Services.GetRequiredService<SqliteConnectionFactory>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ImportSessions;";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task CancellingColumnMappingCreatesNoImportSession()
    {
        Directory.CreateDirectory(root);
        var workbookPath = Path.Combine(root, "cancelled.xlsx");
        WriteWorkbook(workbookPath, [["https://pan.baidu.com/s/id", "a1b2"]]);
        using var host = AppBootstrap.CreateHost(root, services =>
            services.AddSingleton<IExcelColumnMappingPresenter>(new CancellingMappingPresenter()));
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var vm = new ImportViewModel(
            host.Services.GetRequiredService<IImportWorkflow>(),
            host.Services.GetRequiredService<IImportPreviewPageSource>(),
            new FakeDialogService(workbookPath),
            host.Services.GetRequiredService<IAdapterAvailability>());

        await vm.SelectAndImportExcelCommand.ExecuteAsync(null);

        Assert.Null(vm.SessionId);
        Assert.False(vm.CanResumeImport);
        await using var connection = await host.Services.GetRequiredService<SqliteConnectionFactory>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ImportSessions;";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Theory]
    [InlineData(false, "第 1 列", 3)]
    [InlineData(true, "https://pan.baidu.com/s/header-value", 1)]
    public async Task HeaderOverrideSnapshotFlowsFromPresenterThroughWorkflowIntoDatabase(
        bool firstRowIsHeader,
        string expectedFirstHeader,
        int expectedRows)
    {
        Directory.CreateDirectory(root);
        var workbookPath = Path.Combine(root, $"header-override-{firstRowIsHeader}.xlsx");
        if (firstRowIsHeader)
        {
            WriteWorkbook(
                workbookPath,
                [
                    ["https://pan.baidu.com/s/header-value", "a1b2"],
                    ["https://pan.baidu.com/s/imported", "c3d4"],
                ],
                includeTemplateHeader: false);
        }
        else
        {
            WriteWorkbook(
                workbookPath,
                [
                    ["https://pan.baidu.com/s/one", "a1b2"],
                    ["https://pan.baidu.com/s/two", "c3d4"],
                ]);
        }
        var presenter = new HeaderOverrideMappingPresenter(firstRowIsHeader);
        using var host = AppBootstrap.CreateHost(root, services =>
            services.AddSingleton<IExcelColumnMappingPresenter>(presenter));
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var sessionId = Guid.NewGuid();

        await host.Services.GetRequiredService<IImportWorkflow>()
            .ImportExcelAsync(sessionId, workbookPath, CancellationToken.None);

        await using var unit = await host.Services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync();
        var session = await unit.Imports.GetSessionAsync(sessionId);
        var definitions = await unit.Imports.GetColumnDefinitionsAsync(sessionId);
        Assert.Equal(expectedRows, session!.TotalRows);
        Assert.Equal(presenter.LastMapping!.Fingerprint, session.ColumnMappingFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", session.SourceContentFingerprint!);
        Assert.Equal(firstRowIsHeader ? 1 : null, session.HeaderRowNumber);
        Assert.Equal(expectedFirstHeader, definitions[0].SourceHeader);
        Assert.Equal(presenter.LastMapping.SourceColumns.Select(column => column.Header), definitions.Select(column => column.SourceHeader));
        Assert.NotEqual(presenter.InitialFingerprint, presenter.LastMapping.Fingerprint);
    }

    public void Dispose()
    {
        var fullRoot = Path.GetFullPath(root);
        var expectedParent = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "BaiduBatchTransfer-LocalExcelImportIntegrationTests"));
        var prefix = expectedParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (fullRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullRoot))
            Directory.Delete(fullRoot, recursive: true);
    }

    private static void WriteWorkbook(
        string path,
        IReadOnlyList<string[]> rows,
        bool includeTemplateHeader = true)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        workbookPart.Workbook = new Workbook(new Sheets(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "导入",
        }));
        using var writer = OpenXmlWriter.Create(worksheetPart);
        writer.WriteStartElement(new Worksheet());
        writer.WriteStartElement(new SheetData());
        uint nextRow = 1;
        if (includeTemplateHeader) WriteInlineRow(writer, nextRow++, ["分享链接", "提取码"]);
        for (var index = 0; index < rows.Count; index++)
            WriteInlineRow(writer, checked((uint)index + nextRow), rows[index]);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteInlineRow(OpenXmlWriter writer, uint rowIndex, IReadOnlyList<string> values)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex });
        foreach (var value in values)
            writer.WriteElement(new Cell(new InlineString(new Text(value)))
                { DataType = CellValues.InlineString });
        writer.WriteEndElement();
    }

    private sealed class FakeDialogService(string? selectedPath) : IDialogService
    {
        public Task<bool> ConfirmAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task ShowMessageAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(selectedPath);
        }
    }

    private sealed class SequenceDialogService(params string?[] selectedPaths) : IDialogService
    {
        private readonly Queue<string?> selections = new(selectedPaths);

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task ShowMessageAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(selections.Dequeue());
        }
    }

    private static void AppendTemplateRow(string path, IReadOnlyList<string> values)
    {
        using var document = SpreadsheetDocument.Open(path, true);
        var workbookPart = document.WorkbookPart!;
        var worksheet = workbookPart.WorksheetParts.Single().Worksheet!;
        var sheetData = worksheet.GetFirstChild<SheetData>()!;
        var row = new Row { RowIndex = 2 };
        foreach (var value in values)
            row.Append(new Cell(new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve }))
                { DataType = CellValues.InlineString });
        sheetData.Append(row);
        worksheet.Save();
        workbookPart.Workbook!.Save();
    }

    private static async IAsyncEnumerable<ImportSourceRow> OneRow(ImportSourceRow row)
    {
        yield return row;
        await Task.Yield();
    }

    private sealed class ConfirmingMappingPresenter : IExcelColumnMappingPresenter
    {
        public int ConfirmationCount { get; private set; }
        public ConfirmedImportColumnMapping? LastMapping { get; private set; }

        public Task<ConfirmedImportColumnMapping?> ConfirmAsync(
            TabularWorkbookInspection inspection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfirmationCount++;
            var suggestion = new ExcelColumnSuggestionEngine().Suggest(inspection);
            LastMapping = new ConfirmedImportColumnMapping(
                suggestion.HeaderRowNumber,
                suggestion.Columns,
                inspection.Columns);
            return Task.FromResult<ConfirmedImportColumnMapping?>(LastMapping);
        }
    }

    private sealed class CancellingMappingPresenter : IExcelColumnMappingPresenter
    {
        public Task<ConfirmedImportColumnMapping?> ConfirmAsync(
            TabularWorkbookInspection inspection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConfirmedImportColumnMapping?>(null);
    }

    private sealed class MutatingMappingPresenter(Action mutate) : IExcelColumnMappingPresenter
    {
        public Task<ConfirmedImportColumnMapping?> ConfirmAsync(
            TabularWorkbookInspection inspection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suggestion = new ExcelColumnSuggestionEngine().Suggest(inspection);
            var mapping = new ConfirmedImportColumnMapping(
                suggestion.HeaderRowNumber,
                suggestion.Columns,
                inspection.Columns);
            mutate();
            return Task.FromResult<ConfirmedImportColumnMapping?>(mapping);
        }
    }

    private sealed class HeaderOverrideMappingPresenter(bool firstRowIsHeader) : IExcelColumnMappingPresenter
    {
        public string? InitialFingerprint { get; private set; }
        public ConfirmedImportColumnMapping? LastMapping { get; private set; }

        public Task<ConfirmedImportColumnMapping?> ConfirmAsync(
            TabularWorkbookInspection inspection,
            CancellationToken cancellationToken = default)
        {
            var suggestion = new ExcelColumnSuggestionEngine().Suggest(inspection);
            InitialFingerprint = new ConfirmedImportColumnMapping(
                suggestion.HeaderRowNumber,
                suggestion.Columns,
                inspection.Columns).Fingerprint;
            var viewModel = new ExcelColumnMappingViewModel(inspection, suggestion)
            {
                FirstRowIsHeader = firstRowIsHeader,
            };
            Assert.True(viewModel.TryConfirm());
            LastMapping = viewModel.ConfirmedMapping;
            return Task.FromResult(LastMapping);
        }
    }
}
