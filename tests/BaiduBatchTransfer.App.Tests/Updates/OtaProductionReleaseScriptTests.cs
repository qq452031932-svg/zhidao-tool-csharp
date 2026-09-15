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
        var result = await RunProductionPowerShellAsync(
            "-Production", "-Version", version, "-OutputDirectory", output,
            "-ProductionFeedUrl", "https://ota.example.test/releases/win-x64/stable/",
            "-SigningCertificateThumbprint", new string('A', 40));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Version", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("https://ota.example.test/releases/win-x64/stable/;Injected=true")]
    [InlineData("https://ota.example.test/releases/win-x64/stable/,Injected=true")]
    [InlineData("https://ota.example.test/releases/win-x64/stable/%3bInjected=true")]
    public async Task ProductionRejectsMsBuildFeedInjectionBeforeOutput(string feedUrl)
    {
        var root = FindRepositoryRoot();
        var output = Path.Combine(root, ".artifacts", $"ota-production-feed-test-{Guid.NewGuid():N}");
        var result = await RunProductionPowerShellAsync(
            "-Production", "-Version", "1.2.3", "-OutputDirectory", output,
            "-ProductionFeedUrl", feedUrl,
            "-SigningCertificateThumbprint", new string('A', 40));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ProductionFeedUrl", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("https://timestamp.example.test/path;Injected=true")]
    [InlineData("https://timestamp.example.test/path,Injected=true")]
    [InlineData("https://timestamp.example.test/path%3bInjected=true")]
    [InlineData("https://timestamp.example.test/path with-space")]
    public async Task ProductionRejectsTimestampCommandInjectionBeforeOutput(string timestampUrl)
    {
        var root = FindRepositoryRoot();
        var output = Path.Combine(root, ".artifacts", $"ota-production-timestamp-test-{Guid.NewGuid():N}");
        var result = await RunProductionPowerShellAsync(
            "-Production", "-Version", "1.2.3", "-OutputDirectory", output,
            "-ProductionFeedUrl", "https://ota.example.test/releases/win-x64/stable/",
            "-SigningCertificateThumbprint", new string('A', 40),
            "-TimestampUrl", timestampUrl);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("TimestampUrl", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(output));
    }

    private static Task<ScriptResult> RunProductionPowerShellAsync(params string[] arguments)
    {
        var complete = arguments.Concat(new[]
        {
            "-LicenseSigningKeyId", "key-1",
            "-LicenseSigningPublicKey", "public-key",
            "-LicenseCurrentSpkiPin", new string('a', 64),
            "-LicenseNextSpkiPin", new string('b', 64),
            "-LicenseServiceUrl", "https://license.example.test/license-api/",
        }).ToArray();
        return RunPowerShellAsync(BuildScriptPath(), complete);
    }

    [Fact]
    public void ProductionPipelineFixesIdentitySigningAndReopenedClosureGate()
    {
        var script = File.ReadAllText(BuildScriptPath());
        var validator = File.ReadAllText(ValidatorScriptPath());

        Assert.Contains("$productionPackageId = 'SuperPan.BaiduBatchTransfer'", script, StringComparison.Ordinal);
        Assert.Contains("$productionChannel = 'win-x64-stable'", script, StringComparison.Ordinal);
        Assert.Contains("'--signParams', $signParams", script, StringComparison.Ordinal);
        Assert.Contains("$timestampUri.AbsoluteUri -cne 'http://timestamp.digicert.com/'", script, StringComparison.Ordinal);
        Assert.Contains("approved DigiCert RFC 3161 endpoint", script, StringComparison.Ordinal);
        Assert.Contains("RSASignaturePadding]::Pkcs1", script, StringComparison.Ordinal);
        Assert.Contains("verify-production-directory", script, StringComparison.Ordinal);
        Assert.Contains("verify-reopened-production-zip", script, StringComparison.Ordinal);
        Assert.Contains("rev-parse", script, StringComparison.Ordinal);
        Assert.Contains("ls-tree", script, StringComparison.Ordinal);
        Assert.Contains("production-source-overrides.sha256", script, StringComparison.Ordinal);
        Assert.Contains("eng/license/ProductionLicenseTrust.targets", script, StringComparison.Ordinal);
        Assert.Contains("Reviewed production source override hash differs", script, StringComparison.Ordinal);
        Assert.Contains("Staged reviewed production source override hash differs", script, StringComparison.Ordinal);
        Assert.DoesNotContain("git -C $Source ls-files", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ls-files --others", script, StringComparison.Ordinal);
        Assert.Contains("$($Commit):$RelativePath", script, StringComparison.Ordinal);
        Assert.Contains("dotnet-restore-locked", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--force-evaluate", script, StringComparison.Ordinal);
        Assert.Contains("Private-key material is forbidden", script, StringComparison.Ordinal);
        Assert.Contains("failure-logs", script, StringComparison.Ordinal);
        Assert.Contains("$productionStage", script, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Move($productionStage, $productionOutput)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Join-Path $productionOutput '.build'", script, StringComparison.Ordinal);
        Assert.Contains("Published production metadata", script, StringComparison.Ordinal);
        Assert.Contains("Previous production upload ZIP", script, StringComparison.Ordinal);
        Assert.Contains("Previous production generation directory must contain exactly one canonical upload ZIP.", script, StringComparison.Ordinal);
        Assert.Contains("'delta','patch'", script, StringComparison.Ordinal);
        Assert.Contains("Delta reconstruction differs from the current Full package", script, StringComparison.Ordinal);
        Assert.Contains("Join-Path $sourceSnapshot 'scripts\\Test-OtaUploadBundle.ps1'", script, StringComparison.Ordinal);
        Assert.Contains("Join-Path $sourceSnapshot 'eng\\ota\\SuperPan.OtaPublisher.cer'", script, StringComparison.Ordinal);
        Assert.Contains("Set-Location -LiteralPath $WorkingDirectory", script, StringComparison.Ordinal);
        Assert.Contains("'--tool-manifest',$toolManifestPath", script, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", validator, StringComparison.Ordinal);
        Assert.Contains("SignedCms", validator, StringComparison.Ordinal);
        Assert.Contains("Authenticode image digest", validator, StringComparison.Ordinal);
        Assert.Contains("1.3.6.1.4.1.311.2.1.4", validator, StringComparison.Ordinal);
        Assert.Contains("1.3.6.1.4.1.311.2.1.15", validator, StringComparison.Ordinal);
        Assert.Contains("Velopack package nuspec", validator, StringComparison.Ordinal);
        Assert.Contains("Portable archive", validator, StringComparison.Ordinal);
        Assert.Contains("current/sq.version", validator, StringComparison.Ordinal);
        Assert.Contains("SignerInfo digest algorithm must be SHA-256", validator, StringComparison.Ordinal);
        Assert.Contains("signer certificate is missing the Code Signing EKU", validator, StringComparison.Ordinal);
        Assert.Contains("Third-party executable signer certificate is missing the Code Signing EKU", validator, StringComparison.Ordinal);
        Assert.Contains("Authenticode must contain exactly one accepted timestamp token", validator, StringComparison.Ordinal);
        Assert.Contains("manifest-hash-package-data", validator, StringComparison.Ordinal);
        Assert.Contains("package-data-not-authenticode-executable", validator, StringComparison.Ordinal);
        Assert.Contains("lib/app/BaiduBatchTransfer.App.exe", validator, StringComparison.Ordinal);
        Assert.Contains("Every executable carried by an inner archive", validator, StringComparison.Ordinal);
        Assert.Contains("$publisherTimestampRequired", validator, StringComparison.Ordinal);
        Assert.Contains("$ExpectedPackageVersion -ceq $currentVersion", validator, StringComparison.Ordinal);
        Assert.Contains("current/BaiduBatchTransfer.App.exe", validator, StringComparison.Ordinal);
        Assert.Contains("lib/app/BaiduBatchTransfer.App.exe", validator, StringComparison.Ordinal);
        Assert.Contains("Publisher certificate SPKI pin does not match", validator, StringComparison.Ordinal);
        Assert.Contains("Upload ZIP contains a directory or nested entry", validator, StringComparison.Ordinal);
        Assert.Contains("duplicate or case-colliding file", validator, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionVpkArgumentsUseCanonicalBrandingAndKeepStableDistributionIdentity()
    {
        var script = File.ReadAllText(BuildScriptPath());

        Assert.Contains("$productionVpkArguments = @(", script, StringComparison.Ordinal);
        Assert.Contains("'--packId', $productionPackageId", script, StringComparison.Ordinal);
        Assert.Contains("'--mainExe', $productMainExecutableName", script, StringComparison.Ordinal);
        Assert.Contains("'--packTitle', $productDisplayName", script, StringComparison.Ordinal);
        Assert.Contains("'--icon', $productionIconPath", script, StringComparison.Ordinal);
        Assert.Contains("$productionPackageId = 'SuperPan.BaiduBatchTransfer'", script, StringComparison.Ordinal);
        Assert.Contains("$productionChannel = 'win-x64-stable'", script, StringComparison.Ordinal);
        Assert.Contains("$productionPackageId-Setup.exe", script, StringComparison.Ordinal);
        Assert.Contains("$productionPackageId-Portable.zip", script, StringComparison.Ordinal);
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

    [Fact]
    public async Task CommittedGoldenBundlePassesValidatorEndToEnd()
    {
        var fixture = Path.Combine(
            FindRepositoryRoot(), "tests", "BaiduBatchTransfer.App.Tests", "Updates", "Fixtures", "OtaProduction");
        var bundle = Path.Combine(fixture, "valid-upload.zip");
        var certificate = Path.Combine(fixture, "publisher.cer");
        Assert.True(File.Exists(bundle), $"Missing golden bundle: {bundle}");
        Assert.True(File.Exists(certificate), $"Missing golden certificate: {certificate}");
        using var publicCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
        var spki = Convert.ToHexString(
            SHA256.HashData(publicCertificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        var result = await RunPowerShellAsync(
            ValidatorScriptPath(),
            "-BundlePath", bundle,
            "-PublisherCertificatePath", certificate,
            "-ExpectedPublisherSpkiSha256", spki,
            "-ExpectedVersion", "1.2.3");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("\"verified\": true", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"version\": \"1.2.3\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("Portable archive::SuperPan.BaiduBatchTransfer.exe", result.Output, StringComparison.Ordinal);
        Assert.Contains("Package SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg::lib/app/BaiduBatchTransfer.App.exe", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedManifestCannotAuthorizeBogusInnerPackage()
    {
        var fixture = Path.Combine(
            FindRepositoryRoot(), "tests", "BaiduBatchTransfer.App.Tests", "Updates", "Fixtures", "OtaProduction");
        var bundle = Path.Combine(fixture, "invalid-bogus-inner-signed-upload.zip");
        var certificate = Path.Combine(fixture, "publisher.cer");
        using var publicCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
        var spki = Convert.ToHexString(
            SHA256.HashData(publicCertificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        var result = await RunPowerShellAsync(
            ValidatorScriptPath(),
            "-BundlePath", bundle,
            "-PublisherCertificatePath", certificate,
            "-ExpectedPublisherSpkiSha256", spki,
            "-ExpectedVersion", "1.2.3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("missing required Velopack package content", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UntimestampedGoldenBundleFailsWhenTimestampIsRequired()
    {
        var fixture = Path.Combine(
            FindRepositoryRoot(), "tests", "BaiduBatchTransfer.App.Tests", "Updates", "Fixtures", "OtaProduction");
        var bundle = Path.Combine(fixture, "valid-upload.zip");
        var certificate = Path.Combine(fixture, "publisher.cer");
        using var publicCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
        var spki = Convert.ToHexString(
            SHA256.HashData(publicCertificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        var result = await RunPowerShellAsync(
            ValidatorScriptPath(),
            "-BundlePath", bundle,
            "-PublisherCertificatePath", certificate,
            "-ExpectedPublisherSpkiSha256", spki,
            "-ExpectedVersion", "1.2.3",
            "-RequireTimestamp");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("required Authenticode timestamp", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignedJunkOnlyDeltaBundleIsRejected()
    {
        var fixture = Path.Combine(
            FindRepositoryRoot(), "tests", "BaiduBatchTransfer.App.Tests", "Updates", "Fixtures", "OtaProduction");
        var bundle = Path.Combine(fixture, "invalid-junk-delta-signed-upload.zip");
        var certificate = Path.Combine(fixture, "publisher.cer");
        Assert.True(File.Exists(bundle), $"Missing delta rejection fixture: {bundle}");
        using var publicCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
        var spki = Convert.ToHexString(
            SHA256.HashData(publicCertificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        var result = await RunPowerShellAsync(
            ValidatorScriptPath(),
            "-BundlePath", bundle,
            "-PublisherCertificatePath", certificate,
            "-ExpectedPublisherSpkiSha256", spki,
            "-ExpectedVersion", "1.2.3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Delta package has no meaningful patch payload", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("invalid-wrong-inner-signer-upload.zip", "signer SPKI does not match")]
    [InlineData("invalid-tampered-inner-image-upload.zip", "image digest does not match")]
    public async Task SignedBundleCannotAuthorizeWrongSignerOrTamperedPe(string fixtureName, string expectedError)
    {
        var fixture = Path.Combine(
            FindRepositoryRoot(), "tests", "BaiduBatchTransfer.App.Tests", "Updates", "Fixtures", "OtaProduction");
        var bundle = Path.Combine(fixture, fixtureName);
        var certificate = Path.Combine(fixture, "publisher.cer");
        Assert.True(File.Exists(bundle), $"Missing executable rejection fixture: {bundle}");
        using var publicCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
        var spki = Convert.ToHexString(
            SHA256.HashData(publicCertificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        var result = await RunPowerShellAsync(
            ValidatorScriptPath(),
            "-BundlePath", bundle,
            "-PublisherCertificatePath", certificate,
            "-ExpectedPublisherSpkiSha256", spki,
            "-ExpectedVersion", "1.2.3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("invalid-portable-wrong-version-signed-upload.zip", "Portable archive Velopack metadata")]
    [InlineData("invalid-portable-wrong-channel-signed-upload.zip", "Portable archive Velopack metadata")]
    [InlineData("invalid-portable-windows-name-signed-upload.zip", "unsafe Windows path")]
    [InlineData("invalid-sha1-inner-signed-upload.zip", "SignerInfo digest algorithm")]
    public async Task SignedBundleRejectsPortableMetadataPathAndDigestAttacks(string fixtureName, string expectedError)
    {
        var fixture = Path.Combine(
            FindRepositoryRoot(), "tests", "BaiduBatchTransfer.App.Tests", "Updates", "Fixtures", "OtaProduction");
        var bundle = Path.Combine(fixture, fixtureName);
        var certificate = Path.Combine(fixture, "publisher.cer");
        Assert.True(File.Exists(bundle), $"Missing hardened portable fixture: {bundle}");
        using var publicCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
        var spki = Convert.ToHexString(
            SHA256.HashData(publicCertificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        var result = await RunPowerShellAsync(
            ValidatorScriptPath(),
            "-BundlePath", bundle,
            "-PublisherCertificatePath", certificate,
            "-ExpectedPublisherSpkiSha256", spki,
            "-ExpectedVersion", "1.2.3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.Output, StringComparison.OrdinalIgnoreCase);
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
