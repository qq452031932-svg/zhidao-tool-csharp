using System.Collections.ObjectModel;

namespace BaiduBatchTransfer.Core.Domain;

public sealed class TransferConfirmationQuery : IEquatable<TransferConfirmationQuery>
{
    public TransferConfirmationQuery(
        string sourceShareId,
        RemotePath targetPath,
        string expectedOuterNameFingerprint,
        string operationFingerprint,
        DateTimeOffset submitWindowStart,
        DateTimeOffset submitWindowEnd,
        string? adapterRequestId = null,
        IEnumerable<string>? remoteItemIds = null)
    {
        SourceShareId = RequireText(sourceShareId, nameof(sourceShareId));
        TargetPath = targetPath ?? throw new ArgumentNullException(nameof(targetPath));
        ExpectedOuterNameFingerprint = RequireText(
            expectedOuterNameFingerprint,
            nameof(expectedOuterNameFingerprint));
        OperationFingerprint = RequireText(operationFingerprint, nameof(operationFingerprint));
        SubmitWindowStartUtc = submitWindowStart.ToUniversalTime();
        SubmitWindowEndUtc = submitWindowEnd.ToUniversalTime();

        if (SubmitWindowEndUtc < SubmitWindowStartUtc)
        {
            throw new ArgumentException(
                "The submit window end cannot precede its start.",
                nameof(submitWindowEnd));
        }

        AdapterRequestId = NormalizeOptional(adapterRequestId);
        RemoteItemIds = SnapshotIdentifiers(remoteItemIds);
    }

    public string SourceShareId { get; }

    public RemotePath TargetPath { get; }

    public string ExpectedOuterNameFingerprint { get; }

    public string OperationFingerprint { get; }

    public DateTimeOffset SubmitWindowStartUtc { get; }

    public DateTimeOffset SubmitWindowEndUtc { get; }

    public string? AdapterRequestId { get; }

    public IReadOnlyList<string> RemoteItemIds { get; }

    public bool Equals(TransferConfirmationQuery? other) =>
        other is not null &&
        SourceShareId == other.SourceShareId &&
        TargetPath == other.TargetPath &&
        ExpectedOuterNameFingerprint == other.ExpectedOuterNameFingerprint &&
        OperationFingerprint == other.OperationFingerprint &&
        SubmitWindowStartUtc == other.SubmitWindowStartUtc &&
        SubmitWindowEndUtc == other.SubmitWindowEndUtc &&
        AdapterRequestId == other.AdapterRequestId &&
        RemoteItemIds.SequenceEqual(other.RemoteItemIds, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as TransferConfirmationQuery);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SourceShareId, StringComparer.Ordinal);
        hash.Add(TargetPath);
        hash.Add(ExpectedOuterNameFingerprint, StringComparer.Ordinal);
        hash.Add(OperationFingerprint, StringComparer.Ordinal);
        hash.Add(SubmitWindowStartUtc);
        hash.Add(SubmitWindowEndUtc);
        hash.Add(AdapterRequestId, StringComparer.Ordinal);
        foreach (var remoteItemId in RemoteItemIds)
        {
            hash.Add(remoteItemId, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> SnapshotIdentifiers(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var snapshot = values
            .Select(value => RequireText(value, nameof(values)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new ReadOnlyCollection<string>(snapshot);
    }
}
