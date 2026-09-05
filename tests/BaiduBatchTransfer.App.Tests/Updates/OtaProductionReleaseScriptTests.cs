using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class OtaProductionReleaseScriptTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void ProductionAndAcceptanceAreMutuallyExclusiveNamedParameterSets()
    {
        var script = File.ReadAllText(BuildScriptPath());

        Assert.Contains("DefaultParameterSetName = 'Acceptance'", script, StringComparison.Ordinal);
        Assert.Contains("ParameterSetName = 'Acceptance')][string]$AcceptanceFeedPath", script, StringComparison.Ordinal);
        Assert.Contains("ParameterSetName = 'Acceptance')][switch]$OtaAcceptanceBuild", script, StringComparison.Ordinal);
        Assert.Contains("ParameterSetName = 'Acceptance')][switch]$AllowUnsignedLocal", script, StringComparison.Ordinal);
        Assert.Contains("ParameterSetName = 'Production')][switch]$Production", script, StringComparison.Ordinal);
        Assert.Contains("ParameterSetName = 'Production')][string]$ProductionFeedUrl", script, StringComparison.Ordinal);
        Assert.Contains("ParameterSetName = 'Production')][string]$SigningCertificateThumbprint", script, StringComparison.Ordinal);
        Assert.Contains("ParameterSetName = 'Production')][string]$TimestampUrl", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MixingProductionAndAcceptanceFailsBeforeCreatingOutput()
    {
        var root = FindRepositoryRoot();
        var output = Path.Combine(root, ".artifacts", $"ota-production-parameter-test-{Guid.NewGuid():N}");
        var feed = Path.Combine(root, ".artifacts", $"ota-acceptance-parameter-test-{Guid.NewGuid():N}", "feed");
        Directory.CreateDirectory(feed);
        try
        {
            var result = await RunPowerShellAsync(BuildScriptPath(),
                "-Version", "1.2.3", "-OutputDirectory", output,
                "-AcceptanceFeedPath", feed, "-OtaAcceptanceBuild", "-AllowUnsignedLocal",
                "-Production", "-ProductionFeedUrl", "https://ota.example.test/releases/win-x64/stable/",
                "-SigningCertificateThumbprint", new string('A', 40));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("parameter set", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            Directory.Delete(Directory.GetParent(feed)!.FullName, recursive: true);
        }
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-beta")]
    [InlineData("65535.0.0")]
    public async Task ProductionRejectsNonCanonicalThreePartVersionBeforeOutput(string version)
    {
        var root = FindRepositoryRoot();
        var output = Path.Combine(root, ".artifacts", $"ota-production-version-test-{Guid.NewGuid():N}");
        var result = await RunPowerShellAsync(BuildScriptPath(),
            "-Production", "-Version", version, "-OutputDirectory", output,
            "-ProductionFeedUrl", "https://ota.example.test/releases/win-x64/stable/",
            "-SigningCertificateThumbprint", new string('A', 40));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Version", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void ProductionPipelineFixesIdentitySigningAndReopenedClosureGate()
    {
        var script = File.ReadAllText(BuildScriptPath());
        var validator = File.ReadAllText(ValidatorScriptPath());

        Assert.Contains("$productionPackageId = 'SuperPan.BaiduBatchTransfer'", script, StringComparison.Ordinal);
        Assert.Contains("$productionChannel = 'win-x64-stable'", script, StringComparison.Ordinal);
        Assert.Contains("'--signParams',$signParams", script, StringComparison.Ordinal);
        Assert.Contains("RSASignaturePadding]::Pkcs1", script, StringComparison.Ordinal);
        Assert.Contains("verify-production-directory", script, StringComparison.Ordinal);
        Assert.Contains("verify-reopened-production-zip", script, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", validator, StringComparison.Ordinal);
        Assert.Contains("Publisher certificate SPKI pin does not match", validator, StringComparison.Ordinal);
        Assert.Contains("Upload ZIP contains a directory or nested entry", validator, StringComparison.Ordinal);
        Assert.Contains("duplicate or case-colliding file", validator, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalManifestFixtureHasDeterministicGoldenBytesAndRawRsaSignature()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg"] = [1, 2, 3],
            ["assets.win-x64-stable.json"] = Encoding.UTF8.GetBytes("[]"),
            ["releases.win-x64-stable.json"] = Encoding.UTF8.GetBytes("{\"Assets\":[]}"),
        };
        var lines = files.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant()}  {pair.Key}");
        var bytes = new UTF8Encoding(false).GetBytes(string.Join('\n', lines) + "\n");
        const string expected =
            "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81  SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg\n" +
            "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945  assets.win-x64-stable.json\n" +
            "041e2b719b439eb948a28658a48b2dde418956613d82d9fa9fa9939d27729602  releases.win-x64-stable.json\n";

        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.DoesNotContain((byte)'\r', bytes);
        using var rsa = RSA.Create(3072);
        var signature = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.Equal(384, signature.Length);
        Assert.True(rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Theory]
    [InlineData("nested/file.bin", "directory or nested")]
    [InlineData("foreign.bin", "unknown or unsafe")]
    public async Task ZipReopenGateRejectsNestedAndUnknownFiles(string injectedName, string expected)
    {
        var root = Path.Combine(FindRepositoryRoot(), ".artifacts", $"ota-production-zip-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "candidate.zip");
        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var name in new[]
                         {
                             injectedName, "upload-manifest.sha256", "upload-manifest.sha256.sig",
                             "releases.win-x64-stable.json", "assets.win-x64-stable.json",
                             "RELEASES-win-x64-stable", "SuperPan.BaiduBatchTransfer-Setup.exe",
                         })
                    archive.CreateEntry(name);
            }

            var result = await RunPowerShellAsync(ValidatorScriptPath(), "-BundlePath", zipPath);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(expected, result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ZipReopenGateRejectsCaseCollidingTopLevelFiles()
    {
        var root = Path.Combine(FindRepositoryRoot(), ".artifacts", $"ota-production-zip-case-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "candidate.zip");
        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var name in new[]
                         {
                             "upload-manifest.sha256", "UPLOAD-MANIFEST.SHA256", "upload-manifest.sha256.sig",
                             "releases.win-x64-stable.json", "assets.win-x64-stable.json", "RELEASES-win-x64-stable",
                             "SuperPan.BaiduBatchTransfer-Setup.exe", "SuperPan.BaiduBatchTransfer-Portable.zip",
                         })
                    archive.CreateEntry(name);
            }

            var result = await RunPowerShellAsync(ValidatorScriptPath(), "-BundlePath", zipPath);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("case-colliding", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<ScriptResult> RunPowerShellAsync(string script, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(script);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(Timeout);
        await process.WaitForExitAsync(timeout.Token);
        return new ScriptResult(process.ExitCode, string.Join(Environment.NewLine, await stdout, await stderr));
    }

    private static string BuildScriptPath() => Path.Combine(FindRepositoryRoot(), "scripts", "build_ota_release.ps1");
    private static string ValidatorScriptPath() => Path.Combine(FindRepositoryRoot(), "scripts", "Test-OtaUploadBundle.ps1");

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

    private sealed record ScriptResult(int ExitCode, string Output);
}
