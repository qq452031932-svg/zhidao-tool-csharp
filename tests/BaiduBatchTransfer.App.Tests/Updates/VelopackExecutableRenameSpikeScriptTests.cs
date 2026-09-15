using System.Diagnostics;
using System.Runtime.CompilerServices;

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
        Assert.Contains("VELOPACK_RENAME_SPIKE_ISOLATED_PROFILE", script, StringComparison.Ordinal);
        Assert.Contains("GetFolderPath([Environment+SpecialFolder]::UserProfile)", script, StringComparison.Ordinal);
        Assert.Contains("$isolatedProfile = Assert-IsolatedWindowsProfile", script, StringComparison.Ordinal);
        Assert.Contains("$localAppData = Join-Path $isolatedProfile 'AppData\\Local'", script, StringComparison.Ordinal);
        Assert.Contains("$desktop = Join-Path $isolatedProfile 'Desktop'", script, StringComparison.Ordinal);
        Assert.Contains("$startMenu = Join-Path $isolatedProfile 'AppData\\Roaming\\Microsoft\\Windows\\Start Menu'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SpecialFolder]::LocalApplicationData", script, StringComparison.Ordinal);
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
        Assert.Contains("$lifecycleEvidence = $null", script, StringComparison.Ordinal);
        Assert.Contains("$restartEvidence = $null", script, StringComparison.Ordinal);
        Assert.Contains("Get-Content -LiteralPath $evidenceLog -Encoding UTF8", script, StringComparison.Ordinal);
        Assert.Contains("WScript.Shell", script, StringComparison.Ordinal);
        Assert.Contains("--veloapp-updated", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallUpgradePhaseRefusesOrdinaryUserProfileBeforeCreatingWorkspace()
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
            startInfo.Environment.Remove("VELOPACK_RENAME_SPIKE_ISOLATED_PROFILE");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start Windows PowerShell.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("isolated Windows profile", stdout + stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
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
}
