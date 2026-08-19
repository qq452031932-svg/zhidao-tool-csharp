using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Core.Tests.Services;

public sealed class BaiduPathNormalizerTests
{
    private readonly BaiduPathNormalizer _normalizer = new();

    [Theory]
    [InlineData("/", "/")]
    [InlineData("////", "/")]
    [InlineData(" //\u8D44\u6599//\u5B50\u76EE\u5F55/ ", "/\u8D44\u6599/\u5B50\u76EE\u5F55")]
    [InlineData("\\\u8D44\u6599\\\\\u5B50\u76EE\u5F55\\", "/\u8D44\u6599/\u5B50\u76EE\u5F55")]
    [InlineData("/folder/.../file.txt", "/folder/.../file.txt")]
    public void SeparatorsRootAndUnicode_AreNormalized(string input, string expected)
    {
        var result = _normalizer.Normalize(input);

        Assert.Equal(expected, result.DisplayPath);
        Assert.Equal(expected, result.ComparisonValue);
        Assert.Equal(expected, result.RemotePath.Value);
    }

    [Fact]
    public void CanonicallyEquivalentUnicode_HasTheSameComparisonValue()
    {
        var composed = _normalizer.Normalize("/caf\u00E9");
        var decomposed = _normalizer.Normalize("/cafe\u0301");

        Assert.Equal(composed.ComparisonValue, decomposed.ComparisonValue);
        Assert.Equal("/caf\u00E9", decomposed.DisplayPath);
    }

    [Fact]
    public void AdapterObservedCaseSensitiveSemantics_AreExplicitAndPreserveCase()
    {
        var upper = _normalizer.Normalize("/Batch/FILE");
        var lower = _normalizer.Normalize("/batch/file");

        Assert.Equal("/Batch/FILE", upper.DisplayPath);
        Assert.Equal(StringComparison.Ordinal, upper.ComparisonSemantics);
        Assert.NotEqual(upper.ComparisonValue, lower.ComparisonValue);
    }

    [Theory]
    [InlineData("/folder/./file")]
    [InlineData("/folder/../file")]
    [InlineData("../file")]
    public void DotSegmentsAndTraversal_AreRejected(string input)
    {
        Assert.Throws<ArgumentException>(() => _normalizer.Normalize(input));
    }

    [Theory]
    [InlineData("/folder/\0file")]
    [InlineData("/folder/\u001Ffile")]
    [InlineData("/folder/\u007Ffile")]
    [InlineData("/folder/a:file")]
    [InlineData("/folder/a*file")]
    [InlineData("/folder/a?file")]
    [InlineData("/folder/a\"file")]
    [InlineData("/folder/a<file")]
    [InlineData("/folder/a>file")]
    [InlineData("/folder/a|file")]
    public void InvalidOrControlCharacters_AreRejected(string input)
    {
        Assert.Throws<ArgumentException>(() => _normalizer.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPaths_AreRejected(string input)
    {
        Assert.Throws<ArgumentException>(() => _normalizer.Normalize(input));
    }
}
