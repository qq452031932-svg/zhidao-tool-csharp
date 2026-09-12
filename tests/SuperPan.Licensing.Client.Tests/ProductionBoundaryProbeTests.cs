using System.Runtime.CompilerServices;

namespace SuperPan.Licensing.Client.Tests;

public sealed class ProductionBoundaryProbeTests
{
    [Fact]
    public void ProbeIsVisiblyStampedAndAcceptsOnlyTimeAndProtectedStoreOverrides()
    {
        var root = Root();
        var program = File.ReadAllText(Path.Combine(root, "tools", "license_production_boundary_probe", "Program.cs"));
        var project = File.ReadAllText(Path.Combine(root, "tools", "license_production_boundary_probe", "SuperPan.Licensing.ProductionBoundaryProbe.csproj"));

        Assert.Contains("BOUNDARY-PROBE-NONSHIPPING", project, StringComparison.Ordinal);
        Assert.Contains("--utc-now", program, StringComparison.Ordinal);
        Assert.Contains("--store-root", program, StringComparison.Ordinal);
        Assert.Contains("DpapiLicenseStore", program, StringComparison.Ordinal);
        Assert.Contains("MachineFingerprintProvider", program, StringComparison.Ordinal);
        Assert.DoesNotContain("--machine", program, StringComparison.Ordinal);
        Assert.DoesNotContain("--config", program, StringComparison.Ordinal);
        Assert.DoesNotContain("--endpoint", program, StringComparison.Ordinal);
        Assert.DoesNotContain("--product", program, StringComparison.Ordinal);
    }

    private static string Root([CallerFilePath] string path = "") =>
        Directory.GetParent(path)!.Parent!.Parent!.FullName;
}
