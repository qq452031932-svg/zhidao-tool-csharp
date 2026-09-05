using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BaiduBatchTransfer.App.Updates;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class OtaPublisherTrustTests
{
    private const string FixedSubject = "CN=SuperPan OTA Publisher";
    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    [Fact]
    public void EmbeddedCertificateIsTheExactPinnedRsa3072PublicIdentity()
    {
        var repositoryRoot = FindRepositoryRoot();
        var repositoryCertificate = File.ReadAllBytes(Path.Combine(
            repositoryRoot,
            "eng",
            "ota",
            "SuperPan.OtaPublisher.cer"));
        using var resource = typeof(OtaPublisherTrust).Assembly.GetManifestResourceStream(
            OtaPublisherTrust.EmbeddedCertificateResourceName);

        Assert.NotNull(resource);
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        Assert.Equal(repositoryCertificate, memory.ToArray());

        using var certificate = new X509Certificate2(repositoryCertificate);
        Assert.Equal(repositoryCertificate, certificate.RawData);
        Assert.Equal(FixedSubject, certificate.Subject);
        Assert.False(certificate.HasPrivateKey);
        var keyUsage = Assert.Single(certificate.Extensions.OfType<X509KeyUsageExtension>());
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, keyUsage.KeyUsages);
        var enhancedKeyUsage = Assert.Single(
            certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Equal(
            CodeSigningOid,
            Assert.Single(enhancedKeyUsage.EnhancedKeyUsages.Cast<Oid>()).Value);
        using var rsa = certificate.GetRSAPublicKey();
        Assert.NotNull(rsa);
        Assert.Equal(3072, rsa.KeySize);
        Assert.Equal(
            OtaPublisherTrust.SubjectPublicKeyInfoSha256,
            Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()))
                .ToLowerInvariant());
        Assert.Matches("^[0-9a-f]{64}$", OtaPublisherTrust.SubjectPublicKeyInfoSha256);
    }

    [Fact]
    public void CreateVerifiedPublicKeyReturnsOnlyThePinnedPublicRsaKey()
    {
        using var rsa = OtaPublisherTrust.CreateVerifiedPublicKey();

        Assert.Equal(3072, rsa.KeySize);
        Assert.Throws<CryptographicException>(() =>
        {
            _ = rsa.ExportParameters(includePrivateParameters: true);
        });
    }

    [Fact]
    public void EnrolledPrivateIdentityExistsOnlyAsOneNonExportableStoreKey()
    {
        using var publicCertificate = new X509Certificate2(File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(),
            "eng",
            "ota",
            "SuperPan.OtaPublisher.cer")));
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, FixedSubject, validOnly: false)
            .OfType<X509Certificate2>()
            .Where(certificate => certificate.HasPrivateKey)
            .ToArray();

        var privateIdentity = Assert.Single(matches);
        try
        {
            Assert.Equal(publicCertificate.Thumbprint, privateIdentity.Thumbprint);
            using var privateRsa = privateIdentity.GetRSAPrivateKey();
            Assert.NotNull(privateRsa);
            Assert.Equal(3072, privateRsa.KeySize);
            Assert.Throws<CryptographicException>(() =>
            {
                _ = privateRsa.ExportParameters(includePrivateParameters: true);
            });
        }
        finally
        {
            foreach (var certificate in matches)
                certificate.Dispose();
        }
    }

    [Fact]
    public void MissingEmbeddedCertificateFailsClosed()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            OtaPublisherTrust.LoadVerifiedPublicKey(
                typeof(OtaPublisherTrustTests).Assembly,
                "missing.ota.publisher.cer",
                new string('0', 64)));

        Assert.Contains("embedded", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedCertificateFailsClosed()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            OtaPublisherTrust.VerifyCertificate([0x30, 0x01, 0x00], new string('0', 64)));

        Assert.Contains("certificate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonRsaCertificateFailsClosed()
    {
        using var certificate = CreateEcdsaCertificate();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            OtaPublisherTrust.VerifyCertificate(certificate.RawData, ComputeSpkiPin(certificate)));

        Assert.Contains("RSA", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortRsaCertificateFailsClosed()
    {
        using var certificate = CreateRsaCertificate(2048);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            OtaPublisherTrust.VerifyCertificate(certificate.RawData, ComputeSpkiPin(certificate)));

        Assert.Contains("3072", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongSpkiPinFailsClosed()
    {
        using var certificate = CreateRsaCertificate(3072);
        using var anotherCertificate = CreateRsaCertificate(3072);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            OtaPublisherTrust.VerifyCertificate(certificate.RawData, ComputeSpkiPin(anotherCertificate)));

        Assert.Contains("SPKI", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingRsa3072CertificateReturnsPublicKeyOnly()
    {
        using var certificate = CreateRsaCertificate(3072);

        using var rsa = OtaPublisherTrust.VerifyCertificate(
            certificate.RawData,
            ComputeSpkiPin(certificate));

        Assert.Equal(3072, rsa.KeySize);
        Assert.Throws<CryptographicException>(() =>
        {
            _ = rsa.ExportParameters(includePrivateParameters: true);
        });
    }

    [Fact]
    public void BuildTargetsEmbedExactlyTheRepositoryPublicCertificate()
    {
        var targets = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Directory.Build.targets"));

        Assert.Contains("'$(MSBuildProjectName)' == 'BaiduBatchTransfer.App'", targets, StringComparison.Ordinal);
        Assert.Contains("eng\\ota\\SuperPan.OtaPublisher.cer", targets, StringComparison.Ordinal);
        Assert.Contains(OtaPublisherTrust.EmbeddedCertificateResourceName, targets, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateKey", targets, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".pfx", targets, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnrollmentScriptHasFailClosedNonExportableIdentityPolicy()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "create_ota_publisher_certificate.ps1");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("CN=SuperPan OTA Publisher", script, StringComparison.Ordinal);
        Assert.Contains("StoreName]::My", script, StringComparison.Ordinal);
        Assert.Contains("StoreLocation]::CurrentUser", script, StringComparison.Ordinal);
        Assert.Contains("CngAlgorithm]::Rsa", script, StringComparison.Ordinal);
        Assert.Contains("GetBytes(3072)", script, StringComparison.Ordinal);
        Assert.Contains("MicrosoftSoftwareKeyStorageProvider", script, StringComparison.Ordinal);
        Assert.Contains("HashAlgorithmName]::SHA256", script, StringComparison.Ordinal);
        Assert.Contains("CngExportPolicies]::None", script, StringComparison.Ordinal);
        Assert.Contains("CngKeyUsages]::Signing", script, StringComparison.Ordinal);
        Assert.Contains("$PSVersionTable.PSVersion.Major -lt 7", script, StringComparison.Ordinal);
        Assert.Contains("Get-Command 'pwsh'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-KeySpec Signature", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CodeSigningOid, script, StringComparison.Ordinal);
        Assert.Contains("DigitalSignature", script, StringComparison.Ordinal);
        Assert.Contains("File]::Replace", script, StringComparison.Ordinal);
        Assert.Contains("publisher-thumbprint.txt", script, StringComparison.Ordinal);
        Assert.Contains("multiple", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SPKI", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".pfx", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pkcs12", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Export-PfxCertificate", script, StringComparison.OrdinalIgnoreCase);

        var creationFunctionStart = script.IndexOf("function New-PublisherCertificate", StringComparison.Ordinal);
        var creationFunctionEnd = script.IndexOf(
            "function Invoke-OtaPublisherCertificateEnrollment",
            creationFunctionStart,
            StringComparison.Ordinal);
        var creationFunction = script[creationFunctionStart..creationFunctionEnd];
        Assert.True(
            creationFunction.IndexOf("Test-PublisherCertificate", StringComparison.Ordinal) is var validationIndex &&
            validationIndex >= 0 &&
            validationIndex < creationFunction.IndexOf("$Store.Add", StringComparison.Ordinal),
            "The new private identity must pass policy validation before it is added to CurrentUser\\My.");
        Assert.Contains("$Store.Remove($certificate)", creationFunction, StringComparison.Ordinal);
        Assert.Contains("$cngKey.Delete()", creationFunction, StringComparison.Ordinal);
        Assert.True(
            creationFunction.IndexOf("$rsa.Dispose()", StringComparison.Ordinal) <
            creationFunction.IndexOf("$cngKey.Delete()", StringComparison.Ordinal),
            "The RSA wrapper must be disposed before deleting its named CNG key after failed creation.");

        var pfxFiles = Directory.EnumerateFiles(repositoryRoot, "*.pfx", SearchOption.AllDirectories);
        Assert.Empty(pfxFiles);
    }

    [Fact]
    public async Task EnrollmentScriptParsesWithoutExecutingOrTouchingCertificateStore()
    {
        var scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "create_ota_publisher_certificate.ps1");
        var escapedPath = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        var command = string.Join(
            Environment.NewLine,
            "$tokens = $null",
            "$errors = $null",
            $"[void][System.Management.Automation.Language.Parser]::ParseFile('{escapedPath}', [ref]$tokens, [ref]$errors)",
            "if ($errors.Count -ne 0) { $errors | ForEach-Object { Write-Error $_ }; exit 1 }");

        var result = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-Command", command]);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task EnrollmentScriptAtomicallyOverwritesAnExistingPublicOutput()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bbt-ota-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var outputPath = Path.Combine(directory, "public-output.cer");
            var scriptPath = Path.Combine(
                FindRepositoryRoot(),
                "scripts",
                "create_ota_publisher_certificate.ps1");
            var command = string.Join(
                Environment.NewLine,
                $". '{EscapePowerShellLiteral(scriptPath)}'",
                $"Write-BytesAtomically -Path '{EscapePowerShellLiteral(outputPath)}' -Bytes ([byte[]]@(1, 2, 3))",
                $"Write-BytesAtomically -Path '{EscapePowerShellLiteral(outputPath)}' -Bytes ([byte[]]@(4, 5, 6))",
                $"if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]](Get-Content -AsByteStream -Raw -LiteralPath '{EscapePowerShellLiteral(outputPath)}'), [byte[]]@(4, 5, 6))) {{ exit 2 }}");

            var result = await ProcessRunner.RunAsync("pwsh.exe", ["-NoProfile", "-Command", command]);

            Assert.True(result.ExitCode == 0, result.Output);
            Assert.Equal([4, 5, 6], File.ReadAllBytes(outputPath));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.bak"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PublisherPolicyRequiresIssuerEqualityAndAValidSelfSignature()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bbt-ota-self-signature-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var validCertificate = CreateRsaCertificate(3072);
            using var issuerMismatchCertificate = CreateIssuerSignedPublisherCertificate();
            var forgedCertificate = validCertificate.RawData.ToArray();
            forgedCertificate[^1] ^= 0x01;

            var validPath = Path.Combine(directory, "valid.cer");
            var issuerMismatchPath = Path.Combine(directory, "issuer-mismatch.cer");
            var forgedPath = Path.Combine(directory, "forged-signature.cer");
            File.WriteAllBytes(validPath, validCertificate.RawData);
            File.WriteAllBytes(issuerMismatchPath, issuerMismatchCertificate.RawData);
            File.WriteAllBytes(forgedPath, forgedCertificate);

            var command = string.Join(
                Environment.NewLine,
                $". '{EscapePowerShellLiteral(GetEnrollmentScriptPath())}'",
                "function Assert-PolicyResult([string]$Path, [bool]$Expected) {",
                "  $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([System.IO.File]::ReadAllBytes($Path))",
                "  try { $actual = Test-PublisherCertificate -Certificate $certificate -ExpectedSpkiSha256 $null -RequirePrivateKey $false } finally { $certificate.Dispose() }",
                "  if ($actual -ne $Expected) { throw \"Unexpected publisher policy result for ${Path}: $actual\" }",
                "}",
                $"Assert-PolicyResult '{EscapePowerShellLiteral(validPath)}' $true",
                $"Assert-PolicyResult '{EscapePowerShellLiteral(issuerMismatchPath)}' $false",
                $"Assert-PolicyResult '{EscapePowerShellLiteral(forgedPath)}' $false");

            var result = await ProcessRunner.RunAsync("pwsh.exe", ["-NoProfile", "-Command", command]);

            Assert.True(result.ExitCode == 0, result.Output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SelectionDecisionIsPureFailClosedAndIdempotent()
    {
        var command = string.Join(
            Environment.NewLine,
            $". '{EscapePowerShellLiteral(GetEnrollmentScriptPath())}'",
            "$validator = { param($certificate, $expectedSpki) if (-not $certificate.Valid) { return $false }; if ($expectedSpki) { return $certificate.Spki -ceq $expectedSpki }; return $true }",
            "$candidate = [pscustomobject]@{ Id = 'publisher'; Valid = $true; Spki = 'pin-a' }",
            "$first = Resolve-PublisherCertificateSelection -SubjectPrivateCertificates @($candidate) -RepositorySpkiSha256 'pin-a' -CertificateValidator $validator",
            "$second = Resolve-PublisherCertificateSelection -SubjectPrivateCertificates @($candidate) -RepositorySpkiSha256 'pin-a' -CertificateValidator $validator",
            "if ($first.Action -ne 'reused' -or $second.Action -ne 'reused') { throw 'Existing identity was not reused.' }",
            "if (-not [object]::ReferenceEquals($first.Certificate, $candidate) -or -not [object]::ReferenceEquals($second.Certificate, $candidate)) { throw 'Selection did not return the exact candidate.' }",
            "$create = Resolve-PublisherCertificateSelection -SubjectPrivateCertificates @() -RepositorySpkiSha256 $null -CertificateValidator $validator",
            "if ($create.Action -ne 'create' -or $null -ne $create.Certificate) { throw 'Empty enrollment did not request creation.' }",
            "$mismatchFailed = $false",
            "try { [void](Resolve-PublisherCertificateSelection -SubjectPrivateCertificates @($candidate) -RepositorySpkiSha256 'pin-b' -CertificateValidator $validator) } catch { $mismatchFailed = $_.Exception.Message -match 'disagree' }",
            "if (-not $mismatchFailed) { throw 'Repository/store SPKI mismatch did not fail closed.' }",
            "$multipleFailed = $false",
            "try { [void](Resolve-PublisherCertificateSelection -SubjectPrivateCertificates @($candidate, $candidate) -RepositorySpkiSha256 'pin-a' -CertificateValidator $validator) } catch { $multipleFailed = $_.Exception.Message -match 'multiple' }",
            "if (-not $multipleFailed) { throw 'Multiple fixed-subject identities did not fail closed.' }");

        var result = await ProcessRunner.RunAsync("pwsh.exe", ["-NoProfile", "-Command", command]);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task ExistingPublisherVerificationCanRunTwiceWithoutCreatingAnotherIdentity()
    {
        var repositoryCertificatePath = Path.Combine(
            FindRepositoryRoot(),
            "eng",
            "ota",
            "SuperPan.OtaPublisher.cer");
        var command = string.Join(
            Environment.NewLine,
            $". '{EscapePowerShellLiteral(GetEnrollmentScriptPath())}'",
            $"$repositoryCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([System.IO.File]::ReadAllBytes('{EscapePowerShellLiteral(repositoryCertificatePath)}'))",
            "$store = [System.Security.Cryptography.X509Certificates.X509Store]::new([System.Security.Cryptography.X509Certificates.StoreName]::My, [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)",
            "try {",
            "  $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)",
            "  $candidates = @($store.Certificates | Where-Object { $_.Subject -ceq 'CN=SuperPan OTA Publisher' -and $_.HasPrivateKey })",
            "  $pin = Get-SubjectPublicKeyInfoSha256 -Certificate $repositoryCertificate",
            "  $first = Resolve-PublisherCertificateSelection -SubjectPrivateCertificates $candidates -RepositorySpkiSha256 $pin",
            "  $second = Resolve-PublisherCertificateSelection -SubjectPrivateCertificates $candidates -RepositorySpkiSha256 $pin",
            "  if ($candidates.Count -ne 1 -or $first.Action -ne 'reused' -or $second.Action -ne 'reused') { throw 'Read-only reuse verification was not stable.' }",
            "  if ($first.Certificate.Thumbprint -cne $repositoryCertificate.Thumbprint -or $second.Certificate.Thumbprint -cne $repositoryCertificate.Thumbprint) { throw 'Read-only reuse selected the wrong identity.' }",
            "} finally { $store.Dispose(); $repositoryCertificate.Dispose() }");

        var result = await ProcessRunner.RunAsync("pwsh.exe", ["-NoProfile", "-Command", command]);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static X509Certificate2 CreateRsaCertificate(int keySize)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest(FixedSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddPublisherExtensions(request);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 CreateEcdsaCertificate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(FixedSubject, ecdsa, HashAlgorithmName.SHA256);
        AddPublisherExtensions(request);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 CreateIssuerSignedPublisherCertificate()
    {
        using var issuerKey = RSA.Create(3072);
        var issuerRequest = new CertificateRequest(
            "CN=Different OTA Issuer",
            issuerKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));
        using var issuer = issuerRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddDays(2));

        using var publisherKey = RSA.Create(3072);
        var publisherRequest = new CertificateRequest(
            FixedSubject,
            publisherKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        AddPublisherExtensions(publisherRequest);
        return publisherRequest.Create(
            issuer,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            RandomNumberGenerator.GetBytes(16));
    }

    private static void AddPublisherExtensions(CertificateRequest request)
    {
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            critical: true));
        var enhancedKeyUsages = new OidCollection
        {
            new Oid(CodeSigningOid),
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            enhancedKeyUsages,
            critical: true));
    }

    private static string ComputeSpkiPin(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()))
            .ToLowerInvariant();

    private static string EscapePowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string GetEnrollmentScriptPath() =>
        Path.Combine(FindRepositoryRoot(), "scripts", "create_ota_publisher_certificate.ps1");

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static class ProcessRunner
    {
        public static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start {fileName}.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            return new ProcessResult(
                process.ExitCode,
                string.Join(Environment.NewLine, await standardOutput, await standardError));
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
