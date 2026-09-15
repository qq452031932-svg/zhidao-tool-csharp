using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class LicenseLocalStackScriptTests
{
    [Theory]
    [InlineData("build_local_license_acceptance.ps1")]
    [InlineData("test_local_license_stack.ps1")]
    public void AcceptanceScriptsLoadCanonicalMainAssemblyIdentityOnce(string scriptName)
    {
        var script = File.ReadAllText(ScriptPath(scriptName));

        Assert.Equal(1, CountOccurrences(script, ". (Join-Path $PSScriptRoot 'ProductIdentity.ps1')"));
        Assert.Equal(1, CountOccurrences(script, "Get-ProductIdentity"));
        Assert.Contains("$productIdentity.ProductMainDllName", script, StringComparison.Ordinal);
        Assert.DoesNotContain("知道综合工具", script, StringComparison.Ordinal);
        Assert.DoesNotContain("BaiduBatchTransfer.App.dll", script, StringComparison.Ordinal);
    }

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
        Assert.Contains("$manifestCommandLine.IndexOf([string]$ProcessRecord.instance_token", stop, StringComparison.Ordinal);
        Assert.Contains("Stop-Process -InputObject $process", stop, StringComparison.Ordinal);
        Assert.Contains("$null = $process.Handle", stop, StringComparison.Ordinal);
        Assert.Contains("Move-LocalDirectoryAtomicallyWithRetry -Source $RuntimeRoot -Destination $quarantineRoot", stop, StringComparison.Ordinal);
        Assert.Contains("Remove-VerifiedQuarantineTree -Journal $Journal -QuarantineRoot $quarantineRoot", stop, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-LocalArtifactWithRetry -LiteralPath $quarantineRoot -Recurse", stop, StringComparison.Ordinal);
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
        Assert.Contains("validateLoopbackAddress(licenseAddress)", host, StringComparison.Ordinal);
        Assert.Contains("host != \"127.0.0.1\"", host, StringComparison.Ordinal);
        Assert.DoesNotContain("licensehttp", host, StringComparison.Ordinal);
        Assert.DoesNotContain("licenseServer", host, StringComparison.Ordinal);
        Assert.Contains(".tmp/", ignore, StringComparison.Ordinal);
        Assert.Contains("license-local", ignore, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".tmp/license-local/admin-password.txt")]
    [InlineData(".tmp/license-local/card-encryption.key")]
    [InlineData(".tmp/license-local/card-lookup.key")]
    [InlineData(".tmp/license-local/session-source.key")]
    [InlineData(".tmp/license-local/license.db")]
    [InlineData(".tmp/license-local/admin.db")]
    [InlineData(".tmp/license-local/signing-private.pem")]
    [InlineData(".tmp/license-local/signing-public.pem")]
    [InlineData(".tmp/license-local/internal-admin.key")]
    [InlineData(".tmp/license-local/license-service.json")]
    [InlineData(".tmp/license-local/client.json")]
    [InlineData(".tmp/license-local/ready.json")]
    [InlineData(".tmp/license-local/pid-manifest.json")]
    [InlineData(".tmp/license-local/instance-token")]
    [InlineData(".tmp/license-local/startup-cleanup.json")]
    [InlineData(".tmp/license-local-cleanup.json")]
    [InlineData(".tmp/license-local-quarantine-00000000000000000000000000000000/card-encryption.key")]
    [InlineData(".tmp/license-admin-local.exe")]
    [InlineData(".tmp/license-service-local.exe")]
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
        Assert.Contains("$quotedRoot", start, StringComparison.Ordinal);
        Assert.Contains("$InstanceToken", start, StringComparison.Ordinal);
        Assert.Contains("Get-ExactChildIdentity", start, StringComparison.Ordinal);
        Assert.Contains("$null = $licenseProcess.Handle", start, StringComparison.Ordinal);
        Assert.Contains("$null = $adminProcess.Handle", start, StringComparison.Ordinal);
        Assert.Contains("Write-TextFileAtomic -LiteralPath $ManifestPath", start, StringComparison.Ordinal);
        Assert.Contains("startup-cleanup.json", start, StringComparison.Ordinal);
        Assert.Contains("stop_local_license_stack.ps1", start, StringComparison.Ordinal);
        Assert.Contains("$OwnerPath = Join-Path $RuntimeRoot 'instance-token'", start, StringComparison.Ordinal);
        Assert.True(start.LastIndexOf("Get-CimInstance", StringComparison.Ordinal) <
                    start.LastIndexOf("Stop-Process -InputObject $process", StringComparison.Ordinal));
        Assert.DoesNotContain("explorer.exe", start, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartAndStopScriptsModelTwoIndependentExactChildren()
    {
        var start = File.ReadAllText(ScriptPath("run_local_license_stack.ps1"));
        var stop = File.ReadAllText(ScriptPath("stop_local_license_stack.ps1"));

        Assert.Contains("$LicenseExecutablePath", start, StringComparison.Ordinal);
        Assert.Contains("$AdminExecutablePath", start, StringComparison.Ordinal);
        Assert.Contains("./cmd/license-service", start, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(start, "Start-Process -FilePath"));
        Assert.Contains("processes = @(", start, StringComparison.Ordinal);
        Assert.Contains("license-service", start, StringComparison.Ordinal);
        Assert.Contains("license-admin-local", start, StringComparison.Ordinal);
        Assert.Contains("foreach ($processRecord in $manifest.processes)", stop, StringComparison.Ordinal);
        Assert.Contains("finally", stop, StringComparison.OrdinalIgnoreCase);
        var childStopTry = stop.IndexOf("$stopFailures =", StringComparison.Ordinal);
        Assert.True(childStopTry >= 0);
        Assert.True(stop.IndexOf("finally", childStopTry, StringComparison.OrdinalIgnoreCase) > childStopTry);
    }

    [Fact]
    public void CleanupUsesSerializedRecoverableAtomicQuarantine()
    {
        var start = File.ReadAllText(ScriptPath("run_local_license_stack.ps1"));
        var stop = File.ReadAllText(ScriptPath("stop_local_license_stack.ps1"));

        foreach (var script in new[] { start, stop })
        {
            Assert.Contains("license-local-cleanup.json", script, StringComparison.Ordinal);
            Assert.Contains("license-local-quarantine-", script, StringComparison.Ordinal);
            Assert.Contains("[IO.File]::Move", script, StringComparison.Ordinal);
            Assert.Contains("Get-LocalFileSha256", script, StringComparison.Ordinal);
        }
        Assert.Contains("SuperPanLicenseLocalAcceptanceStop", stop, StringComparison.Ordinal);
        Assert.Contains("WaitOne", stop, StringComparison.Ordinal);
        Assert.Contains("Invoke-TestHook", stop, StringComparison.Ordinal);
        Assert.Contains("runtime_files", stop, StringComparison.Ordinal);
        Assert.Contains("Assert-QuarantineInventory", stop, StringComparison.Ordinal);
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
            Assert.False(File.Exists(Path.Combine(RepositoryRoot(), ".tmp", "license-service-local.exe")));
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
            var login = JsonNode.Parse(await loginResponse.Content.ReadAsStringAsync())!.AsObject();
            Assert.True(login["must_change_password"]!.GetValue<bool>());
            var replacementPassword = "Local-Acceptance-Password-2026!";
            var passwordRequest = JsonRequest(HttpMethod.Post, "/admin/api/session/password",
                new JsonObject { ["current_password"] = password, ["new_password"] = replacementPassword },
                login["csrf_token"]!.GetValue<string>());
            var passwordResponse = await client.SendAsync(passwordRequest);
            Assert.Equal(HttpStatusCode.OK, passwordResponse.StatusCode);
            var passwordResult = JsonNode.Parse(await passwordResponse.Content.ReadAsStringAsync())!.AsObject();
            var csrf = passwordResult["csrf_token"]!.GetValue<string>();

            async Task<JsonObject> Mutate(string path, JsonObject body)
            {
                var mutation = JsonRequest(HttpMethod.Post, path, body, csrf);
                mutation.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
                var response = await client.SendAsync(mutation);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            }

            var software = await Mutate("/admin/api/license/software", new JsonObject { ["name"] = "Local acceptance product" });
            var softwareId = software["id"]!.GetValue<string>();
            var package = await Mutate("/admin/api/license/packages", new JsonObject
            {
                ["name"] = "Local acceptance package",
                ["software_ids"] = new JsonArray(softwareId)
            });
            var packageId = package["id"]!.GetValue<string>();
            var generated = await Mutate("/admin/api/license/cards/generate", new JsonObject
            {
                ["package_id"] = packageId,
                ["duration_days"] = 30,
                ["count"] = 1
            });
            var cards = generated["items"]!.AsArray();
            Assert.Single(cards);
            Assert.Equal(30, cards[0]!["card"]!["base_days"]!.GetValue<int>());
            Assert.False(string.IsNullOrWhiteSpace(cards[0]!["plaintext"]!.GetValue<string>()));

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
            using var process = Process.GetProcessById(ManifestProcess(manifest, "license-service")["pid"]!.GetValue<int>());
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
        Process? licenseProcess = null;
        Process? adminProcess = null;

        try
        {
            var started = await RunScriptAsync("run_local_license_stack.ps1");
            AssertScriptSucceeded(started);
            manifestText = File.ReadAllText(manifestPath);
            readyText = File.ReadAllText(readyPath);
            var manifest = JsonNode.Parse(manifestText)!.AsObject();
            licenseProcess = Process.GetProcessById(ManifestProcess(manifest, "license-service")["pid"]!.GetValue<int>());
            adminProcess = Process.GetProcessById(ManifestProcess(manifest, "license-admin-local")["pid"]!.GetValue<int>());
            Assert.NotEqual(licenseProcess.Id, adminProcess.Id);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("http://127.0.0.1:19090/admin/health")).StatusCode);
            using (var licenseSocket = new TcpClient())
                await licenseSocket.ConnectAsync(IPAddress.Loopback, 19091);

            var secondStart = await RunScriptAsync("run_local_license_stack.ps1");
            Assert.NotEqual(0, secondStart.ExitCode);
            Assert.False(licenseProcess.HasExited);
            Assert.False(adminProcess.HasExited);

            var tamperedReady = JsonNode.Parse(readyText)!.AsObject();
            tamperedReady["instance_token"] = new string('0', 32);
            File.WriteAllText(readyPath, tamperedReady.ToJsonString());
            var readyRejected = await RunScriptAsync("stop_local_license_stack.ps1");
            Assert.NotEqual(0, readyRejected.ExitCode);
            Assert.False(licenseProcess.HasExited);
            Assert.False(adminProcess.HasExited);
            File.WriteAllText(readyPath, readyText);

            var tamperedManifest = JsonNode.Parse(manifestText)!.AsObject();
            var tamperedLicense = ManifestProcess(tamperedManifest, "license-service");
            tamperedLicense["command_line"] = tamperedLicense["command_line"]!.GetValue<string>() + " tampered";
            File.WriteAllText(manifestPath, tamperedManifest.ToJsonString());
            var manifestRejected = await RunScriptAsync("stop_local_license_stack.ps1");
            Assert.NotEqual(0, manifestRejected.ExitCode);
            Assert.False(licenseProcess.HasExited);
            adminProcess.WaitForExit(10_000);
            Assert.True(adminProcess.HasExited);
            File.WriteAllText(manifestPath, manifestText);

            Assert.Equal(File.ReadAllText(ownerPath).Trim(), manifest["instance_token"]!.GetValue<string>());
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

        Assert.NotNull(licenseProcess);
        Assert.NotNull(adminProcess);
        licenseProcess!.WaitForExit(10_000);
        Assert.True(licenseProcess.HasExited);
        licenseProcess.Dispose();
        adminProcess!.Dispose();
        AssertRuntimeAbsent();
    }

    [Fact]
    public async Task StopScriptNeverDeletesAnExecutableReplacedAfterServiceExit()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        var executablePath = Path.Combine(RepositoryRoot(), ".tmp", "license-admin-local.exe");
        byte[]? originalExecutable = null;
        try
        {
            var started = await RunScriptAsync("run_local_license_stack.ps1");
            AssertScriptSucceeded(started);
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(RuntimeRoot(), "pid-manifest.json")))!.AsObject();
            using var process = Process.GetProcessById(ManifestProcess(manifest, "license-admin-local")["pid"]!.GetValue<int>());
            process.Kill(entireProcessTree: false);
            Assert.True(process.WaitForExit(10_000));

            originalExecutable = File.ReadAllBytes(executablePath);
            File.WriteAllText(executablePath, "replacement sentinel");
            var rejected = await RunScriptAsync("stop_local_license_stack.ps1");

            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Equal("replacement sentinel", File.ReadAllText(executablePath));
            Assert.True(Directory.Exists(RuntimeRoot()));
        }
        finally
        {
            if (originalExecutable is not null)
                File.WriteAllBytes(executablePath, originalExecutable);
            if (File.Exists(Path.Combine(RuntimeRoot(), "pid-manifest.json")))
            {
                var stopped = await RunScriptAsync("stop_local_license_stack.ps1");
                AssertScriptSucceeded(stopped);
            }
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

    [Fact]
    public async Task StopPreservesExecutableReplacedAfterQuarantineVerificationAndCanRetry()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        var hookRoot = TestHookRoot();
        byte[]? executable = null;
        string? stagedPath = null;
        try
        {
            AssertScriptSucceeded(await RunScriptAsync("run_local_license_stack.ps1"));
            var stopTask = RunScriptAsync("stop_local_license_stack.ps1", new Dictionary<string, string>
            {
                ["LICENSE_LOCAL_TEST_HOOK_ROOT"] = hookRoot,
                ["LICENSE_LOCAL_TEST_HOOK_NAME"] = "after-executable-verify-license-admin-local"
            });
            await WaitForFileAsync(Path.Combine(hookRoot, "after-executable-verify-license-admin-local.ready"));
            var journal = ReadCleanupJournal();
            var entry = journal["executables"]!.AsArray().Select(x => x!.AsObject())
                .Single(x => x["role"]!.GetValue<string>() == "license-admin-local");
            stagedPath = Path.Combine(journal["quarantine_root"]!.GetValue<string>(), entry["staged_name"]!.GetValue<string>());
            executable = File.ReadAllBytes(stagedPath);
            File.WriteAllText(stagedPath, "replacement sentinel");
            File.WriteAllText(Path.Combine(hookRoot, "after-executable-verify-license-admin-local.release"), "release");

            var rejected = await stopTask;
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Equal("replacement sentinel", File.ReadAllText(stagedPath));
            Assert.True(File.Exists(CleanupJournalPath()));

            File.WriteAllBytes(stagedPath, executable);
            AssertScriptSucceeded(await RunScriptAsync("stop_local_license_stack.ps1"));
        }
        finally
        {
            if (stagedPath is not null && executable is not null && File.Exists(stagedPath))
                File.WriteAllBytes(stagedPath, executable);
            ReleaseAllHooks(hookRoot);
            await RecoverCleanupAsync();
            if (Directory.Exists(hookRoot))
                Directory.Delete(hookRoot, recursive: true);
        }
        AssertRuntimeAbsent();
    }

    [Fact]
    public async Task StopPreservesChangedRuntimeArtifactAfterVerificationAndCanRetry()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        var hookRoot = TestHookRoot();
        byte[]? clientSettings = null;
        string? stagedClientPath = null;
        try
        {
            AssertScriptSucceeded(await RunScriptAsync("run_local_license_stack.ps1"));
            var stopTask = RunScriptAsync("stop_local_license_stack.ps1", new Dictionary<string, string>
            {
                ["LICENSE_LOCAL_TEST_HOOK_ROOT"] = hookRoot,
                ["LICENSE_LOCAL_TEST_HOOK_NAME"] = "before-quarantine-delete"
            });
            await WaitForFileAsync(Path.Combine(hookRoot, "before-quarantine-delete.ready"));
            var journal = ReadCleanupJournal();
            stagedClientPath = Path.Combine(journal["quarantine_root"]!.GetValue<string>(), "client.json");
            clientSettings = File.ReadAllBytes(stagedClientPath);
            File.WriteAllText(stagedClientPath, "replacement sentinel");
            File.WriteAllText(Path.Combine(hookRoot, "before-quarantine-delete.release"), "release");

            var rejected = await stopTask;
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Equal("replacement sentinel", File.ReadAllText(stagedClientPath));
            Assert.True(File.Exists(CleanupJournalPath()));

            File.WriteAllBytes(stagedClientPath, clientSettings);
            AssertScriptSucceeded(await RunScriptAsync("stop_local_license_stack.ps1"));
        }
        finally
        {
            if (stagedClientPath is not null && clientSettings is not null && File.Exists(stagedClientPath))
                File.WriteAllBytes(stagedClientPath, clientSettings);
            ReleaseAllHooks(hookRoot);
            await RecoverCleanupAsync();
            if (Directory.Exists(hookRoot))
                Directory.Delete(hookRoot, recursive: true);
        }
        AssertRuntimeAbsent();
    }

    [Fact]
    public async Task StopPreservesUnownedEmptyDirectoryAddedAfterInventoryAndCanRetry()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        var hookRoot = TestHookRoot();
        string? insertedDirectory = null;
        try
        {
            AssertScriptSucceeded(await RunScriptAsync("run_local_license_stack.ps1"));
            var stopTask = RunScriptAsync("stop_local_license_stack.ps1", new Dictionary<string, string>
            {
                ["LICENSE_LOCAL_TEST_HOOK_ROOT"] = hookRoot,
                ["LICENSE_LOCAL_TEST_HOOK_NAME"] = "before-quarantine-delete"
            });
            await WaitForFileAsync(Path.Combine(hookRoot, "before-quarantine-delete.ready"));
            var journal = ReadCleanupJournal();
            insertedDirectory = Path.Combine(journal["quarantine_root"]!.GetValue<string>(), "unowned-empty");
            Directory.CreateDirectory(insertedDirectory);
            File.WriteAllText(Path.Combine(hookRoot, "before-quarantine-delete.release"), "release");

            var rejected = await stopTask;
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.True(Directory.Exists(insertedDirectory));
            Assert.True(File.Exists(CleanupJournalPath()));

            Directory.Delete(insertedDirectory);
            insertedDirectory = null;
            AssertScriptSucceeded(await RunScriptAsync("stop_local_license_stack.ps1"));
        }
        finally
        {
            if (insertedDirectory is not null && Directory.Exists(insertedDirectory))
                Directory.Delete(insertedDirectory);
            ReleaseAllHooks(hookRoot);
            await RecoverCleanupAsync();
            if (Directory.Exists(hookRoot))
                Directory.Delete(hookRoot, recursive: true);
        }
        AssertRuntimeAbsent();
    }

    [Fact]
    public async Task StopPreservesEmptyDirectoryReplacedByReparsePointAndCanRetry()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        var hookRoot = TestHookRoot();
        var replacementTarget = Path.Combine(RepositoryRoot(), ".tmp", "license-local-directory-replacement-target");
        string? stagedDirectory = null;
        try
        {
            AssertScriptSucceeded(await RunScriptAsync("run_local_license_stack.ps1"));
            Directory.CreateDirectory(Path.Combine(RuntimeRoot(), "owned-empty"));
            Directory.CreateDirectory(replacementTarget);
            File.WriteAllText(Path.Combine(replacementTarget, "sentinel.txt"), "outside replacement");

            var stopTask = RunScriptAsync("stop_local_license_stack.ps1", new Dictionary<string, string>
            {
                ["LICENSE_LOCAL_TEST_HOOK_ROOT"] = hookRoot,
                ["LICENSE_LOCAL_TEST_HOOK_NAME"] = "before-quarantine-delete"
            });
            await WaitForFileAsync(Path.Combine(hookRoot, "before-quarantine-delete.ready"));
            var journal = ReadCleanupJournal();
            stagedDirectory = Path.Combine(journal["quarantine_root"]!.GetValue<string>(), "owned-empty");
            Directory.Delete(stagedDirectory);
            CreateJunction(stagedDirectory, replacementTarget);
            File.WriteAllText(Path.Combine(hookRoot, "before-quarantine-delete.release"), "release");

            var rejected = await stopTask;
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.True(Directory.Exists(stagedDirectory));
            Assert.Equal("outside replacement", File.ReadAllText(Path.Combine(replacementTarget, "sentinel.txt")));
            Assert.True(File.Exists(CleanupJournalPath()));

            Directory.Delete(stagedDirectory);
            Directory.CreateDirectory(stagedDirectory);
            AssertScriptSucceeded(await RunScriptAsync("stop_local_license_stack.ps1"));
            stagedDirectory = null;
        }
        finally
        {
            if (stagedDirectory is not null && Directory.Exists(stagedDirectory))
                Directory.Delete(stagedDirectory);
            ReleaseAllHooks(hookRoot);
            await RecoverCleanupAsync();
            if (Directory.Exists(replacementTarget))
                Directory.Delete(replacementTarget, recursive: true);
            if (Directory.Exists(hookRoot))
                Directory.Delete(hookRoot, recursive: true);
        }
        AssertRuntimeAbsent();
    }

    [Fact]
    public async Task PartialQuarantineDeletionRetainsJournalForIdempotentRetry()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        try
        {
            AssertScriptSucceeded(await RunScriptAsync("run_local_license_stack.ps1"));
            var interrupted = await RunScriptAsync("stop_local_license_stack.ps1", new Dictionary<string, string>
            {
                ["LICENSE_LOCAL_TEST_FAIL_DELETE_ROLE"] = "license-admin-local"
            });
            Assert.NotEqual(0, interrupted.ExitCode);
            var journal = ReadCleanupJournal();
            Assert.True(Directory.Exists(journal["quarantine_root"]!.GetValue<string>()));
            Assert.False(Directory.Exists(RuntimeRoot()));

            AssertScriptSucceeded(await RunScriptAsync("stop_local_license_stack.ps1"));
        }
        finally
        {
            await RecoverCleanupAsync();
        }
        AssertRuntimeAbsent();
    }

    [Fact]
    public async Task ConcurrentStopsAreSerializedAndOnlyOneOwnsCleanup()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        var hookRoot = TestHookRoot();
        try
        {
            AssertScriptSucceeded(await RunScriptAsync("run_local_license_stack.ps1"));
            var first = RunScriptAsync("stop_local_license_stack.ps1", new Dictionary<string, string>
            {
                ["LICENSE_LOCAL_TEST_HOOK_ROOT"] = hookRoot,
                ["LICENSE_LOCAL_TEST_HOOK_NAME"] = "before-stage"
            });
            await WaitForFileAsync(Path.Combine(hookRoot, "before-stage.ready"));
            var second = RunScriptAsync("stop_local_license_stack.ps1");
            await Task.Delay(250);
            Assert.False(second.IsCompleted);
            File.WriteAllText(Path.Combine(hookRoot, "before-stage.release"), "release");

            var firstResult = await first;
            var secondResult = await second;
            AssertScriptSucceeded(firstResult);
            Assert.NotEqual(0, secondResult.ExitCode);
        }
        finally
        {
            ReleaseAllHooks(hookRoot);
            await RecoverCleanupAsync();
            if (Directory.Exists(hookRoot))
                Directory.Delete(hookRoot, recursive: true);
        }
        AssertRuntimeAbsent();
    }

    [Fact]
    public async Task StopRetriesTransientRuntimeRenameWhilePreservingTheJournal()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        var hookRoot = TestHookRoot();
        try
        {
            AssertScriptSucceeded(await RunScriptAsync("run_local_license_stack.ps1"));
            var stopTask = RunScriptAsync("stop_local_license_stack.ps1", new Dictionary<string, string>
            {
                ["LICENSE_LOCAL_TEST_HOOK_ROOT"] = hookRoot,
                ["LICENSE_LOCAL_TEST_HOOK_NAME"] = "before-stage"
            });
            await WaitForFileAsync(Path.Combine(hookRoot, "before-stage.ready"));
            using (var locked = new FileStream(Path.Combine(RuntimeRoot(), "client.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                File.WriteAllText(Path.Combine(hookRoot, "before-stage.release"), "release");
                await Task.Delay(500);
            }

            AssertScriptSucceeded(await stopTask);
        }
        finally
        {
            ReleaseAllHooks(hookRoot);
            await RecoverCleanupAsync();
            if (Directory.Exists(hookRoot))
                Directory.Delete(hookRoot, recursive: true);
        }
        AssertRuntimeAbsent();
    }

    [Fact]
    public async Task StackStartsAndStopsThroughCheckoutPathContainingSpaces()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertRuntimeAbsent();
        var junction = Path.Combine(RepositoryRoot(), ".tmp", "license local checkout link");
        Directory.CreateDirectory(Path.GetDirectoryName(junction)!);
        CreateJunction(junction, RepositoryRoot());
        try
        {
            AssertScriptSucceeded(await RunScriptAsync("run_local_license_stack.ps1", checkoutRoot: junction));
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(RuntimeRoot(), "pid-manifest.json")))!.AsObject();
            Assert.Contains("license local checkout link", manifest["runtime_root"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal(2, manifest["processes"]!.AsArray().Count);
            AssertScriptSucceeded(await RunScriptAsync("stop_local_license_stack.ps1", checkoutRoot: junction));
        }
        finally
        {
            if (File.Exists(CleanupJournalPath()) || File.Exists(Path.Combine(RuntimeRoot(), "pid-manifest.json")))
                AssertScriptSucceeded(await RunScriptAsync("stop_local_license_stack.ps1", checkoutRoot: junction));
            if (Directory.Exists(junction))
                Directory.Delete(junction);
        }
        AssertRuntimeAbsent();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunScriptAsync(
        string name,
        IReadOnlyDictionary<string, string>? environment = null,
        string? checkoutRoot = null)
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
        process.StartInfo.ArgumentList.Add(checkoutRoot is null ? ScriptPath(name) : Path.Combine(checkoutRoot, "scripts", name));
        if (environment is not null)
            foreach (var entry in environment)
                process.StartInfo.Environment[entry.Key] = entry.Value;
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
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
        Assert.False(File.Exists(Path.Combine(RepositoryRoot(), ".tmp", "license-service-local.exe")));
        Assert.False(File.Exists(CleanupJournalPath()));
        Assert.Empty(Directory.Exists(Path.Combine(RepositoryRoot(), ".tmp"))
            ? Directory.GetDirectories(Path.Combine(RepositoryRoot(), ".tmp"), "license-local-quarantine-*")
            : Array.Empty<string>());
    }

    private static JsonObject ReadCleanupJournal() =>
        JsonNode.Parse(File.ReadAllText(CleanupJournalPath()))!.AsObject();

    private static string CleanupJournalPath() => Path.Combine(RepositoryRoot(), ".tmp", "license-local-cleanup.json");

    private static string TestHookRoot() => Path.Combine(RepositoryRoot(), ".tmp", "license-local-test-hooks");

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!File.Exists(path))
            await Task.Delay(20, timeout.Token);
    }

    private static void ReleaseAllHooks(string hookRoot)
    {
        if (!Directory.Exists(hookRoot))
            return;
        foreach (var ready in Directory.GetFiles(hookRoot, "*.ready"))
            File.WriteAllText(Path.ChangeExtension(ready, ".release"), "release");
    }

    private static async Task RecoverCleanupAsync()
    {
        if (File.Exists(CleanupJournalPath()) || File.Exists(Path.Combine(RuntimeRoot(), "pid-manifest.json")))
        {
            var recovery = await RunScriptAsync("stop_local_license_stack.ps1");
            AssertScriptSucceeded(recovery);
        }
    }

    private static void AssertScriptSucceeded((int ExitCode, string Output, string Error) result)
    {
        Assert.True(result.ExitCode == 0, $"Script failed with exit code {result.ExitCode}. Output: {result.Output} Error: {result.Error}");
    }

    private static void CreateJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", junction, target }
        });
        Assert.NotNull(process);
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Could not create checkout-path junction: {process.StandardError.ReadToEnd()}");
    }

    private static string RuntimeRoot() => Path.Combine(RepositoryRoot(), ".tmp", "license-local");

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static JsonObject ManifestProcess(JsonObject manifest, string role) =>
        manifest["processes"]!.AsArray().Select(item => item!.AsObject()).Single(item => item["role"]!.GetValue<string>() == role);

    private static HttpRequestMessage JsonRequest(HttpMethod method, string path, JsonObject body, string csrf)
    {
        var request = new HttpRequestMessage(method, "http://127.0.0.1:19090" + path)
        {
            Content = new StringContent(body.ToJsonString())
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("Origin", "http://127.0.0.1:19090");
        request.Headers.Add("X-CSRF-Token", csrf);
        return request;
    }

    private static string ScriptPath(string name) => Path.Combine(RepositoryRoot(), "scripts", name);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BaiduBatchTransfer.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
