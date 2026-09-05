using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class OtaPublisherEnrollmentWorkflowTests
{
    [Fact]
    public async Task PublisherMutexUsesGlobalNamespaceAndCurrentUserOnlySecurity()
    {
        var command = string.Join(
            Environment.NewLine,
            DotSourceEnrollmentScript(),
            "$testSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-21-1-2-3-1001')",
            "$expectedName = 'Global\\SuperPan.OtaPublisher.Enrollment.' + $testSid.Value",
            "$actualName = Get-PublisherEnrollmentMutexName -CurrentSid $testSid",
            "if ($actualName -cne $expectedName) { throw \"Mutex name is not cross-session and per-user: $actualName\" }",
            "$security = New-PublisherEnrollmentMutexSecurity -CurrentSid $testSid",
            "if (-not $security.AreAccessRulesProtected) { throw 'Mutex DACL permits inherited access.' }",
            "$owner = $security.GetOwner([System.Security.Principal.SecurityIdentifier])",
            "if ($owner.Value -cne $testSid.Value) { throw 'Mutex owner is not the current user SID.' }",
            "$rules = @($security.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))",
            "if ($rules.Count -ne 1) { throw \"Expected one explicit access rule, got $($rules.Count).\" }",
            "$rule = $rules[0]",
            "if ($rule.IdentityReference.Value -cne $testSid.Value -or $rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow -or $rule.MutexRights -ne [System.Security.AccessControl.MutexRights]::FullControl) { throw 'Mutex access is not restricted to the current user SID.' }",
            "$realSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User",
            "$disposableName = 'Global\\SuperPan.OtaPublisher.Enrollment.Test.' + $realSid.Value + '.' + [Guid]::NewGuid().ToString('N')",
            "$mutex = New-PublisherEnrollmentMutex -MutexName $disposableName -CurrentSid $realSid",
            "try { Assert-PublisherEnrollmentMutexSecurity -MutexName $disposableName -CurrentSid $realSid } finally { $mutex.Dispose() }");

        var result = await RunAsync("pwsh.exe", ["-NoProfile", "-Command", command]);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task PublisherMutexFailsClosedWhenAclConstructionOrValidationIsUnavailable()
    {
        var command = string.Join(
            Environment.NewLine,
            DotSourceEnrollmentScript(),
            "$sid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-21-1-2-3-1001')",
            "$state = [pscustomobject]@{ MutexCreates = 0; Disposals = 0; Validations = 0 }",
            "$securityFailure = $false",
            "try { [void](New-PublisherEnrollmentMutex -MutexName 'Global\\acl-construction-failure' -CurrentSid $sid -SecurityFactory { param($currentSid) throw 'injected ACL construction unavailable' } -MutexFactory { param($name, $security) $state.MutexCreates++; throw 'must not create mutex' }) } catch { $securityFailure = $_.Exception.ToString() -match 'ACL construction unavailable' }",
            "if (-not $securityFailure -or $state.MutexCreates -ne 0) { throw 'ACL construction failure did not stop mutex creation.' }",
            "$fakeMutex = [pscustomobject]@{ }",
            "$fakeMutex | Add-Member ScriptMethod Dispose { $state.Disposals++ }",
            "$validationFailure = $false",
            "try { [void](New-PublisherEnrollmentMutex -MutexName 'Global\\acl-validation-failure' -CurrentSid $sid -SecurityFactory { param($currentSid) return [pscustomobject]@{} } -MutexFactory { param($name, $security) $state.MutexCreates++; return $fakeMutex } -SecurityValidator { param($name, $currentSid) $state.Validations++; throw 'injected ACL validation unavailable' }) } catch { $validationFailure = $_.Exception.ToString() -match 'ACL validation unavailable' }",
            "if (-not $validationFailure -or $state.MutexCreates -ne 1 -or $state.Validations -ne 1 -or $state.Disposals -ne 1) { throw 'ACL validation failure was not fail-closed and disposed.' }");

        var result = await RunAsync("pwsh.exe", ["-NoProfile", "-Command", command]);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task CngExportPolicyRequiresExactlyNone()
    {
        var command = string.Join(
            Environment.NewLine,
            DotSourceEnrollmentScript(),
            "$none = [System.Security.Cryptography.CngExportPolicies]::None",
            "if (-not (Test-CngExportPolicyIsStrictlyNonExportable $none)) { throw 'None was rejected.' }",
            "$nonzeroPolicies = @(",
            "  [System.Security.Cryptography.CngExportPolicies]::AllowExport,",
            "  [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport,",
            "  [System.Security.Cryptography.CngExportPolicies]::AllowArchiving,",
            "  [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextArchiving)",
            "foreach ($policy in $nonzeroPolicies) { if (Test-CngExportPolicyIsStrictlyNonExportable $policy) { throw \"Nonzero export policy was accepted: $policy\" } }",
            "$combined = [System.Security.Cryptography.CngExportPolicies]::AllowExport -bor [System.Security.Cryptography.CngExportPolicies]::AllowArchiving",
            "if (Test-CngExportPolicyIsStrictlyNonExportable $combined) { throw 'Combined export policy was accepted.' }");

        var result = await RunAsync("pwsh.exe", ["-NoProfile", "-Command", command]);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task HermeticWorkflowCreatesOnceThenReusesIdenticalPublicOutputs()
    {
        var command = string.Join(
            Environment.NewLine,
            DotSourceEnrollmentScript(),
            "$state = [pscustomobject]@{ Candidates = @(); Creates = 0; PublicWrites = 0; ThumbWrites = 0; PublicBytes = $null; Thumbprint = $null }",
            "$identity = [pscustomobject]@{ Valid = $true; Spki = 'stable-pin'; PublicBytes = [byte[]]@(10, 20, 30); Thumbprint = 'ABC123' }",
            "$getCandidates = { @($state.Candidates) }",
            "$create = { $state.Creates++; $state.Candidates = @($identity); return $identity }",
            "$validatePublic = { param($certificate) return $certificate.Valid }",
            "$validatePrivate = { param($certificate, $expectedSpki) return $certificate.Valid -and (-not $expectedSpki -or $certificate.Spki -ceq $expectedSpki) }",
            "$getSpki = { param($certificate) return $certificate.Spki }",
            "$exportPublic = { param($certificate) return ,([byte[]]$certificate.PublicBytes) }",
            "$getThumbprint = { param($certificate) return $certificate.Thumbprint }",
            "$writePublic = { param([byte[]]$bytes) $state.PublicWrites++; $state.PublicBytes = [byte[]]$bytes }",
            "$writeThumbprint = { param([string]$thumbprint) $state.ThumbWrites++; $state.Thumbprint = $thumbprint }",
            "$first = Invoke-PublisherEnrollmentWorkflow -RepositoryCertificate $null -GetSubjectPrivateCertificates $getCandidates -CreatePublisherCertificate $create -ValidatePublicCertificate $validatePublic -ValidatePrivateCertificate $validatePrivate -GetSpkiSha256 $getSpki -ExportPublicCertificate $exportPublic -GetThumbprint $getThumbprint -WritePublicCertificate $writePublic -WriteThumbprint $writeThumbprint",
            "$firstBytes = [byte[]]$state.PublicBytes.Clone()",
            "$firstThumbprint = $state.Thumbprint",
            "$repositoryCertificate = [pscustomobject]@{ Valid = $true; Spki = 'stable-pin'; PublicBytes = [byte[]]$firstBytes; Thumbprint = $firstThumbprint }",
            "$second = Invoke-PublisherEnrollmentWorkflow -RepositoryCertificate $repositoryCertificate -GetSubjectPrivateCertificates $getCandidates -CreatePublisherCertificate $create -ValidatePublicCertificate $validatePublic -ValidatePrivateCertificate $validatePrivate -GetSpkiSha256 $getSpki -ExportPublicCertificate $exportPublic -GetThumbprint $getThumbprint -WritePublicCertificate $writePublic -WriteThumbprint $writeThumbprint",
            "if ($first.Action -ne 'created' -or $second.Action -ne 'reused') { throw 'Unexpected workflow actions.' }",
            "if ($state.Creates -ne 1 -or $state.PublicWrites -ne 2 -or $state.ThumbWrites -ne 2) { throw 'Workflow operation counts were not idempotent.' }",
            "if (-not [System.Linq.Enumerable]::SequenceEqual($firstBytes, [byte[]]$state.PublicBytes)) { throw 'Public bytes changed on reuse.' }",
            "if ($firstThumbprint -cne $state.Thumbprint -or $first.Thumbprint -cne $second.Thumbprint) { throw 'Thumbprint changed on reuse.' }");

        var result = await RunAsync("pwsh.exe", ["-NoProfile", "-Command", command]);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task HermeticWorkflowFailsClosedOnInjectedStoreAndOutputFailures()
    {
        var command = string.Join(
            Environment.NewLine,
            DotSourceEnrollmentScript(),
            "$state = [pscustomobject]@{ Creates = 0; PublicWrites = 0; ThumbWrites = 0 }",
            "$identity = [pscustomobject]@{ Valid = $true; Spki = 'stable-pin'; PublicBytes = [byte[]]@(1, 2, 3); Thumbprint = 'ABC123' }",
            "$validatePublic = { param($certificate) return $certificate.Valid }",
            "$validatePrivate = { param($certificate, $expectedSpki) return $certificate.Valid -and (-not $expectedSpki -or $certificate.Spki -ceq $expectedSpki) }",
            "$getSpki = { param($certificate) return $certificate.Spki }",
            "$exportPublic = { param($certificate) return ,([byte[]]$certificate.PublicBytes) }",
            "$getThumbprint = { param($certificate) return $certificate.Thumbprint }",
            "$noopPublic = { param([byte[]]$bytes) $state.PublicWrites++ }",
            "$noopThumb = { param([string]$thumbprint) $state.ThumbWrites++ }",
            "$storeReadFailed = $false",
            "try { [void](Invoke-PublisherEnrollmentWorkflow -RepositoryCertificate $null -GetSubjectPrivateCertificates { throw 'injected store read failure' } -CreatePublisherCertificate { $state.Creates++; return $identity } -ValidatePublicCertificate $validatePublic -ValidatePrivateCertificate $validatePrivate -GetSpkiSha256 $getSpki -ExportPublicCertificate $exportPublic -GetThumbprint $getThumbprint -WritePublicCertificate $noopPublic -WriteThumbprint $noopThumb) } catch { $storeReadFailed = $_.Exception.Message -match 'store read failure' }",
            "if (-not $storeReadFailed -or $state.Creates -ne 0 -or $state.PublicWrites -ne 0 -or $state.ThumbWrites -ne 0) { throw 'Store read failure was not fail-closed.' }",
            "$storeWriteFailed = $false",
            "try { [void](Invoke-PublisherEnrollmentWorkflow -RepositoryCertificate $null -GetSubjectPrivateCertificates { @() } -CreatePublisherCertificate { $state.Creates++; throw 'injected store write failure' } -ValidatePublicCertificate $validatePublic -ValidatePrivateCertificate $validatePrivate -GetSpkiSha256 $getSpki -ExportPublicCertificate $exportPublic -GetThumbprint $getThumbprint -WritePublicCertificate $noopPublic -WriteThumbprint $noopThumb) } catch { $storeWriteFailed = $_.Exception.Message -match 'store write failure' }",
            "if (-not $storeWriteFailed -or $state.Creates -ne 1 -or $state.PublicWrites -ne 0 -or $state.ThumbWrites -ne 0) { throw 'Store write failure was not fail-closed.' }",
            "$publicWriteFailed = $false",
            "try { [void](Invoke-PublisherEnrollmentWorkflow -RepositoryCertificate $null -GetSubjectPrivateCertificates { @($identity) } -CreatePublisherCertificate { throw 'must not create' } -ValidatePublicCertificate $validatePublic -ValidatePrivateCertificate $validatePrivate -GetSpkiSha256 $getSpki -ExportPublicCertificate $exportPublic -GetThumbprint $getThumbprint -WritePublicCertificate { param([byte[]]$bytes) $state.PublicWrites++; throw 'injected public write failure' } -WriteThumbprint $noopThumb) } catch { $publicWriteFailed = $_.Exception.Message -match 'public write failure' }",
            "if (-not $publicWriteFailed -or $state.PublicWrites -ne 1 -or $state.ThumbWrites -ne 0) { throw 'Public output failure was not fail-closed.' }",
            "$thumbWriteFailed = $false",
            "try { [void](Invoke-PublisherEnrollmentWorkflow -RepositoryCertificate $null -GetSubjectPrivateCertificates { @($identity) } -CreatePublisherCertificate { throw 'must not create' } -ValidatePublicCertificate $validatePublic -ValidatePrivateCertificate $validatePrivate -GetSpkiSha256 $getSpki -ExportPublicCertificate $exportPublic -GetThumbprint $getThumbprint -WritePublicCertificate $noopPublic -WriteThumbprint { param([string]$thumbprint) $state.ThumbWrites++; throw 'injected thumbprint write failure' }) } catch { $thumbWriteFailed = $_.Exception.Message -match 'thumbprint write failure' }",
            "if (-not $thumbWriteFailed -or $state.PublicWrites -ne 2 -or $state.ThumbWrites -ne 1) { throw 'Thumbprint output failure was not fail-closed.' }");

        var result = await RunAsync("pwsh.exe", ["-NoProfile", "-Command", command]);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task MutexSeamSerializesContentionAndHandlesAbandonment()
    {
        var command = string.Join(
            Environment.NewLine,
            DotSourceEnrollmentScript(),
            "function New-FakeMutex([bool]$Abandoned) {",
            "  $mutex = [pscustomobject]@{ Held = $false; Abandoned = $Abandoned; Releases = 0; Disposals = 0 }",
            "  $mutex | Add-Member ScriptMethod WaitOne { param($timeout) if ($this.Abandoned) { throw [System.Threading.AbandonedMutexException]::new() }; if ($this.Held) { return $false }; $this.Held = $true; return $true }",
            "  $mutex | Add-Member ScriptMethod ReleaseMutex { $this.Held = $false; $this.Releases++ }",
            "  $mutex | Add-Member ScriptMethod Dispose { $this.Disposals++ }",
            "  return $mutex",
            "}",
            "$shared = New-FakeMutex $false",
            "$factory = { param($name) return $shared }",
            "$mutexState = [pscustomobject]@{ NestedTimedOut = $false; Actions = 0 }",
            "Invoke-WithPublisherEnrollmentMutex -MutexName 'Global\\test-current-user' -Timeout ([TimeSpan]::Zero) -MutexFactory $factory -Action {",
            "  $mutexState.Actions++",
            "  try { Invoke-WithPublisherEnrollmentMutex -MutexName 'Global\\test-current-user' -Timeout ([TimeSpan]::Zero) -MutexFactory $factory -Action { throw 'must not enter' } } catch [System.TimeoutException] { $mutexState.NestedTimedOut = $_.Exception.Message -match 'timed out' }",
            "}",
            "if (-not $mutexState.NestedTimedOut -or $mutexState.Actions -ne 1 -or $shared.Releases -ne 1 -or $shared.Disposals -ne 2 -or $shared.Held) { throw 'Mutex contention was not serialized.' }",
            "$abandoned = New-FakeMutex $true",
            "$abandonedFailed = $false",
            "try { Invoke-WithPublisherEnrollmentMutex -MutexName 'Global\\test-current-user' -Timeout ([TimeSpan]::Zero) -MutexFactory { param($name) return $abandoned } -Action { throw 'must not enter' } } catch { $abandonedFailed = $_.Exception.Message -match 'abandoned' }",
            "if (-not $abandonedFailed -or $abandoned.Releases -ne 1 -or $abandoned.Disposals -ne 1) { throw 'Abandoned mutex was not failed and released clearly.' }");

        var result = await RunAsync("pwsh.exe", ["-NoProfile", "-Command", command]);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task TrackedTaskOnePathsContainNoPrivateKeyContainer()
    {
        var result = await RunAsync(
            "git",
            [
                "ls-files",
                "--",
                "Directory.Build.targets",
                "eng/ota",
                "scripts/create_ota_publisher_certificate.ps1",
                "src/BaiduBatchTransfer.App/Updates/OtaPublisherTrust.cs",
                "tests/BaiduBatchTransfer.App.Tests/Updates/OtaPublisherTrustTests.cs",
                "tests/BaiduBatchTransfer.App.Tests/Updates/OtaPublisherEnrollmentWorkflowTests.cs",
            ],
            FindRepositoryRoot());

        Assert.True(result.ExitCode == 0, result.Output);
        var trackedPaths = result.Output.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("eng/ota/SuperPan.OtaPublisher.cer", trackedPaths);
        Assert.DoesNotContain(trackedPaths, path =>
            path.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".p12", StringComparison.OrdinalIgnoreCase));
    }

    private static string DotSourceEnrollmentScript() =>
        $". '{EscapePowerShellLiteral(Path.Combine(FindRepositoryRoot(), "scripts", "create_ota_publisher_certificate.ps1"))}'";

    private static string EscapePowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(
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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}

public sealed class OtaPublisherLiveStoreFactAttribute : FactAttribute
{
    public OtaPublisherLiveStoreFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("BAIDU_BATCH_OTA_PUBLISHER_LIVE_STORE"),
                "approved",
                StringComparison.Ordinal))
        {
            Skip = "Set BAIDU_BATCH_OTA_PUBLISHER_LIVE_STORE=approved to run this read-only acceptance test.";
        }
    }
}
