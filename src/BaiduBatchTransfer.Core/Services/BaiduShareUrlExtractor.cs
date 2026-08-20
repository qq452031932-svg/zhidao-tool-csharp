using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.Core.Services;

public sealed partial class BaiduShareUrlExtractor
{
    private const string TrailingPunctuation =
        ".,;!?:')\"]}>\u2019\u201D\u3002\uFF0C\uFF1A\uFF1B\uFF01\uFF1F\uFF09\u3011\u300B\u300D\u300F\u3015\u3017\u3019\u301B";

    private readonly BaiduShareNormalizer normalizer = new();

    public string? ExtractSupportedUrl(string? cellValue)
    {
        if (string.IsNullOrEmpty(cellValue)) return null;
        foreach (Match match in CandidatePattern().Matches(cellValue))
        {
            var candidate = TrimTrailingPunctuation(match.Value);
            try
            {
                _ = normalizer.Normalize(candidate);
                return candidate;
            }
            catch (ArgumentException)
            {
                // Continue looking for a later supported URL without exposing cell content.
            }
        }
        return null;
    }

    public static string? NormalizeExtractionCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string TrimTrailingPunctuation(string value)
    {
        var length = value.Length;
        while (length > 0 && TrailingPunctuation.Contains(value[length - 1], StringComparison.Ordinal)) length--;
        return value[..length];
    }

    [GeneratedRegex(@"https://(?:pan|yun)\.baidu\.com/[^\s<>\""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CandidatePattern();
}
