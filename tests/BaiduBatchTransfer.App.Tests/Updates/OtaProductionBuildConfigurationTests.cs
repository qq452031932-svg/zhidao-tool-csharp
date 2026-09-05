using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class OtaProductionBuildConfigurationTests
{
    [Fact]
    public async Task SignedProductionIdentityAndHttpsFeedPassBuildGate()
    {
        var result = await RunAsync(
            "VersionPrefix=1.2.3",
            "PackageId=SuperPan.BaiduBatchTransfer",
            "UpdateChannel=win-x64-stable",
            "UpdateFeedUrl=https://ota.fanfh.cn/releases/win-x64/stable/");

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Theory]
    [InlineData("PackageId=SuperPan.BaiduBatchTransfer.OtaAcceptance")]
    [InlineData("UpdateChannel=win-x64-ota-acceptance")]
    [InlineData("UpdateFeedUrl=http://ota.fanfh.cn/releases/win-x64/stable/")]
    public async Task ProductionRejectsNonProductionIdentityOrFeed(string overrideProperty)
    {
        var properties = new List<string>
        {
            "VersionPrefix=1.2.3",
            "PackageId=SuperPan.BaiduBatchTransfer",
            "UpdateChannel=win-x64-stable",
            "UpdateFeedUrl=https://ota.fanfh.cn/releases/win-x64/stable/",
        };
        var key = overrideProperty[..overrideProperty.IndexOf('=')];
        properties.RemoveAll(value => value.StartsWith(key + "=", StringComparison.Ordinal));
        properties.Add(overrideProperty);

        var result = await RunAsync(properties.ToArray());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OTA", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<GateResult> RunAsync(params string[] properties)
    {
        var project = Path.Combine(FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "BaiduBatchTransfer.App.csproj");
        var info = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "msbuild", project, "-nologo", "-t:ValidateOtaBuildConfiguration", "-p:Configuration=Release" })
            info.ArgumentList.Add(argument);
        foreach (var property in properties) info.ArgumentList.Add($"-p:{property}");
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return new GateResult(process.ExitCode, string.Join(Environment.NewLine, await stdout, await stderr));
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record GateResult(int ExitCode, string Output);
}
