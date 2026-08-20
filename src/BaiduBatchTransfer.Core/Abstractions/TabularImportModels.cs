using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.Core.Abstractions;

public enum TabularImportColumnRole
{
    Preserve,
    SourceTitle,
    ShareUrl,
    ExtractionCode,
}

public sealed record TabularImportColumnMapping
{
    public TabularImportColumnMapping(int sourceColumnIndex, TabularImportColumnRole role)
    {
        if (sourceColumnIndex < 0) throw new ArgumentOutOfRangeException(nameof(sourceColumnIndex));
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        SourceColumnIndex = sourceColumnIndex;
        Role = role;
    }

    public int SourceColumnIndex { get; }

    public TabularImportColumnRole Role { get; }
}

public sealed record TabularInspectionColumn
{
    public TabularInspectionColumn(int sourceColumnIndex, string? header)
    {
        if (sourceColumnIndex < 0) throw new ArgumentOutOfRangeException(nameof(sourceColumnIndex));
        SourceColumnIndex = sourceColumnIndex;
        Header = header;
    }

    public int SourceColumnIndex { get; }

    public string? Header { get; }

    public override string ToString() =>
        $"TabularInspectionColumn {{ SourceColumnIndex = {SourceColumnIndex}, Header = [REDACTED] }}";
}

public sealed record TabularInspectionRow
{
    public TabularInspectionRow(int sourceRowNumber, IEnumerable<string> rawValues)
    {
        if (sourceRowNumber <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRowNumber));
        ArgumentNullException.ThrowIfNull(rawValues);
        SourceRowNumber = sourceRowNumber;
        RawValues = rawValues.Select(value => value ?? string.Empty).ToImmutableArray();
    }

    public int SourceRowNumber { get; }

    public IReadOnlyList<string> RawValues { get; }

    public override string ToString() =>
        $"TabularInspectionRow {{ SourceRowNumber = {SourceRowNumber}, ColumnCount = {RawValues.Count}, UserContent = [REDACTED] }}";
}

public sealed record TabularWorkbookInspection
{
    public TabularWorkbookInspection(
        int? headerRowNumber,
        IEnumerable<TabularInspectionColumn> columns,
        IEnumerable<TabularInspectionRow> sampleRows,
        IEnumerable<TabularInspectionRow>? observedRows = null)
    {
        if (headerRowNumber <= 0) throw new ArgumentOutOfRangeException(nameof(headerRowNumber));
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(sampleRows);
        HeaderRowNumber = headerRowNumber;
        Columns = columns.ToImmutableArray();
        SampleRows = sampleRows.ToImmutableArray();
        ObservedRows = (observedRows ?? RebuildObservedRows()).ToImmutableArray();
    }

    public int? HeaderRowNumber { get; }

    public IReadOnlyList<TabularInspectionColumn> Columns { get; }

    public IReadOnlyList<TabularInspectionRow> SampleRows { get; }

    public IReadOnlyList<TabularInspectionRow> ObservedRows { get; }

    public override string ToString() =>
        $"TabularWorkbookInspection {{ HasSuggestedHeader = {HeaderRowNumber is not null}, ColumnCount = {Columns.Count}, SampleRowCount = {SampleRows.Count}, UserContent = [REDACTED] }}";

    private IEnumerable<TabularInspectionRow> RebuildObservedRows()
    {
        if (HeaderRowNumber is int headerRowNumber)
        {
            yield return new TabularInspectionRow(
                headerRowNumber,
                Columns.OrderBy(column => column.SourceColumnIndex).Select(column => column.Header ?? string.Empty));
        }
        foreach (var row in SampleRows) yield return row;
    }
}

public sealed record ImportColumnMappingSuggestion
{
    public ImportColumnMappingSuggestion(
        int? headerRowNumber,
        IEnumerable<TabularImportColumnMapping> columns)
    {
        if (headerRowNumber <= 0) throw new ArgumentOutOfRangeException(nameof(headerRowNumber));
        ArgumentNullException.ThrowIfNull(columns);
        HeaderRowNumber = headerRowNumber;
        Columns = columns.OrderBy(column => column.SourceColumnIndex).ToImmutableArray();
    }

    public int? HeaderRowNumber { get; }

