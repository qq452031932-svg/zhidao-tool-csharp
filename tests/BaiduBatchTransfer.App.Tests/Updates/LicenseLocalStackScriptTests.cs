using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class LicenseLocalStackScriptTests
{
    [Fact]
    public void LocalScriptsUseIsolatedLoopbackRuntimeAndExactProcessIdentity()
    {
        var start = File.ReadAllText(ScriptPath("run_local_license_stack.ps1"));
        var stop = File.ReadAllText(ScriptPath("stop_local_license_stack.ps1"));
        var host = File.ReadAllText(Path.Combine(RepositoryRoot(), "services", "ota_admin", "cmd", "license-admin-local", "main.go"));
        var ignore = File.ReadAllText(Path.Combine(RepositoryRoot(), ".gitignore"));

        Assert.Contains("127.0.0.1:19090", start, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:19091", start, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:19090", stop, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:19091", stop, StringComparison.Ordinal);
        Assert.Contains(".tmp\\license-local", start, StringComparison.Ordinal);
        Assert.Contains("pid-manifest.json", start, StringComparison.Ordinal);
        Assert.Contains("try", start, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("finally", start, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("try", stop, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("finally", stop, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Process", stop, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("pid-manifest.json", stop, StringComparison.Ordinal);
        Assert.Contains(".tmp\\license-local", stop, StringComparison.Ordinal);
        Assert.Contains("$manifestRoot, $RuntimeRoot", stop, StringComparison.Ordinal);
        Assert.Contains("Get-Process -Id", stop, StringComparison.Ordinal);
        Assert.Contains("Get-CimInstance", stop, StringComparison.Ordinal);
        Assert.Contains(".Path", stop, StringComparison.Ordinal);
        Assert.Contains("StartTime", stop, StringComparison.Ordinal);
        Assert.Contains("CommandLine", stop, StringComparison.Ordinal);
        Assert.Contains("$actualCommandLine", stop, StringComparison.Ordinal);
        Assert.Contains("$null = $process.Handle", stop, StringComparison.Ordinal);
        Assert.Contains("$manifestCommandLine, $actualCommandLine", stop, StringComparison.Ordinal);
        Assert.Contains("instance_token", stop, StringComparison.Ordinal);
        Assert.Contains("executable_sha256", stop, StringComparison.Ordinal);
        Assert.Contains("SHA256]::Create()", stop, StringComparison.Ordinal);
        Assert.Contains("$manifestCommandLine.IndexOf($manifestToken", stop, StringComparison.Ordinal);
        Assert.Contains("Stop-Process -InputObject $process", stop, StringComparison.Ordinal);
        Assert.Contains("$null = $process.Handle", stop, StringComparison.Ordinal);
        Assert.Contains("Remove-LocalArtifactWithRetry -LiteralPath $RuntimeRoot -Recurse", stop, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Process -Id", stop, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Process *", stop, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stop-Process -Name", stop, StringComparison.OrdinalIgnoreCase);

        var combined = start + "\n" + stop;
        Assert.All(combined, character => Assert.InRange((int)character, 0, 127));
        Assert.DoesNotContain(".Contains(", combined, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "ota.fanfh.cn", "ssh ", "nginx", "systemctl", "systemd" })
            Assert.DoesNotContain(forbidden, combined, StringComparison.OrdinalIgnoreCase);

        Assert.StartsWith("//go:build license_local_acceptance && windows", host, StringComparison.Ordinal);
        Assert.Contains("validateLoopbackAddress(*adminAddress)", host, StringComparison.Ordinal);
        Assert.Contains("validateLoopbackAddress(*licenseAddress)", host, StringComparison.Ordinal);
        Assert.Contains("host != \"127.0.0.1\"", host, StringComparison.Ordinal);
        Assert.Contains(".tmp/", ignore, StringComparison.Ordinal);
        Assert.Contains("license-local", ignore, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".tmp/license-local/admin-password.txt")]
    [InlineData(".tmp/license-local/license.db")]
    [InlineData(".tmp/license-local/admin.db")]
    [InlineData(".tmp/license-local/signing-private.pem")]
    [InlineData(".tmp/license-local/internal-admin.key")]
    [InlineData(".tmp/license-admin-local.exe")]
    public void GitIgnoresLocalSecretsDatabasesAndGeneratedExecutable(string repositoryRelativePath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = RepositoryRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "check-ignore", "--no-index", "--quiet", "--", repositoryRelativePath }
        });
        Assert.NotNull(process);
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Git did not ignore {repositoryRelativePath}: {process.StandardError.ReadToEnd()}");
    }

    [Fact]
    public void StartScriptBuildsTaggedHostWithoutOpeningBrowserAndWritesValidatedManifest()
    {
        var start = File.ReadAllText(ScriptPath("run_local_license_stack.ps1"));

        Assert.Contains("license_local_acceptance", start, StringComparison.Ordinal);
        Assert.Contains("license-admin-local", start, StringComparison.Ordinal);
        Assert.Contains("client.json", start, StringComparison.Ordinal);
        Assert.Contains("Get-Process -Id", start, StringComparison.Ordinal);
        Assert.Contains("Get-CimInstance", start, StringComparison.Ordinal);
        Assert.Contains(".Path", start, StringComparison.Ordinal);
        Assert.Contains("StartTime", start, StringComparison.Ordinal);
        Assert.Contains("CommandLine", start, StringComparison.Ordinal);
        Assert.Contains("instance_token", start, StringComparison.Ordinal);
        Assert.Contains("executable_sha256", start, StringComparison.Ordinal);
        Assert.Contains("SHA256]::Create()", start, StringComparison.Ordinal);
        Assert.Contains("RandomNumberGenerator]::Create()", start, StringComparison.Ordinal);
        Assert.DoesNotContain("RandomNumberGenerator]::GetBytes", start, StringComparison.Ordinal);
        Assert.Contains("Start-Process", start, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-WindowStyle Hidden", start, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[IO.FileMode]::CreateNew", start, StringComparison.Ordinal);
        Assert.Contains("[IO.FileShare]::None", start, StringComparison.Ordinal);
        Assert.Contains("$QuotedRuntimeRoot", start, StringComparison.Ordinal);
        Assert.Contains("$failureCommandLine.IndexOf($InstanceToken", start, StringComparison.Ordinal);
        Assert.Contains("$failureStartTicks", start, StringComparison.Ordinal);
        Assert.Contains("$null = $startedProcess.Handle", start, StringComparison.Ordinal);
        Assert.Contains("Write-TextFileAtomic -LiteralPath $ManifestPath", start, StringComparison.Ordinal);
        Assert.Contains("$null = $startedProcess.Handle", start, StringComparison.Ordinal);
        Assert.Contains("Remove-LocalArtifactWithRetry -LiteralPath $RuntimeRoot -Recurse", start, StringComparison.Ordinal);
        Assert.Contains("Remove-LocalArtifactWithRetry -LiteralPath $ExecutablePath", start, StringComparison.Ordinal);
        Assert.Contains("$OwnerPath = Join-Path $RuntimeRoot 'instance-token'", start, StringComparison.Ordinal);
        Assert.True(start.LastIndexOf("Get-CimInstance", StringComparison.Ordinal) <
                    start.LastIndexOf("Stop-Process -InputObject $startedProcess", StringComparison.Ordinal));
        Assert.DoesNotContain("explorer.exe", start, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingRuntimeIsNeverDeletedByARejectedStart()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        Directory.CreateDirectory(RuntimeRoot());
        var sentinel = Path.Combine(RuntimeRoot(), "keep.txt");
        File.WriteAllText(sentinel, "keep");
        try
        {
            var rejected = await RunScriptAsync("run_local_license_stack.ps1");
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Assert.False(File.Exists(Path.Combine(RepositoryRoot(), ".tmp", "license-admin-local.exe")));
        }
        finally
        {
            Directory.Delete(RuntimeRoot(), recursive: true);
        }
        AssertRuntimeAbsent();
    }

    [Fact]
    public async Task LocalStackSupportsHttpLoginWithTheOneTimePassword()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        try
        {
            var started = await RunScriptAsync("run_local_license_stack.ps1");
            AssertScriptSucceeded(started);

            using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var bootstrapResponse = await client.GetAsync("http://127.0.0.1:19090/admin/api/session/bootstrap");
            Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);
            var bootstrap = JsonNode.Parse(await bootstrapResponse.Content.ReadAsStringAsync())!.AsObject();
            var password = File.ReadAllText(Path.Combine(RuntimeRoot(), "admin-password.txt")).Trim();
            var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:19090/admin/api/session/login")
            {
                Content = new StringContent(new JsonObject { ["username"] = "admin", ["password"] = password }.ToJsonString())
            };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add("Origin", "http://127.0.0.1:19090");
            request.Headers.Add("X-CSRF-Token", bootstrap["csrf_token"]!.GetValue<string>());

            var loginResponse = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
            var currentResponse = await client.GetAsync("http://127.0.0.1:19090/admin/api/session/current");
            Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
        }
        finally
        {
            if (File.Exists(Path.Combine(RuntimeRoot(), "pid-manifest.json")))
            {
                var stopped = await RunScriptAsync("stop_local_license_stack.ps1");
                AssertScriptSucceeded(stopped);
            }
        }
        AssertRuntimeAbsent();
    }

    [Fact]
    public async Task StopScriptCleansArtifactsAfterTheOwnedServiceAlreadyExited()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        try
        {
            var started = await RunScriptAsync("run_local_license_stack.ps1");
            AssertScriptSucceeded(started);
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(RuntimeRoot(), "pid-manifest.json")))!.AsObject();
            using var process = Process.GetProcessById(manifest["process"]!["pid"]!.GetValue<int>());
            process.Kill(entireProcessTree: false);
            Assert.True(process.WaitForExit(10_000));

            var stopped = await RunScriptAsync("stop_local_license_stack.ps1");
            AssertScriptSucceeded(stopped);
        }
        finally
        {
            if (File.Exists(Path.Combine(RuntimeRoot(), "pid-manifest.json")))
                await RunScriptAsync("stop_local_license_stack.ps1");
        }
        AssertRuntimeAbsent();
    }

    [Fact]
    public async Task ScriptsRejectConcurrentStartAndTamperedIdentityThenStopExactProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        var manifestPath = Path.Combine(RuntimeRoot(), "pid-manifest.json");
        var readyPath = Path.Combine(RuntimeRoot(), "ready.json");
        var ownerPath = Path.Combine(RuntimeRoot(), "instance-token");
        string? manifestText = null;
        string? readyText = null;
        Process? process = null;

        try
        {
            var started = await RunScriptAsync("run_local_license_stack.ps1");
            AssertScriptSucceeded(started);
            manifestText = File.ReadAllText(manifestPath);
            readyText = File.ReadAllText(readyPath);
            var manifest = JsonNode.Parse(manifestText)!.AsObject();
            var pid = manifest["process"]!["pid"]!.GetValue<int>();
            process = Process.GetProcessById(pid);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("http://127.0.0.1:19090/admin/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("http://127.0.0.1:19091/health")).StatusCode);

            var secondStart = await RunScriptAsync("run_local_license_stack.ps1");
            Assert.NotEqual(0, secondStart.ExitCode);
            Assert.False(process.HasExited);

            var tamperedReady = JsonNode.Parse(readyText)!.AsObject();
            tamperedReady["instance_token"] = new string('0', 32);
            File.WriteAllText(readyPath, tamperedReady.ToJsonString());
            var readyRejected = await RunScriptAsync("stop_local_license_stack.ps1");
            Assert.NotEqual(0, readyRejected.ExitCode);
            Assert.False(process.HasExited);
            File.WriteAllText(readyPath, readyText);

            var tamperedManifest = JsonNode.Parse(manifestText)!.AsObject();
            tamperedManifest["process"]!["command_line"] = manifest["process"]!["command_line"]!.GetValue<string>() + " tampered";
            File.WriteAllText(manifestPath, tamperedManifest.ToJsonString());
            var manifestRejected = await RunScriptAsync("stop_local_license_stack.ps1");
            Assert.NotEqual(0, manifestRejected.ExitCode);
            Assert.False(process.HasExited);
            File.WriteAllText(manifestPath, manifestText);

            Assert.Equal(File.ReadAllText(ownerPath).Trim(), manifest["process"]!["instance_token"]!.GetValue<string>());
        }
        finally
        {
            if (File.Exists(manifestPath))
            {
                if (readyText is not null)
                    File.WriteAllText(readyPath, readyText);
                if (manifestText is not null)
                    File.WriteAllText(manifestPath, manifestText);
                var stopped = await RunScriptAsync("stop_local_license_stack.ps1");
                AssertScriptSucceeded(stopped);
            }
        }

        Assert.NotNull(process);
        process!.WaitForExit(10_000);
        Assert.True(process.HasExited);
        process.Dispose();
        AssertRuntimeAbsent();
    }

    [Fact]
    public async Task StopScriptNeverDeletesAnExecutableReplacedAfterServiceExit()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        var executablePath = Path.Combine(RepositoryRoot(), ".tmp", "license-admin-local.exe");
        try
        {
            var started = await RunScriptAsync("run_local_license_stack.ps1");
            AssertScriptSucceeded(started);
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(RuntimeRoot(), "pid-manifest.json")))!.AsObject();
            using var process = Process.GetProcessById(manifest["process"]!["pid"]!.GetValue<int>());
            process.Kill(entireProcessTree: false);
            Assert.True(process.WaitForExit(10_000));

            File.WriteAllText(executablePath, "replacement sentinel");
            var rejected = await RunScriptAsync("stop_local_license_stack.ps1");

            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Equal("replacement sentinel", File.ReadAllText(executablePath));
            Assert.True(Directory.Exists(RuntimeRoot()));
        }
        finally
        {
            if (File.Exists(executablePath))
                File.Delete(executablePath);
            if (Directory.Exists(RuntimeRoot()))
                Directory.Delete(RuntimeRoot(), recursive: true);
        }
        AssertRuntimeAbsent();
    }

    [Fact]
    public async Task StartupFailureRemovesOwnedSecretsAndExecutable()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        var listener = new TcpListener(IPAddress.Loopback, 19090);
        listener.Start();
        try
        {
            var failed = await RunScriptAsync("run_local_license_stack.ps1");
            Assert.NotEqual(0, failed.ExitCode);
        }
        finally
        {
            listener.Stop();
        }
        AssertRuntimeAbsent();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunScriptAsync(string name)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = RepositoryRoot()
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(ScriptPath(name));
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    private static void AssertRuntimeAbsent()
    {
        Assert.False(Directory.Exists(RuntimeRoot()));
        Assert.False(File.Exists(Path.Combine(RepositoryRoot(), ".tmp", "license-admin-local.exe")));
    }

    private static void AssertScriptSucceeded((int ExitCode, string Output, string Error) result)
    {
        Assert.True(result.ExitCode == 0, $"Script failed with exit code {result.ExitCode}. Output: {result.Output} Error: {result.Error}");
    }

    private static string RuntimeRoot() => Path.Combine(RepositoryRoot(), ".tmp", "license-local");

    private static string ScriptPath(string name) => Path.Combine(RepositoryRoot(), "scripts", name);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BaiduBatchTransfer.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
