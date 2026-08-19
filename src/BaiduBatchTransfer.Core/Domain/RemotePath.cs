namespace BaiduBatchTransfer.Core.Domain;

public sealed record RemotePath
{
    public RemotePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = Normalize(value);
    }

    public string Value { get; }

    public override string ToString() => Value;

    private static string Normalize(string value)
    {
        var candidate = value.Trim().Replace('\\', '/');
        if (candidate.IndexOf('\0', StringComparison.Ordinal) >= 0)
        {
            throw new ArgumentException("A remote path cannot contain null characters.", nameof(value));
        }

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("A remote path cannot contain traversal segments.", nameof(value));
        }

        return segments.Length == 0 ? "/" : $"/{string.Join('/', segments)}";
    }
}