    public IReadOnlyList<TabularImportColumnMapping> Columns { get; }

    public override string ToString() =>
        $"ImportColumnMappingSuggestion {{ HasSuggestedHeader = {HeaderRowNumber is not null}, ColumnCount = {Columns.Count} }}";
}

public sealed record ConfirmedImportColumnMapping
{
    public ConfirmedImportColumnMapping(
        int? headerRowNumber,
        IEnumerable<TabularImportColumnMapping> columns,
        IEnumerable<TabularInspectionColumn>? sourceColumns = null)
    {
        if (headerRowNumber <= 0) throw new ArgumentOutOfRangeException(nameof(headerRowNumber));
        ArgumentNullException.ThrowIfNull(columns);
        var ordered = columns.OrderBy(column => column.SourceColumnIndex).ToImmutableArray();
        if (ordered.Length == 0 || ordered.Select(column => column.SourceColumnIndex).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Every source column must be mapped exactly once.", nameof(columns));
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].SourceColumnIndex != index)
                throw new ArgumentException("Source-column mappings must be contiguous and include preserved columns.", nameof(columns));
        }
        if (ordered.Count(column => column.Role == TabularImportColumnRole.ShareUrl) != 1)
            throw new ArgumentException("Exactly one share-link column must be confirmed.", nameof(columns));
        if (ordered.Count(column => column.Role == TabularImportColumnRole.SourceTitle) > 1 ||
            ordered.Count(column => column.Role == TabularImportColumnRole.ExtractionCode) > 1)
            throw new ArgumentException("Title and extraction-code mappings are optional but cannot be repeated.", nameof(columns));

        HeaderRowNumber = headerRowNumber;
        Columns = ordered;
        SourceColumns = (sourceColumns ?? []).OrderBy(column => column.SourceColumnIndex).ToImmutableArray();
        if (SourceColumns.Count != 0 &&
            (SourceColumns.Count != ordered.Length ||
             SourceColumns.Select(column => column.SourceColumnIndex).Distinct().Count() != SourceColumns.Count ||
             SourceColumns.Where((column, index) => column.SourceColumnIndex != index).Any()))
        {
            throw new ArgumentException("Confirmed source columns must match the mapping snapshot.", nameof(sourceColumns));
        }
        Fingerprint = ComputeFingerprint(headerRowNumber, ordered, SourceColumns);
    }

    public int? HeaderRowNumber { get; }

    public IReadOnlyList<TabularImportColumnMapping> Columns { get; }

    public IReadOnlyList<TabularInspectionColumn> SourceColumns { get; }

    public int SourceColumnCount => Columns.Count;

    public string Fingerprint { get; }

    public int ShareUrlColumnIndex =>
        Columns.Single(column => column.Role == TabularImportColumnRole.ShareUrl).SourceColumnIndex;

    public int? SourceTitleColumnIndex => FindOptional(TabularImportColumnRole.SourceTitle);

    public int? ExtractionCodeColumnIndex => FindOptional(TabularImportColumnRole.ExtractionCode);

    public override string ToString() =>
        $"ConfirmedImportColumnMapping {{ HasHeader = {HeaderRowNumber is not null}, ColumnCount = {Columns.Count}, Fingerprint = {Fingerprint} }}";

    private int? FindOptional(TabularImportColumnRole role) =>
        Columns.FirstOrDefault(column => column.Role == role)?.SourceColumnIndex;

    private static string ComputeFingerprint(
        int? headerRowNumber,
        IEnumerable<TabularImportColumnMapping> columns,
        IReadOnlyList<TabularInspectionColumn> sourceColumns)
    {
        var canonical = new StringBuilder("tabular-import-mapping-v2\n")
            .Append(headerRowNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")
            .Append('\n');
        foreach (var column in columns)
        {
            canonical.Append(column.SourceColumnIndex)
                .Append(':')
                .Append((int)column.Role)
                .Append('\n');
        }
        canonical.Append("headers:").Append(sourceColumns.Count).Append('\n');
        foreach (var column in sourceColumns)
        {
            canonical.Append(column.SourceColumnIndex).Append(':');
            if (column.Header is null) canonical.Append("null");
            else canonical.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(column.Header)));
            canonical.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }
}
