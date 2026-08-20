using System.Globalization;
using System.Text;

namespace BaiduBatchTransfer.Core.Domain;

public sealed record AdapterCapability
{
    public AdapterCapability(
        bool transferValidated,
        bool shareValidated,
        string safeReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeReason);
        if (safeReason.Length > 200 || ContainsUnsafeUnicode(safeReason))
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

    private static bool ContainsUnsafeUnicode(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator)
            {
                return true;
            }
        }

        return false;
    }
}
