using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class VelopackExecutableRenameMigrationScriptTests
{
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
        Assert.Contains("database_relative_path", script, StringComparison.Ordinal);
        Assert.Contains("license_state_relative_path", script, StringComparison.Ordinal);
        Assert.Contains("production_trust_chain", script, StringComparison.Ordinal);
        Assert.Contains("signed_payload_sha256", script, StringComparison.Ordinal);
        Assert.Contains("expires_at_utc", script, StringComparison.Ordinal);
        Assert.Contains("activation_request_id", script, StringComparison.Ordinal);
        Assert.Contains("environment_authorization_is_auxiliary", script, StringComparison.Ordinal);
        Assert.Contains("Assert-MigrationStatePreserved", script, StringComparison.Ordinal);
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
        Assert.Contains("Assert-MigrationStatePreserved", script, StringComparison.Ordinal);
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
        var state = """
            {
              "account_marker":"RUN",
              "settings_marker":"RUN",
              "task_marker":"RUN",
              "database_relative_path":"data\\baidu-batch-transfer.db",
              "license_state_relative_path":"AppData\\Local\\SuperPan\\Licensing\\v1\\licenses.dat",
              "production_trust_chain":true,
              "signed_payload_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "expires_at_utc":"2020-01-01T00:00:00.0000000+00:00",
              "activation_request_id":"11111111-1111-1111-1111-111111111111",
              "environment_authorization_is_auxiliary":true,
              "reactivation_required":false
            }
            """;

        var result = await RunStateValidationAsync(state);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("expired", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Output)> RunStateValidationAsync(string stateJson)
    {
        var id = Guid.NewGuid().ToString("N");
        var harness = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-state-harness-{id}.ps1");
        var statePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-state-{id}.json");
        try
        {
            await File.WriteAllTextAsync(statePath, stateJson);
            await File.WriteAllTextAsync(harness, """
                param([string]$ScriptPath,[string]$StatePath)
                $ErrorActionPreference='Stop'
                $tokens=$null;$errors=$null
                $ast=[Management.Automation.Language.Parser]::ParseFile($ScriptPath,[ref]$tokens,[ref]$errors)
                if($errors.Count){throw 'Migration script parse failed.'}
                $function=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Assert-StateObservation'},$true))
                if($function.Count -ne 1){throw 'Assert-StateObservation was not found exactly once.'}
                Invoke-Expression $function[0].Extent.Text
                $state=Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8|ConvertFrom-Json
                Assert-StateObservation -State $state -RunId 'RUN'|Out-Null
                """);
            var startInfo = PowerShell();
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harness, "-ScriptPath", ScriptPath(), "-StatePath", statePath })
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout + stderr);
        }
        finally
        {
            File.Delete(harness);
            File.Delete(statePath);
        }
    }

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
