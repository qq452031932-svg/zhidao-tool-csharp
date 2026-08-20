using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed partial class ResolvedOutputTitleBuilder
{
    public const string FallbackTitle = "未命名资源";

    public string Build(string? sourceTitle, IReadOnlyList<string?> displayedTopLevelNames) =>
        Resolve(sourceTitle, displayedTopLevelNames);

    public static string Resolve(string? sourceTitle, IReadOnlyList<string?> displayedTopLevelNames)
    {
        if (!string.IsNullOrWhiteSpace(sourceTitle)) return sourceTitle;
        ArgumentNullException.ThrowIfNull(displayedTopLevelNames);
        if (displayedTopLevelNames.Count == 0) return FallbackTitle;
        var first = Sanitize(displayedTopLevelNames[0]);
        return displayedTopLevelNames.Count == 1
            ? first
            : $"{first} 等 {displayedTopLevelNames.Count.ToString(CultureInfo.InvariantCulture)} 项";
    }

    private static string Sanitize(string? value)
    {
        var text = (value ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        if (text.Length == 0) return FallbackTitle;
        var builder = new StringBuilder(Math.Min(text.Length, 120));
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsUnsafeRune(rune)) continue;
            builder.Append(rune.ToString());
            if (builder.Length >= 120) break;
        }
        text = builder.ToString().Trim();
        return text.Length == 0 || UnsafeTitleText().IsMatch(text) || GuidText().IsMatch(text) || LooksLikePath(text)
            ? FallbackTitle
            : text;
    }

    private static bool IsUnsafeRune(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or
            UnicodeCategory.Surrogate or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    }

    private static bool LooksLikePath(string value) =>
        value.StartsWith("/", StringComparison.Ordinal) ||
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'));

    [GeneratedRegex(@"(?i)(?:https?://|pan\.baidu\.|cookie|token|secret|password|passwd|authorization|bearer)", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeTitleText();

    [GeneratedRegex(@"(?i)^(?:[0-9a-f]{32}|[{(]?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}[)}]?)$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidText();
}
