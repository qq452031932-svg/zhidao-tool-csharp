using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class ProductionLicenseBuildScriptTests
{
    [Fact]
    public async Task ProductionScriptsStartUnderWindowsPowerShell51AndAvoidModernRuntimeApis()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Root();
        var zero40 = new string('0', 40);
        var zero64 = new string('0', 64);
        var one64 = new string('1', 64);
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signingSpki = Convert.ToBase64String(signingKey.ExportSubjectPublicKeyInfo());
        var build = await RunPowerShellAsync(
            "build_production_license_clients.ps1",
            "-SourceCommit", zero40,
            "-LicenseSigningKeyId", "compatibility-test",
            "-LicenseSigningPublicKey", signingSpki,
            "-CurrentTlsSpkiPin", zero64,
            "-NextTlsSpkiPin", one64,
            "-SigningCertificateThumbprint", zero40,
            "-TimestampUrl", "http://timestamp.digicert.com",
            "-OutputDirectory", Path.Combine(root, ".artifacts", "license-production-clients-compatibility-test"),
            "-ManagedBrowserHelperPackageDirectory", Path.Combine(root, ".artifacts", "missing-helper-package"));
        Assert.NotEqual(0, build.ExitCode);
        Assert.Contains("Managed-browser helper package path is invalid", build.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("ScriptRequiresUnmatchedPSVersion", build.Error, StringComparison.OrdinalIgnoreCase);

        var rotation = await RunPowerShellAsync(
            "test_license_spki_rotation.ps1",
            "-Endpoint", "https://example.invalid",
            "-ClientRootA", root,
            "-ClientRootBC", root,
            "-ExpectedSourceCommit", zero40,
            "-ApprovedSigningCertificateThumbprint", zero40,
            "-ServerHost", "example.invalid",
            "-ProbeStoreRoot", Path.Combine(root, ".tmp", "spki-compatibility-probe"),
            "-ProbeUtcNow", "2026-01-01T00:00:00Z",
            "-EvidencePath", Path.Combine(root, ".tmp", "spki-compatibility-evidence.json"));
        Assert.NotEqual(0, rotation.ExitCode);
        Assert.Contains("exact production origin", rotation.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("ScriptRequiresUnmatchedPSVersion", rotation.Error, StringComparison.OrdinalIgnoreCase);

        var challenge = RandomNumberGenerator.GetBytes(32);
        var derSignature = signingKey.SignData(
            challenge,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var request = new CertificateRequest("CN=compatibility-test", signingKey, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(1));
        using var rsa = RSA.Create(2048);
        var rsaRequest = new CertificateRequest(
            "CN=compatibility-rsa-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var rsaCertificate = rsaRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(1));
        var rsaSpki = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        var quotedScript = QuotePowerShellLiteral(Path.Combine(root, "scripts", "test_license_spki_rotation.ps1"));
        var cryptoProbe = await RunPowerShellCommandAsync($$"""
            $ErrorActionPreference = 'Stop'
            try {
                . {{quotedScript}} -Endpoint 'https://example.invalid' -ClientRootA '.' -ClientRootBC '.' -ExpectedSourceCommit '{{zero40}}' -ApprovedSigningCertificateThumbprint '{{zero40}}' -ServerHost 'example.invalid' -ProbeStoreRoot '.' -ProbeUtcNow '2026-01-01T00:00:00Z' -EvidencePath 'evidence.json'
            }
            catch {
                if ($_.Exception.Message -notlike '*exact production origin*') { throw }
            }
            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String('{{Convert.ToBase64String(certificate.Export(X509ContentType.Cert))}}'))
            try {
                $spki = Get-CertificateSubjectPublicKeyInfo $certificate
                if ([Convert]::ToBase64String($spki) -cne '{{signingSpki}}') { throw 'Certificate SPKI reconstruction mismatch.' }
                $cngKey = Import-P256SubjectPublicKeyInfo $spki
                try {
                    $ecdsa = [Security.Cryptography.ECDsaCng]::new($cngKey)
                    try {
                        $ecdsa.HashAlgorithm = [Security.Cryptography.CngAlgorithm]::Sha256
                        $signature = Convert-DerEcdsaSignatureToP1363 ([Convert]::FromBase64String('{{Convert.ToBase64String(derSignature)}}'))
                        if (-not $ecdsa.VerifyData([Convert]::FromBase64String('{{Convert.ToBase64String(challenge)}}'), $signature)) { throw 'ECDSA compatibility verification failed.' }
                    }
                    finally { $ecdsa.Dispose() }
                }
                finally { $cngKey.Dispose() }
            }
            finally { $certificate.Dispose() }
            $rsaCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String('{{Convert.ToBase64String(rsaCertificate.Export(X509ContentType.Cert))}}'))
            try {
                if ([Convert]::ToBase64String((Get-CertificateSubjectPublicKeyInfo $rsaCertificate)) -cne '{{rsaSpki}}') { throw 'RSA certificate SPKI reconstruction mismatch.' }
            }
            finally { $rsaCertificate.Dispose() }
            'compatibility crypto ok'
            """);
        Assert.Equal(0, cryptoProbe.ExitCode);
        Assert.Contains("compatibility crypto ok", cryptoProbe.Output, StringComparison.Ordinal);

        foreach (var scriptName in new[] { "build_production_license_clients.ps1", "test_license_spki_rotation.ps1" })
        {
            var script = File.ReadAllText(Path.Combine(root, "scripts", scriptName));
            foreach (var forbidden in new[]
            {
                "#requires -Version 7", "[Convert]::ToHexString", "[Security.Cryptography.SHA256]::HashData",
                "[IO.Path]::GetRelativePath", ".ArgumentList", "RandomNumberGenerator]::GetBytes",
                ".ImportSubjectPublicKeyInfo", ".ExportSubjectPublicKeyInfo", "DSASignatureFormat"
            })
                Assert.DoesNotContain(forbidden, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WrapperPinsTrustAndRejectsSecretBearingInputs()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "scripts", "build_production_license_clients.ps1"));

        Assert.Contains("https://ota.fanfh.cn", script, StringComparison.Ordinal);
        Assert.Contains("8270e8d7-1791-487d-ad76-0da7a0cbed12", script, StringComparison.Ordinal);
        Assert.Contains("SigningCertificateThumbprint", script, StringComparison.Ordinal);
        Assert.Contains("Cert:\\CurrentUser\\My", script, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", script, StringComparison.Ordinal);
        Assert.Contains("SHA256", script, StringComparison.Ordinal);
        Assert.Contains("git", script, StringComparison.Ordinal);
        Assert.Contains("status", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$PfxPath", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$CertificatePath", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$Password", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$SigningKeyFile", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("license-production-clients-", script, StringComparison.Ordinal);
        Assert.Contains("never an OTA stable feed", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetDirectoryName($output)", script, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePointAncestors", script, StringComparison.Ordinal);
        Assert.Contains("ManagedBrowserHelperPackageDirectory -match", script, StringComparison.Ordinal);
        Assert.Contains("git -C $repo archive", script, StringComparison.Ordinal);
        Assert.Contains("SignedCms", script, StringComparison.Ordinal);
        Assert.Contains("Source changed during", script, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet('http://timestamp.digicert.com')]", script, StringComparison.Ordinal);
        Assert.Contains("$timestamp.AbsoluteUri -cne 'http://timestamp.digicert.com/'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WrapperKeepsMsBuildPropertyNamesAndValuesInSingleArguments()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "scripts", "build_production_license_clients.ps1"));

        Assert.Contains("\"-p:LicenseSigningKeyId=$LicenseSigningKeyId\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-p:LicenseSigningPublicKey=$LicenseSigningPublicKey\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-p:LicenseCurrentSpkiPin=$CurrentTlsSpkiPin\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-p:LicenseNextSpkiPin=$NextTlsSpkiPin\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-p:LicenseServiceUrl=$origin\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-p:LicenseProductId=$superPanProduct\"", script, StringComparison.Ordinal);
        Assert.Contains("\"-p:ManagedBrowserHelperPackageDirectory=$helperPackage\"", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("build_production_license_clients.ps1", "$productIdentity.ProductMainExecutableName")]
    [InlineData("test_license_spki_rotation.ps1", "$productIdentity.ProductMainExecutableName")]
    public void ProductionScriptsLoadCanonicalProductIdentityOnce(string scriptName, string expectedIdentityUse)
    {
        var script = File.ReadAllText(Path.Combine(Root(), "scripts", scriptName));

        Assert.Equal(1, CountOccurrences(script, ". (Join-Path $PSScriptRoot 'ProductIdentity.ps1')"));
        Assert.Equal(1, CountOccurrences(script, "Get-ProductIdentity"));
        Assert.Contains(expectedIdentityUse, script, StringComparison.Ordinal);
        Assert.DoesNotContain("知道综合工具", script, StringComparison.Ordinal);
        Assert.DoesNotContain("BaiduBatchTransfer.App.exe", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RotationRehearsalIsFailClosedAndRedacted()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "scripts", "test_license_spki_rotation.ps1"));

        Assert.Contains("A-to-B-to-A", script, StringComparison.Ordinal);
        Assert.Contains("B-to-C", script, StringComparison.Ordinal);
        Assert.Contains("challenge", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("response-signing", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("private material", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[string]$Card", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scriptblock", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SslStream", script, StringComparison.Ordinal);
        Assert.Contains("Import-P256SubjectPublicKeyInfo", script, StringComparison.Ordinal);
        Assert.Contains("VerifyData", script, StringComparison.Ordinal);
        Assert.Contains("Convert-DerEcdsaSignatureToP1363", script, StringComparison.Ordinal);
        Assert.Contains("signingSpkiDerBase64", script, StringComparison.Ordinal);
        Assert.Contains("currentTlsSpki", script, StringComparison.Ordinal);
        Assert.Contains("Read-VerifiedClientRoot", script, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", script, StringComparison.Ordinal);
        Assert.Contains("RandomNumberGenerator", script, StringComparison.Ordinal);
        Assert.Contains("root@", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-ExactSmoke", script, StringComparison.Ordinal);
        Assert.Contains("ApprovedSigningCertificateThumbprint", script, StringComparison.Ordinal);
        Assert.Contains("CheckSignature($false)", script, StringComparison.Ordinal);
        Assert.Contains("$productIdentity.ProductMainExecutableName", script, StringComparison.Ordinal);
        Assert.Contains("signtool.exe", script, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparseContainedPath", script, StringComparison.Ordinal);
        Assert.Contains("$cursor=$cursor.Parent", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".TryAdd(", script, StringComparison.Ordinal);
        Assert.Contains("$expected.ContainsKey($path)", script, StringComparison.Ordinal);
        Assert.Contains("$expected.Add($path,$file.sha256)", script, StringComparison.Ordinal);
        Assert.Contains("tr -d ''\\r\\n'' | base64 -d", script, StringComparison.Ordinal);
        Assert.Contains("/etc/superpan-license/response-signing.key", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/etc/superpan-license/response-signing.pem", script, StringComparison.Ordinal);
    }

    private static string Root([CallerFilePath] string path = "") =>
        Directory.GetParent(path)!.Parent!.Parent!.Parent!.FullName;

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static async Task<(int ExitCode, string Output, string Error)> RunPowerShellAsync(
        string scriptName,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(Path.Combine(Root(), "scripts", scriptName));
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunPowerShellCommandAsync(string command)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-EncodedCommand");
        process.StartInfo.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)));
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    private static string QuotePowerShellLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
