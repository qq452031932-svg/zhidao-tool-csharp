using BaiduBatchTransfer.Application.Jobs;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class CanonicalHashWriterTests
{
    [Fact]
    public void TypedLengthPrefixedFieldsCannotCollideAcrossDelimitersOrTypes()
    {
        var delimiterLeft = Hash(writer =>
        {
            writer.WriteString("first", "a|b");
            writer.WriteString("second", "c\n\0");
        });
        var delimiterRight = Hash(writer =>
        {
            writer.WriteString("first", "a");
            writer.WriteString("second", "b|c\n\0");
        });
        var typedNumber = Hash(writer => writer.WriteInt32("value", 1));
        var typedText = Hash(writer => writer.WriteString("value", "1"));

        Assert.NotEqual(delimiterLeft, delimiterRight);
        Assert.NotEqual(typedNumber, typedText);
    }

    [Fact]
    public void NullEmptyAndCollectionBoundariesAreDistinct()
    {
        var nullValue = Hash(writer => writer.WriteString("value", null));
        var emptyValue = Hash(writer => writer.WriteString("value", string.Empty));
        var single = Hash(writer =>
        {
            writer.BeginCollection("values", 1);
            writer.WriteString("item", "a\nb");
        });
        var split = Hash(writer =>
        {
            writer.BeginCollection("values", 2);
            writer.WriteString("item", "a");
            writer.WriteString("item", "b");
        });

        Assert.NotEqual(nullValue, emptyValue);
        Assert.NotEqual(single, split);
    }

    [Fact]
    public void ValidUnusualUnicodeReplaysExactlyAndInvalidUtf16IsRejected()
    {
        const string unusual = "资源😀𠀀e\u0301\u200D";

        Assert.Equal(
            Hash(writer => writer.WriteString("value", unusual)),
            Hash(writer => writer.WriteString("value", unusual)));
        Assert.Throws<ArgumentException>(() =>
            Hash(writer => writer.WriteString("value", "broken\uD800")));
    }

    private static string Hash(Action<CanonicalHashWriter> write)
    {
        using var writer = new CanonicalHashWriter("test-v1");
        write(writer);
        return writer.GetHashHex();
    }
}
