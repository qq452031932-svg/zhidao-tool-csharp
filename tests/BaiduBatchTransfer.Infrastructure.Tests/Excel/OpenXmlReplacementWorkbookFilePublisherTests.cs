using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.Infrastructure.Tests.Excel;

public sealed class OpenXmlReplacementWorkbookFilePublisherTests
{
    private static readonly ReplacementWorkbookLayout Layout = new(["A", "B"]);

    [Fact]
    public async Task SuccessPublishesOnlyAfterCloseAndValidationWithoutChangingSource()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source.xlsx");
        await File.WriteAllBytesAsync(sourcePath, Encoding.UTF8.GetBytes("source-bytes"));
        var sourceHash = await HashAsync(sourcePath);
        const string finalFileName = "source_转存分享结果.xlsx";
        var finalPath = Path.Combine(directory.Path, finalFileName);
        var realWriter = new OpenXmlReplacementResultWriter(maxRowsPerSheet: 3);
        var writer = new DelegatingWriter(async (output, layout, rows, token) =>
        {
            Assert.False(File.Exists(finalPath));
            await realWriter.WriteAsync(output, layout, rows, token);
            Assert.False(File.Exists(finalPath));
        });

        var receipt = await new OpenXmlReplacementWorkbookFilePublisher(writer).PublishAsync(
            directory.Path,
            finalFileName,
            Layout,
            Rows(new(1, ["1a", "1b"]), new(2, ["2a", "2b"]), new(3, ["3a", "3b"])));

