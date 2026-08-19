using System.Collections.ObjectModel;

namespace BaiduBatchTransfer.Core.Domain;

public sealed class TransferReceipt
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
