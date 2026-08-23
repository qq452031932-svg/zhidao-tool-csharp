using System.Globalization;
using System.Text;
using System.Xml;

namespace BaiduBatchTransfer.Core.Services;

public static class SpreadsheetTextSanitizer
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var cleaned = RemoveInvalidXmlCharacters(value);
        var index = 0;
        while (index < cleaned.Length)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(cleaned, index);
            var character = cleaned[index];
            if (!char.IsWhiteSpace(character) &&
                category is not (UnicodeCategory.Format or UnicodeCategory.Control))
            {
                break;
            }

            index += char.IsSurrogatePair(cleaned, index) ? 2 : 1;
        }

        return index < cleaned.Length && cleaned[index] is '=' or '+' or '-' or '@'
            ? $"'{cleaned}"
            : cleaned;
    }

    private static string RemoveInvalidXmlCharacters(string value)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                if (builder is not null)
                {
                    builder.Append(character);
                    builder.Append(value[++index]);
                }
                else
                {
                    index++;
                }
                continue;
            }

            if (XmlConvert.IsXmlChar(character))
            {
                builder?.Append(character);
                continue;
            }

            builder ??= new StringBuilder(value.Length).Append(value, 0, index);
        }

        return builder?.ToString() ?? value;
    }
}
