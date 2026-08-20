namespace BaiduBatchTransfer.Core.Domain;

public sealed record AdapterCapability
{
    public AdapterCapability(
        bool transferValidated,
        bool shareValidated,
        string safeReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeReason);
        if (safeReason.Length > 200 || safeReason.Any(char.IsControl))
            throw new ArgumentException("The safe capability reason is invalid.", nameof(safeReason));

        TransferValidated = transferValidated;
        ShareValidated = shareValidated;
        SafeReason = safeReason.Trim();
    }

    public bool TransferValidated { get; }

    public bool ShareValidated { get; }

    public string SafeReason { get; }

    public override string ToString() =>
        $"AdapterCapability {{ TransferValidated = {TransferValidated}, ShareValidated = {ShareValidated}, SafeReason = [REDACTED] }}";
}
