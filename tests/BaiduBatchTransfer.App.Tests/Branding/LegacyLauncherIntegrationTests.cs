using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BaiduBatchTransfer.LegacyLauncher;
using Xunit.Abstractions;

namespace BaiduBatchTransfer.App.Tests.Branding;

public sealed class LegacyLauncherIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public LegacyLauncherIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void RealInspectorRejectsHardLinkedTarget()
    {
        using var installation = new TemporaryInstallation();
        var source = Path.Combine(installation.Root, "source.exe");
        File.WriteAllText(source, "target");
        if (!CreateHardLinkW(installation.TargetPath, source, IntPtr.Zero))
        {
            _output.WriteLine(
                $"SKIPPED: CreateHardLinkW unavailable in this test environment: {Marshal.GetLastWin32Error()}.");
            return;
        }

        Assert.False(CreateRealPlan(installation.LauncherPath, out _));
    }

    [Fact]
    public void RealInspectorRejectsSymbolicLinkTarget()
    {
        using var installation = new TemporaryInstallation();
        var source = Path.Combine(installation.Root, "source.exe");
        File.WriteAllText(source, "target");
        try
        {
            File.CreateSymbolicLink(installation.TargetPath, source);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            _output.WriteLine(
                $"SKIPPED: File symlink creation unavailable in this test environment: {exception.Message}");
            return;
        }

        Assert.False(CreateRealPlan(installation.LauncherPath, out _));
    }

    [Fact]
    public void RealInspectorRejectsJunctionAncestor()
    {
        using var installation = new TemporaryInstallation(createTarget: true);
        var linkedDirectory = Path.Combine(installation.Root, "linked-install");
        if (!TryCreateJunction(linkedDirectory, installation.InstallationDirectory, out var reason))
        {
            _output.WriteLine(
                $"SKIPPED: Directory junction creation unavailable in this test environment: {reason}");
            return;
        }

        try
        {
            var linkedLauncher = Path.Combine(linkedDirectory, "BaiduBatchTransfer.App.exe");
            Assert.False(CreateRealPlan(linkedLauncher, out _));
        }
        finally
        {
            Directory.Delete(linkedDirectory);
        }
    }

    [Fact]
    public void RealInspectorAcceptsOrdinaryVelopackDirectory()
    {
        using var installation = new TemporaryInstallation(createTarget: true);

        Assert.True(CreateRealPlan(installation.LauncherPath, out var error), error);
    }

    [Fact]
    public void ProgramOrdinaryLaunchPreservesArgumentElementsAndDetaches()
    {
        using var installation = TemporaryInstallation.WithProbeTarget();
        var output = Path.Combine(installation.Root, "ordinary-argv.json");
        string[] forwarded = ["", "two words", "\"quoted\"", "中文参数", "trailing\\"];
        var arguments = new[] { output, "3000", "23" }.Concat(forwarded).ToArray();
        var stopwatch = Stopwatch.StartNew();
        string? launchError = null;

        var exitCode = BaiduBatchTransfer.LegacyLauncher.Program.Run(
            installation.LauncherPath,
            arguments,
            error => launchError = error);

        stopwatch.Stop();
        Assert.True(exitCode == 0, launchError);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => File.Exists(output), TimeSpan.FromSeconds(5)));
        Assert.Equal(forwarded, JsonSerializer.Deserialize<string[]>(File.ReadAllText(output)));
        Assert.Equal(
            Path.GetFullPath(Environment.CurrentDirectory),
            Path.GetFullPath(File.ReadAllText(output + ".cwd")),
            ignoreCase: true);
        Assert.False(File.Exists(output + ".done"));
        Assert.True(SpinWait.SpinUntil(() => File.Exists(output + ".done"), TimeSpan.FromSeconds(5)));
        Assert.True(WaitForProbeExit(output), "Probe process did not exit in time.");
    }

    [Fact]
    public void ProgramLifecycleLaunchWaitsAndPropagatesChildExitCode()
    {
        using var installation = TemporaryInstallation.WithProbeTarget();
        var output = Path.Combine(installation.Root, "lifecycle-argv.json");
        string[] forwarded = ["before", "--veloapp-test", "after"];
        var arguments = new[] { output, "300", "57" }.Concat(forwarded).ToArray();
        var stopwatch = Stopwatch.StartNew();
        string? launchError = null;

        var exitCode = BaiduBatchTransfer.LegacyLauncher.Program.Run(
            installation.LauncherPath,
            arguments,
            error => launchError = error);

        stopwatch.Stop();
        Assert.True(exitCode == 57, launchError);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(200));
        Assert.True(File.Exists(output + ".done"));
        Assert.Equal(forwarded, JsonSerializer.Deserialize<string[]>(File.ReadAllText(output)));
    }

    private static bool CreateRealPlan(string launcherPath, out string error) =>
        LegacyLaunchPlan.TryCreate(
            launcherPath,
            [],
            WindowsPathInspector.Inspect,
            out _,
            out error);

    private static bool WaitForProbeExit(string outputPath)
    {
        var processId = int.Parse(File.ReadAllText(outputPath + ".pid"));
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // The process exited before GetProcessById observed it.
            return true;
        }
    }

    private static bool TryCreateJunction(string junctionPath, string targetPath, out string reason)
    {
        var commandInterpreter = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = commandInterpreter,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            reason = "cmd.exe did not start.";
            return false;
        }

        process.WaitForExit();
        reason = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
        return process.ExitCode == 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private sealed class TemporaryInstallation : IDisposable
    {
        internal TemporaryInstallation(bool createTarget = false)
        {
            Root = Path.Combine(Path.GetTempPath(), $"legacy-launcher-{Guid.NewGuid():N}");
            InstallationDirectory = Path.Combine(Root, "app-1.2.3");
            Directory.CreateDirectory(InstallationDirectory);
            LauncherPath = Path.Combine(InstallationDirectory, "BaiduBatchTransfer.App.exe");
            TargetPath = Path.Combine(InstallationDirectory, "知道综合工具.exe");
            File.WriteAllText(LauncherPath, "launcher");
            if (createTarget)
            {
                File.WriteAllText(TargetPath, "target");
            }
        }

        internal string Root { get; }

        internal string InstallationDirectory { get; }

        internal string LauncherPath { get; }

        internal string TargetPath { get; }

        internal static TemporaryInstallation WithProbeTarget()
        {
            var installation = new TemporaryInstallation();
            var repositoryRoot = FindRepositoryRoot();
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            var probeOutput = Path.Combine(
                repositoryRoot,
                "tests",
                "BaiduBatchTransfer.LegacyLauncher.Probe",
                "bin",
                configuration,
                "net8.0-windows");
            foreach (var source in Directory.EnumerateFiles(probeOutput))
            {
                File.Copy(source, Path.Combine(installation.InstallationDirectory, Path.GetFileName(source)));
            }

            File.Copy(
                Path.Combine(probeOutput, "BaiduBatchTransfer.LegacyLauncher.Probe.exe"),
                installation.TargetPath);
            return installation;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                foreach (var processIdFile in Directory.EnumerateFiles(
                             Root,
                             "*.pid",
                             SearchOption.AllDirectories))
                {
                    _ = WaitForProbeExit(processIdFile[..^".pid".Length]);
                }

                Directory.Delete(Root, recursive: true);
            }
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BaiduBatchTransfer.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }
}
