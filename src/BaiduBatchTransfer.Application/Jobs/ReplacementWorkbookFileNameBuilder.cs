using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class ReplacementWorkbookFileNameBuilder
{
    private const int MaximumFileNameLength = 240;

    public string Build(string sourceFileName, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(sourceFileName))
            throw new ArgumentException("A source file name is required.", nameof(sourceFileName));
        var basis = Path.GetFileNameWithoutExtension(sourceFileName.Trim());
        if (string.IsNullOrWhiteSpace(basis))
            throw new ArgumentException("A source file name is required.", nameof(sourceFileName));
        basis = SanitizeBasis(basis);
        var suffix = $"_转存分享结果_{timestamp.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.xlsx";
        var maximumBasisLength = checked(MaximumFileNameLength - suffix.Length);
        if (basis.Length > maximumBasisLength)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis)))[..8];
            var prefixLength = checked(maximumBasisLength - hash.Length - 1);
            if (prefixLength > 0 && char.IsHighSurrogate(basis[prefixLength - 1])) prefixLength--;
            basis = $"{basis[..prefixLength]}_{hash}";
        }
        return basis + suffix;
    }

    private static string SanitizeBasis(string basis)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(basis.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim()
            .TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized)
            ? throw new ArgumentException("The source file name has no usable file-name characters.", nameof(basis))
            : sanitized;
    }
}