        Assert.True(File.Exists(finalPath));
        Assert.Equal(sourceHash, await HashAsync(sourcePath));
        Assert.Equal(Path.GetFullPath(finalPath), receipt.FinalPath);
        Assert.Equal(3, receipt.ExportedRows);
        Assert.Equal(2, receipt.SheetCount);
        Assert.DoesNotContain(directory.Path, receipt.ToString(), StringComparison.Ordinal);
        AssertNoTemporaryFiles(directory.Path);
    }

    [Fact]
    public async Task ManifestIsAtomicallyPublishedAsTheOnlyVeryHiddenMetadataSheet()
    {
        using var directory = new TemporaryDirectory();
        var operation = new LocalResultOperationEvidence(
            "BaiduTransfer",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            3,
            new string('a', 64));
        var manifest = new LocalResultWorkbookManifest(
            new LocalResultExportKey(
                LocalResultOwnerKind.TransferBatch,
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                LocalResultExportKind.ReplacementWorkbook,
                1),
            new string('b', 64),
            [operation]);

        await new OpenXmlReplacementWorkbookFilePublisher(
                new OpenXmlReplacementResultWriter())
            .PublishWithManifestAsync(
                directory.Path,
                "result.xlsx",
                Layout,
                Rows(new ReplacementWorkbookRow(1, ["1a", "1b"])),
                manifest);

        using var document = SpreadsheetDocument.Open(
            Path.Combine(directory.Path, "result.xlsx"), false);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var workbook = Assert.IsType<Workbook>(workbookPart.Workbook);
        var sheets = Assert.IsType<Sheets>(workbook.Sheets).Elements<Sheet>().ToArray();
        var metadata = Assert.Single(
            sheets, sheet => sheet.Name == LocalResultWorkbookManifest.SheetName);
        Assert.Equal(SheetStateValues.VeryHidden, metadata.State?.Value);
        Assert.Single(sheets, sheet => sheet.State?.Value == SheetStateValues.VeryHidden);
    }

    [Fact]
    public void ManifestRejectsDuplicateIdentityAndNonCanonicalFingerprint()
    {
        var key = new LocalResultExportKey(
            LocalResultOwnerKind.TransferBatch,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            LocalResultExportKind.ReplacementWorkbook,
            1);
        var operation = new LocalResultOperationEvidence(
            "BaiduTransfer",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            3,
            new string('a', 64));

        Assert.Throws<ArgumentException>(() => new LocalResultWorkbookManifest(
            key, new string('b', 64), [operation, operation with { ContentFingerprint = new string('c', 64) }]));
        Assert.Throws<ArgumentException>(() => new LocalResultWorkbookManifest(
            key, new string('B', 64), [operation]));
    }

    [Fact]
    public async Task WriterFailureLeavesNoFinalAndCleansPartialTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var finalPath = Path.Combine(directory.Path, "result.xlsx");
        var writer = new DelegatingWriter(async (output, _, _, token) =>
        {
            await output.WriteAsync(Encoding.UTF8.GetBytes("partial"), token);
            throw new IOException("writer failed");
        });

        var error = await Assert.ThrowsAsync<ReplacementWorkbookFilePublishException>(() =>
            new OpenXmlReplacementWorkbookFilePublisher(writer).PublishAsync(
                directory.Path, "result.xlsx", Layout, Rows()));

        Assert.Equal(ReplacementWorkbookFilePublishErrorCode.PublishFailed, error.Code);
        Assert.DoesNotContain(directory.Path, error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(finalPath));
        AssertNoTemporaryFiles(directory.Path);
    }

    [Fact]
    public async Task EnumerationCancellationLeavesNoFinalAndCleansTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var finalPath = Path.Combine(directory.Path, "result.xlsx");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OpenXmlReplacementWorkbookFilePublisher(new OpenXmlReplacementResultWriter()).PublishAsync(
                directory.Path,
                "result.xlsx",
                Layout,
                CancellingRows(cancellation),
                cancellation.Token));

        Assert.False(File.Exists(finalPath));
        AssertNoTemporaryFiles(directory.Path);
    }

    [Fact]
    public async Task CorruptWriterOutputFailsValidationAndCleansTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var finalPath = Path.Combine(directory.Path, "result.xlsx");
        var writer = new DelegatingWriter((output, _, _, token) =>
            output.WriteAsync(Encoding.UTF8.GetBytes("not-an-openxml-workbook"), token).AsTask());

        var error = await Assert.ThrowsAsync<ReplacementWorkbookFilePublishException>(() =>
            new OpenXmlReplacementWorkbookFilePublisher(writer).PublishAsync(
                directory.Path, "result.xlsx", Layout, Rows()));

        Assert.Equal(ReplacementWorkbookFilePublishErrorCode.InvalidWorkbook, error.Code);
        Assert.DoesNotContain(directory.Path, error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(finalPath));
        AssertNoTemporaryFiles(directory.Path);
    }

    [Theory]
    [InlineData("Formula")]
    [InlineData("Hyperlink")]
    [InlineData("ExternalRelationship")]
    [InlineData("WrongColumnCount")]
    public async Task StructurallyUnsafeWriterOutputFailsStreamingValidation(string corruption)
    {
        using var directory = new TemporaryDirectory();
        var finalPath = Path.Combine(directory.Path, "result.xlsx");
        var writer = new DelegatingWriter((output, _, _, _) =>
        {
            WriteUnsafeWorkbook(output, corruption);
            return Task.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<ReplacementWorkbookFilePublishException>(() =>
            new OpenXmlReplacementWorkbookFilePublisher(writer).PublishAsync(
                directory.Path, "result.xlsx", Layout, Rows()));

        Assert.Equal(ReplacementWorkbookFilePublishErrorCode.InvalidWorkbook, error.Code);
        Assert.False(File.Exists(finalPath));
        AssertNoTemporaryFiles(directory.Path);
    }

    [Fact]
    public async Task ExistingFinalFileRemainsByteForByteUnchangedAndTemporaryFileIsCleaned()
    {
        using var directory = new TemporaryDirectory();
        var finalPath = Path.Combine(directory.Path, "result.xlsx");
        var originalBytes = Encoding.UTF8.GetBytes("existing-final");
        await File.WriteAllBytesAsync(finalPath, originalBytes);

        var error = await Assert.ThrowsAsync<ReplacementWorkbookFilePublishException>(() =>
            new OpenXmlReplacementWorkbookFilePublisher(new OpenXmlReplacementResultWriter()).PublishAsync(
                directory.Path,
                "result.xlsx",
                Layout,
                Rows(new ReplacementWorkbookRow(1, ["1a", "1b"]))));

        Assert.Equal(ReplacementWorkbookFilePublishErrorCode.DestinationExists, error.Code);
        Assert.Equal("BaiduBatchTransfer.Application.Jobs", error.GetType().Namespace);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(finalPath));
        AssertNoTemporaryFiles(directory.Path);
    }

    [Fact]
    public async Task ShareAnalyticsLayoutPublishesFormulaLikeLinkAndTitleAsPlainText()
    {
        using var directory = new TemporaryDirectory();
        var layout = new ReplacementWorkbookLayout(
        [
            "序号", "指标", "历史累计", "新增", "今天", "昨天",
            "08-31", "08-30", "08-29", "链接", "标题", "状态",
        ]);
        const string dangerousLink = "=HYPERLINK(\"https://evil.invalid\")";
        const string dangerousTitle = "+SUM(1,1)";

        await new OpenXmlReplacementWorkbookFilePublisher(new OpenXmlReplacementResultWriter())
            .PublishAsync(
                directory.Path,
                "分享数据.xlsx",
                layout,
                Rows(new ReplacementWorkbookRow(
                    1,
                    ["1", "转存", "12", "-1", "2", "3", "", "", "", dangerousLink, dangerousTitle, "有效"])));

        using var document = SpreadsheetDocument.Open(Path.Combine(directory.Path, "分享数据.xlsx"), false);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var workbook = Assert.IsType<Workbook>(workbookPart.Workbook);
        var sheet = Assert.Single(Assert.IsType<Sheets>(workbook.Sheets).Elements<Sheet>());
        var worksheetPart = Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!));
        var worksheet = Assert.IsType<Worksheet>(worksheetPart.Worksheet);
        var dataRow = Assert.Single(worksheet.Descendants<Row>().Skip(1));
        var cells = dataRow.Elements<Cell>().ToArray();

        Assert.Equal($"'{dangerousLink}", cells[9].InlineString!.Text!.Text);
        Assert.Equal($"'{dangerousTitle}", cells[10].InlineString!.Text!.Text);
        Assert.All(cells, cell => Assert.Null(cell.CellFormula));
        Assert.Empty(worksheet.Descendants<Hyperlinks>());
        Assert.Empty(worksheetPart.HyperlinkRelationships);
        AssertNoTemporaryFiles(directory.Path);
    }

    [Fact]
    public async Task FileNameMustBeASingleLeafAndCannotEscapeDestinationDirectory()
    {
        using var directory = new TemporaryDirectory();

        var error = await Assert.ThrowsAsync<ReplacementWorkbookFilePublishException>(() =>
            new OpenXmlReplacementWorkbookFilePublisher(new OpenXmlReplacementResultWriter()).PublishAsync(
                directory.Path,
                "..\\escaped.xlsx",
                Layout,
                Rows()));

        Assert.Equal(ReplacementWorkbookFilePublishErrorCode.InvalidFileName, error.Code);
        AssertNoTemporaryFiles(directory.Path);
    }

    [Theory]
    [InlineData("bad:name.xlsx")]
    [InlineData("bad*name.xlsx")]
    [InlineData("bad?name.xlsx")]
    public async Task FileNameCannotContainPlatformInvalidCharacters(string finalFileName)
    {
        using var directory = new TemporaryDirectory();

        var error = await Assert.ThrowsAsync<ReplacementWorkbookFilePublishException>(() =>
            new OpenXmlReplacementWorkbookFilePublisher(new OpenXmlReplacementResultWriter()).PublishAsync(
                directory.Path,
                finalFileName,
                Layout,
                Rows()));

        Assert.Equal(ReplacementWorkbookFilePublishErrorCode.InvalidFileName, error.Code);
        AssertNoTemporaryFiles(directory.Path);
    }

    private static async Task<byte[]> HashAsync(string path)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await SHA256.HashDataAsync(input);
    }

    private static void WriteUnsafeWorkbook(Stream output, string corruption)
    {
        using var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook, autoSave: true);
        var workbookPart = document.AddWorkbookPart();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var firstCell = TextCell("A");
        if (corruption == "Formula") firstCell.CellFormula = new CellFormula("1+1");
        var header = corruption == "WrongColumnCount"
            ? new Row(firstCell) { RowIndex = 1 }
            : new Row(firstCell, TextCell("B")) { RowIndex = 1 };
        var worksheet = new Worksheet(new SheetData(header));
        if (corruption == "Hyperlink")
        {
            var relationship = worksheetPart.AddHyperlinkRelationship(
                new Uri("https://example.invalid/"), isExternal: true);
            worksheet.Append(new Hyperlinks(new Hyperlink { Reference = "A1", Id = relationship.Id }));
        }
        worksheetPart.Worksheet = worksheet;
        if (corruption == "ExternalRelationship")
            workbookPart.AddExternalRelationship("urn:unsafe", new Uri("https://example.invalid/"));
        workbookPart.Workbook = new Workbook(new Sheets(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "替换结果",
        }));
    }

    private static Cell TextCell(string value) => new(
        new InlineString(new Text(value)))
    {
        DataType = CellValues.InlineString,
    };

    private static void AssertNoTemporaryFiles(string directory) =>
        Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly));

    private static async IAsyncEnumerable<ReplacementWorkbookRow> Rows(
        params ReplacementWorkbookRow[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ReplacementWorkbookRow> CancellingRows(
        CancellationTokenSource cancellation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new(1, ["1a", "1b"]);
        await Task.Yield();
        cancellation.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class DelegatingWriter(
        Func<Stream, ReplacementWorkbookLayout, IAsyncEnumerable<ReplacementWorkbookRow>, CancellationToken, Task> write)
        : IReplacementWorkbookWriter
    {
        public Task WriteAsync(
            Stream output,
            ReplacementWorkbookLayout layout,
            IAsyncEnumerable<ReplacementWorkbookRow> rows,
            CancellationToken cancellationToken = default) => write(output, layout, rows, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"baidu-batch-transfer-publish-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
