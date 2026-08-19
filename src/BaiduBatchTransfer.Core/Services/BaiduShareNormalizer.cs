using System.Text;

namespace BaiduBatchTransfer.Core.Services;

public sealed class BaiduShareNormalizer
{
    private static readonly string[] AllowedHosts = ["pan.baidu.com", "yun.baidu.com"];
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private const string TrailingSentencePunctuation =
        ".,;!?:')\"]}>\u2019\u201D\u3002\uFF0C\uFF1A\uFF1B\uFF01\uFF1F\uFF09\u3011\u300B\u300D\u300F\u3015\u3017\u3019\u301B";

    public BaiduShareNormalizationResult Normalize(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var candidate = TrimTrailingSentencePunctuation(input.Trim());
        if (!candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            candidate.Contains('\\', StringComparison.Ordinal))
        {
            throw InvalidShareUrl();
        }

        ValidateWellFormedUnicode(candidate);
        ValidateRawPercentEncoding(candidate);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 ||
            !AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
        {
            throw InvalidShareUrl();
        }

        var query = ParseQuery(uri.Query);
        var rawPath = GetRawPath(candidate);

        string shareId;
        if (rawPath.StartsWith("/s/", StringComparison.Ordinal) &&
            rawPath.Length > 3 &&
            rawPath.IndexOf('/', 3) < 0)
        {
            shareId = DecodeShareId(rawPath[3..]);
        }
        else if (string.Equals(rawPath, "/share/init", StringComparison.Ordinal))
        {
            shareId = ValidateShareId(
                GetSingleQueryValue(query, "surl") ?? throw InvalidShareUrl());
        }
        else
        {
            // Short links must be resolved by IShareLinkResolver before reaching Core.
            throw InvalidShareUrl();
        }

        var extractionCode = NormalizeExtractionCode(GetSingleQueryValue(query, "pwd"));
        var canonicalUrl = $"https://pan.baidu.com/s/{Uri.EscapeDataString(shareId)}";

        return new BaiduShareNormalizationResult(canonicalUrl, shareId, extractionCode);
    }

    private static string TrimTrailingSentencePunctuation(string value)
    {
        var length = value.Length;
        while (length > 0 && TrailingSentencePunctuation.Contains(value[length - 1], StringComparison.Ordinal))
        {
            length--;
        }

        return value[..length].TrimEnd();
    }

    private static string GetRawPath(string value)
    {
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var componentStart = FindFirst(value, authorityStart, '/', '?', '#');
        if (componentStart < 0 || value[componentStart] != '/')
        {
            return string.Empty;
        }

        var pathEnd = FindFirst(value, componentStart, '?', '#');
        return pathEnd < 0
            ? value[componentStart..]
            : value[componentStart..pathEnd];
    }

    private static int FindFirst(string value, int startIndex, params char[] characters)
    {
        for (var index = startIndex; index < value.Length; index++)
        {
            if (characters.Contains(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ParseQuery(string query)
    {
        if (query.Length <= 1)
        {
            return [];
        }

        var values = new List<KeyValuePair<string, string>>();
        foreach (var component in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = component.IndexOf('=');
            var escapedKey = separator < 0 ? component : component[..separator];
            var escapedValue = separator < 0 ? string.Empty : component[(separator + 1)..];
            values.Add(new KeyValuePair<string, string>(
                DecodeQueryComponent(escapedKey),
                DecodeQueryComponent(escapedValue)));
        }

        return values;
    }

    private static string? GetSingleQueryValue(
        IReadOnlyList<KeyValuePair<string, string>> query,
        string key)
    {
        var matches = query
            .Where(pair => string.Equals(pair.Key, key, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw InvalidShareUrl(),
        };
    }

    private static string DecodeQueryComponent(string value) =>
        DecodePercentEncoded(value, plusAsSpace: true);

    private static string DecodeShareId(string escapedShareId)
    {
        return ValidateShareId(DecodePercentEncoded(escapedShareId, plusAsSpace: false));
    }

    private static string ValidateShareId(string value)
    {
        var shareId = value;
        if (string.IsNullOrWhiteSpace(shareId) ||
            shareId is "." or ".." ||
            shareId.Any(character =>
                character is '/' or '\\' or '?' or '#' ||
                char.IsControl(character) ||
                char.IsWhiteSpace(character)))
        {
            throw InvalidShareUrl();
        }

        return shareId;
    }

    private static string? NormalizeExtractionCode(string? extractionCode)
    {
        if (string.IsNullOrEmpty(extractionCode))
        {
            return null;
        }

        if (extractionCode.Any(char.IsControl))
        {
            throw InvalidShareUrl();
        }

        return extractionCode;
    }

    private static void ValidateRawPercentEncoding(string value) =>
        _ = DecodePercentEncoded(value, plusAsSpace: false);

    private static void ValidateWellFormedUnicode(string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw InvalidShareUrl();
        }
    }

    private static string DecodePercentEncoded(string value, bool plusAsSpace)
    {
        var decoded = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            if (value[index] != '%')
            {
                decoded.Append(plusAsSpace && value[index] == '+' ? ' ' : value[index]);
                index++;
                continue;
            }

            var bytes = new List<byte>();
            while (index < value.Length && value[index] == '%')
            {
                if (index + 2 >= value.Length)
                {
                    throw InvalidShareUrl();
                }

                var high = HexValue(value[index + 1]);
                var low = HexValue(value[index + 2]);
                if (high < 0 || low < 0)
                {
                    throw InvalidShareUrl();
                }

                bytes.Add((byte)((high << 4) | low));
                index += 3;
            }

            try
            {
                decoded.Append(StrictUtf8.GetString(bytes.ToArray()));
            }
            catch (DecoderFallbackException)
            {
                throw InvalidShareUrl();
            }
        }

        return decoded.ToString();
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1,
    };

    private static ArgumentException InvalidShareUrl() =>
        new("The value is not a supported resolved Baidu HTTPS share URL.");
}

public sealed class BaiduShareNormalizationResult
{
    internal BaiduShareNormalizationResult(
        string canonicalUrl,
        string shareId,
        string? extractionCode)
    {
        CanonicalUrl = canonicalUrl;
        ShareId = shareId;
        ExtractionCode = extractionCode;
    }

    public string CanonicalUrl { get; }

    public string ShareId { get; }

    public string? ExtractionCode { get; }

    public override string ToString() =>
        "BaiduShareNormalizationResult { CanonicalUrl = [REDACTED], ShareId = [REDACTED], ExtractionCode = [REDACTED] }";
}
