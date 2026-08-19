using System.Text;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Services;

public sealed class BaiduPathNormalizer
{
    private const string InvalidSegmentCharacters = ":*?\"<>|";

    public BaiduPathNormalizationResult Normalize(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var candidate = input.Trim().Replace('\\', '/').Normalize(NormalizationForm.FormC);
        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException("A Baidu path cannot contain dot segments.", nameof(input));
            }

            if (segment.Any(character =>
                    char.IsControl(character) ||
                    InvalidSegmentCharacters.Contains(character, StringComparison.Ordinal)))
            {
                throw new ArgumentException("A Baidu path contains an unsupported character.", nameof(input));
            }
        }

        var displayPath = segments.Length == 0 ? "/" : $"/{string.Join('/', segments)}";
        return new BaiduPathNormalizationResult(displayPath);
    }
}

public sealed class BaiduPathNormalizationResult
{
    internal BaiduPathNormalizationResult(string displayPath)
    {
        DisplayPath = displayPath;
        ComparisonValue = displayPath;
        RemotePath = new RemotePath(displayPath);
    }

    public string DisplayPath { get; }

    public string ComparisonValue { get; }

    public StringComparison ComparisonSemantics => StringComparison.Ordinal;

    public RemotePath RemotePath { get; }

    public override string ToString() => DisplayPath;
}
