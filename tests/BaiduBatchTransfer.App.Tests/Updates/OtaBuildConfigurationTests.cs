using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class OtaBuildConfigurationTests
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ProductionBuildRejectsAlternatePackageId()
    {
        var result = await RunGateAsync(
            [
                "VersionPrefix=1.2.3",
                "PackageId=Contoso.Untrusted",
                "UpdateChannel=win-x64-stable",
            ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OTA", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Version=9.9.9")]
    [InlineData("InformationalVersion=9.9.9")]
    [InlineData("FileVersion=9.9.9.9")]
    [InlineData("AssemblyVersion=9.0.0.0")]
    public async Task BuildRejectsOverridesOfDerivedVersionProperties(string overriddenProperty)
    {
        var result = await RunGateAsync(
            [
                "VersionPrefix=1.2.3",
                "PackageId=SuperPan.BaiduBatchTransfer",
                "UpdateChannel=win-x64-stable",
                overriddenProperty,
            ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OTA", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildRejectsAttemptToOverrideInternalExpectedVersion()
    {
        var result = await RunGateAsync(
            [
                "VersionPrefix=1.2.3",
                "Version=9.9.9",
                "_ExpectedProductVersion=9.9.9",
                "PackageId=SuperPan.BaiduBatchTransfer",
                "UpdateChannel=win-x64-stable",
            ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OTA", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("VersionPrefix=01.2.3")]
    [InlineData("VersionPrefix=65535.2.3")]
    [InlineData("VersionPrefix=1.65535.3")]
    [InlineData("VersionPrefix=1.2.65535")]
    [InlineData("VersionPrefix=999999999999999999999999.2.3")]
    public async Task BuildRejectsNonCanonicalOrOutOfRangeVersionPrefix(string versionProperty)
    {
        var result = await RunGateAsync(
            [
                versionProperty,
                "PackageId=SuperPan.BaiduBatchTransfer",
                "UpdateChannel=win-x64-stable",
            ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OTA", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public async Task InvalidOtaBuildConfigurationFailsTheDedicatedGate(params string[] properties)
    {
        var result = await RunGateAsync(properties);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OTA", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ValidConfigurations))]
    public async Task ValidOtaBuildConfigurationPassesTheDedicatedGate(params string[] properties)
    {
        var result = await RunGateAsync(properties);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    public static TheoryData<string[]> InvalidConfigurations()
    {
        var acceptanceFeed = Path.Combine(Path.GetTempPath(), "bbt-ota-acceptance-feed");
        var data = new TheoryData<string[]>();
        data.Add(
            [
                "VersionPrefix=1.2.3",
                "UpdateFeedUrl=https://updates.example.test/stable/",
                $"OtaAcceptanceFeedPath={acceptanceFeed}",
            ]);
        data.Add(
            [
                "VersionPrefix=1.2.3",
                "OtaAcceptanceBuild=true",
                $"OtaAcceptanceFeedPath={acceptanceFeed}",
                "PackageId=SuperPan.BaiduBatchTransfer",
                "UpdateChannel=win-x64-ota-acceptance",
            ]);
        data.Add(
            [
                "VersionPrefix=1.2.3",
                "OtaAcceptanceBuild=true",
                $"OtaAcceptanceFeedPath={acceptanceFeed}",
                "PackageId=SuperPan.BaiduBatchTransfer.OtaAcceptance",
                "UpdateChannel=win-x64-stable",
            ]);
        data.Add(
            [
                "VersionPrefix=1.2",
                "UpdateFeedUrl=https://updates.example.test/stable/",
                "PackageId=SuperPan.BaiduBatchTransfer",
                "UpdateChannel=win-x64-stable",
            ]);
        data.Add(
            [
                "VersionPrefix=1.2.3",
                @"UpdateFeedUrl=C:\local-feed",
                "PackageId=SuperPan.BaiduBatchTransfer",
                "UpdateChannel=win-x64-stable",
            ]);
        data.Add(
            [
                "VersionPrefix=1.2.3",
                "UpdateFeedUrl=https://:443/stable/",
                "PackageId=SuperPan.BaiduBatchTransfer",
                "UpdateChannel=win-x64-stable",
            ]);
        data.Add(
            [
                "VersionPrefix=1.2.3",
                "UpdateFeedUrl=https://updates.example.test:abc/stable/",
                "PackageId=SuperPan.BaiduBatchTransfer",
                "UpdateChannel=win-x64-stable",
            ]);
        return data;
    }

    public static TheoryData<string[]> ValidConfigurations()
    {
        var acceptanceFeed = Path.Combine(Path.GetTempPath(), "bbt-ota-acceptance-feed");
        var data = new TheoryData<string[]>();
        data.Add(
            [
                "VersionPrefix=1.2.3",
                "UpdateFeedUrl=https://updates.example.test/stable/",
                "PackageId=SuperPan.BaiduBatchTransfer",
                "UpdateChannel=win-x64-stable",
            ]);
        data.Add(
            [
                "VersionPrefix=65534.65534.65534",
                "PackageId=SuperPan.BaiduBatchTransfer",
                "UpdateChannel=win-x64-stable",
            ]);
        data.Add(
            [
                "VersionPrefix=1.2.3",
                "OtaAcceptanceBuild=true",
                $"OtaAcceptanceFeedPath={acceptanceFeed}",
                "PackageId=SuperPan.BaiduBatchTransfer.OtaAcceptance",
                "UpdateChannel=win-x64-ota-acceptance",
            ]);
        return data;
    }

    private static async Task<GateResult> RunGateAsync(string[] properties)
    {
        var project = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "BaiduBatchTransfer.App",
            "BaiduBatchTransfer.App.csproj");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-t:ValidateOtaBuildConfiguration");
        startInfo.ArgumentList.Add("-p:Configuration=Release");
        foreach (var property in properties)
            startInfo.ArgumentList.Add($"-p:{property}");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start dotnet msbuild.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(GateTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited while the timeout was being handled.
            }

            await process.WaitForExitAsync();
            throw new TimeoutException(
                $"OTA build gate exceeded {GateTimeout}." + Environment.NewLine +
                string.Join(Environment.NewLine, await standardOutput, await standardError));
        }

        return new GateResult(
            process.ExitCode,
            string.Join(Environment.NewLine, await standardOutput, await standardError));
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record GateResult(int ExitCode, string Output);
}
