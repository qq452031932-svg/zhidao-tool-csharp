using BaiduBatchTransfer.Application.Jobs;

namespace BaiduBatchTransfer.Infrastructure.Excel;

public sealed class OpenXmlStandaloneShareResultWriter : IReplacementWorkbookWriter
{
    private readonly OpenXmlReplacementResultWriter inner = new();

    public Task WriteAsync(
        Stream output,
        ReplacementWorkbookLayout layout,
        IAsyncEnumerable<ReplacementWorkbookRow> rows,
        CancellationToken cancellationToken = default) =>
        inner.WriteAsync(output, layout, rows, cancellationToken);
}
