using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace BaiduBatchTransfer.App.Tests.Branding;

public sealed class ProductIconTests
{
    private static readonly int[] RequiredSizes = [16, 24, 32, 48, 64, 128, 256];

    [Fact]
    public void CanonicalPngIsExactly640PixelsSquare()
    {
        var bytes = File.ReadAllBytes(CanonicalPngPath());
        var image = DecodeBitmap(bytes);

        Assert.Equal(640, image.PixelWidth);
        Assert.Equal(640, image.PixelHeight);
        Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], bytes[..8]);
    }

    [Fact]
    public void IcoContainsExactlyTheRequiredDecodableFrames()
    {
        var iconBytes = File.ReadAllBytes(IconPath());
        var entries = ReadIconEntries(iconBytes);

        Assert.Equal(RequiredSizes, entries.Select(entry => entry.Width).Order());
        foreach (var entry in entries)
        {
            Assert.Equal(entry.Width, entry.Height);
            Assert.True(entry.Length > 0);
            Assert.InRange(entry.Offset, 6 + entries.Count * 16, iconBytes.Length - 1);
            Assert.InRange(entry.Offset + entry.Length, entry.Offset + 1, iconBytes.Length);

            var encodedFrame = iconBytes.AsSpan(entry.Offset, entry.Length).ToArray();
            Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], encodedFrame[..8]);
            var frame = DecodeBitmap(encodedFrame);
            Assert.Equal(entry.Width, frame.PixelWidth);
            Assert.Equal(entry.Height, frame.PixelHeight);
        }

        var orderedRanges = entries.OrderBy(entry => entry.Offset).ToArray();
        for (var index = 1; index < orderedRanges.Length; index++)
        {
            Assert.True(
                orderedRanges[index - 1].Offset + orderedRanges[index - 1].Length <= orderedRanges[index].Offset,
                "ICO frame byte ranges must not overlap.");
        }
    }

    [Fact]
    public void LargestIcoFrameMatchesDocumentedWicTransformedBitmapScaling()
    {
        var iconBytes = File.ReadAllBytes(IconPath());
        var entry = Assert.Single(ReadIconEntries(iconBytes), candidate => candidate.Width == 256);
        var actual = DecodeBitmap(iconBytes.AsSpan(entry.Offset, entry.Length).ToArray());
        var canonical = DecodeBitmap(File.ReadAllBytes(CanonicalPngPath()));
        var expected = ResizeLikeConversionScript(canonical, 256);

        Assert.Equal(PixelHash(expected), PixelHash(actual));
    }

    [Fact]
    public void ProjectUsesPropertyBackedApplicationIconAndWpfPngResource()
    {
        var projectPath = Path.Combine(RepositoryRoot(), "src", "BaiduBatchTransfer.App", "BaiduBatchTransfer.App.csproj");
        var document = XDocument.Load(projectPath);
        var properties = document.Descendants()
            .Where(element => element.Parent?.Name.LocalName == "PropertyGroup")
            .GroupBy(element => element.Name.LocalName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);

        Assert.Equal("$(ProductApplicationIconPath)", properties["ApplicationIcon"]);
        Assert.Equal(@"Assets\Branding\知道综合工具.ico", properties["ProductApplicationIconPath"]);
        Assert.Equal(@"Assets\Branding\知道综合工具.png", properties["ProductBrandingPngPath"]);
        Assert.Contains(
            document.Descendants().Where(element => element.Name.LocalName == "Resource"),
            resource => string.Equals(
                resource.Attribute("Include")?.Value,
                "$(ProductBrandingPngPath)",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ConversionScriptPinsTheUserSourceAndSupportsReadOnlyVerification()
    {
        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "Convert-ProductIcon.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("94BD33FCEB2228C45F6CCF44862D49E90F6DBECD2859445D2DBA67241AFA0A9B", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[switch]$VerifyOnly", script, StringComparison.Ordinal);
        Assert.Contains("TransformedBitmap", script, StringComparison.Ordinal);
    }

    private static IReadOnlyList<IconEntry> ReadIconEntries(byte[] bytes)
    {
        Assert.True(bytes.Length >= 6);
        Assert.Equal((ushort)0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal((ushort)1, BitConverter.ToUInt16(bytes, 2));
        var count = BitConverter.ToUInt16(bytes, 4);
        Assert.True(bytes.Length >= 6 + count * 16);
        var entries = new List<IconEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var start = 6 + index * 16;
            var width = bytes[start] == 0 ? 256 : bytes[start];
            var height = bytes[start + 1] == 0 ? 256 : bytes[start + 1];
            var length = checked((int)BitConverter.ToUInt32(bytes, start + 8));
            var offset = checked((int)BitConverter.ToUInt32(bytes, start + 12));
            entries.Add(new IconEntry(width, height, length, offset));
        }
        return entries;
    }

    private static BitmapSource ResizeLikeConversionScript(BitmapSource source, int size)
    {
        var transform = new ScaleTransform(
            (double)size / source.PixelWidth,
            (double)size / source.PixelHeight);
        var output = new TransformedBitmap();
        output.BeginInit();
        output.Source = source;
        output.Transform = transform;
        output.EndInit();
        output.Freeze();
        return output;
    }

    private static string PixelHash(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static BitmapSource DecodeBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.Single();
        frame.Freeze();
        return frame;
    }

    private static string CanonicalPngPath() => Path.Combine(
        RepositoryRoot(), "src", "BaiduBatchTransfer.App", "Assets", "Branding", "知道综合工具.png");

    private static string IconPath() => Path.Combine(
        RepositoryRoot(), "src", "BaiduBatchTransfer.App", "Assets", "Branding", "知道综合工具.ico");

    private static string RepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record IconEntry(int Width, int Height, int Length, int Offset);
}
