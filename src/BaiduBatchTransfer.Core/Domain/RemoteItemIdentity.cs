using System.Globalization;
using System.Text;

namespace BaiduBatchTransfer.Core.Domain;

public sealed class RemoteItemIdentity : IEquatable<RemoteItemIdentity>
{
    private const int MaximumDisplayedNameLength = 256;

    public RemoteItemIdentity(string exactId, string? displayedTopLevelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactId);
        ExactId = exactId;
        DisplayedTopLevelName = SanitizeDisplayName(displayedTopLevelName);
    }

    public string ExactId { get; }

    public string DisplayedTopLevelName { get; }

    public bool Equals(RemoteItemIdentity? other) =>
        other is not null && string.Equals(ExactId, other.ExactId, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as RemoteItemIdentity);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ExactId);

    public static bool operator ==(RemoteItemIdentity? left, RemoteItemIdentity? right) =>
        object.Equals(left, right);

    public static bool operator !=(RemoteItemIdentity? left, RemoteItemIdentity? right) =>
        !object.Equals(left, right);

    public override string ToString() =>
        "RemoteItemIdentity { ExactId = [REDACTED], DisplayedTopLevelName = [REDACTED] }";

    private static string SanitizeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "未命名资源";

        var builder = new StringBuilder(Math.Min(value.Length, MaximumDisplayedNameLength));
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            var text = rune.ToString();
            if (builder.Length + text.Length > MaximumDisplayedNameLength) break;
            builder.Append(text);
        }

        var sanitized = builder.ToString().Trim();
        return sanitized.Length == 0 ? "未命名资源" : sanitized;
    }

}
