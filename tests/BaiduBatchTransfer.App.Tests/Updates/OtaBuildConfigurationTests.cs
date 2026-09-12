using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class OtaBuildConfigurationTests
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);
    private const string ProductId = "8270e8d7-1791-487d-ad76-0da7a0cbed12";
    private const string CurrentPin = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string NextPin = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void LicenseProductIdIsImmutableAssemblyMetadata()
    {
        var project = File.ReadAllText(ProjectPath());

        Assert.Contains($"<LicenseProductId>{ProductId}</LicenseProductId>", project, StringComparison.Ordinal);
        Assert.Contains("<_Parameter1>LicenseProductId</_Parameter1>", project, StringComparison.Ordinal);
        Assert.Contains("<_Parameter2>$(LicenseProductId)</_Parameter2>", project, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DebugBuildDefaultsToDisabledLicensing()
    {
        var result = await RunLicenseGateAsync("Debug", []);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task ReleaseBuildFailsClosedWhenLicenseTrustIsMissing()
    {
        var result = await RunLicenseGateAsync("Release", []);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("license", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseBuildAcceptsCompleteHttpsLicenseTrust()
    {
        var result = await RunLicenseGateAsync("Release", ValidProductionLicenseProperties());

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task ReleaseBuildRejectsAnyOriginExceptExactProductionOrigin()
    {
        var result = await RunLicenseGateAsync("Release", WithProductionProperty("LicenseServiceUrl", "https://license.example.test"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("license", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseBuildRejectsAcceptanceStampEvenWithValidLoopbackAcceptanceProperties()
    {
        var result = await RunLicenseGateAsync("Release",
        [
            "LicenseAcceptanceBuild=true",
            "LicenseAcceptanceConfigPath=C:\\acceptance\\client.json",
            "LicenseServiceUrl=http://127.0.0.1:19091",
        ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("license", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Acceptance")]
    public async Task ReleaseBuildModeOverrideCannotBypassProductionTrust(string mode)
    {
        var result = await RunLicenseGateAsync("Release", [$"LicenseRuntimeMode={mode}"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("license", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Acceptance")]
    public async Task DebugBuildModeOverrideCannotChangeDisabledDefault(string mode)
    {
        var result = await RunLicenseGateAsync("Debug", [$"LicenseRuntimeMode={mode}"]);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Theory]
    [InlineData("LicenseServiceUrl=http://license.example.test")]
    [InlineData("LicenseSigningPublicKey=not-base64")]
    [InlineData("LicenseCurrentSpkiPin=ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("LicenseNextSpkiPin=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public async Task ReleaseBuildRejectsInvalidLicenseTrust(string replacement)
    {
        var properties = ValidProductionLicenseProperties().ToList();
        var name = replacement[..replacement.IndexOf('=')];
        properties.RemoveAll(value => value.StartsWith(name + "=", StringComparison.Ordinal));
        properties.Add(replacement);

        var result = await RunLicenseGateAsync("Release", properties);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("license", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseBuildRejectsBase64ThatIsNotAnImportablePublicKey()
    {
        var properties = WithProductionProperty(
            "LicenseSigningPublicKey",
            Convert.ToBase64String(new byte[91]));

        var result = await RunLicenseGateAsync("Release", properties);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("license", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseBuildRejectsRsaPublicKey()
    {
        using var key = RSA.Create(2048);
        var properties = WithProductionProperty(
            "LicenseSigningPublicKey",
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

        var result = await RunLicenseGateAsync("Release", properties);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("license", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseBuildRejectsNonP256EcdsaPublicKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var properties = WithProductionProperty(
            "LicenseSigningPublicKey",
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

        var result = await RunLicenseGateAsync("Release", properties);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("license", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseBuildRejectsCanonicalP256SpkiWithInvalidCurvePoint()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var encoded = key.ExportSubjectPublicKeyInfo();
        Array.Clear(encoded, encoded.Length - 64, 64);
        var properties = WithProductionProperty(
            "LicenseSigningPublicKey",
            Convert.ToBase64String(encoded));

        var result = await RunLicenseGateAsync("Release", properties);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("license", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseBuildRejectsOutOfRangeHttpsPort()
    {
        var properties = WithProductionProperty(
            "LicenseServiceUrl",
            "https://license.example.test:99999");

        var result = await RunLicenseGateAsync("Release", properties);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("license", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("LicenseAcceptanceConfigPath=relative\\client.json", "LicenseServiceUrl=http://127.0.0.1:19091")]
    [InlineData("LicenseAcceptanceConfigPath=C:\\acceptance\\client.json", "LicenseServiceUrl=http://localhost:19091")]
    [InlineData("LicenseAcceptanceConfigPath=C:\\acceptance\\client.json", "LicenseServiceUrl=https://license.example.test")]
    public async Task AcceptanceBuildRejectsNonAbsoluteOrNonLoopbackConfiguration(
        string configPath,
        string serviceUrl)
    {
        var result = await RunLicenseGateAsync("Debug",
        [
            "LicenseAcceptanceBuild=true",
            configPath,
            serviceUrl,
        ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("license", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptanceBuildAllowsAbsoluteConfigAndLiteralLoopbackUrl()
    {
        var result = await RunLicenseGateAsync("Debug",
        [
            "LicenseAcceptanceBuild=true",
            "LicenseAcceptanceConfigPath=C:\\acceptance\\client.json",
            "LicenseServiceUrl=http://127.0.0.1:19091",
        ]);

        Assert.True(result.ExitCode == 0, result.Output);
    }

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

    [Fact]
    public async Task AcceptanceBuildDerivesDedicatedPackageIdWhenCallerLeavesItUnset()
    {
        var acceptanceFeed = Path.Combine(Path.GetTempPath(), "bbt-ota-acceptance-feed");
        var result = await RunGateAsync(
            [
                "VersionPrefix=1.2.3",
                "OtaAcceptanceBuild=true",
                $"OtaAcceptanceFeedPath={acceptanceFeed}",
                "UpdateChannel=win-x64-ota-acceptance",
            ]);

        Assert.True(result.ExitCode == 0, result.Output);
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

    private static string[] ValidProductionLicenseProperties()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return
        [
            "LicenseServiceUrl=https://ota.fanfh.cn",
            $"LicenseSigningPublicKey={Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}",
            $"LicenseCurrentSpkiPin={CurrentPin}",
            $"LicenseNextSpkiPin={NextPin}",
        ];
    }

    private static string[] WithProductionProperty(string name, string value)
    {
        var properties = ValidProductionLicenseProperties().ToList();
        properties.RemoveAll(property => property.StartsWith(name + "=", StringComparison.Ordinal));
        properties.Add($"{name}={value}");
        return properties.ToArray();
    }

    private static Task<GateResult> RunLicenseGateAsync(
        string configuration,
        IEnumerable<string> properties) =>
        RunMsBuildGateAsync("ValidateLicenseBuildConfiguration", configuration, properties);

    private static async Task<GateResult> RunMsBuildGateAsync(
        string target,
        string configuration,
        IEnumerable<string> properties)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(ProjectPath());
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add($"-t:{target}");
        startInfo.ArgumentList.Add($"-p:Configuration={configuration}");
        foreach (var property in properties) startInfo.ArgumentList.Add($"-p:{property}");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start dotnet msbuild.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(GateTimeout);
        await process.WaitForExitAsync(timeout.Token);
        return new GateResult(process.ExitCode,
            string.Join(Environment.NewLine, await standardOutput, await standardError));
    }

    private static string ProjectPath() => Path.Combine(
        FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "BaiduBatchTransfer.App.csproj");

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
