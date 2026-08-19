using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Imports;

public sealed partial class TextImportParser
{
    private const string MissingLinkMessage = "No supported Baidu share link was found.";
    private const string MalformedBaiduLinkMessage = "The Baidu link is malformed or unsupported.";

    public async IAsyncEnumerable<ImportSourceRow> ParseAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var outputRowNumber = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var matches = UrlPattern().Matches(line);
            var baiduCandidates = new List<(Match Match, string Url, bool IsSupported)>();
            foreach (Match match in matches)
            {
                var url = TrimSentencePunctuation(match.Value);
                if (IsBaiduLikeUrl(url))
                {
                    baiduCandidates.Add((match, url, IsSupportedBaiduHttpsUrl(url)));
                }
            }

            if (baiduCandidates.Count == 0)
            {
                yield return new ImportSourceRow(
                    ++outputRowNumber,
                    string.Empty,
                    Note: line,
                    ValidationError: MissingLinkMessage);
                continue;
            }

            var note = CodePattern().Replace(UrlPattern().Replace(line, string.Empty), string.Empty).Trim();
            for (var index = 0; index < baiduCandidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = baiduCandidates[index];
                var followingEnd = matches
                    .Cast<Match>()
                    .Where(match => match.Index > current.Match.Index)
                    .Select(match => match.Index)
                    .DefaultIfEmpty(line.Length)
                    .Min();
                var following = line[(current.Match.Index + current.Match.Length)..followingEnd];
                var codeMatch = CodePattern().Match(following);
                var code = codeMatch.Success && codeMatch.Index <= 32 ? codeMatch.Groups[1].Value : null;
                yield return new ImportSourceRow(
                    ++outputRowNumber,
                    current.Url,
                    code,
                    Note: note,
                    ValidationError: current.IsSupported ? null : MalformedBaiduLinkMessage);
            }
        }
    }

    private static bool IsBaiduLikeUrl(string value)
    {
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0) return false;
        var authorityStart = schemeEnd + 3;
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = authorityEnd < 0 ? value[authorityStart..] : value[authorityStart..authorityEnd];
        var colon = authority.LastIndexOf(':');
        var host = colon > 0 ? authority[..colon] : authority;
        return string.Equals(host, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "yun.baidu.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedBaiduHttpsUrl(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length || !IsHex(value[index + 1]) || !IsHex(value[index + 2])) return false;
            index += 2;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0)
        {
            return false;
        }

        return string.Equals(uri.IdnHost, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.IdnHost, "yun.baidu.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static string TrimSentencePunctuation(string value) =>
        value.TrimEnd('.', ',', ';', '!', '?', ':', ')', ']', '}', '\'', '"',
            '\u3002', '\uFF0C', '\uFF1A', '\uFF1B', '\uFF01', '\uFF1F', '\uFF09', '\u3011');

    [GeneratedRegex(@"https?://[^\s<>\""'，。；！？、]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?:提取码|访问码|密码|pwd)\s*[:：]?\s*([A-Za-z0-9]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
