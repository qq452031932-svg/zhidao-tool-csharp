using System.Globalization;
using System.Text;

namespace BaiduBatchTransfer.Core.Domain;

public sealed record AdapterCapabilityVersion
{
    public AdapterCapabilityVersion(
        string adapterKind,
        string protocolVersion,
        string selectorVersion,
        string buildVersion)
    {
        AdapterKind = Validate(adapterKind, nameof(adapterKind));
        ProtocolVersion = Validate(protocolVersion, nameof(protocolVersion));
        SelectorVersion = Validate(selectorVersion, nameof(selectorVersion));
        BuildVersion = Validate(buildVersion, nameof(buildVersion));
    }

    public string AdapterKind { get; }
    public string ProtocolVersion { get; }
    public string SelectorVersion { get; }
    public string BuildVersion { get; }

    public override string ToString() =>
        $"AdapterCapabilityVersion {{ AdapterKind = {AdapterKind}, Versions = [REDACTED] }}";

    private static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > 100 || ContainsUnsafeUnicode(normalized))
            throw new ArgumentException("The adapter capability key is invalid.", parameterName);
        return normalized;
    }

    private static bool ContainsUnsafeUnicode(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator)
                return true;
        }
        return false;
    }
}

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
