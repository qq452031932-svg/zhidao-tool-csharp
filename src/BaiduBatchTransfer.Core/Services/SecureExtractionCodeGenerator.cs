using System.Security.Cryptography;

namespace BaiduBatchTransfer.Core.Services;

public sealed class SecureExtractionCodeGenerator
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public string Generate()
    {
        Span<char> code = stackalloc char[4];
        for (var index = 0; index < code.Length; index++)
            code[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(code);
    }
}
