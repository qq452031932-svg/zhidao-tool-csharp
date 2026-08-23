using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Infrastructure.Excel;

public static class ExcelTextSanitizer
{
    public static string Sanitize(string? value) => SpreadsheetTextSanitizer.Sanitize(value);
}
