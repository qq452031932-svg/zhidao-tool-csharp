using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed record ResultAccountColumnLayout(
    Guid AccountId,
    int OutputOrdinal,
    string OutputLabel,
    string ShareUrlHeader,
    string ExtractionCodeHeader,
    string ShareStatusHeader)
{
    public override string ToString() =>
        $"ResultAccountColumnLayout {{ Identity = [REDACTED], OutputOrdinal = {OutputOrdinal}, OutputLabel = [REDACTED], Headers = [REDACTED] }}";
}

public sealed class ResultColumnLayout
{
    internal ResultColumnLayout(
        IReadOnlyList<string> sourceHeaders,
        string resolvedTitleHeader,
        string originalShareUrlHeader,
        IReadOnlyList<ResultAccountColumnLayout> accounts,
        string fingerprint)
    {
        SourceHeaders = sourceHeaders.ToImmutableArray();
        ResolvedTitleHeader = resolvedTitleHeader;
        OriginalShareUrlHeader = originalShareUrlHeader;
        Accounts = accounts.ToImmutableArray();
        Fingerprint = fingerprint;
    }

    public ImmutableArray<string> SourceHeaders { get; }
    public string ResolvedTitleHeader { get; }
    public string OriginalShareUrlHeader { get; }
    public ImmutableArray<ResultAccountColumnLayout> Accounts { get; }
    public int ColumnCount => checked(SourceHeaders.Length + 2 + Accounts.Length * 3);
    public string Fingerprint { get; }

    public override string ToString() =>
        $"ResultColumnLayout {{ SourceColumns = {SourceHeaders.Length}, Accounts = {Accounts.Length}, ColumnCount = {ColumnCount}, Headers = [REDACTED], Fingerprint = [REDACTED] }}";
}

public sealed partial class ResultColumnLayoutBuilder
{
    public const int ExcelMaximumColumnCount = 16_384;
    private const string ResolvedTitleBase = "转存结果_解析后标题";
    private const string OriginalShareUrlBase = "转存结果_原分享链接";

    public ResultColumnLayout Build(
        IReadOnlyList<string?> sourceHeaders,
        IReadOnlyList<BatchWorkloadAccountInput> accounts)
    {
        ArgumentNullException.ThrowIfNull(sourceHeaders);
        ArgumentNullException.ThrowIfNull(accounts);
        var selected = accounts.Where(account => account.IsSelected)
            .OrderBy(account => account.PriorityOrder)
            .ToArray();
        if (selected.Length == 0)
            throw new BatchDraftValidationException("请至少选择一个输出账号。");
        var required = checked((long)sourceHeaders.Count + 2L + 3L * selected.Length);
        if (required > ExcelMaximumColumnCount)
            throw new BatchDraftValidationException("结果列数超过 Excel 的 16,384 列限制，请减少原始列或参与账号。");

        var headers = sourceHeaders.Select(header => header ?? string.Empty).ToArray();
        var used = new HashSet<string>(headers, StringComparer.Ordinal);
        var resolved = UniqueSystemHeader(ResolvedTitleBase, used);
        var original = UniqueSystemHeader(OriginalShareUrlBase, used);
        var labels = CreateSafeAccountLabels(selected.Select(account => account.DisplayName).ToArray());
        var output = new List<ResultAccountColumnLayout>(selected.Length);
        for (var index = 0; index < selected.Length; index++)
        {
            var label = labels[index];
            output.Add(new(
                selected[index].AccountId,
                index + 1,
                label,
                UniqueSystemHeader($"转存结果_{label}_新分享链接", used),
                UniqueSystemHeader($"转存结果_{label}_提取码", used),
                UniqueSystemHeader($"转存结果_{label}_分享状态", used)));
        }

        var canonical = string.Join('\n', headers.Append(resolved).Append(original)
            .Concat(output.SelectMany(account => new[]
            {
                account.OutputOrdinal.ToString(CultureInfo.InvariantCulture),
                account.OutputLabel,
                account.ShareUrlHeader,
                account.ExtractionCodeHeader,
                account.ShareStatusHeader,
            })));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(headers, resolved, original, output, fingerprint);
    }

    internal static ImmutableArray<string> CreateSafeAccountLabels(IReadOnlyList<string> displayNames)
    {
        var sanitized = displayNames.Select((name, index) => SanitizeAccountLabel(name, index + 1)).ToArray();
        var duplicates = sanitized.GroupBy(value => value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        return sanitized.Select(value =>
        {
            var candidate = value;
            if (duplicates.Contains(value))
            {
                var ordinal = ordinals.TryGetValue(value, out var current) ? current + 1 : 1;
                ordinals[value] = ordinal;
                candidate = $"{value}（{ordinal}）";
            }
            if (used.Add(candidate)) return candidate;
            for (var collisionOrdinal = 2; ; collisionOrdinal++)
            {
                var collisionCandidate = $"{candidate}（{collisionOrdinal}）";
                if (used.Add(collisionCandidate)) return collisionCandidate;
            }
        }).ToImmutableArray();
    }

    private static string UniqueSystemHeader(string basis, HashSet<string> used)
    {
        if (used.Add(basis)) return basis;
        for (var ordinal = 2; ; ordinal++)
        {
            var candidate = $"{basis}（系统{ordinal}）";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static string SanitizeAccountLabel(string? value, int ordinal)
    {
        var text = (value ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        if (text.Length == 0) return $"账号{ordinal}";
        var canonical = new StringBuilder(Math.Min(text.Length, 40));
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or
                UnicodeCategory.Surrogate or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                continue;
            canonical.Append(rune.ToString());
            if (canonical.Length >= 40) break;
        }
        text = canonical.ToString().Trim();
        if (text.Length == 0 || UnsafeAccountText().IsMatch(text) || GuidText().IsMatch(text) || LooksLikePath(text))
            return $"账号{ordinal}";
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\\' or '/' or ':' or '*' or '?' or '[' or ']') continue;
            builder.Append(rune.ToString());
        }
        text = builder.ToString().Trim();
        if (UnsafeAccountText().IsMatch(text) || GuidText().IsMatch(text) || LooksLikePath(text))
            return $"账号{ordinal}";
        return text.Length == 0 ? $"账号{ordinal}" : text;
    }

    private static bool LooksLikePath(string value) =>
        value.StartsWith("/", StringComparison.Ordinal) ||
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'));

    [GeneratedRegex(@"(?i)(?:https?://|pan\.baidu\.|cookie|token|secret|password|passwd|authorization|bearer)", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeAccountText();

    [GeneratedRegex(@"(?i)^(?:[0-9a-f]{32}|[{(]?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}[)}]?)$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidText();
}
