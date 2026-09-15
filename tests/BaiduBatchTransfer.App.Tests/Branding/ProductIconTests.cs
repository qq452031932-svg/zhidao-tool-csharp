using System.Diagnostics;
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

    [Fact]
    public void CanonicalIcoHashMatchesProductIdentityContract()
    {
        var identity = XDocument.Load(Path.Combine(RepositoryRoot(), "eng", "ProductIdentity.props"));
        var expected = identity.Descendants()
            .Single(element => element.Name.LocalName == "ProductIconSha256")
            .Value;
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(IconPath()))).ToLowerInvariant();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task VerifyOnlyWithoutSourceReportsLockedExpectationButNoObservedSource()
    {
        var root = RepositoryRoot();
        var pngPath = CanonicalPngPath();
        var icoPath = IconPath();
        var pngBytes = await File.ReadAllBytesAsync(pngPath);
        var icoBytes = await File.ReadAllBytesAsync(icoPath);
        var pngWriteTime = File.GetLastWriteTimeUtc(pngPath);
        var icoWriteTime = File.GetLastWriteTimeUtc(icoPath);
        var result = await RunScriptAsync(
            Path.Combine(root, "scripts", "Convert-ProductIcon.ps1"),
            root,
            ["-VerifyOnly"]);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains(
            "ExpectedSourceJpegSha256=94BD33FCEB2228C45F6CCF44862D49E90F6DBECD2859445D2DBA67241AFA0A9B",
            result.Output,
            StringComparison.Ordinal);
        Assert.Contains("SourceVerification=not-performed", result.Output, StringComparison.Ordinal);
        Assert.Contains("SourceDimensions=not-verified", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(
            result.Output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries),
            line => line.StartsWith("SourceJpegSha256=", StringComparison.Ordinal));
        Assert.Contains("CanonicalDimensions=640x640", result.Output, StringComparison.Ordinal);
        Assert.Equal(pngBytes, await File.ReadAllBytesAsync(pngPath));
        Assert.Equal(icoBytes, await File.ReadAllBytesAsync(icoPath));
        Assert.Equal(pngWriteTime, File.GetLastWriteTimeUtc(pngPath));
        Assert.Equal(icoWriteTime, File.GetLastWriteTimeUtc(icoPath));
    }

    [Fact]
    public async Task VerifyOnlyWithSourceReportsObservedVerifiedProvenance()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"product-icon-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            var fixture = await CreateSourceFixtureAsync(fixtureRoot);
            var sourceBytes = await File.ReadAllBytesAsync(fixture.SourcePath);
            var canonicalBytes = await File.ReadAllBytesAsync(fixture.CanonicalPath);
            var iconBytes = await File.ReadAllBytesAsync(fixture.IconPath);
            var sourceWriteTime = File.GetLastWriteTimeUtc(fixture.SourcePath);
            var canonicalWriteTime = File.GetLastWriteTimeUtc(fixture.CanonicalPath);
            var iconWriteTime = File.GetLastWriteTimeUtc(fixture.IconPath);
            var result = await RunScriptAsync(
                fixture.ScriptPath,
                fixtureRoot,
                [
                    "-VerifyOnly",
                    "-SourceJpegPath", fixture.SourcePath,
                    "-CanonicalPngPath", fixture.CanonicalPath,
                    "-IconPath", fixture.IconPath,
                ]);

            Assert.True(result.ExitCode == 0, result.Output);
            Assert.Contains($"ExpectedSourceJpegSha256={fixture.SourceHash}", result.Output, StringComparison.Ordinal);
            Assert.Contains($"SourceJpegSha256={fixture.SourceHash}", result.Output, StringComparison.Ordinal);
            Assert.Contains("SourceVerification=verified", result.Output, StringComparison.Ordinal);
            Assert.Contains("SourceDimensions=640x640", result.Output, StringComparison.Ordinal);
            Assert.Contains("CanonicalDimensions=640x640", result.Output, StringComparison.Ordinal);
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(fixture.SourcePath));
            Assert.Equal(canonicalBytes, await File.ReadAllBytesAsync(fixture.CanonicalPath));
            Assert.Equal(iconBytes, await File.ReadAllBytesAsync(fixture.IconPath));
            Assert.Equal(sourceWriteTime, File.GetLastWriteTimeUtc(fixture.SourcePath));
            Assert.Equal(canonicalWriteTime, File.GetLastWriteTimeUtc(fixture.CanonicalPath));
            Assert.Equal(iconWriteTime, File.GetLastWriteTimeUtc(fixture.IconPath));
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    public static TheoryData<string> AliasedPathCases => new()
    {
        "SourceCanonical",
        "SourceIcon",
        "CanonicalIcon",
    };

    [Theory]
    [MemberData(nameof(AliasedPathCases))]
    public async Task ConversionRejectsAliasedPathsBeforeModifyingAnything(string aliasCase)
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"product-icon-alias-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            var firstPath = Path.Combine(fixtureRoot, "first.bin");
            var secondPath = Path.Combine(fixtureRoot, "second.bin");
            await File.WriteAllBytesAsync(firstPath, [1, 3, 5, 7]);
            await File.WriteAllBytesAsync(secondPath, [2, 4, 6, 8]);
            var fixedTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(firstPath, fixedTime);
            File.SetLastWriteTimeUtc(secondPath, fixedTime);
            var beforeFirst = await File.ReadAllBytesAsync(firstPath);
            var beforeSecond = await File.ReadAllBytesAsync(secondPath);

            var arguments = aliasCase switch
            {
                "SourceCanonical" => new[]
                {
                    "-SourceJpegPath", firstPath, "-CanonicalPngPath", firstPath, "-IconPath", secondPath,
                },
                "SourceIcon" => new[]
                {
                    "-SourceJpegPath", firstPath, "-CanonicalPngPath", secondPath, "-IconPath", firstPath,
                },
                "CanonicalIcon" => new[] { "-CanonicalPngPath", firstPath, "-IconPath", firstPath },
                _ => throw new ArgumentOutOfRangeException(nameof(aliasCase)),
            };
            var result = await RunScriptAsync(
                Path.Combine(RepositoryRoot(), "scripts", "Convert-ProductIcon.ps1"),
                fixtureRoot,
                arguments);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("pairwise distinct", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(beforeFirst, await File.ReadAllBytesAsync(firstPath));
            Assert.Equal(beforeSecond, await File.ReadAllBytesAsync(secondPath));
            Assert.Equal(fixedTime, File.GetLastWriteTimeUtc(firstPath));
            Assert.Equal(fixedTime, File.GetLastWriteTimeUtc(secondPath));
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
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

    private static byte[] EncodeBitmap(BitmapSource bitmap, BitmapEncoder encoder)
    {
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] BuildIconBytes(BitmapSource canonical)
    {
        var frames = RequiredSizes
            .Select(size => EncodeBitmap(ResizeLikeConversionScript(canonical, size), new PngBitmapEncoder()))
            .ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Length);
        var offset = 6 + frames.Length * 16;
        for (var index = 0; index < frames.Length; index++)
        {
            var sizeByte = RequiredSizes[index] == 256 ? (byte)0 : (byte)RequiredSizes[index];
            writer.Write(sizeByte);
            writer.Write(sizeByte);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)frames[index].Length);
            writer.Write((uint)offset);
            offset += frames[index].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        writer.Flush();
        return stream.ToArray();
    }

    private static async Task<SourceFixture> CreateSourceFixtureAsync(string fixtureRoot)
    {
        var pixels = new byte[640 * 640 * 4];
        for (var y = 0; y < 640; y++)
        {
            for (var x = 0; x < 640; x++)
            {
                var offset = (y * 640 + x) * 4;
                pixels[offset] = (byte)(x % 256);
                pixels[offset + 1] = (byte)(y % 256);
                pixels[offset + 2] = (byte)((x + y) % 256);
                pixels[offset + 3] = 255;
            }
        }
        var original = BitmapSource.Create(640, 640, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4);
        var jpegEncoder = new JpegBitmapEncoder { QualityLevel = 100 };
        var sourceBytes = EncodeBitmap(original, jpegEncoder);
        var decodedSource = DecodeBitmap(sourceBytes);
        var canonicalBytes = EncodeBitmap(decodedSource, new PngBitmapEncoder());
        var canonical = DecodeBitmap(canonicalBytes);
        var iconBytes = BuildIconBytes(canonical);
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes));
        var canonicalHash = Convert.ToHexString(SHA256.HashData(canonicalBytes));
        var iconHash = Convert.ToHexString(SHA256.HashData(iconBytes));
        var sourcePath = Path.Combine(fixtureRoot, "source.jpg");
        var canonicalPath = Path.Combine(fixtureRoot, "canonical.png");
        var iconPath = Path.Combine(fixtureRoot, "product.ico");
        var scriptPath = Path.Combine(fixtureRoot, "Convert-ProductIcon.ps1");
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        await File.WriteAllBytesAsync(canonicalPath, canonicalBytes);
        await File.WriteAllBytesAsync(iconPath, iconBytes);
        var script = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "scripts", "Convert-ProductIcon.ps1"));
        script = script
            .Replace("94BD33FCEB2228C45F6CCF44862D49E90F6DBECD2859445D2DBA67241AFA0A9B", sourceHash, StringComparison.Ordinal)
            .Replace("358C5937AC2791EF15D4C1B46FDC90EB401BBFD05996FE916C8B066C47AB1A39", canonicalHash, StringComparison.Ordinal)
            .Replace("0CC6A91ABED05FAE671C4635E8903B9F2DB3E6931B72BDAD907B57840D7F97FA", iconHash, StringComparison.Ordinal);
        await File.WriteAllTextAsync(scriptPath, script, new System.Text.UTF8Encoding(false));
        return new SourceFixture(scriptPath, sourcePath, canonicalPath, iconPath, sourceHash);
    }

    private static async Task<ProcessResult> RunScriptAsync(
        string scriptPath,
        string workingDirectory,
        IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
        {
            info.ArgumentList.Add(argument);
        }
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(process.ExitCode, string.Join(Environment.NewLine, await stdout, await stderr));
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

    private sealed record SourceFixture(
        string ScriptPath,
        string SourcePath,
        string CanonicalPath,
        string IconPath,
        string SourceHash);

    private sealed record ProcessResult(int ExitCode, string Output);
}
