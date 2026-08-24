using BaiduBatchTransfer.App.Features.Acceptance;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduAcceptanceProbeOutputStoreTests
{
    [Fact]
    public async Task SavesConcurrentEvidenceWithDistinctNewFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "BaiduAcceptanceProbeTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BaiduAcceptanceProbeOutputStore(root);
            var evidence = new[] { new BaiduAcceptanceEvidence("pan.baidu.com", "/segments/1", "GET", 200, ["errno"]) };

            var paths = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.SaveAsync(evidence)));

            Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(paths, path => Assert.True(File.Exists(path)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
