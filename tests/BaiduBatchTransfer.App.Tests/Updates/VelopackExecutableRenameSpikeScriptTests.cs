using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class VelopackExecutableRenameSpikeScriptTests
{
    [Fact]
    public void ScriptBuildsOldAndNewNoBusinessSamplesWithRepositoryPinnedVelopackPair()
    {
        var script = ReadScript();

        Assert.Contains("BaiduBatchTransfer.App.exe", script, StringComparison.Ordinal);
        Assert.Contains("知道综合工具.exe", script, StringComparison.Ordinal);
        Assert.Contains("[char]0x77E5", script, StringComparison.Ordinal);
        Assert.Contains("Directory.Packages.props", script, StringComparison.Ordinal);
        Assert.Contains(".config", script, StringComparison.Ordinal);
        Assert.Contains("dotnet-tools.json", script, StringComparison.Ordinal);
        Assert.Contains("PackageReference Include=\"Velopack\"", script, StringComparison.Ordinal);
        Assert.Contains("ManagePackageVersionsCentrally>false", script, StringComparison.Ordinal);
        Assert.Contains("dotnet tool run vpk", script, StringComparison.Ordinal);
        Assert.Contains("--mainExe", script, StringComparison.Ordinal);
        Assert.Contains("old-setup.exe", script, StringComparison.Ordinal);
        Assert.Contains("IO.Compression.ZipFile", script, StringComparison.Ordinal);
        Assert.Contains("missing expected main executable", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptSeparatesSafePackPhaseFromIsolatedInstallUpgradePhase()
    {
        var script = ReadScript();

        Assert.Contains("ValidateSet('Pack', 'InstallUpgrade')", script, StringComparison.Ordinal);
        Assert.Contains("Assert-IsolatedWindowsProfile", script, StringComparison.Ordinal);
        Assert.Contains("Test-IsolationMarker", script, StringComparison.Ordinal);
        Assert.Contains(".velopack-rename-spike-isolation", script, StringComparison.Ordinal);
        Assert.Contains("GetFolderPath([Environment+SpecialFolder]::UserProfile)", script, StringComparison.Ordinal);
        Assert.Contains("$isolatedProfile = Assert-IsolatedWindowsProfile", script, StringComparison.Ordinal);
        Assert.Contains("$localAppData = Join-Path $isolatedProfile 'AppData\\Local'", script, StringComparison.Ordinal);
        Assert.Contains("$desktop = Join-Path $isolatedProfile 'Desktop'", script, StringComparison.Ordinal);
        Assert.Contains("$startMenu = Join-Path $isolatedProfile 'AppData\\Roaming\\Microsoft\\Windows\\Start Menu'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SpecialFolder]::LocalApplicationData", script, StringComparison.Ordinal);
        Assert.DoesNotContain("I_UNDERSTAND_THIS_PROFILE_IS_DISPOSABLE", script, StringComparison.Ordinal);
        Assert.Contains("WindowsIdentity]::GetCurrent()", script, StringComparison.Ordinal);
        Assert.Contains("AreAccessRulesProtected", script, StringComparison.Ordinal);
        Assert.Contains("FileMode]::CreateNew", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptUsesWindowsPowerShellCompatiblePinnedPackageSelection()
    {
        var script = ReadScript();

        Assert.DoesNotContain("Select-Object -Single", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$sdkNodes.Count -ne 1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCommandDiagnosticsDoNotBypassExitCodeHandlingUnderWindowsPowerShell()
    {
        var script = ReadScript();

        Assert.Contains("$previousErrorActionPreference = $ErrorActionPreference", script, StringComparison.Ordinal);
        Assert.Contains("$ErrorActionPreference = 'Continue'", script, StringComparison.Ordinal);
        Assert.Contains("$commandExitCode = $LASTEXITCODE", script, StringComparison.Ordinal);
        Assert.Contains("$ErrorActionPreference = $previousErrorActionPreference", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallUpgradePhaseChecksAllExecutableRenameSurfaces()
    {
        var script = ReadScript();

        Assert.Contains("InstallRootLauncher", script, StringComparison.Ordinal);
        Assert.Contains("CurrentMainExecutable", script, StringComparison.Ordinal);
        Assert.Contains("DesktopShortcut", script, StringComparison.Ordinal);
        Assert.Contains("StartMenuShortcut", script, StringComparison.Ordinal);
        Assert.Contains("UpgradeRestartTarget", script, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-LaunchEvidenceRecord", script, StringComparison.Ordinal);
        Assert.Contains("Assert-LaunchEvidence", script, StringComparison.Ordinal);
        Assert.Contains("--veloapp-obsolete 1.0.0", script, StringComparison.Ordinal);
        Assert.Contains("Get-Content -LiteralPath $evidenceLog -Encoding UTF8", script, StringComparison.Ordinal);
        Assert.Contains("WScript.Shell", script, StringComparison.Ordinal);
        Assert.Contains("--veloapp-updated", script, StringComparison.Ordinal);
        Assert.Contains("--spike-restarted", script, StringComparison.Ordinal);
        Assert.Contains("Assert-CanonicalPathEquals -Label 'DesktopShortcut'", script, StringComparison.Ordinal);
        Assert.Contains("Assert-CanonicalPathEquals -Label 'StartMenuShortcut'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Assert-PathEndsWith", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnvironmentAndCallerPathSpoofDoNotAuthorizeInstallUpgrade()
    {
        var output = Path.Combine(Path.GetTempPath(), $"velopack-rename-spike-test-{Guid.NewGuid():N}");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(),
                         "-Phase", "InstallUpgrade", "-WorkRoot", output,
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["VELOPACK_RENAME_SPIKE_ISOLATED_PROFILE"] =
                "I_UNDERSTAND_THIS_PROFILE_IS_DISPOSABLE";
            startInfo.ArgumentList.Add("-ExpectedIsolatedProfilePath");
            startInfo.ArgumentList.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start Windows PowerShell.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("profile-bound isolation marker", stdout + stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedIsolationMarkerFailsClosed()
    {
        using var fixture = IsolationMarkerFixture.Create("{");

        var result = await RunIsolationMarkerValidationAsync(fixture.ProfilePath, fixture.CurrentSid);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("malformed", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IsolationMarkerBoundToWrongSidFailsClosed()
    {
        using var fixture = IsolationMarkerFixture.CreateMarker(sid: "S-1-5-18");

        var result = await RunIsolationMarkerValidationAsync(fixture.ProfilePath, fixture.CurrentSid);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SID", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WritableIsolationMarkerFailsClosed()
    {
        using var fixture = IsolationMarkerFixture.CreateMarker();

        var result = await RunIsolationMarkerValidationAsync(fixture.ProfilePath, fixture.CurrentSid);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("writable", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("obsolete")]
    [InlineData("updated")]
    [InlineData("restart")]
    public async Task LifecycleEventsFromWrongExecutablePathsFailClosed(string eventToCorrupt)
    {
        var root = Path.Combine(Path.GetTempPath(), $"velopack-launch-evidence-{Guid.NewGuid():N}");
        var oldExecutable = Path.Combine(root, "current", "BaiduBatchTransfer.App.exe");
        var newExecutable = Path.Combine(root, "current", "知道综合工具.exe");
        var wrongExecutable = Path.Combine(root, "decoy", "知道综合工具.exe");
        var records = new[]
        {
            $"{DateTimeOffset.UtcNow:O}|{(eventToCorrupt == "obsolete" ? wrongExecutable : oldExecutable)}|--veloapp-obsolete 1.0.0",
            $"{DateTimeOffset.UtcNow.AddSeconds(1):O}|{(eventToCorrupt == "updated" ? wrongExecutable : newExecutable)}|--veloapp-updated 1.1.0",
            $"{DateTimeOffset.UtcNow.AddSeconds(2):O}|{(eventToCorrupt == "restart" ? wrongExecutable : newExecutable)}|--spike-restarted",
        };

        var result = await RunLaunchEvidenceValidationAsync(records, oldExecutable, newExecutable);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ordered exact-path", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ScriptResult> RunIsolationMarkerValidationAsync(string profilePath, string sid)
    {
        var harnessPath = Path.Combine(Path.GetTempPath(), $"velopack-isolation-harness-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(harnessPath, """
                param([string]$ScriptPath, [string]$ProfilePath, [string]$IdentitySid)
                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $ast = [Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) { throw 'Spike script parse failed.' }
                $function = @($ast.FindAll({
                    param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                        $node.Name -ceq 'Test-IsolationMarker'
                }, $true))
                if ($function.Count -ne 1) { throw 'Test-IsolationMarker function was not found exactly once.' }
                Invoke-Expression $function[0].Extent.Text
                Test-IsolationMarker -ProfilePath $ProfilePath -IdentitySid $IdentitySid | Out-Null
                """);

            var startInfo = CreatePowerShellStartInfo();
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harnessPath,
                         "-ScriptPath", ScriptPath(), "-ProfilePath", profilePath, "-IdentitySid", sid,
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start Windows PowerShell.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new ScriptResult(process.ExitCode, stdout + stderr);
        }
        finally
        {
            File.Delete(harnessPath);
        }
    }

    private static ProcessStartInfo CreatePowerShellStartInfo() => new()
    {
        FileName = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe"),
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static async Task<ScriptResult> RunLaunchEvidenceValidationAsync(
        string[] records,
        string oldExecutable,
        string newExecutable)
    {
        var id = Guid.NewGuid().ToString("N");
        var harnessPath = Path.Combine(Path.GetTempPath(), $"velopack-launch-harness-{id}.ps1");
        var recordsPath = Path.Combine(Path.GetTempPath(), $"velopack-launch-records-{id}.txt");
        try
        {
            File.WriteAllLines(recordsPath, records);
            File.WriteAllText(harnessPath, """
                param([string]$ScriptPath, [string]$RecordsPath, [string]$OldExecutable, [string]$NewExecutable)
                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $ast = [Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) { throw 'Spike script parse failed.' }
                foreach ($name in @('ConvertFrom-LaunchEvidenceRecord', 'Assert-LaunchEvidence')) {
                    $function = @($ast.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
                    }, $true))
                    if ($function.Count -ne 1) { throw "Function '$name' was not found exactly once." }
                    Invoke-Expression $function[0].Extent.Text
                }
                $records = @(Get-Content -LiteralPath $RecordsPath -Encoding UTF8)
                Assert-LaunchEvidence -Records $records -ExpectedOldExecutable $OldExecutable -ExpectedNewExecutable $NewExecutable | Out-Null
                """);

            var startInfo = CreatePowerShellStartInfo();
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harnessPath,
                         "-ScriptPath", ScriptPath(), "-RecordsPath", recordsPath,
                         "-OldExecutable", oldExecutable, "-NewExecutable", newExecutable,
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start Windows PowerShell.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new ScriptResult(process.ExitCode, stdout + stderr);
        }
        finally
        {
            File.Delete(harnessPath);
            File.Delete(recordsPath);
        }
    }

    private static string ReadScript() => File.ReadAllText(ScriptPath());

    private static string ScriptPath() => Path.Combine(
        FindRepositoryRoot(), "scripts", "Test-VelopackExecutableRenameSpike.ps1");

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BaiduBatchTransfer.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Unable to locate repository root.");
    }

    private sealed record ScriptResult(int ExitCode, string Output);

    private sealed class IsolationMarkerFixture : IDisposable
    {
        private IsolationMarkerFixture(string profilePath, string currentSid)
        {
            ProfilePath = profilePath;
            CurrentSid = currentSid;
        }

        public string ProfilePath { get; }
        public string CurrentSid { get; }

        public static IsolationMarkerFixture Create(string markerContent)
        {
            var profile = Path.Combine(Path.GetTempPath(), $"velopack-isolation-profile-{Guid.NewGuid():N}");
            var markerDirectory = Path.Combine(profile, ".velopack-rename-spike-isolation");
            Directory.CreateDirectory(markerDirectory);
            File.WriteAllText(Path.Combine(markerDirectory, "marker.json"), markerContent);
            return new IsolationMarkerFixture(
                profile,
                WindowsIdentity.GetCurrent().User?.Value
                    ?? throw new InvalidOperationException("Current Windows SID is unavailable."));
        }

        public static IsolationMarkerFixture CreateMarker(string? sid = null)
        {
            var profile = Path.Combine(Path.GetTempPath(), $"velopack-isolation-profile-{Guid.NewGuid():N}");
            var currentSid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("Current Windows SID is unavailable.");
            var marker = JsonSerializer.Serialize(new
            {
                ProfilePath = Path.GetFullPath(profile),
                Sid = sid ?? currentSid,
                Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            });
            var markerDirectory = Path.Combine(profile, ".velopack-rename-spike-isolation");
            Directory.CreateDirectory(markerDirectory);
            File.WriteAllText(Path.Combine(markerDirectory, "marker.json"), marker);
            return new IsolationMarkerFixture(profile, currentSid);
        }

        public void Dispose()
        {
            if (Directory.Exists(ProfilePath)) Directory.Delete(ProfilePath, recursive: true);
        }
    }
}
