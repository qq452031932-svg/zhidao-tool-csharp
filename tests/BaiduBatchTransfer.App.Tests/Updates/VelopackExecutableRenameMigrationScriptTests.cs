using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class VelopackExecutableRenameMigrationScriptTests
{
    private const string ProductionPublisherSpki = "f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff";

    [Fact]
    public void ProductionScriptPinsSignedFixtureAndEveryExecutableToRepositoryPublisher()
    {
        var script = ReadScript();

        Assert.Contains("eng\\ota\\SuperPan.OtaPublisher.cer", script, StringComparison.Ordinal);
        Assert.Contains(ProductionPublisherSpki, script, StringComparison.Ordinal);
        Assert.Contains("migration-gate-fixture.json.sig", script, StringComparison.Ordinal);
        Assert.Contains("RSASignaturePadding]::Pkcs1", script, StringComparison.Ordinal);
        Assert.Contains("VerifyData", script, StringComparison.Ordinal);
        Assert.Contains("Assert-CanonicalSignedFixture", script, StringComparison.Ordinal);
        Assert.Contains("Assert-PublisherExecutable", script, StringComparison.Ordinal);
        Assert.Contains("Get-CertificateSpkiSha256", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionTopologyIsFixedAndCannotComeFromFixture()
    {
        var script = ReadScript();

        Assert.Contains("SuperPan.BaiduBatchTransfer", script, StringComparison.Ordinal);
        Assert.Contains("AppData\\Local\\SuperPan.BaiduBatchTransfer", script, StringComparison.Ordinal);
        Assert.Contains("current", script, StringComparison.Ordinal);
        Assert.Contains("InstallLocation", script, StringComparison.Ordinal);
        Assert.Contains("UninstallString", script, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "install_root_relative_path", "portable_root_relative_path", "old_main_relative_path",
                     "new_main_relative_path", "root_launcher_relative_path", "legacy_launcher_relative_path",
                 })
        {
            Assert.DoesNotContain(forbidden, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ScriptProvesOfflineIsolationBeforeAndAfterExternalProcesses()
    {
        var script = ReadScript();

        Assert.Contains("Assert-NoNonLoopbackNetwork", script, StringComparison.Ordinal);
        Assert.Contains("Get-NetAdapter", script, StringComparison.Ordinal);
        Assert.Contains("-IncludeHidden", script, StringComparison.Ordinal);
        Assert.Contains("Get-NetRoute", script, StringComparison.Ordinal);
        Assert.Contains("IsInRole", script, StringComparison.Ordinal);
        Assert.Contains("WindowsBuiltInRole]::Administrator", script, StringComparison.Ordinal);
        Assert.Contains("Stop-OwnedProcessTree", script, StringComparison.Ordinal);
        Assert.True(script.Split("Assert-NoNonLoopbackNetwork", StringSplitOptions.None).Length >= 4);
    }

    [Fact]
    public void DriverEvidenceIsBoundedCanonicalStdoutAndBoundToIndependentFileHashes()
    {
        var script = ReadScript();

        Assert.Contains("MaximumDriverOutputBytes", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-BoundedDriver", script, StringComparison.Ordinal);
        Assert.Contains("StandardOutput", script, StringComparison.Ordinal);
        Assert.Contains("Assert-StateEvidence", script, StringComparison.Ordinal);
        Assert.Contains("Get-StreamingSha256", script, StringComparison.Ordinal);
        Assert.Contains("fsutil.exe hardlink list", script, StringComparison.Ordinal);
        Assert.Contains("account_row_sha256", script, StringComparison.Ordinal);
        Assert.Contains("settings_row_sha256", script, StringComparison.Ordinal);
        Assert.Contains("task_row_sha256", script, StringComparison.Ordinal);
        Assert.Contains("marker_count", script, StringComparison.Ordinal);
        Assert.Contains("account_row", script, StringComparison.Ordinal);
        Assert.Contains("query_completed_utc", script, StringComparison.Ordinal);
        Assert.Contains("challenge_sha256", script, StringComparison.Ordinal);
        Assert.Contains("Assert-IndependentLicenseAudit", script, StringComparison.Ordinal);
        Assert.Contains("ProtectedData]::Unprotect", script, StringComparison.Ordinal);
        Assert.Contains("SuperPan.Licensing.State.v1", script, StringComparison.Ordinal);
        Assert.Contains("Import-P256SubjectPublicKeyInfo", script, StringComparison.Ordinal);
        Assert.Contains("Convert-DerEcdsaSignatureToP1363", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UpgradeIsDelegatedToTrustedDriverWithRealFailureEvidenceContract()
    {
        var script = ReadScript();

        Assert.Contains("perform-upgrade", script, StringComparison.Ordinal);
        Assert.Contains("Assert-UpgradeEvidence", script, StringComparison.Ordinal);
        Assert.Contains("update_manager_invoked", script, StringComparison.Ordinal);
        Assert.Contains("updater_invoked", script, StringComparison.Ordinal);
        Assert.Contains("source_asset_sha256", script, StringComparison.Ordinal);
        Assert.Contains("DownloadInterrupted", script, StringComparison.Ordinal);
        Assert.Contains("ApplicationFailed", script, StringComparison.Ordinal);
        Assert.Contains("fault_observed", script, StringComparison.Ordinal);
        Assert.Contains("bytes_consumed", script, StringComparison.Ordinal);
        Assert.Contains("old_version_after_failure", script, StringComparison.Ordinal);
        Assert.Contains("ObservedUpdater", script, StringComparison.Ordinal);
        Assert.Contains("challenge_sha256", script, StringComparison.Ordinal);
        Assert.Contains("update_manager_stage", script, StringComparison.Ordinal);
        Assert.Contains("partial_artifact_path", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayedUpgradeChallengeIsRejected()
    {
        var evidence = "{\"run_id\":\"RUN\",\"challenge_sha256\":\"" + new string('b', 64) + "\",\"scenario\":\"Setup-Full\",\"asset_kind\":\"Setup\",\"path\":\"Full\",\"failure_injection\":\"ApplicationFailed\"}";
        var result = await RunFunctionAsync("Assert-UpgradeEvidence", new Dictionary<string, string>
        {
            ["EvidenceJson"] = evidence, ["RunId"] = "RUN", ["ExpectedChallengeSha256"] = new string('a', 64),
            ["Scenario"] = "Setup-Full", ["AssetKind"] = "Setup", ["Path"] = "Full", ["FailureInjection"] = "ApplicationFailed",
        });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("challenge", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplicationFailedWithoutObservedUpdaterIsRejected()
    {
        var result = await RunFunctionAsync("Assert-UpgradeEvidence", new Dictionary<string, string>
        {
            ["EvidenceJson"] = "{\"run_id\":\"RUN\",\"challenge_sha256\":\"" + new string('a', 64) + "\",\"scenario\":\"Setup-Full\",\"asset_kind\":\"Setup\",\"path\":\"Full\",\"failure_injection\":\"ApplicationFailed\"}",
            ["RunId"] = "RUN", ["ExpectedChallengeSha256"] = new string('a', 64), ["Scenario"] = "Setup-Full",
            ["AssetKind"] = "Setup", ["Path"] = "Full", ["FailureInjection"] = "ApplicationFailed",
            ["ObservedUpdaterJson"] = "null",
        });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("observed", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestScriptCopyActuallyObservesUpdaterAndValidatesChallengeBoundSuccessEvidence()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-orchestration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var updater = System.IO.Path.Combine(root, "Update.exe");
        var driver = System.IO.Path.Combine(root, "driver.ps1");
        var harness = System.IO.Path.Combine(root, "harness.ps1");
        File.Copy(System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"), updater);
        try
        {
            await File.WriteAllTextAsync(driver, """
                param([string]$Updater,[string]$ChallengeSha)
                $started=[DateTimeOffset]::UtcNow
                $child=Start-Process -FilePath $Updater -ArgumentList '/d','/c','timeout /t 2 /nobreak >nul' -PassThru
                $cim=Get-CimInstance Win32_Process -Filter "ProcessId=$($child.Id)"
                $creation=$cim.CreationDate
                $creationUtc=if($creation-is[DateTime]){([DateTime]$creation).ToUniversalTime()}else{[Management.ManagementDateTimeConverter]::ToDateTime([string]$creation).ToUniversalTime()}
                $childStarted=$creationUtc.ToString('O')
                $child.WaitForExit()
                $record=[ordered]@{asset_kind='Setup';challenge_sha256=$ChallengeSha;completed_utc=[DateTimeOffset]::UtcNow.ToString('O');failure_injection='None';outcome='updated';path='Full';run_id='RUN';scenario='Setup-Full';source_asset_length=100;source_asset_sha256=('a'*64);started_utc=$started.ToString('O');update_manager_invoked=$true;update_manager_stage='apply-completed';update_manager_succeeded=$true;updater_exit_code=$child.ExitCode;updater_invoked=$true;updater_path=[IO.Path]::GetFullPath($Updater);updater_pid=$child.Id;updater_started_utc=$childStarted}
                [Console]::Out.Write(($record|ConvertTo-Json -Compress)+"`n")
                """, new UTF8Encoding(false));
            await File.WriteAllTextAsync(harness, """
                param([string]$ScriptPath,[string]$Driver,[string]$Updater,[string]$WorkingDirectory)
                $ErrorActionPreference='Stop'
                $tokens=$null;$errors=$null
                $ast=[Management.Automation.Language.Parser]::ParseFile($ScriptPath,[ref]$tokens,[ref]$errors)
                foreach($function in @($ast.FindAll({param($node)$node-is[Management.Automation.Language.FunctionDefinitionAst]},$true))){Invoke-Expression $function.Extent.Text}
                function Assert-NoNonLoopbackNetwork{return @{TestOverride=$true}}
                $script:MaximumDriverOutputBytes=1048576
                $challenge='e'*64
                $run=Invoke-BoundedDriver -Driver "$PSHOME\powershell.exe" -Arguments @('-NoProfile','-ExecutionPolicy','Bypass','-File',$Driver,'-Updater',$Updater,'-ChallengeSha',$challenge) -WorkingDirectory $WorkingDirectory -TimeoutSeconds 15 -ExpectedUpdater $Updater -RequireUpdaterObservation
                Assert-UpgradeEvidence -EvidenceJson $run.StandardOutput -RunId RUN -ExpectedChallengeSha256 $challenge -Scenario Setup-Full -AssetKind Setup -Path Full -FailureInjection None -ExpectedAssetSha256 ('a'*64) -ExpectedAssetBytes 100 -ExpectedUpdaterPath $Updater -ObservedUpdaterJson (ConvertTo-CanonicalJson $run.ObservedUpdater) -WindowStartedUtc $run.StartedUtc -WindowCompletedUtc $run.CompletedUtc -RunOwnedRoot $WorkingDirectory|Out-Null
                """, new UTF8Encoding(false));
            var startInfo = PowerShell();
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harness, "-ScriptPath", ScriptPath(), "-Driver", driver, "-Updater", updater, "-WorkingDirectory", root }) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo)!;
            var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SignedFixtureRejectsWrongKeyAndAcceptsCanonicalRsaPkcs1Signature()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-fixture-signature-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var key = RSA.Create(3072);
            using var wrongKey = RSA.Create(3072);
            using var licenseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Migration Gate Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var certPath = System.IO.Path.Combine(root, "publisher.cer");
            var fixturePath = System.IO.Path.Combine(root, "migration-gate-fixture.json");
            var signaturePath = fixturePath + ".sig";
            await File.WriteAllBytesAsync(certPath, certificate.Export(X509ContentType.Cert));
            var fixtureBytes = new UTF8Encoding(false).GetBytes(
                "{\"license_signing_key_id\":\"production-key-1\",\"license_signing_spki_base64\":\"" + Convert.ToBase64String(licenseKey.ExportSubjectPublicKeyInfo()) + "\",\"new_delta_file\":\"new-delta.nupkg\",\"new_full_file\":\"new-full.nupkg\",\"old_full_file\":\"old-full.nupkg\",\"old_portable_file\":\"old.zip\",\"old_setup_file\":\"old.exe\",\"schema_version\":1,\"state_driver_file\":\"driver.exe\",\"state_driver_sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}\n");
            await File.WriteAllBytesAsync(fixturePath, fixtureBytes);
            await File.WriteAllBytesAsync(signaturePath, wrongKey.SignData(fixtureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            var pin = Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

            var rejected = await RunFunctionAsync("Assert-CanonicalSignedFixture", new Dictionary<string, string>
            {
                ["FixturePath"] = fixturePath, ["SignaturePath"] = signaturePath,
                ["CertificatePath"] = certPath, ["ExpectedSpki"] = pin,
            });
            Assert.NotEqual(0, rejected.ExitCode);

            await File.WriteAllBytesAsync(signaturePath, key.SignData(fixtureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            var accepted = await RunFunctionAsync("Assert-CanonicalSignedFixture", new Dictionary<string, string>
            {
                ["FixturePath"] = fixturePath, ["SignaturePath"] = signaturePath,
                ["CertificatePath"] = certPath, ["ExpectedSpki"] = pin,
            });
            Assert.True(accepted.ExitCode == 0, accepted.Output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("..\\escape.exe")]
    [InlineData("child/asset.exe")]
    [InlineData("C:\\absolute.exe")]
    public async Task FixtureAssetNamesCannotEscapeAssetRoot(string maliciousName)
    {
        var result = await RunFunctionAsync("Assert-AssetFileName", new Dictionary<string, string>
        {
            ["FileName"] = maliciousName, ["Label"] = "fixture asset",
        });
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task ReparsePointAssetIsRejected()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-reparse-{Guid.NewGuid():N}");
        var target = System.IO.Path.Combine(root, "target");
        var link = System.IO.Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        try
        {
            var junctionInfo = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) junctionInfo.ArgumentList.Add(argument);
            using (var junction = Process.Start(junctionInfo)!)
            {
                var error = await junction.StandardError.ReadToEndAsync();
                await junction.WaitForExitAsync();
                Assert.True(junction.ExitCode == 0, error);
            }
            var result = await RunFunctionAsync("Assert-NoReparsePoint", new Dictionary<string, string>
            {
                ["ExistingPath"] = link, ["Label"] = "fixture asset",
            });
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("reparse", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnsignedExecutableCannotMasqueradeAsTrustedStateDriver()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test process path is unavailable.");
        var result = await RunFunctionAsync("Assert-PublisherExecutable", new Dictionary<string, string>
        {
            ["Executable"] = executable, ["ExpectedSpki"] = ProductionPublisherSpki,
        });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Authenticode", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForgedStateJsonCannotOverrideIndependentDatabaseHash()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-state-binding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(System.IO.Path.Combine(root, "AppData", "Local", "BaiduBatchTransfer", "data"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "AppData", "Local", "SuperPan", "Licensing", "v1"));
        try
        {
            var database = System.IO.Path.Combine(root, "AppData", "Local", "BaiduBatchTransfer", "data", "baidu-batch-transfer.db");
            var license = System.IO.Path.Combine(root, "AppData", "Local", "SuperPan", "Licensing", "v1", "licenses.dat");
            await File.WriteAllTextAsync(database, "RUN RUN RUN database");
            await File.WriteAllTextAsync(license, "protected-license");
            var accountRow = "{\"marker\":\"RUN\",\"value\":\"account\"}";
            var settingsRow = "{\"marker\":\"RUN\",\"value\":\"settings\"}";
            var taskRow = "{\"marker\":\"RUN\",\"value\":\"task\"}";
            var state = "{\"account_marker\":\"RUN\",\"account_marker_count\":1,\"account_row\":" + accountRow + ",\"account_row_sha256\":\"" + HashText(accountRow) +
                        "\",\"challenge_sha256\":\"" + new string('e', 64) + "\",\"query_completed_utc\":\"2026-09-15T00:00:00.0000000+00:00\",\"run_id\":\"RUN" +
                        "\",\"database_file_sha256\":\"" + new string('0', 64) +
                        "\",\"database_path\":\"" + JsonEscape(database) +
                        "\",\"environment_authorization_is_auxiliary\":true,\"expires_at_utc\":\"2099-01-01T00:00:00.0000000+00:00\",\"license_file_sha256\":\"" + Hash(license) +
                        "\",\"license_path\":\"" + JsonEscape(license) +
                        "\",\"license_source\":\"preissued-local-production-chain-license\",\"network_accessed\":false,\"production_trust_chain\":true,\"reactivation_required\":false,\"settings_marker\":\"RUN\",\"settings_marker_count\":1,\"settings_row\":" + settingsRow + ",\"settings_row_sha256\":\"" + HashText(settingsRow) +
                        "\",\"signed_payload_sha256\":\"" + new string('c', 64) +
                        "\",\"task_marker\":\"RUN\",\"task_marker_count\":1,\"task_row\":" + taskRow + ",\"task_row_sha256\":\"" + HashText(taskRow) + "\"}";
            var statePath = System.IO.Path.Combine(root, "state.json");
            await File.WriteAllTextAsync(statePath, state);

            var result = await RunFunctionAsync("Assert-StateEvidence", new Dictionary<string, string>
            {
                ["StateJsonPath"] = statePath, ["ProfileRoot"] = root, ["RunId"] = "RUN", ["ExpectedChallengeSha256"] = new string('e',64),
                ["WindowStartedUtc"] = "2026-09-14T00:00:00.0000000+00:00", ["WindowCompletedUtc"] = "2026-09-16T00:00:00.0000000+00:00",
            });
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("hash", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DownloadFailureEvidenceWithoutUpdateManagerInvocationFailsClosed()
    {
        const string updater = "C:\\isolated\\Update.exe";
        var observed = "{\"Path\":\"" + JsonEscape(updater) + "\",\"Pid\":42,\"StartedUtc\":\"2026-09-15T00:00:01.0000000+00:00\"}";
        var evidence = "{\"asset_kind\":\"Setup\",\"challenge_sha256\":\"" + new string('e',64) + "\",\"completed_utc\":\"2026-09-15T00:00:03.0000000+00:00\",\"failure_injection\":\"DownloadInterrupted\",\"path\":\"Full\",\"run_id\":\"RUN\",\"scenario\":\"Setup-Full\",\"source_asset_length\":100,\"source_asset_sha256\":\"" + new string('a',64) + "\",\"started_utc\":\"2026-09-15T00:00:00.0000000+00:00\",\"update_manager_invoked\":false,\"update_manager_stage\":\"download-failed\",\"updater_path\":\"" + JsonEscape(updater) + "\",\"updater_pid\":42,\"updater_started_utc\":\"2026-09-15T00:00:01.0000000+00:00\"}";
        var result = await RunFunctionAsync("Assert-UpgradeEvidence", new Dictionary<string, string>
        {
            ["EvidenceJson"] = evidence, ["RunId"]="RUN", ["ExpectedChallengeSha256"]=new string('e',64), ["Scenario"]="Setup-Full", ["AssetKind"]="Setup", ["Path"]="Full", ["FailureInjection"] = "DownloadInterrupted",
            ["ExpectedAssetSha256"] = new string('a', 64), ["ExpectedAssetBytes"] = "100",
            ["ExpectedUpdaterPath"]=updater, ["ObservedUpdaterJson"]=observed, ["WindowStartedUtc"]="2026-09-14T00:00:00.0000000+00:00", ["WindowCompletedUtc"]="2026-09-16T00:00:00.0000000+00:00",
        });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("UpdateManager", result.Output, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void ScriptRequiresExplicitLocalAssetsModeAndEvidenceInputs()
    {
        var script = ReadScript();

        Assert.Contains("[string]$OldAssets", script, StringComparison.Ordinal);
        Assert.Contains("[string]$NewAssets", script, StringComparison.Ordinal);
        Assert.Contains("[string]$EvidenceDirectory", script, StringComparison.Ordinal);
        Assert.Contains("ValidateSet('Setup', 'Portable')", script, StringComparison.Ordinal);
        Assert.Contains("[string]$AssetKind", script, StringComparison.Ordinal);
        Assert.Contains("ValidateSet('Delta', 'Full')", script, StringComparison.Ordinal);
        Assert.Contains("[string]$Path", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$IUnderstandThisIsAnIsolatedWindowsProfile", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptFailsClosedUnlessProfileBoundIsolationMarkerIsValid()
    {
        var script = ReadScript();

        Assert.Contains("Assert-IsolatedWindowsProfile", script, StringComparison.Ordinal);
        Assert.Contains(".velopack-rename-migration-isolation", script, StringComparison.Ordinal);
        Assert.Contains("WindowsIdentity]::GetCurrent()", script, StringComparison.Ordinal);
        Assert.Contains("GetFolderPath([Environment+SpecialFolder]::UserProfile)", script, StringComparison.Ordinal);
        Assert.Contains("AreAccessRulesProtected", script, StringComparison.Ordinal);
        Assert.Contains("RandomNumberGenerator", script, StringComparison.Ordinal);
        Assert.Contains("FileMode]::CreateNew", script, StringComparison.Ordinal);
        Assert.Contains("IUnderstandThisIsAnIsolatedWindowsProfile", script, StringComparison.Ordinal);
        Assert.DoesNotContain("I_UNDERSTAND_THIS_PROFILE_IS_DISPOSABLE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$env:USERPROFILE", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScriptStagesOnlyCallerSuppliedAssetsInsideFreshOwnedEvidenceDirectory()
    {
        var script = ReadScript();

        Assert.Contains("Assert-SafeInputDirectory", script, StringComparison.Ordinal);
        Assert.Contains("Assert-NewOwnedDirectory", script, StringComparison.Ordinal);
        Assert.Contains("Assert-StrictChildPath", script, StringComparison.Ordinal);
        Assert.Contains("ownership.json", script, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath", script, StringComparison.Ordinal);
        Assert.Contains("AssetSource = 'caller-supplied-local-directories'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WebRequest", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-RestMethod", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HttpClient", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Item -LiteralPath $OldAssets", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Item -LiteralPath $NewAssets", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FixtureContractRequiresRealMarkedDataAndProductionTrustChainLicense()
    {
        var script = ReadScript();

        Assert.Contains("migration-gate-fixture.json", script, StringComparison.Ordinal);
        Assert.Contains("account_marker", script, StringComparison.Ordinal);
        Assert.Contains("settings_marker", script, StringComparison.Ordinal);
        Assert.Contains("task_marker", script, StringComparison.Ordinal);
        Assert.Contains("database_path", script, StringComparison.Ordinal);
        Assert.Contains("license_path", script, StringComparison.Ordinal);
        Assert.Contains("production_trust_chain", script, StringComparison.Ordinal);
        Assert.Contains("signed_payload_sha256", script, StringComparison.Ordinal);
        Assert.Contains("expires_at_utc", script, StringComparison.Ordinal);
        Assert.Contains("activation_request_id", script, StringComparison.Ordinal);
        Assert.Contains("environment_authorization_is_auxiliary", script, StringComparison.Ordinal);
        Assert.Contains("Assert-StateEvidencePreserved", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptChecksRenamedInstallSurfacesAndRestartProcess()
    {
        var script = ReadScript();

        foreach (var marker in new[]
                 {
                     "InstallRoot", "CurrentDirectory", "InstallRootLauncher", "CurrentMainExecutable",
                     "LegacyCompatibilityLauncher", "DesktopShortcut", "StartMenuShortcut", "UninstallEntry",
                     "UpgradeRestartProcess",
                 })
        {
            Assert.Contains(marker, script, StringComparison.Ordinal);
        }
        Assert.Contains("知道综合工具.exe", script, StringComparison.Ordinal);
        Assert.Contains("BaiduBatchTransfer.App.exe", script, StringComparison.Ordinal);
        Assert.Contains("WScript.Shell", script, StringComparison.Ordinal);
        Assert.Contains("Get-Process", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Win32_Product", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Uninstall\\*", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptCoversSuccessAndInjectedFailureRollbackWithoutDeletingState()
    {
        var script = ReadScript();

        Assert.Contains("ValidateSet('None', 'DownloadInterrupted', 'ApplicationFailed')", script, StringComparison.Ordinal);
        Assert.Contains("FailureInjection", script, StringComparison.Ordinal);
        Assert.Contains("Assert-OldVersionStillLaunches", script, StringComparison.Ordinal);
        Assert.Contains("1.1.0", script, StringComparison.Ordinal);
        Assert.Contains("Setup-Delta", script, StringComparison.Ordinal);
        Assert.Contains("Setup-Full", script, StringComparison.Ordinal);
        Assert.Contains("Portable-Full", script, StringComparison.Ordinal);
        Assert.Contains("FailureRollbackVerified", script, StringComparison.Ordinal);
        Assert.Contains("Assert-StateEvidencePreserved", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptWritesCanonicalJsonEvidenceAndSha256Inventory()
    {
        var script = ReadScript();

        Assert.Contains("ConvertTo-CanonicalJson", script, StringComparison.Ordinal);
        Assert.Contains("Write-CanonicalJson", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -Algorithm SHA256", script, StringComparison.Ordinal);
        Assert.Contains("evidence.json", script, StringComparison.Ordinal);
        Assert.Contains("files.sha256.json", script, StringComparison.Ordinal);
        Assert.Contains("RunId", script, StringComparison.Ordinal);
        Assert.Contains("Succeeded = $false", script, StringComparison.Ordinal);
        Assert.Contains("REAL_MIGRATION_GATE_PASS", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingIsolationConfirmationIsRejectedBeforeAnyAssetMutation()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-migration-reject-{Guid.NewGuid():N}");
        var oldAssets = System.IO.Path.Combine(root, "old");
        var newAssets = System.IO.Path.Combine(root, "new");
        var evidence = System.IO.Path.Combine(root, "evidence");
        Directory.CreateDirectory(oldAssets);
        Directory.CreateDirectory(newAssets);
        try
        {
            var startInfo = PowerShell();
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(),
                         "-OldAssets", oldAssets, "-NewAssets", newAssets,
                         "-EvidenceDirectory", evidence, "-AssetKind", "Setup", "-Path", "Delta",
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start Windows PowerShell.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("explicit isolation confirmation", stdout + stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(evidence));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScriptParsesUnderWindowsPowerShell51()
    {
        var startInfo = PowerShell();
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$tokens=$null;$errors=$null;[Management.Automation.Language.Parser]::ParseFile(" +
            "'" + ScriptPath().Replace("'", "''", StringComparison.Ordinal) +
            "',[ref]$tokens,[ref]$errors)|Out-Null;if($errors.Count){$errors|% Message;exit 1}");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start Windows PowerShell.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task ExpiredProductionChainLicenseFixtureFailsClosed()
    {
        var profile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-expired-{Guid.NewGuid():N}");
        var database = System.IO.Path.Combine(profile, "AppData", "Local", "BaiduBatchTransfer", "data", "baidu-batch-transfer.db");
        var license = System.IO.Path.Combine(profile, "AppData", "Local", "SuperPan", "Licensing", "v1", "licenses.dat");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(database)!);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(license)!);
        try
        {
            await File.WriteAllTextAsync(database, "RUN account settings task");
            await File.WriteAllTextAsync(license, "RUN signed license");
            const string rowHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var row = "{\"marker\":\"RUN\"}";
            var state = $$"""
                {"account_marker":"RUN","account_marker_count":1,"account_row":{{row}},"account_row_sha256":"{{HashText(row)}}","challenge_sha256":"{{rowHash}}","query_completed_utc":"2026-09-15T00:00:00.0000000+00:00","run_id":"RUN","settings_marker":"RUN","settings_marker_count":1,"settings_row":{{row}},"settings_row_sha256":"{{HashText(row)}}","task_marker":"RUN","task_marker_count":1,"task_row":{{row}},"task_row_sha256":"{{HashText(row)}}","database_path":"{{JsonEscape(database)}}","database_file_sha256":"{{Hash(database)}}","database_length":{{new FileInfo(database).Length}},"license_path":"{{JsonEscape(license)}}","license_file_sha256":"{{Hash(license)}}","license_length":{{new FileInfo(license).Length}},"production_trust_chain":true,"signed_payload_sha256":"{{rowHash}}","expires_at_utc":"2020-01-01T00:00:00.0000000+00:00","activation_request_id":"11111111-1111-1111-1111-111111111111","environment_authorization_is_auxiliary":true,"network_accessed":false,"license_source":"preissued-local-production-chain-license","reactivation_required":false}
                """;
            var result = await RunFunctionAsync("Assert-StateEvidence", new Dictionary<string, string>
            {
                ["StateJson"] = state, ["ProfileRoot"] = profile, ["RunId"] = "RUN", ["ExpectedChallengeSha256"]=rowHash,
                ["WindowStartedUtc"]="2026-09-14T00:00:00.0000000+00:00", ["WindowCompletedUtc"]="2026-09-16T00:00:00.0000000+00:00",
            });
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("expired", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunFunctionAsync(
        string functionName,
        IReadOnlyDictionary<string, string> arguments)
    {
        var id = Guid.NewGuid().ToString("N");
        var harness = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-function-harness-{id}.ps1");
        var argumentsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-function-arguments-{id}.json");
        try
        {
            await File.WriteAllTextAsync(argumentsPath, System.Text.Json.JsonSerializer.Serialize(arguments));
            await File.WriteAllTextAsync(harness, """
                param([string]$ScriptPath,[string]$FunctionName,[string]$ArgumentsPath)
                $ErrorActionPreference='Stop'
                $tokens=$null;$errors=$null
                $ast=[Management.Automation.Language.Parser]::ParseFile($ScriptPath,[ref]$tokens,[ref]$errors)
                if($errors.Count){throw 'Migration script parse failed.'}
                foreach($function in @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true))){
                    Invoke-Expression $function.Extent.Text
                }
                $object=Get-Content -LiteralPath $ArgumentsPath -Raw -Encoding UTF8|ConvertFrom-Json
                $splat=@{}
                foreach($property in $object.PSObject.Properties){$splat[$property.Name]=[string]$property.Value}
                & $FunctionName @splat|Out-Null
                """);
            var startInfo = PowerShell();
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harness,
                         "-ScriptPath", ScriptPath(), "-FunctionName", functionName, "-ArgumentsPath", argumentsPath,
                     })
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, string.Join(Environment.NewLine, await stdout, await stderr));
        }
        finally
        {
            File.Delete(harness);
            File.Delete(argumentsPath);
        }
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string JsonEscape(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value)[1..^1];

    private static ProcessStartInfo PowerShell() => new()
    {
        FileName = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"),
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static string ReadScript() => File.ReadAllText(ScriptPath());

    private static string ScriptPath() => System.IO.Path.Combine(
        FindRepositoryRoot(), "scripts", "Test-VelopackExecutableRenameMigration.ps1");

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(System.IO.Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "BaiduBatchTransfer.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Unable to locate repository root.");
    }
}
