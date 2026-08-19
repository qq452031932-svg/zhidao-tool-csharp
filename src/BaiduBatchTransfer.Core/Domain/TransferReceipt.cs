using System.Collections.ObjectModel;

namespace BaiduBatchTransfer.Core.Domain;

public sealed class TransferReceipt : IEquatable<TransferReceipt>
{
    public TransferReceipt(
        DateTimeOffset submittedAt,
        string? adapterRequestId = null,
        IEnumerable<string>? remoteItemIds = null)
    {
        SubmittedAtUtc = submittedAt.ToUniversalTime();
        AdapterRequestId = NormalizeOptional(adapterRequestId);
        RemoteItemIds = SnapshotIdentifiers(remoteItemIds);
    }

    public DateTimeOffset SubmittedAtUtc { get; }

    public string? AdapterRequestId { get; }

    public IReadOnlyList<string> RemoteItemIds { get; }

    public bool Equals(TransferReceipt? other) =>
        other is not null &&
        SubmittedAtUtc == other.SubmittedAtUtc &&
        string.Equals(AdapterRequestId, other.AdapterRequestId, StringComparison.Ordinal) &&
        RemoteItemIds.SequenceEqual(other.RemoteItemIds, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as TransferReceipt);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SubmittedAtUtc);
        hash.Add(AdapterRequestId, StringComparer.Ordinal);
        foreach (var remoteItemId in RemoteItemIds)
        {
            hash.Add(remoteItemId, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(TransferReceipt? left, TransferReceipt? right) =>
        object.Equals(left, right);

    public static bool operator !=(TransferReceipt? left, TransferReceipt? right) =>
        !object.Equals(left, right);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> SnapshotIdentifiers(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var snapshot = values
            .Select(value =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(values));
                return value.Trim();
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new ReadOnlyCollection<string>(snapshot);
    }
}
