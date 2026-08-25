using BaiduBatchTransfer.App.Features.Results;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LocalResultFileNameAllocatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-local-result-{Guid.NewGuid():N}");

    [Fact]
    public void SameNameUsesIncreasingSuffixWithoutOverwriting()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "百度分享结果_20260825_160000.xlsx"), "old-0");
        File.WriteAllText(Path.Combine(root, "百度分享结果_20260825_160000 (1).xlsx"), "old-1");

        var chosen = LocalResultFileNameAllocator.Choose(
            root, "百度分享结果_20260825_160000.xlsx");

        Assert.Equal("百度分享结果_20260825_160000 (2).xlsx", chosen);
        Assert.Equal("old-0", File.ReadAllText(Path.Combine(root, "百度分享结果_20260825_160000.xlsx")));
        Assert.Equal("old-1", File.ReadAllText(Path.Combine(root, "百度分享结果_20260825_160000 (1).xlsx")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
