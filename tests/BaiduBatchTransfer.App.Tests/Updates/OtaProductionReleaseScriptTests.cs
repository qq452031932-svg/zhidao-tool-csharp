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
        Assert.Contains("-ExpectedVersion '1.1.0' -HistoricalProductIdentity", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$HistoricalProductIdentity", validator, StringComparison.Ordinal);
        Assert.Contains("HistoricalProductIdentity is restricted to the signed 1.1.0 migration base.", validator, StringComparison.Ordinal);
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
        Assert.Contains("lib/app/$productMainExecutableName", validator, StringComparison.Ordinal);
        Assert.Contains("Every executable carried by an inner archive", validator, StringComparison.Ordinal);
        Assert.Contains("$publisherTimestampRequired", validator, StringComparison.Ordinal);
        Assert.Contains("$ExpectedPackageVersion -ceq $currentVersion", validator, StringComparison.Ordinal);
        Assert.Contains("current/$productLegacyExecutableName", validator, StringComparison.Ordinal);
        Assert.Contains("lib/app/$productLegacyExecutableName", validator, StringComparison.Ordinal);
        Assert.Contains("Publisher certificate SPKI pin does not match", validator, StringComparison.Ordinal);
        Assert.Contains("Upload ZIP contains a directory or nested entry", validator, StringComparison.Ordinal);
        Assert.Contains("duplicate or case-colliding file", validator, StringComparison.Ordinal);
        Assert.Contains("[DateTime]::ParseExact($timestampText", validator, StringComparison.Ordinal);
        Assert.Contains("yyyyMMddHHmmss", validator, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionSnapshotEnumeratesGitPathsAsNulDelimitedStrictUtf8()
    {
        var script = File.ReadAllText(BuildScriptPath());

        Assert.Contains("function Get-TrackedGitPaths", script, StringComparison.Ordinal);
        Assert.Contains("$startInfo.ArgumentList.Add('-z')", script, StringComparison.Ordinal);
        Assert.Contains("[Text.UTF8Encoding]::new($false, $true)", script, StringComparison.Ordinal);
        Assert.Contains(".Split([char]0", script, StringComparison.Ordinal);
        Assert.Contains("[void]$copyTask.GetAwaiter().GetResult()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("@(& git -C $Source ls-tree", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UploadValidatorUsesCentralIdentityAndKeepsStableOuterAliases()
    {
        var validator = File.ReadAllText(ValidatorScriptPath());

        Assert.Contains("ProductIdentity.ps1", validator, StringComparison.Ordinal);
        Assert.Contains("Get-ProductIdentity", validator, StringComparison.Ordinal);
        Assert.Contains("$productIdentity.ProductMainExecutableName", validator, StringComparison.Ordinal);
        Assert.Contains("$productIdentity.ProductMainDllName", validator, StringComparison.Ordinal);
        Assert.Contains("$productIdentity.ProductLegacyExecutableName", validator, StringComparison.Ordinal);
        Assert.Contains("$productIdentity.ProductDisplayName", validator, StringComparison.Ordinal);
        Assert.Contains("$productIdentity.ProductDescription", validator, StringComparison.Ordinal);
        Assert.Contains("$productIdentity.ProductPackageId", validator, StringComparison.Ordinal);
        Assert.Contains("$packageId-Setup.exe", validator, StringComparison.Ordinal);
        Assert.Contains("$packageId-Portable.zip", validator, StringComparison.Ordinal);
        Assert.Contains("RELEASES-$channel", validator, StringComparison.Ordinal);
    }

    [Fact]
    public void UploadValidatorEnforcesRenamedLayoutBrandingArchitectureAndDeltaMigration()
    {
        var validator = File.ReadAllText(ValidatorScriptPath());

        Assert.Contains("$productMainExecutableName", validator, StringComparison.Ordinal);
        Assert.Contains("$productMainDllName", validator, StringComparison.Ordinal);
        Assert.Contains("$productLegacyExecutableName", validator, StringComparison.Ordinal);
        Assert.Contains("$productDisplayName", validator, StringComparison.Ordinal);
        Assert.Contains("$productDescription", validator, StringComparison.Ordinal);
        Assert.Contains("setup.ico", validator, StringComparison.Ordinal);
        Assert.Contains("canonical package icon hash differs", validator, StringComparison.Ordinal);
        Assert.Contains("0x8664", validator, StringComparison.Ordinal);
        Assert.Contains("Root launcher does not reference the configured main executable", validator, StringComparison.Ordinal);
        Assert.Contains("Delta package does not replace the legacy executable with the compatibility launcher", validator, StringComparison.Ordinal);
        Assert.Contains("Delta package does not add the renamed main application", validator, StringComparison.Ordinal);
        Assert.Contains("COM¹", validator, StringComparison.Ordinal);
        Assert.Contains("LPT³", validator, StringComparison.Ordinal);
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
    public async Task ProductionRejectsDirtyIdentityInputsBeforeCertificateOrToolchainUse()
    {
        var root = Path.Combine(
            FindRepositoryRoot(), ".artifacts", $"ota-production-identity-test-{Guid.NewGuid():N}");
        var scripts = Path.Combine(root, "scripts");
        var eng = Path.Combine(root, "eng");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(eng);
        try
        {
            File.Copy(BuildScriptPath(), Path.Combine(scripts, "build_ota_release.ps1"));
            File.Copy(
                Path.Combine(FindRepositoryRoot(), "scripts", "ProductIdentity.ps1"),
                Path.Combine(scripts, "ProductIdentity.ps1"));
            File.Copy(
                Path.Combine(FindRepositoryRoot(), "eng", "ProductIdentity.props"),
                Path.Combine(eng, "ProductIdentity.props"));
            RunGit(root, "init");
            RunGit(root, "add", "scripts/build_ota_release.ps1", "scripts/ProductIdentity.ps1", "eng/ProductIdentity.props");
            RunGit(root, "-c", "user.name=OTA Test", "-c", "user.email=ota@example.invalid", "commit", "-m", "fixture");

            await File.AppendAllTextAsync(Path.Combine(scripts, "ProductIdentity.ps1"), "\n# dirty helper\n");
            var propsPath = Path.Combine(eng, "ProductIdentity.props");
            var props = await File.ReadAllTextAsync(propsPath);
            await File.WriteAllTextAsync(
                propsPath,
                props.Replace("<ProductDisplayName>知道综合工具</ProductDisplayName>",
                    "<ProductDisplayName>Dirty Display</ProductDisplayName>", StringComparison.Ordinal));
            var output = Path.Combine(root, ".artifacts", "ota-production-dirty-identity", "release");

            var result = await RunPowerShellAsync(
                Path.Combine(scripts, "build_ota_release.ps1"),
                "-Production", "-Version", "1.2.3", "-OutputDirectory", output,
                "-ProductionFeedUrl", "https://ota.example.test/releases/win-x64/stable/",
                "-SigningCertificateThumbprint", new string('A', 40),
                "-LicenseSigningKeyId", "key-1", "-LicenseSigningPublicKey", "public-key",
                "-LicenseCurrentSpkiPin", new string('a', 64),
                "-LicenseNextSpkiPin", new string('b', 64),
                "-LicenseServiceUrl", "https://license.example.test/license-api/");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("product identity inputs differ from source commit", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("signing certificate", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            if (Directory.Exists(root)) DeleteDirectoryTree(root);
        }
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
        Assert.Contains("Portable archive::current/知道综合工具.exe", result.Output, StringComparison.Ordinal);
        Assert.Contains("Portable archive::current/BaiduBatchTransfer.App.exe", result.Output, StringComparison.Ordinal);
        Assert.Contains("Package SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg::lib/app/知道综合工具.exe", result.Output, StringComparison.Ordinal);
        Assert.Contains("Package SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg::lib/app/BaiduBatchTransfer.App.exe", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.1.1", "restricted to the signed 1.1.0 migration base")]
    [InlineData("1.1.0", "version does not match ExpectedVersion")]
    public async Task HistoricalIdentityCannotAuthorizeCurrentOrMixedSignedBundles(string expectedVersion, string expectedError)
    {
        var (bundle, certificate, spki) = GoldenFixture();
        var result = await RunPowerShellAsync(
            ValidatorScriptPath(),
            "-BundlePath", bundle,
            "-PublisherCertificatePath", certificate,
            "-ExpectedPublisherSpkiSha256", spki,
            "-ExpectedVersion", expectedVersion,
            "-HistoricalProductIdentity");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.Output, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("missing required Velopack content", result.Output, StringComparison.OrdinalIgnoreCase);
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
    public async Task CommittedGoldenBundlePassesValidatorUnderWindowsPowerShell51()
    {
        var (bundle, certificate, spki) = GoldenFixture();
        var result = await RunPowerShellHostAsync("powershell.exe", ValidatorScriptPath(),
            "-BundlePath", bundle, "-PublisherCertificatePath", certificate,
            "-ExpectedPublisherSpkiSha256", spki, "-ExpectedVersion", "1.2.3");

        Assert.True(result.ExitCode == 0, result.Output);
        using var report = System.Text.Json.JsonDocument.Parse(result.Output);
        Assert.True(report.RootElement.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task RenamedBusinessClientCannotMasqueradeAsLegacyLauncherUnderWindowsPowerShell51()
    {
        var (_, certificate, spki) = GoldenFixture();
        var bundle = Path.Combine(Path.GetDirectoryName(certificate)!, "invalid-main-copied-as-legacy-upload.zip");
        var result = await RunPowerShellHostAsync("powershell.exe", ValidatorScriptPath(),
            "-BundlePath", bundle, "-PublisherCertificatePath", certificate,
            "-ExpectedPublisherSpkiSha256", spki, "-ExpectedVersion", "1.2.3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("compatibility launcher identity", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticatedCompatibilityAndExecutionStubCannotMasqueradeAsCurrentMainApplication()
    {
        var root = Path.Combine(FindRepositoryRoot(), ".artifacts", $"ota-role-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var compatibility = ExtractGoldenInnerEntry(root, "SuperPan.BaiduBatchTransfer-Portable.zip",
                "current/BaiduBatchTransfer.App.exe", "compat.exe");
            var main = ExtractGoldenInnerEntry(root, "SuperPan.BaiduBatchTransfer-Portable.zip",
                "current/知道综合工具.exe", "main.exe");
            var stub = ExtractGoldenInnerEntry(root, "SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg",
                "lib/app/知道综合工具_ExecutionStub.exe", "stub.exe");
            var compatibilityResult = await RunValidatorFunctionsAsync(
                $"Assert-AuthenticatedExecutableIdentity '{PsQuote(compatibility)}' 'main-application' '知道综合工具.dll' 'BaiduBatchTransfer.App.dll' '知道综合工具' '知道综合工具'");
            Assert.NotEqual(0, compatibilityResult.ExitCode);
            Assert.Contains("main application identity", compatibilityResult.Output, StringComparison.OrdinalIgnoreCase);

            var stubResult = await RunValidatorFunctionsAsync(
                $"$main=Get-VerifiedAuthenticode '{PsQuote(main)}';$stub=Get-VerifiedAuthenticode '{PsQuote(stub)}';" +
                "$roles=@([pscustomobject]@{ArtifactRole='main-application';ImageDigest=$stub.ImageDigest}," +
                "[pscustomobject]@{ArtifactRole='velopack-bootstrapper';ImageDigest=$stub.ImageDigest});" +
                "Assert-DistinctCurrentApplicationImages $roles");
            Assert.NotEqual(0, stubResult.ExitCode);
            Assert.Contains("shares its authenticated image", stubResult.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteDirectoryTree(root); }
    }

    [Fact]
    public async Task PeEnvelopeRejectsOversizedFilesAndMalformedCertificateLengthsBeforeAllocation()
    {
        var root = Path.Combine(FindRepositoryRoot(), ".artifacts", $"ota-pe-bounds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var oversized = Path.Combine(root, "oversized.exe");
            await using (var stream = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(536_870_913);
            var oversizedResult = await RunValidatorFunctionsAsync($"Get-PeEnvelope '{PsQuote(oversized)}' | Out-Null");
            Assert.NotEqual(0, oversizedResult.ExitCode);
            Assert.Contains("exceeds the allowed size", oversizedResult.Output, StringComparison.OrdinalIgnoreCase);

            var malformed = Path.Combine(root, "malformed.exe");
            var bytes = new byte[512];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
            BitConverter.GetBytes(0x00004550).CopyTo(bytes, 0x80);
            BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
            BitConverter.GetBytes((ushort)0x20b).CopyTo(bytes, 0x98);
            BitConverter.GetBytes(0x100u).CopyTo(bytes, 0x128);
            BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 0x12c);
            await File.WriteAllBytesAsync(malformed, bytes);
            var malformedResult = await RunValidatorFunctionsAsync($"Get-PeEnvelope '{PsQuote(malformed)}' | Out-Null");
            Assert.NotEqual(0, malformedResult.ExitCode);
            Assert.Contains("certificate table", malformedResult.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteDirectoryTree(root); }
    }

    [Theory]
    [InlineData("COM¹.txt")]
    [InlineData("com².log")]
    [InlineData("LPT³.dat")]
    public async Task WindowsPathGateRejectsSuperscriptDeviceAliases(string name)
    {
        var result = await RunValidatorFunctionsAsync(
            $"if ($null -ne (Get-WindowsCanonicalInnerPath 'current/{PsQuote(name)}')) {{ throw 'device alias accepted' }}");
        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task RootLauncherTextSearchIsBoundedAndHandlesChunkBoundaries()
    {
        var root = Path.Combine(FindRepositoryRoot(), ".artifacts", $"ota-launcher-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var oversized = Path.Combine(root, "oversized.exe");
            await using (var stream = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(67_108_865);
            var oversizedResult = await RunValidatorFunctionsAsync(
                $"Assert-FileContainsText '{PsQuote(oversized)}' '知道综合工具' 'missing' 67108864");
            Assert.NotEqual(0, oversizedResult.ExitCode);
            Assert.Contains("exceeds the allowed size", oversizedResult.Output, StringComparison.OrdinalIgnoreCase);

            var boundary = Path.Combine(root, "boundary.exe");
            var needle = Encoding.UTF8.GetBytes("知道综合工具");
            var bytes = new byte[65_536 + needle.Length];
            Array.Fill(bytes, (byte)'x');
            needle.CopyTo(bytes, 65_535);
            await File.WriteAllBytesAsync(boundary, bytes);
            var boundaryResult = await RunValidatorFunctionsAsync(
                $"Assert-FileContainsText '{PsQuote(boundary)}' '知道综合工具' 'missing' 67108864");
            Assert.True(boundaryResult.ExitCode == 0, boundaryResult.Output);
        }
        finally { DeleteDirectoryTree(root); }
    }

    [Fact]
    public async Task DeltaContractRejectsDualPatchWrongTargetShasumAndHistoricalDelta()
    {
        const string common = "$targets=@{'lib/app/a.dll'=[pscustomobject]@{Sha1=('A'*40);Length=7}};";
        var cases = new[]
        {
            (Command: common + "$p=@([pscustomobject]@{BasePath='lib/app/a.dll';Extension='.diff';Length=1;Shasum=(('A'*40)+' a.dll.shasum 7')},[pscustomobject]@{BasePath='lib/app/a.dll';Extension='.zsdiff';Length=1;Shasum=(('A'*40)+' a.dll.shasum 7')});Assert-DeltaPatchContract $p $targets '1.2.3' '1.2.3' 'lib/app/a.dll'", Error: "both .diff and .zsdiff"),
            (Command: common + "$p=@([pscustomobject]@{BasePath='lib/app/a.dll';Extension='.diff';Length=1;Shasum=(('B'*40)+' a.dll.shasum 7')});Assert-DeltaPatchContract $p $targets '1.2.3' '1.2.3' 'lib/app/a.dll'", Error: "does not identify the current Full target"),
            (Command: common + "$p=@([pscustomobject]@{BasePath='lib/app/a.dll';Extension='.diff';Length=1;Shasum=(('A'*40)+' a.dll.shasum 7')});Assert-DeltaPatchContract $p $targets '1.2.2' '1.2.3' 'lib/app/a.dll'", Error: "historical Delta"),
        };
        foreach (var item in cases)
        {
            var result = await RunValidatorFunctionsAsync(item.Command);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(item.Error, result.Output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CurrentDeltaReconstructsTheAuthenticatedCurrentFullPackage()
    {
        var root = Path.Combine(FindRepositoryRoot(), ".artifacts", $"ota-delta-reconstruct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var basePackage = ExtractGoldenOuterEntry(root, "SuperPan.BaiduBatchTransfer-1.2.2-full.nupkg");
            var deltaPackage = ExtractGoldenOuterEntry(root, "SuperPan.BaiduBatchTransfer-1.2.3-delta.nupkg");
            var currentPackage = ExtractGoldenOuterEntry(root, "SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg");
            var manifest = Path.Combine(FindRepositoryRoot(), ".config", "dotnet-tools.json");

            var result = await RunValidatorFunctionsAsync(
                $"Assert-DeltaReconstructsFull '{PsQuote(basePackage)}' '{PsQuote(deltaPackage)}' " +
                $"'{PsQuote(currentPackage)}' '{PsQuote(manifest)}'");

            Assert.True(result.ExitCode == 0, result.Output);
        }
        finally { DeleteDirectoryTree(root); }
    }

    [Fact]
    public async Task DeltaReconstructionRejectsRandomPatchEvenWhenDeclarationsRemainPlausible()
    {
        var root = Path.Combine(FindRepositoryRoot(), ".artifacts", $"ota-delta-random-patch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var basePackage = ExtractGoldenOuterEntry(root, "SuperPan.BaiduBatchTransfer-1.2.2-full.nupkg");
            var deltaPackage = ExtractGoldenOuterEntry(root, "SuperPan.BaiduBatchTransfer-1.2.3-delta.nupkg");
            var currentPackage = ExtractGoldenOuterEntry(root, "SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg");
            MutateZipEntry(deltaPackage,
                name => name.StartsWith("lib/app/BaiduBatchTransfer.App.exe.", StringComparison.Ordinal) &&
                        (name.EndsWith(".diff", StringComparison.Ordinal) || name.EndsWith(".zsdiff", StringComparison.Ordinal)),
                bytes =>
                {
                    var replacement = new byte[bytes.Length];
                    new Random(1729).NextBytes(replacement);
                    return replacement;
                });

            var result = await RunValidatorFunctionsAsync(
                $"Assert-DeltaReconstructsFull '{PsQuote(basePackage)}' '{PsQuote(deltaPackage)}' " +
                $"'{PsQuote(currentPackage)}' '{PsQuote(Path.Combine(FindRepositoryRoot(), ".config", "dotnet-tools.json"))}'");

            Assert.NotEqual(0, result.ExitCode);
        }
        finally { DeleteDirectoryTree(root); }
    }

    [Theory]
    [InlineData("lib/app/知道综合工具.dll")]
    [InlineData("lib/app/知道综合工具.deps.json")]
    [InlineData("lib/app/知道综合工具.runtimeconfig.json")]
    public async Task DeltaReconstructionRejectsTamperedRawAddedMainPayload(string entryName)
    {
        var root = Path.Combine(FindRepositoryRoot(), ".artifacts", $"ota-delta-raw-tamper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var basePackage = ExtractGoldenOuterEntry(root, "SuperPan.BaiduBatchTransfer-1.2.2-full.nupkg");
            var deltaPackage = ExtractGoldenOuterEntry(root, "SuperPan.BaiduBatchTransfer-1.2.3-delta.nupkg");
            var currentPackage = ExtractGoldenOuterEntry(root, "SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg");
            MutateZipEntry(deltaPackage, name => name == entryName, bytes => bytes.Concat(new byte[] { 0x7f }).ToArray());

            var result = await RunValidatorFunctionsAsync(
                $"Assert-DeltaReconstructsFull '{PsQuote(basePackage)}' '{PsQuote(deltaPackage)}' " +
                $"'{PsQuote(currentPackage)}' '{PsQuote(Path.Combine(FindRepositoryRoot(), ".config", "dotnet-tools.json"))}'");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("differs from the current Full package", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteDirectoryTree(root); }
    }

    [Fact]
    public async Task ContainedProcessKillsLogFloodingParentAndFileHoldingChild()
    {
        var root = Path.Combine(FindRepositoryRoot(), ".artifacts", $"ota-job-containment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var childScript = Path.Combine(root, "child.ps1");
        var parentScript = Path.Combine(root, "parent.ps1");
        var heldFile = Path.Combine(root, "held.txt");
        var pidFile = Path.Combine(root, "pids.txt");
        var stdout = Path.Combine(root, "stdout.log");
        var stderr = Path.Combine(root, "stderr.log");
        await File.WriteAllTextAsync(childScript,
            "$s=[IO.File]::Open($args[0],[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None);while($true){Start-Sleep -Milliseconds 100}");
        await File.WriteAllTextAsync(parentScript,
            "$c=Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$args[0],$args[1]) -PassThru;" +
            "[IO.File]::WriteAllText($args[2],\"$PID`n$($c.Id)\");$x='x'*8192;while($true){[Console]::Out.WriteLine($x)}");
        try
        {
            var result = await RunValidatorFunctionsAsync(
                $"$run=Invoke-ContainedProcess '{PsQuote(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))}' " +
                $"@('-NoProfile','-ExecutionPolicy','Bypass','-File','{PsQuote(parentScript)}','{PsQuote(childScript)}','{PsQuote(heldFile)}','{PsQuote(pidFile)}') " +
                $"'{PsQuote(root)}' '{PsQuote(stdout)}' '{PsQuote(stderr)}' 10000 65536 $null 0 1073741824 8;" +
                "if([string]::IsNullOrEmpty([string]$run.Failure)){throw 'contained process unexpectedly succeeded'};if(-not $run.SafeToCleanup){throw 'job did not become empty'}");
            Assert.True(result.ExitCode == 0, result.Output);
            Assert.True(File.Exists(pidFile), "The parent did not run before containment terminated it.");
            var pids = (await File.ReadAllLinesAsync(pidFile)).Select(int.Parse).ToArray();
            Assert.Equal(2, pids.Length);
            Assert.All(pids, pid => Assert.False(IsProcessAlive(pid), $"Contained PID {pid} survived."));
        }
        finally { DeleteDirectoryTree(root); }
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ContainedProcessEnforcesJobMemoryLimit()
    {
        var root = Path.Combine(FindRepositoryRoot(), ".artifacts", $"ota-job-memory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "allocate.ps1");
        var pidFile = Path.Combine(root, "pid.txt");
        await File.WriteAllTextAsync(script,
            "$ErrorActionPreference='Stop';[IO.File]::WriteAllText($args[0],[string]$PID);$x=New-Object byte[] (96MB);for($i=0;$i-lt $x.Length;$i+=4096){$x[$i]=1};while($true){Start-Sleep -Milliseconds 100}");
        try
        {
            var result = await RunValidatorFunctionsAsync(
                $"$run=Invoke-ContainedProcess '{PsQuote(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))}' " +
                $"@('-NoProfile','-ExecutionPolicy','Bypass','-File','{PsQuote(script)}','{PsQuote(pidFile)}') '{PsQuote(root)}' " +
                $"'{PsQuote(Path.Combine(root, "stdout.log"))}' '{PsQuote(Path.Combine(root, "stderr.log"))}' 10000 1048576 $null 0 83886080 4;" +
                "if([string]$run.Failure -notlike '*job resource limit*'){throw 'memory limit was not enforced'};if(-not $run.SafeToCleanup){throw 'job did not become empty'}");
            Assert.True(result.ExitCode == 0, result.Output);
            Assert.True(File.Exists(pidFile));
            Assert.False(IsProcessAlive(int.Parse(await File.ReadAllTextAsync(pidFile))));
        }
        finally { DeleteDirectoryTree(root); }
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ContainedProcessEnforcesActiveProcessLimit()
    {
        var root = Path.Combine(FindRepositoryRoot(), ".artifacts", $"ota-job-active-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var childScript = Path.Combine(root, "child.ps1");
        var parentScript = Path.Combine(root, "fanout.ps1");
        var pidFile = Path.Combine(root, "pids.txt");
        await File.WriteAllTextAsync(childScript, "while($true){Start-Sleep -Milliseconds 100}");
        await File.WriteAllTextAsync(parentScript,
            "$ids=[Collections.Generic.List[int]]::new();$ids.Add($PID);for($i=0;$i-lt 4;$i++){try{$c=Start-Process powershell.exe -ArgumentList @('-NoProfile','-File',$args[0]) -PassThru -ErrorAction Stop;$ids.Add($c.Id)}catch{}};" +
            "[IO.File]::WriteAllLines($args[1],[string[]]@($ids));while($true){Start-Sleep -Milliseconds 100}");
        try
        {
            var result = await RunValidatorFunctionsAsync(
                $"$run=Invoke-ContainedProcess '{PsQuote(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))}' " +
                $"@('-NoProfile','-ExecutionPolicy','Bypass','-File','{PsQuote(parentScript)}','{PsQuote(childScript)}','{PsQuote(pidFile)}') '{PsQuote(root)}' " +
                $"'{PsQuote(Path.Combine(root, "stdout.log"))}' '{PsQuote(Path.Combine(root, "stderr.log"))}' 1500 1048576 $null 0 1073741824 3;" +
                "if([string]::IsNullOrEmpty([string]$run.Failure)){throw 'active process limit was not enforced'};if(-not $run.SafeToCleanup){throw 'job did not become empty'}");
            Assert.True(result.ExitCode == 0, result.Output);
            var pids = (await File.ReadAllLinesAsync(pidFile)).Select(int.Parse).ToArray();
            Assert.InRange(pids.Length, 2, 3);
            Assert.All(pids, pid => Assert.False(IsProcessAlive(pid), $"Contained PID {pid} survived."));
        }
        finally { DeleteDirectoryTree(root); }
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("invalid-missing-main-exe-upload.zip", "missing required Velopack content: current/知道综合工具.exe")]
    [InlineData("invalid-bogus-inner-signed-upload.zip", "missing required Velopack content: current/知道综合工具.dll")]
    [InlineData("invalid-missing-main-deps-upload.zip", "missing required Velopack content: current/知道综合工具.deps.json")]
    [InlineData("invalid-missing-main-runtimeconfig-upload.zip", "missing required Velopack content: current/知道综合工具.runtimeconfig.json")]
    [InlineData("invalid-missing-legacy-upload.zip", "missing required Velopack content: current/BaiduBatchTransfer.App.exe")]
    [InlineData("invalid-wrong-mainexe-upload.zip", "metadata identity")]
    [InlineData("invalid-wrong-brand-upload.zip", "branding")]
    [InlineData("invalid-missing-icon-upload.zip", "canonical package icon")]
    [InlineData("invalid-tampered-inner-image-upload.zip", "image digest does not match")]
    [InlineData("invalid-wrong-inner-signer-upload.zip", "signer SPKI does not match")]
    [InlineData("invalid-legacy-architecture-upload.zip", "architecture must be x64")]
    [InlineData("invalid-main-copied-as-legacy-upload.zip", "compatibility launcher identity")]
    [InlineData("invalid-junk-delta-signed-upload.zip", "does not replace the legacy executable")]
    [InlineData("invalid-delta-missing-renamed-main-upload.zip", "does not add the renamed main application")]
    [InlineData("invalid-delta-oversized-shasum-upload.zip", "shasum exceeds the allowed size")]
    [InlineData("invalid-delta-empty-patch-upload.zip", "patch payload is empty")]
    public async Task RenamedBundleRejectsIncompleteOrUntrustedLayout(string fixtureName, string expectedError)
    {
        var fixture = Path.Combine(
            FindRepositoryRoot(), "tests", "BaiduBatchTransfer.App.Tests", "Updates", "Fixtures", "OtaProduction");
        var bundle = Path.Combine(fixture, fixtureName);
        var certificate = Path.Combine(fixture, "publisher.cer");
        Assert.True(File.Exists(bundle), $"Missing renamed rejection fixture: {bundle}");
        using var publicCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
        var spki = Convert.ToHexString(
            SHA256.HashData(publicCertificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        var result = await RunPowerShellAsync(
            ValidatorScriptPath(), "-BundlePath", bundle,
            "-PublisherCertificatePath", certificate,
            "-ExpectedPublisherSpkiSha256", spki,
            "-ExpectedVersion", "1.2.3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.Output, StringComparison.OrdinalIgnoreCase);
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

    private static async Task<ScriptResult> RunPowerShellAsync(string script, params string[] arguments) =>
        await RunPowerShellHostAsync("pwsh", script, arguments);

    private static async Task<ScriptResult> RunPowerShellHostAsync(string host, string script, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = host,
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

    private static (string Bundle, string Certificate, string Spki) GoldenFixture()
    {
        var fixture = Path.Combine(
            FindRepositoryRoot(), "tests", "BaiduBatchTransfer.App.Tests", "Updates", "Fixtures", "OtaProduction");
        var certificate = Path.Combine(fixture, "publisher.cer");
        using var publicCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
        var spki = Convert.ToHexString(
            SHA256.HashData(publicCertificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        return (Path.Combine(fixture, "valid-upload.zip"), certificate, spki);
    }

    private static async Task<ScriptResult> RunValidatorFunctionsAsync(string command)
    {
        var root = Path.Combine(FindRepositoryRoot(), ".artifacts", $"ota-validator-functions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var wrapper = Path.Combine(root, "invoke.ps1");
        var validator = ValidatorScriptPath();
        await File.WriteAllTextAsync(wrapper, $$"""
            $ErrorActionPreference='Stop'
            if ($PSVersionTable.PSEdition -eq 'Desktop') { Add-Type -AssemblyName System.Security } else { Add-Type -AssemblyName System.Security.Cryptography.Pkcs }
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile('{{PsQuote(validator)}}',[ref]$tokens,[ref]$errors)
            if ($errors.Count) { throw 'validator parse failed' }
            foreach($function in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)) { Invoke-Expression $function.Extent.Text }
            {{command}}
            """);
        try { return await RunPowerShellAsync(wrapper); }
        finally { DeleteDirectoryTree(root); }
    }

    private static string ExtractGoldenInnerEntry(string root, string outerEntryName, string innerEntryName, string outputName)
    {
        var (bundle, _, _) = GoldenFixture();
        using var outer = ZipFile.OpenRead(bundle);
        using var innerBytes = new MemoryStream();
        using (var input = outer.GetEntry(outerEntryName)!.Open()) input.CopyTo(innerBytes);
        innerBytes.Position = 0;
        using var inner = new ZipArchive(innerBytes, ZipArchiveMode.Read);
        var output = Path.Combine(root, outputName);
        using (var input = inner.GetEntry(innerEntryName)!.Open())
        using (var destination = File.Create(output)) input.CopyTo(destination);
        return output;
    }

    private static string ExtractGoldenOuterEntry(string root, string outerEntryName)
    {
        var (bundle, _, _) = GoldenFixture();
        using var outer = ZipFile.OpenRead(bundle);
        var output = Path.Combine(root, outerEntryName);
        using var input = outer.GetEntry(outerEntryName)!.Open();
        using var destination = File.Create(output);
        input.CopyTo(destination);
        return output;
    }

    private static void MutateZipEntry(string zipPath, Func<string, bool> predicate, Func<byte[], byte[]> mutate)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        var matches = archive.Entries.Where(entry => predicate(entry.FullName)).ToArray();
        Assert.Single(matches);
        var name = matches[0].FullName;
        byte[] bytes;
        using (var input = matches[0].Open())
        using (var memory = new MemoryStream())
        {
            input.CopyTo(memory);
            bytes = memory.ToArray();
        }
        matches[0].Delete();
        var replacement = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var output = replacement.Open();
        var mutated = mutate(bytes);
        output.Write(mutated);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private static string PsQuote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string BuildScriptPath() => Path.Combine(FindRepositoryRoot(), "scripts", "build_ota_release.ps1");
    private static string ValidatorScriptPath() => Path.Combine(FindRepositoryRoot(), "scripts", "Test-OtaUploadBundle.ps1");

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, string.Join(Environment.NewLine, stdout, stderr));
    }

    private static void DeleteDirectoryTree(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        File.SetAttributes(root, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
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

    private sealed record ScriptResult(int ExitCode, string Output);
}
