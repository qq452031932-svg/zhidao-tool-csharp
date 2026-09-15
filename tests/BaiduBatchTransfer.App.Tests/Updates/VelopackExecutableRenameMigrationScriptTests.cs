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
        Assert.Contains("TerminateJobObject", script, StringComparison.Ordinal);
        Assert.Contains("MaximumOfflinePollMilliseconds", script, StringComparison.Ordinal);
        Assert.Contains("defense-in-depth", script, StringComparison.Ordinal);
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
    public void ScriptDirectlyLaunchesTheVerifiedUpdaterInASuspendedContainedJob()
    {
        var script = ReadScript();

        Assert.DoesNotContain("'perform-upgrade'", script, StringComparison.Ordinal);
        Assert.Contains("MigrationContainedRunner", script, StringComparison.Ordinal);
        Assert.Contains("CREATE_SUSPENDED", script, StringComparison.Ordinal);
        Assert.Contains("AssignProcessToJobObject", script, StringComparison.Ordinal);
        Assert.Contains("JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE", script, StringComparison.Ordinal);
        Assert.Contains("@('apply','--package'", script, StringComparison.Ordinal);
        Assert.Contains("source_asset_sha256", script, StringComparison.Ordinal);
        Assert.Contains("DownloadInterrupted", script, StringComparison.Ordinal);
        Assert.Contains("ApplicationFailed", script, StringComparison.Ordinal);
        Assert.Contains("Assert-ArtifactReadLockUnchanged", script, StringComparison.Ordinal);
        Assert.Contains("MigrationFileIdentity", script, StringComparison.Ordinal);
        Assert.Contains("challenge_sha256", script, StringComparison.Ordinal);
        Assert.Contains("partial_artifact_path", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialArtifactMustBeExactSourcePrefix()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-prefix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = System.IO.Path.Combine(root, "source.bin");
        var prefix = System.IO.Path.Combine(root, "prefix.bin");
        try
        {
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(prefix, [1, 9]);
            var result = await RunFunctionAsync("Assert-FileIsExactPrefix", new Dictionary<string, string> { ["Source"] = source, ["Prefix"] = prefix });
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("prefix", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DownloadInterruptedFingerprintAcceptsExactFakeOutputAndRejectsWrongOrInvalidUtf8()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-fingerprint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var stdout = System.IO.Path.Combine(root, "stdout.log"); var stderr = System.IO.Path.Combine(root, "stderr.log");
        try
        {
            await File.WriteAllTextAsync(stdout, "VELOPACK_PARTIAL_REJECTED source.nupkg interrupted-partial.nupkg\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(stderr, "", new UTF8Encoding(false));
            var args = new Dictionary<string, string> { ["ExitCode"]="17", ["StdoutPath"]=stdout, ["StderrPath"]=stderr, ["ExpectedStdoutSha256"]=Hash(stdout), ["ExpectedStderrSha256"]=Hash(stderr), ["ExpectedExitCode"]="17", ["ExpectedMarker"]="VELOPACK_PARTIAL_REJECTED", ["SourceLeaf"]="source.nupkg", ["PartialLeaf"]="interrupted-partial.nupkg" };
            var accepted = await RunFunctionAsync("Assert-DownloadInterruptedFingerprint", args);
            Assert.True(accepted.ExitCode == 0, accepted.Output);
            args["ExpectedStdoutSha256"] = new string('0', 64);
            var wrong = await RunFunctionAsync("Assert-DownloadInterruptedFingerprint", args);
            Assert.NotEqual(0, wrong.ExitCode);
            await File.WriteAllBytesAsync(stdout, [0xff, 0xfe]); args["ExpectedStdoutSha256"] = Hash(stdout);
            var invalid = await RunFunctionAsync("Assert-DownloadInterruptedFingerprint", args);
            Assert.NotEqual(0, invalid.ExitCode);
            Assert.Contains("UTF-8", invalid.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task IndependentDpapiP256LicenseAuditAcceptsBoundClaimsAndRejectsMismatchedExpiry()
    {
        var profile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-license-audit-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(profile);
            var valid = WriteDpapiLicenseFixture(profile, mismatchClaimsExpiry: false);
            var accepted = await RunFunctionAsync("Assert-IndependentLicenseAudit", new Dictionary<string, string>
            {
                ["StateJson"] = valid.StateJson, ["ProfileRoot"] = profile, ["FixtureJson"] = valid.FixtureJson,
            });
            Assert.True(accepted.ExitCode == 0, accepted.Output);

            var invalid = WriteDpapiLicenseFixture(profile, mismatchClaimsExpiry: true);
            var rejected = await RunFunctionAsync("Assert-IndependentLicenseAudit", new Dictionary<string, string>
            {
                ["StateJson"] = invalid.StateJson, ["ProfileRoot"] = profile, ["FixtureJson"] = invalid.FixtureJson,
            });
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains("bindings", rejected.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); }
    }

    [Theory]
    [InlineData("1.1.10")]
    [InlineData("1.1.01")]
    [InlineData("1.1.0.0")]
    public async Task NonExactOrNonCanonicalVersionsAreRejected(string actual)
    {
        var result = await RunFunctionAsync("Assert-CanonicalSemverText", new Dictionary<string, string> { ["Actual"] = actual, ["Expected"] = "1.1.0" });
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task NonMinimalDerEcdsaIntegerIsRejected()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-der-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var signature = System.IO.Path.Combine(root, "signature.bin");
        try
        {
            await File.WriteAllBytesAsync(signature, [0x30,0x07,0x02,0x02,0x00,0x01,0x02,0x01,0x01]);
            var result = await RunFunctionAsync("Assert-DerEcdsaSignatureFile", new Dictionary<string, string> { ["SignaturePath"] = signature });
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("integer", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SpoofedUninstallArgumentsAreRejected()
    {
        var result = await RunFunctionAsync("Assert-UninstallCommandLine", new Dictionary<string, string>
        {
            ["CommandLine"] = "\"C:\\app\\Update.exe\" --uninstall --silent", ["ExpectedUpdater"] = "C:\\app\\Update.exe",
        });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("exact", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PortableZipTraversalIsRejectedBeforeExtraction()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-zip-{Guid.NewGuid():N}");
        var destination = System.IO.Path.Combine(root, "destination");
        Directory.CreateDirectory(destination);
        var archive = System.IO.Path.Combine(root, "portable.zip");
        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open())) { writer.Write("escape"); }
            var result = await RunFunctionAsync("Expand-LocalPortableAsset", new Dictionary<string, string> { ["Archive"] = archive, ["Destination"] = destination });
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(System.IO.Path.Combine(root, "escape.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PortableZipLimitsAreExplicit()
    {
        var script = ReadScript();
        Assert.Contains("4096", script, StringComparison.Ordinal);
        Assert.Contains("536870912", script, StringComparison.Ordinal);
        Assert.Contains("2147483648", script, StringComparison.Ordinal);
        Assert.Contains("compression ratio exceeds 200", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PortableZipEntryCountAndCompressionBombAreRejectedBehaviorally()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-zip-limits-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var countArchive = System.IO.Path.Combine(root, "count.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(countArchive, System.IO.Compression.ZipArchiveMode.Create))
                for (var index = 0; index < 4097; index++) zip.CreateEntry($"e{index}");
            var countDestination = System.IO.Path.Combine(root, "count-output");
            Directory.CreateDirectory(countDestination);
            var countResult = await RunFunctionAsync("Expand-LocalPortableAsset", new Dictionary<string, string> { ["Archive"] = countArchive, ["Destination"] = countDestination });
            Assert.NotEqual(0, countResult.ExitCode);
            Assert.Contains("entry count", countResult.Output, StringComparison.OrdinalIgnoreCase);

            var bombArchive = System.IO.Path.Combine(root, "bomb.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(bombArchive, System.IO.Compression.ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry("zeros.bin", System.IO.Compression.CompressionLevel.Optimal).Open())
                stream.Write(new byte[1024 * 1024]);
            var bombDestination = System.IO.Path.Combine(root, "bomb-output");
            Directory.CreateDirectory(bombDestination);
            var bombResult = await RunFunctionAsync("Expand-LocalPortableAsset", new Dictionary<string, string> { ["Archive"] = bombArchive, ["Destination"] = bombDestination });
            Assert.NotEqual(0, bombResult.ExitCode);
            Assert.Contains("compression ratio", bombResult.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ContainedRunnerCapturesAnImmediatelyExitingProcess()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-orchestration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var updater = System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var harness = System.IO.Path.Combine(root, "harness.ps1");
        var rootHelper = System.IO.Path.Combine(root, "root-helper.ps1");
        var childHelper = System.IO.Path.Combine(root, "child-helper.ps1");
        var childExe = System.IO.Path.Combine(root, "RestartChild.exe");
        var invalidUtf8Exe = System.IO.Path.Combine(root, "InvalidUtf8.exe");
        try
        {
            var childSource = System.IO.Path.Combine(root, "RestartChild.cs");
            await File.WriteAllTextAsync(childSource, "using System.Threading; static class P { [System.STAThread] static void Main() { Thread.Sleep(30000); } }", new UTF8Encoding(false));
            var csc = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
            var compile = new ProcessStartInfo(csc) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var value in new[] { "/nologo", "/target:winexe", $"/out:{childExe}", childSource }) compile.ArgumentList.Add(value);
            using (var compiler = Process.Start(compile)!) { var error = await compiler.StandardError.ReadToEndAsync(); await compiler.WaitForExitAsync(); Assert.True(compiler.ExitCode == 0, error); }
            var invalidSource = System.IO.Path.Combine(root, "InvalidUtf8.cs");
            await File.WriteAllTextAsync(invalidSource, "static class P { static void Main() { System.Console.OpenStandardOutput().WriteByte(255); } }", new UTF8Encoding(false));
            var compileInvalid = new ProcessStartInfo(csc) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var value in new[] { "/nologo", $"/out:{invalidUtf8Exe}", invalidSource }) compileInvalid.ArgumentList.Add(value);
            using (var compiler = Process.Start(compileInvalid)!) { var error = await compiler.StandardError.ReadToEndAsync(); await compiler.WaitForExitAsync(); Assert.True(compiler.ExitCode == 0, error); }
            await File.WriteAllTextAsync(childHelper, "Start-Sleep -Seconds 30\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(rootHelper, "param([string]$ChildExe,[string]$ChildScript,[int]$Count=1,[switch]$Ping,[switch]$Raw)\n1..$Count|ForEach-Object{if($Raw){Start-Process -FilePath $ChildExe|Out-Null}elseif($Ping){Start-Process -FilePath $ChildExe -ArgumentList @('-t','127.0.0.1')|Out-Null}else{Start-Process -FilePath $ChildExe -ArgumentList @('-NoProfile','-File',$ChildScript)|Out-Null}}\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(harness, """
                param([string]$ScriptPath,[string]$Updater,[string]$WorkingDirectory,[string]$RootHelper,[string]$ChildHelper,[string]$ChildExe,[string]$InvalidUtf8Exe)
                $ErrorActionPreference='Stop'
                $text=[IO.File]::ReadAllText($ScriptPath)
                $match=[regex]::Match($text,"(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@")
                if(-not$match.Success){throw 'Type block missing.'}
                $source=$match.Groups[1].Value -replace 'static bool Offline\(\)\{foreach\(var n.*?return true;\}','static bool Offline(){return true;}'
                Add-Type -TypeDefinition $source
                $preSource=$source.Replace('Migration','PreassignMigration').Replace('r.ProcessId=(int)pi.processId;','r.ProcessId=(int)pi.processId;if(Environment.GetEnvironmentVariable("MIGRATION_FORCE_PREASSIGN") == "1")throw new InvalidOperationException("forced pre-assign image failure");')
                Add-Type -TypeDefinition $preSource
                $env:MIGRATION_FORCE_PREASSIGN='1';try{$pre=[PreassignMigrationContainedRunner]::Run($Updater,@('-NoProfile','-Command','Start-Sleep 30'),$WorkingDirectory,(Join-Path $WorkingDirectory 'pre.out'),(Join-Path $WorkingDirectory 'pre.err'),5000,65536,'NoRestart',$null)}finally{Remove-Item Env:MIGRATION_FORCE_PREASSIGN}
                if(-not$pre.Failure-or$pre.ProcessId-le 0-or(Get-Process -Id $pre.ProcessId -ErrorAction SilentlyContinue)){throw 'Pre-assign image failure leaked its suspended root process.'}
                $assignSource=$source.Replace('Migration','AssignFailMigration').Replace('if(!AssignProcessToJobObject(job,pi.process))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());assigned=true;','if(Environment.GetEnvironmentVariable("MIGRATION_FORCE_ASSIGN") == "1")throw new InvalidOperationException("forced assign failure");if(!AssignProcessToJobObject(job,pi.process))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());assigned=true;')
                Add-Type -TypeDefinition $assignSource
                $env:MIGRATION_FORCE_ASSIGN='1';try{$assign=[AssignFailMigrationContainedRunner]::Run($Updater,@('-NoProfile','-Command','Start-Sleep 30'),$WorkingDirectory,(Join-Path $WorkingDirectory 'assign.out'),(Join-Path $WorkingDirectory 'assign.err'),5000,65536,'NoRestart',$null)}finally{Remove-Item Env:MIGRATION_FORCE_ASSIGN}
                if(-not$assign.Failure-or$assign.ProcessId-le 0-or(Get-Process -Id $assign.ProcessId -ErrorAction SilentlyContinue)){throw 'Assign failure leaked its suspended root process.'}
                $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($ScriptPath,[ref]$tokens,[ref]$errors)
                foreach($function in @($ast.FindAll({param($node)$node-is[Management.Automation.Language.FunctionDefinitionAst]},$true))){Invoke-Expression $function.Extent.Text}
                $lockedFile=Join-Path $WorkingDirectory 'locked.bin';[IO.File]::WriteAllBytes($lockedFile,[byte[]](1,2,3,4))
                $lock=Open-ArtifactReadLock -File $lockedFile -Label 'test locked file'
                try{$blocked=$false;try{[IO.File]::Delete($lockedFile)}catch [IO.IOException]{$blocked=$true};if(-not$blocked){throw 'Replacement/delete was not blocked by the artifact lock.'};Assert-ArtifactReadLockUnchanged $lock}finally{$lock.Stream.Dispose()}
                $run=[MigrationContainedRunner]::Run($Updater,@('-NoProfile','-Command','exit 7'),$WorkingDirectory,(Join-Path $WorkingDirectory 'out.log'),(Join-Path $WorkingDirectory 'err.log'),5000,65536,'NoRestart',$null)
                if($run.ProcessId-le 0-or$run.ExitCode-ne 7-or-not$run.SafeToCleanup-or$run.Failure){throw ($run|ConvertTo-Json -Compress)}
                $invalid=[MigrationContainedRunner]::Run($InvalidUtf8Exe,@(),$WorkingDirectory,(Join-Path $WorkingDirectory 'invalid.out'),(Join-Path $WorkingDirectory 'invalid.err'),5000,65536,'NoRestart',$null)
                if(-not$invalid.Failure-or$invalid.Failure-notmatch'UTF-8'){throw 'Invalid UTF-8 process output was accepted.'}
                $alive=[MigrationContainedRunner]::Run($ChildExe,@(),$WorkingDirectory,(Join-Path $WorkingDirectory 'alive.out'),(Join-Path $WorkingDirectory 'alive.err'),5000,65536,'ExpectRootAlive',$ChildExe)
                if($alive.Failure-or$alive.JobHandle-eq[IntPtr]::Zero-or-not(Get-Process -Id $alive.RestartPid -ErrorAction SilentlyContinue)){throw ($alive|ConvertTo-Json -Compress)}
                $alivePid=$alive.RestartPid;if(-not[MigrationContainedRunner]::CloseSession($alive)){throw 'ExpectRootAlive job did not empty.'};Start-Sleep -Milliseconds 100;if(Get-Process -Id $alivePid -ErrorAction SilentlyContinue){throw 'ExpectRootAlive process survived session close.'}
                $success=[MigrationContainedRunner]::Run($Updater,@('-NoProfile','-File',$RootHelper,'-ChildExe',$ChildExe,'-Raw','-Count','1'),$WorkingDirectory,(Join-Path $WorkingDirectory 'success.out'),(Join-Path $WorkingDirectory 'success.err'),5000,65536,'SuccessExpectRestart',$ChildExe)
                if($success.Failure-or$success.RestartPid-le 0-or$success.JobHandle-eq[IntPtr]::Zero-or-not(Get-Process -Id $success.RestartPid -ErrorAction SilentlyContinue)){throw ($success|ConvertTo-Json -Compress)}
                $restartPid=$success.RestartPid;if(-not[MigrationContainedRunner]::CloseSession($success)){throw 'Restart job did not empty.'};Start-Sleep -Milliseconds 100;if(Get-Process -Id $restartPid -ErrorAction SilentlyContinue){throw 'Restart child survived job disposal.'}
                $two=[MigrationContainedRunner]::Run($Updater,@('-NoProfile','-File',$RootHelper,'-ChildExe',$ChildExe,'-Raw','-Count','2'),$WorkingDirectory,(Join-Path $WorkingDirectory 'two.out'),(Join-Path $WorkingDirectory 'two.err'),5000,65536,'SuccessExpectRestart',$ChildExe)
                if(-not$two.Failure-or-not$two.SafeToCleanup){throw ($two|ConvertTo-Json -Compress)}
                $ping=Join-Path $env:SystemRoot 'System32\PING.EXE';$wrong=[MigrationContainedRunner]::Run($Updater,@('-NoProfile','-File',$RootHelper,'-ChildExe',$ping,'-Ping'),$WorkingDirectory,(Join-Path $WorkingDirectory 'wrong.out'),(Join-Path $WorkingDirectory 'wrong.err'),5000,65536,'SuccessExpectRestart',$Updater)
                if(-not$wrong.Failure-or-not$wrong.SafeToCleanup){throw ($wrong|ConvertTo-Json -Compress)}
                $noRestart=[MigrationContainedRunner]::Run($Updater,@('-NoProfile','-File',$RootHelper,'-ChildExe',$ChildExe,'-Raw','-Count','1'),$WorkingDirectory,(Join-Path $WorkingDirectory 'none.out'),(Join-Path $WorkingDirectory 'none.err'),5000,65536,'NoRestart',$null)
                if(-not$noRestart.Failure-or-not$noRestart.SafeToCleanup){throw ($noRestart|ConvertTo-Json -Compress)}
                """, new UTF8Encoding(false));
            var startInfo = PowerShell();
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", harness, "-ScriptPath", ScriptPath(), "-Updater", updater, "-WorkingDirectory", root, "-RootHelper", rootHelper, "-ChildHelper", childHelper, "-ChildExe", childExe, "-InvalidUtf8Exe", invalidUtf8Exe }) startInfo.ArgumentList.Add(argument);
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
                "{\"application_failure_hook_relative_path\":\"current\\\\application-failure.json\",\"application_failure_package_file\":\"application-failure.nupkg\",\"download_interrupted_exit_code\":17,\"download_interrupted_marker\":\"VELOPACK_PARTIAL_REJECTED\",\"download_interrupted_stderr_sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"download_interrupted_stdout_sha256\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"license_signing_key_id\":\"production-key-1\",\"license_signing_spki_base64\":\"" + Convert.ToBase64String(licenseKey.ExportSubjectPublicKeyInfo()) + "\",\"new_delta_file\":\"new-delta.nupkg\",\"new_full_file\":\"new-full.nupkg\",\"new_main_sha256\":\"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\",\"old_full_file\":\"old-full.nupkg\",\"old_portable_file\":\"old.zip\",\"old_setup_file\":\"old.exe\",\"schema_version\":1,\"state_driver_file\":\"driver.exe\",\"state_driver_sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}\n");
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
    public async Task ProfileEvidenceJunctionIsRejectedBeforeTheGateCanWriteOrLaunch()
    {
        var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $"migration-profile-topology-{Guid.NewGuid():N}");
        var target = System.IO.Path.Combine(root, "target");
        var link = System.IO.Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        try
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var value in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(value);
            using (var process = Process.Start(start)!)
            {
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, error);
            }
            var result = await RunFunctionAsync("Assert-SafeProfileTopology", new Dictionary<string, string>
            {
                ["EvidencePath"] = System.IO.Path.Combine(link, "evidence"), ["IsolationConfirmed"] = "__POWERSHELL_TRUE__",
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
        Assert.Contains("Copy-BoundedLockedAsset", script, StringComparison.Ordinal);
        Assert.Contains("Copy-FromArtifactReadLock", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ChildItem -LiteralPath $Source -Force -Recurse", script, StringComparison.Ordinal);
        Assert.Contains("AssetSource = 'caller-supplied-local-directories'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WebRequest", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-RestMethod", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HttpClient", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Item -LiteralPath $OldAssets", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Item -LiteralPath $NewAssets", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemainingCopyBudgetFailsBeforeCreatingDestination()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rename-copy-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root); var source = System.IO.Path.Combine(root, "source.bin"); var destination = System.IO.Path.Combine(root, "destination.bin");
        try
        {
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            var result = await RunFunctionAsync("Copy-BoundedLockedAsset", new Dictionary<string, string> { ["Source"]=source, ["Destination"]=destination, ["MaximumBytes"]="10", ["RemainingBudget"]="3" });
            Assert.NotEqual(0, result.ExitCode); Assert.False(File.Exists(destination));
        }
        finally { Directory.Delete(root, recursive: true); }
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
        Assert.Contains("JobObjectBasicProcessIdList", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Process", script, StringComparison.Ordinal);
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
                Add-Type -AssemblyName System.Security
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                Add-Type -TypeDefinition @'
                using System;using System.Runtime.InteropServices;
                public static class MigrationNativeCommandLine {
                  [DllImport("shell32.dll",SetLastError=true)]static extern IntPtr CommandLineToArgvW(string c,out int n);
                  [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr p);
                  public static string[] Split(string c){int n;var p=CommandLineToArgvW(c,out n);var r=new string[n];try{for(int i=0;i<n;i++)r[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(p,i*IntPtr.Size));return r;}finally{LocalFree(p);}}
                }
                '@
                $tokens=$null;$errors=$null
                $ast=[Management.Automation.Language.Parser]::ParseFile($ScriptPath,[ref]$tokens,[ref]$errors)
                if($errors.Count){throw 'Migration script parse failed.'}
                foreach($function in @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true))){
                    Invoke-Expression $function.Extent.Text
                }
                $object=Get-Content -LiteralPath $ArgumentsPath -Raw -Encoding UTF8|ConvertFrom-Json
                $splat=@{}
                foreach($property in $object.PSObject.Properties){$splat[$property.Name]=if([string]$property.Value-ceq'__POWERSHELL_TRUE__'){$true}else{[string]$property.Value}}
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

    private static (string StateJson, string FixtureJson) WriteDpapiLicenseFixture(string profile, bool mismatchClaimsExpiry)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string requestId = "11111111-1111-1111-1111-111111111111";
        const string softwareId = "8270e8d7-1791-487d-ad76-0da7a0cbed12";
        const string machineHash = "machine-hash-1";
        const string packageName = "production";
        const string cardId = "card-1";
        var serverTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        var outerExpires = DateTimeOffset.UtcNow.AddHours(2);
        var outerLease = DateTimeOffset.UtcNow.AddHours(1);
        var claimsExpires = mismatchClaimsExpiry ? DateTimeOffset.UtcNow.AddMinutes(-1) : outerExpires;
        var claims = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["protocol_version"] = 1, ["request_id"] = requestId, ["server_time"] = serverTime,
            ["decision"] = "allow", ["code"] = "ok", ["card_id"] = cardId, ["software_id"] = softwareId,
            ["machine_hash"] = machineHash, ["package_name"] = packageName, ["expires_at"] = claimsExpires,
            ["lease_until"] = outerLease,
        });
        var signature = key.SignData(claims, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var envelope = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["key_id"] = "test-key-1", ["payload"] = Convert.ToBase64String(claims), ["signature"] = Convert.ToBase64String(signature),
        });
        var store = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            credentials = new[] { new { card = "test-card", activation_request_id = requestId, leases = new[] { new { card_id = cardId, software_id = softwareId, machine_hash = machineHash, package_name = packageName, expires_at = outerExpires, lease_until = outerLease, signed_payload = envelope } } } },
        });
        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes("SuperPan.Licensing.State.v1"));
        var protectedBytes = ProtectedData.Protect(store, entropy, DataProtectionScope.CurrentUser);
        var licensePath = System.IO.Path.Combine(profile, "AppData", "Local", "SuperPan", "Licensing", "v1", "licenses.dat");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(licensePath)!);
        File.WriteAllBytes(licensePath, protectedBytes);
        var state = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["activation_request_id"] = requestId, ["expires_at_utc"] = outerExpires.ToString("O"),
            ["lease_until_utc"] = outerLease.ToString("O"), ["machine_hash"] = machineHash, ["package_name"] = packageName,
            ["signed_payload_sha256"] = HashText(envelope),
        });
        var fixture = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["application_failure_hook_relative_path"] = "current\\application-failure.json", ["application_failure_package_file"] = "application-failure.nupkg",
            ["download_interrupted_exit_code"] = 17, ["download_interrupted_marker"] = "VELOPACK_PARTIAL_REJECTED", ["download_interrupted_stderr_sha256"] = new string('b', 64), ["download_interrupted_stdout_sha256"] = new string('c', 64),
            ["license_signing_key_id"] = "test-key-1", ["license_signing_spki_base64"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            ["new_main_sha256"] = new string('d', 64),
        });
        return (state, fixture);
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
