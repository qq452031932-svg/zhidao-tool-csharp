namespace BaiduBatchTransfer.Core.Domain;

public sealed class SessionCookie
{
    public SessionCookie(
        string name,
        string value,
        string domain,
        string path,
        DateTimeOffset? expiresUtc,
        bool isSecure,
        bool isHttpOnly)
    {
        Name = ValidateName(name);
        Value = RequireBounded(value, nameof(value), 16 * 1024);
        Domain = ValidateDomain(domain);
        Path = ValidatePath(path);
        ExpiresUtc = expiresUtc?.ToUniversalTime();
        IsSecure = isSecure;
        IsHttpOnly = isHttpOnly;
    }

    public string Name { get; }
    public string Value { get; }
    public string Domain { get; }
    public string Path { get; }
    public DateTimeOffset? ExpiresUtc { get; }
    public bool IsSecure { get; }
    public bool IsHttpOnly { get; }

    public override string ToString() =>
        "SessionCookie { Name = [REDACTED], Value = [REDACTED], Domain = [REDACTED], Path = [REDACTED] }";

    private static string ValidateName(string value)
    {
        var name = RequireBounded(value, nameof(value), 256);
        const string separators = "()<>@,;:\\\"/[]?={}";
        if (name.Any(character => character <= 0x20 || character >= 0x7f || separators.Contains(character)))
            throw new ArgumentException("A valid cookie name is required.", nameof(value));
        return name;
    }

    private static string ValidateDomain(string value)
    {
        var domain = RequireBounded(value, nameof(value), 253).ToLowerInvariant();
        var host = domain[0] == '.' ? domain[1..] : domain;
        var isBaiduDomain = string.Equals(host, "baidu.com", StringComparison.Ordinal) ||
                            host.EndsWith(".baidu.com", StringComparison.Ordinal) ||
                            string.Equals(host, "baidu.cn", StringComparison.Ordinal) ||
                            host.EndsWith(".baidu.cn", StringComparison.Ordinal);
        if (host.Length == 0 || host.Contains('/') || host.Contains(':') ||
            host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) != UriHostNameType.Dns || !isBaiduDomain)
        {
            throw new ArgumentException("A valid cookie DNS domain is required.", nameof(value));
        }

        return domain;
    }

    private static string ValidatePath(string value)
    {
        var path = RequireBounded(value, nameof(value), 2048);
        if (!path.StartsWith("/", StringComparison.Ordinal) || path.Any(char.IsControl))
        {
            throw new ArgumentException("A valid absolute cookie path is required.", nameof(value));
        }

        return path;
    }

    private static string RequireBounded(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentException("The session field is invalid or too large.", parameterName);
        }

        return value;
    }
}
