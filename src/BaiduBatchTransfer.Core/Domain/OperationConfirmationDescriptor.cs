namespace BaiduBatchTransfer.Core.Domain;

public sealed class OperationConfirmationDescriptor
{
    public OperationConfirmationDescriptor(
        string sourceShareId,
        RemotePath targetPath,
        string expectedOuterNameFingerprint,
        string operationFingerprint,
        DateTimeOffset submitWindowStart,
        DateTimeOffset submitWindowEnd,
        string? adapterRequestId = null,
        IEnumerable<string>? remoteItemIds = null)
    {
        var validated = new TransferConfirmationQuery(
            sourceShareId,
            targetPath,
            expectedOuterNameFingerprint,
            operationFingerprint,
            submitWindowStart,
            submitWindowEnd,
            adapterRequestId,
            remoteItemIds);

        SourceShareId = validated.SourceShareId;
        TargetPath = validated.TargetPath;
        ExpectedOuterNameFingerprint = validated.ExpectedOuterNameFingerprint;
        OperationFingerprint = validated.OperationFingerprint;
        SubmitWindowStartUtc = validated.SubmitWindowStartUtc;
        SubmitWindowEndUtc = validated.SubmitWindowEndUtc;
        AdapterRequestId = validated.AdapterRequestId;
        RemoteItemIds = validated.RemoteItemIds;
    }

    public string SourceShareId { get; }

    public RemotePath TargetPath { get; }

    public string ExpectedOuterNameFingerprint { get; }

    public string OperationFingerprint { get; }

    public DateTimeOffset SubmitWindowStartUtc { get; }

    public DateTimeOffset SubmitWindowEndUtc { get; }

    public string? AdapterRequestId { get; }

    public IReadOnlyList<string> RemoteItemIds { get; }

    public OperationConfirmationDescriptor WithResponseIdentifiers(
        string? adapterRequestId,
        IEnumerable<string>? remoteItemIds)
    {
        var normalizedRequestId = string.IsNullOrWhiteSpace(adapterRequestId)
            ? null
            : adapterRequestId.Trim();
        if (AdapterRequestId is not null &&
            normalizedRequestId is not null &&
            !string.Equals(AdapterRequestId, normalizedRequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An adapter request ID cannot be replaced once recorded.");
        }

        var mergedRemoteItemIds = RemoteItemIds.Concat(remoteItemIds ?? []);
        return new OperationConfirmationDescriptor(
            SourceShareId,
            TargetPath,
            ExpectedOuterNameFingerprint,
            OperationFingerprint,
            SubmitWindowStartUtc,
            SubmitWindowEndUtc,
            AdapterRequestId ?? normalizedRequestId,
            mergedRemoteItemIds);
    }

    public TransferConfirmationQuery ToQuery() => new(
        SourceShareId,
        TargetPath,
        ExpectedOuterNameFingerprint,
        OperationFingerprint,
        SubmitWindowStartUtc,
        SubmitWindowEndUtc,
        AdapterRequestId,
        RemoteItemIds);
}
