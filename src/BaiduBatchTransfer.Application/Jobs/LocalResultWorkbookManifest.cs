using System.Collections.Immutable;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class LocalResultWorkbookManifest
{
    public const string Format = "bbt-local-result-manifest-v1";
    public const string SheetName = "_bbt_meta_v1";

    public LocalResultWorkbookManifest(
        LocalResultExportKey exportKey,
        string layoutFingerprint,
        IEnumerable<LocalResultOperationEvidence> operations)
    {
        exportKey.Validate();
        ValidateFingerprint(layoutFingerprint, nameof(layoutFingerprint));
        ArgumentNullException.ThrowIfNull(operations);
        var ordered = operations
            .Select(ValidateOperation)
            .OrderBy(item => item.AdapterKind, StringComparer.Ordinal)
            .ThenBy(item => item.AccountId)
            .ThenBy(item => item.OperationId)
            .ThenBy(item => item.Generation)
            .ToImmutableArray();
        if (ordered.DistinctBy(item => new
            {
                item.AdapterKind,
                item.AccountId,
                item.OperationId,
                item.Generation,
            }).Count() != ordered.Length)
            throw new ArgumentException("Manifest operation identities must be unique.", nameof(operations));

        ExportKey = exportKey;
        LayoutFingerprint = layoutFingerprint;
        Operations = ordered;
    }

    public LocalResultExportKey ExportKey { get; }
    public string LayoutFingerprint { get; }
    public ImmutableArray<LocalResultOperationEvidence> Operations { get; }

    public static bool IsFingerprint(string value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static LocalResultOperationEvidence ValidateOperation(LocalResultOperationEvidence operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.AdapterKind is not ("BaiduTransfer" or "BaiduShare"))
            throw new ArgumentException("Manifest adapter kind is invalid.", nameof(operation));
        if (operation.AccountId == Guid.Empty || operation.OperationId == Guid.Empty || operation.Generation < 1)
            throw new ArgumentException("Manifest operation identity is invalid.", nameof(operation));
        ValidateFingerprint(operation.ContentFingerprint, nameof(operation));
        return operation;
    }

    private static void ValidateFingerprint(string value, string parameterName)
    {
        if (!IsFingerprint(value))
            throw new ArgumentException("Manifest fingerprints must be canonical lowercase SHA-256 values.", parameterName);
    }
}
