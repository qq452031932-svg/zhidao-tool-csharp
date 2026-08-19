using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Imports;

public sealed partial class TextImportParser
{
    private const string MissingLinkMessage = "No supported Baidu share link was found.";
    private const string MalformedBaiduLinkMessage = "The Baidu link is malformed or unsupported.";
    private readonly Action<TextImportParserLineMetrics>? metricsObserver;

    public TextImportParser(Action<TextImportParserLineMetrics>? metricsObserver = null) =>
        this.metricsObserver = metricsObserver;

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

            var matches = UrlPattern().Matches(line).Cast<Match>().ToArray();
            var note = CodePattern().Replace(UrlPattern().Replace(line, string.Empty), string.Empty).Trim();
            var baiduCandidateCount = 0;
            var candidateEvaluationCount = 0;
            var nextBoundaryReadCount = 0;
            for (var index = 0; index < matches.Length; index++)
            {
                candidateEvaluationCount++;
                var match = matches[index];
                var url = TrimSentencePunctuation(match.Value);
                if (!IsBaiduLikeUrl(url))
                {
                    continue;
                }

                baiduCandidateCount++;
                cancellationToken.ThrowIfCancellationRequested();
                nextBoundaryReadCount++;
                var followingEnd = index + 1 < matches.Length ? matches[index + 1].Index : line.Length;
                var following = line[(match.Index + match.Length)..followingEnd];
                var codeMatch = CodePattern().Match(following);
                var code = codeMatch.Success && codeMatch.Index <= 32 ? codeMatch.Groups[1].Value : null;
                yield return new ImportSourceRow(
                    ++outputRowNumber,
                    url,
                    code,
                    Note: note,
                    ValidationError: IsSupportedBaiduHttpsUrl(url) ? null : MalformedBaiduLinkMessage);
            }

            metricsObserver?.Invoke(new TextImportParserLineMetrics(
                matches.Length,
                candidateEvaluationCount,
                nextBoundaryReadCount));

            if (baiduCandidateCount == 0)
            {
                yield return new ImportSourceRow(
                    ++outputRowNumber,
                    string.Empty,
                    Note: line,
                    ValidationError: MissingLinkMessage);
            }
        }
    }

    private static bool IsBaiduLikeUrl(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IdnHost.Length != 0)
        {
            return IsBaiduHost(uri.IdnHost);
        }

        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0) return false;
        var authorityStart = schemeEnd + 3;
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = authorityEnd < 0 ? value[authorityStart..] : value[authorityStart..authorityEnd];
        var userInfoEnd = authority.LastIndexOf('@');
        var hostAndPort = userInfoEnd >= 0 ? authority[(userInfoEnd + 1)..] : authority;
        var portStart = hostAndPort.LastIndexOf(':');
        var host = portStart > 0 ? hostAndPort[..portStart] : hostAndPort;
        return IsBaiduHost(host);
    }

    private static bool IsBaiduHost(string host) =>
        string.Equals(host, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "yun.baidu.com", StringComparison.OrdinalIgnoreCase);

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

public sealed record TextImportParserLineMetrics(
    int UrlMatchCount,
    int CandidateEvaluationCount,
    int NextBoundaryReadCount)
{
    public long CountedOperations =>
        (long)UrlMatchCount + CandidateEvaluationCount + NextBoundaryReadCount;
}
