using BaiduBatchTransfer.App.Features.Acceptance;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduAcceptanceProbeProfilePathPolicyTests
{
    [Fact]
    public async Task CreatesAndRemovesAnIsolatedProfileWithoutUsingTheAccountProfilePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "BaiduAcceptanceProbeTests", Guid.NewGuid().ToString("N"));
        try
        {
            var policy = new BaiduAcceptanceProbeProfilePathPolicy(root);
            var lease = policy.Create(Guid.NewGuid());

            Assert.StartsWith(Path.Combine(Path.GetFullPath(root), "acceptance-probe"), lease.ProfilePath,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(lease.ProfilePath));

            await lease.DisposeAsync();

            Assert.False(Directory.Exists(lease.ProfilePath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
