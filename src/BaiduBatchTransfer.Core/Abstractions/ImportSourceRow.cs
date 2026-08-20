using System.Collections.Immutable;

namespace BaiduBatchTransfer.Core.Abstractions;

public sealed record ImportSourceRow
{
    public ImportSourceRow(
        int SourceRowNumber,
        string ShareUrl,
        string? ExtractionCode = null,
        string? TargetPath = null,
        string? CreateFolderName = null,
        string? RequestedAccountName = null,
        Guid? MappedAccountId = null,
        string? Note = null,
        string? ValidationError = null,
        string? SourceTitle = null,
        string? OriginalExtractedUrl = null,
        IEnumerable<string>? RawValues = null)
    {
        if (SourceRowNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SourceRowNumber));
        }

        this.SourceRowNumber = SourceRowNumber;
        this.ShareUrl = ShareUrl ?? string.Empty;
        this.ExtractionCode = EmptyToNull(ExtractionCode);
        this.TargetPath = EmptyToNull(TargetPath);
        this.CreateFolderName = EmptyToNull(CreateFolderName);
        this.RequestedAccountName = EmptyToNull(RequestedAccountName);
        this.MappedAccountId = MappedAccountId;
        this.Note = EmptyToNull(Note);
        this.ValidationError = EmptyToNull(ValidationError);
        this.SourceTitle = EmptyToNull(SourceTitle);
        this.OriginalExtractedUrl = EmptyToNull(OriginalExtractedUrl);
        this.RawValues = (RawValues ?? []).Select(value => value ?? string.Empty).ToImmutableArray();
    }

    public int SourceRowNumber { get; }
    public string ShareUrl { get; }
    public string? ExtractionCode { get; }
    public string? TargetPath { get; }
    public string? CreateFolderName { get; }
    public string? RequestedAccountName { get; }
    public Guid? MappedAccountId { get; }
    public string? Note { get; }
    public string? ValidationError { get; }
    public string? SourceTitle { get; }
    public string? OriginalExtractedUrl { get; }
    public IReadOnlyList<string> RawValues { get; }

    public override string ToString() =>
        $"ImportSourceRow {{ SourceRowNumber = {SourceRowNumber}, UserContent = [REDACTED], ValidationError = {(ValidationError is null ? "none" : "present")} }}";

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
