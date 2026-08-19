using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Application.Imports;

public sealed class ImportConflictService
{
    private readonly DuplicateClassifier classifier = new();
    private readonly BaiduPathNormalizer pathNormalizer = new();

    public DuplicateClassification Classify(
        TransferMode mode,
        StagedImportRowRecord first,
        StagedImportRowRecord incoming) =>
        classifier.Classify(mode, ToCandidate(first), ToCandidate(incoming));

    private DuplicateCandidate ToCandidate(StagedImportRowRecord row) => new(
        row.NormalizedShareId ?? throw new ArgumentException("A classified row requires a normalized share id.", nameof(row)),
        row.ExtractionCode,
        NormalizePath(row.TargetPath),
        row.CreateFolderName,
        row.RequestedAccountName,
        row.MappedAccountId,
        row.Note);

    private string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : pathNormalizer.Normalize(path).ComparisonValue;
}
