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

    /// <summary>
    /// 结果表的文件名：网盘账号名 + 时间戳。
    ///
    /// 不再拿原始导入表格的文件名做名字（见 Build）：那个名字跟导出的内容
    /// 没有关系，多个账号导出时还会撞名。
    /// </summary>
    public string BuildForAccount(string accountDisplayName, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(accountDisplayName))
            throw new ArgumentException("Account name is required.", nameof(accountDisplayName));
        var basis = SanitizeBasis(accountDisplayName.Trim());
        var suffix = $"_{timestamp.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.xlsx";
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

    public string BuildSplit(string accountDisplayName, string resultKind, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(accountDisplayName)) throw new ArgumentException("Account name is required.", nameof(accountDisplayName));
        if (resultKind is not ("成功" or "失败")) throw new ArgumentException("Result kind must be 成功 or 失败.", nameof(resultKind));
        var basis = SanitizeBasis(accountDisplayName.Trim());
        var suffix = $"_{resultKind}_{timestamp:yyyyMMddHHmmss}.xlsx";
        var max = MaximumFileNameLength - suffix.Length;
        if (basis.Length > max) basis = basis[..Math.Max(1, max)];
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
