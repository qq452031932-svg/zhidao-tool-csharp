using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class ProductionLicenseBuildScriptTests
{
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
        Assert.Contains("ImportSubjectPublicKeyInfo", script, StringComparison.Ordinal);
        Assert.Contains("VerifyData", script, StringComparison.Ordinal);
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
}
