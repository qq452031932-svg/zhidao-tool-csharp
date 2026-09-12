using System.Runtime.CompilerServices;

namespace SuperPan.Licensing.Client.Tests;

public sealed class ProductionSmokeHarnessTests
{
    [Fact]
    public void SmokeClientHasImmutableNonShippingProductionBoundary()
    {
        var root = Root();
        var program = File.ReadAllText(Path.Combine(root, "tools", "license_production_smoke", "Program.cs"));
        var project = File.ReadAllText(Path.Combine(root, "tools", "license_production_smoke", "SuperPan.Licensing.ProductionSmoke.csproj"));

        Assert.Contains("https://ota.fanfh.cn", project, StringComparison.Ordinal);
        Assert.DoesNotContain("8270e8d7-1791-487d-ad76-0da7a0cbed12", project, StringComparison.Ordinal);
        Assert.Contains("NonShipping", project, StringComparison.Ordinal);
        Assert.Contains("Console.In.ReadLine", program, StringComparison.Ordinal);
        Assert.Contains("DpapiLicenseStore", program, StringComparison.Ordinal);
        Assert.Contains("MachineFingerprintProvider", program, StringComparison.Ordinal);
        Assert.DoesNotContain("--utc-now", program, StringComparison.Ordinal);
        Assert.DoesNotContain("--machine", program, StringComparison.Ordinal);
        Assert.DoesNotContain("--config", program, StringComparison.Ordinal);
        Assert.DoesNotContain("--endpoint", program, StringComparison.Ordinal);
        Assert.Contains("ProductionLicenseTrust.targets", project, StringComparison.Ordinal);
    }

    private static string Root([CallerFilePath] string path = "") =>
        Directory.GetParent(path)!.Parent!.Parent!.FullName;
}
