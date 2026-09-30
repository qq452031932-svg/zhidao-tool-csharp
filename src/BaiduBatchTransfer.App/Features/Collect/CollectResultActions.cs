using System.IO;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.App.Features.Collect;

public interface ICollectResultActions
{
    Task<string> CopyLinksAsync(IReadOnlyList<CollectedRow> rows);
    Task<string> ExportAsync(IReadOnlyList<CollectedRow> rows);
    Task<string> SendToTransferAsync(IReadOnlyList<CollectedRow> rows);
}

/// <summary>结果使用统一工作簿格式，导出和转存导入保留同一份名称、链接、提取码。</summary>
public sealed class CollectResultActions(
    ITextClipboard clipboard,
    IResultSaveDialog saveDialog,
    IDialogService dialogs,
    ImportViewModel import,
    Func<bool> canReceiveImport,
    Action showTransfer,
    string? importDirectory = null) : ICollectResultActions
{
    public async Task<string> CopyLinksAsync(IReadOnlyList<CollectedRow> rows)
    {
        await clipboard.SetTextAsync(string.Join(Environment.NewLine, rows.Select(row =>
            string.IsNullOrWhiteSpace(row.Pwd) || LinkContainsPassword(row.Link, row.Pwd)
                ? row.Link : $"{row.Link} 提取码：{row.Pwd}")));
        return $"已复制 {rows.Count} 条链接（含已有提取码）。";
    }

    private static bool LinkContainsPassword(string link, string password)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;
        var values = uri.Query.TrimStart('?').Split('&')
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == "pwd")
            .Select(parts => Uri.UnescapeDataString(parts[1]))
            .ToArray();
        return values.Length == 1 && string.Equals(values[0], password, StringComparison.Ordinal);
    }

    public async Task<string> ExportAsync(IReadOnlyList<CollectedRow> rows)
    {
        var directory = await saveDialog.PickDestinationDirectoryAsync();
        if (string.IsNullOrWhiteSpace(directory)) return "已取消导出，采集结果仍保留。";
        var path = await Task.Run(() => WriteWorkbook(directory, rows));
        return $"已导出 {rows.Count} 条：{path}";
    }

    public async Task<string> SendToTransferAsync(IReadOnlyList<CollectedRow> rows)
    {
        if (!CanReceive()) return "批量转存暂不能接收新数据，请先处理当前任务或清空当前导入。";
        var previousSession = import.SessionId;
        var previousText = import.TextContent;
        if ((previousSession is not null || !string.IsNullOrWhiteSpace(previousText)) &&
            !await dialogs.ConfirmAsync("送入批量转存", $"批量转存已有导入内容。是否切换为本次采集的 {rows.Count} 条结果？"))
            return "已取消送入批量转存，原导入内容和采集结果均保留。";
        bool Unchanged() => CanReceive() && import.SessionId == previousSession && import.TextContent == previousText;
        if (!Unchanged()) return "批量转存状态已变化，请稍后重试。";
        // 导入中断后仍需从同一个文件继续，因此保留工作簿，不使用短期临时文件。
        var directory = importDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BaiduBatchTransfer", "collect-imports");
        var path = await Task.Run(() => WriteWorkbook(directory, rows));
        if (!Unchanged()) return "批量转存状态已变化，尚未替换导入内容，请稍后重试。";
        showTransfer();
        await import.ImportExcelAsync(path, preserveCurrentOnFailure: true);
        return $"批量转存：{import.StatusMessage}";
    }

    private bool CanReceive() => import.CanImport && canReceiveImport();

    private static string WriteWorkbook(string directory, IReadOnlyList<CollectedRow> rows)
    {
        if (rows.Count is 0 or > 1_048_575) throw new InvalidOperationException("导出条数必须在 1 到 1048575 之间。");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"知道采集_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.xlsx");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
            var workbook = document.AddWorkbookPart();
            var sheet = workbook.AddNewPart<WorksheetPart>();
            workbook.Workbook = new Workbook(new Sheets(new Sheet
            {
                Id = workbook.GetIdOfPart(sheet), SheetId = 1, Name = "采集结果"
            }));
            using var writer = OpenXmlWriter.Create(sheet);
            writer.WriteStartElement(new Worksheet());
            writer.WriteStartElement(new SheetData());
            WriteRow(writer, 1, ["资源名称", "资源链接", "提取码", "来源"]);
            uint index = 1;
            foreach (var row in rows) WriteRow(writer, ++index, [row.Name, row.Link, row.Pwd, row.Source]);
            writer.WriteEndElement();
            writer.WriteEndElement();
            return path;
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void WriteRow(OpenXmlWriter writer, uint index, IEnumerable<string> values)
    {
        writer.WriteStartElement(new Row { RowIndex = index });
        foreach (var value in values)
            writer.WriteElement(new Cell(new InlineString(new Text(ExcelTextSanitizer.Sanitize(value))
            { Space = SpaceProcessingModeValues.Preserve })) { DataType = CellValues.InlineString });
        writer.WriteEndElement();
    }
}
