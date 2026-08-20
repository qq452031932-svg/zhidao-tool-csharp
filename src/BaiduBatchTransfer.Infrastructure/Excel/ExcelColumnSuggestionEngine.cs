using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Infrastructure.Excel;

public sealed class ExcelColumnSuggestionEngine
{
    private static readonly HashSet<string> HeaderIndicators = new(StringComparer.OrdinalIgnoreCase)
    {
        "源标题", "标题", "名称", "资源名称", "source title", "title", "name",
        "分享链接", "原分享链接", "链接", "share url", "share link", "url", "link",
        "提取码", "访问码", "extraction code", "code",
        "目标目录", "目标路径", "target path",
        "外层目录", "创建目录名称", "outer folder",
        "指定账号", "账号", "account",
        "备注", "note",
    };

    private static readonly HashSet<string> LinkHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "分享链接", "原分享链接", "链接", "share url", "share link", "url", "link",
    };

    private static readonly HashSet<string> TitleHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "源标题", "标题", "名称", "资源名称", "source title", "title", "name",
    };

    private static readonly HashSet<string> CodeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "提取码", "访问码", "extraction code", "code",
    };

    private static readonly HashSet<string> TargetPathHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "目标目录", "目标路径", "target path",
    };

    private static readonly HashSet<string> OuterFolderHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "外层目录", "创建目录名称", "outer folder",
    };

    private static readonly HashSet<string> AccountHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "指定账号", "账号", "account",
    };

    private static readonly HashSet<string> NoteHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "备注", "note",
    };

    private readonly BaiduShareUrlExtractor extractor = new();

    public ImportColumnMappingSuggestion Suggest(TabularWorkbookInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var linkColumn = FindLinkColumn(inspection);
        var titleColumn = linkColumn is null ? null : FindTitleColumn(inspection, linkColumn.Value);
        var codeColumn = FindHeaderColumn(inspection, CodeHeaders, excluded: linkColumn);
        var targetPathColumn = FindHeaderColumn(inspection, TargetPathHeaders, excluded: linkColumn);
        var outerFolderColumn = FindHeaderColumn(inspection, OuterFolderHeaders, excluded: linkColumn);
        var accountColumn = FindHeaderColumn(inspection, AccountHeaders, excluded: linkColumn);
        var noteColumn = FindHeaderColumn(inspection, NoteHeaders, excluded: linkColumn);
        var columns = inspection.Columns.Select(column => new TabularImportColumnMapping(
            column.SourceColumnIndex,
            column.SourceColumnIndex == linkColumn ? TabularImportColumnRole.ShareUrl :
            column.SourceColumnIndex == titleColumn ? TabularImportColumnRole.SourceTitle :
            column.SourceColumnIndex == codeColumn ? TabularImportColumnRole.ExtractionCode :
            column.SourceColumnIndex == targetPathColumn ? TabularImportColumnRole.TargetPath :
            column.SourceColumnIndex == outerFolderColumn ? TabularImportColumnRole.OuterFolderName :
            column.SourceColumnIndex == accountColumn ? TabularImportColumnRole.RequestedAccountName :
            column.SourceColumnIndex == noteColumn ? TabularImportColumnRole.Note :
            TabularImportColumnRole.Preserve));
        return new ImportColumnMappingSuggestion(inspection.HeaderRowNumber, columns);
    }

    internal static bool ContainsKnownHeader(IEnumerable<string> values) =>
        values.Select(NormalizeHeader).Any(HeaderIndicators.Contains);

    private int? FindLinkColumn(TabularWorkbookInspection inspection)
    {
        var headerMatch = FindHeaderColumn(inspection, LinkHeaders);
        if (headerMatch is not null) return headerMatch;

        return inspection.Columns
            .Select(column => new
            {
                column.SourceColumnIndex,
                Hits = inspection.SampleRows.Count(row =>
                    column.SourceColumnIndex < row.RawValues.Count &&
                    extractor.ExtractSupportedUrl(row.RawValues[column.SourceColumnIndex]) is not null),
            })
            .Where(candidate => candidate.Hits > 0)
            .OrderByDescending(candidate => candidate.Hits)
            .ThenBy(candidate => candidate.SourceColumnIndex)
            .Select(candidate => (int?)candidate.SourceColumnIndex)
            .FirstOrDefault();
    }

    private int? FindTitleColumn(TabularWorkbookInspection inspection, int linkColumn)
    {
        var headerMatch = FindHeaderColumn(inspection, TitleHeaders, excluded: linkColumn);
        if (headerMatch is not null) return headerMatch;

        for (var column = linkColumn - 1; column >= 0; column--)
        {
            if (inspection.SampleRows.Any(row =>
                column < row.RawValues.Count &&
                !string.IsNullOrWhiteSpace(row.RawValues[column]) &&
                extractor.ExtractSupportedUrl(row.RawValues[column]) is null))
            {
                return column;
            }
        }
        return null;
    }

    private static int? FindHeaderColumn(
        TabularWorkbookInspection inspection,
        IReadOnlySet<string> aliases,
        int? excluded = null) =>
        inspection.Columns
            .Where(column => column.SourceColumnIndex != excluded && column.Header is not null)
            .OrderBy(column => column.SourceColumnIndex)
            .FirstOrDefault(column => aliases.Contains(NormalizeHeader(column.Header!)))
            ?.SourceColumnIndex;

    private static string NormalizeHeader(string value) =>
        string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
