using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class OtaReleaseScriptTests
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void ScriptDefinesAcceptanceOnlyContractAndFixedIdentity()
    {
        var script = ReadScript();

        Assert.Contains("[Parameter(Mandatory = $true)][string]$Version", script, StringComparison.Ordinal);
        Assert.Contains("[Parameter(Mandatory = $true)][string]$AcceptanceFeedPath", script, StringComparison.Ordinal);
        Assert.Contains("[Parameter(Mandatory = $true)][string]$OutputDirectory", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$OtaAcceptanceBuild", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$AllowUnsignedLocal", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$ProductionFeed", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$SignParams", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$AuthenticityMode", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$acceptancePackageId = 'SuperPan.BaiduBatchTransfer.OtaAcceptance'", script, StringComparison.Ordinal);
        Assert.Contains("$acceptanceChannel = 'win-x64-ota-acceptance'", script, StringComparison.Ordinal);
        Assert.Contains("$productionPackageId = 'SuperPan.BaiduBatchTransfer'", script, StringComparison.Ordinal);
        Assert.Contains("$productionChannel = 'win-x64-stable'", script, StringComparison.Ordinal);
        Assert.Contains("acceptancePackageId -ceq $productionPackageId", script, StringComparison.Ordinal);
        Assert.Contains("acceptanceChannel -ceq $productionChannel", script, StringComparison.Ordinal);
        Assert.Contains("Assert-NonReparseExistingAncestors $outputPath $artifactRoot", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-beta")]
    [InlineData("65535.0.0")]
    [InlineData("999999999999999999999.0.0")]
    public async Task RejectsNonCanonicalVersionsBeforeCreatingOutput(string version)
    {
        using var fixture = ScriptFixture.Create();

        var result = await fixture.RunAsync(
            "-Version", version,
            "-AcceptanceFeedPath", fixture.FeedDirectory,
            "-OutputDirectory", fixture.OutputDirectory,
            "-OtaAcceptanceBuild",
            "-AllowUnsignedLocal");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("version", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RequiresBothAcceptanceSwitchesBeforeCreatingOutput(
        bool otaAcceptanceBuild,
        bool allowUnsignedLocal)
    {
        using var fixture = ScriptFixture.Create();
        var arguments = new List<string>
        {
            "-Version", "1.2.3",
            "-AcceptanceFeedPath", fixture.FeedDirectory,
            "-OutputDirectory", fixture.OutputDirectory,
        };
        if (otaAcceptanceBuild) arguments.Add("-OtaAcceptanceBuild");
        if (allowUnsignedLocal) arguments.Add("-AllowUnsignedLocal");

        var result = await fixture.RunAsync(arguments.ToArray());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OtaAcceptanceBuild", result.Output, StringComparison.Ordinal);
        Assert.Contains("AllowUnsignedLocal", result.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
    }

    [Theory]
    [InlineData("-ProductionFeedUrl", "https://updates.example.test/stable/")]
    [InlineData("-SignParams", "--azureTrustedSigningFile secret.json")]
    [InlineData("-AuthenticityMode", "PinnedPublisher")]
    public async Task RejectsProductionAndSigningParametersWithoutCreatingOutput(
        string parameter,
        string value)
    {
        using var fixture = ScriptFixture.Create();

        var result = await fixture.RunAsync(
            "-Version", "1.2.3",
            "-AcceptanceFeedPath", fixture.FeedDirectory,
            "-OutputDirectory", fixture.OutputDirectory,
            "-OtaAcceptanceBuild",
            "-AllowUnsignedLocal",
            parameter, value);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(parameter.TrimStart('-'), result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
    }

    [Fact]
    public async Task RejectsOutputOutsideDedicatedAcceptanceArtifactTree()
    {
        using var fixture = ScriptFixture.Create();
        var outside = Path.Combine(Path.GetTempPath(), $"bbt-ota-output-{Guid.NewGuid():N}");
        try
        {
            var result = await fixture.RunAsync(
                "-Version", "1.2.3",
                "-AcceptanceFeedPath", fixture.FeedDirectory,
                "-OutputDirectory", outside,
                "-OtaAcceptanceBuild",
                "-AllowUnsignedLocal");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("OutputDirectory", result.Output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(outside));
        }
        finally
        {
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsNonEmptyOutputWithoutChangingExistingContent()
    {
        using var fixture = ScriptFixture.Create();
        Directory.CreateDirectory(fixture.OutputDirectory);
        var sentinel = Path.Combine(fixture.OutputDirectory, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "do-not-overwrite");

        var result = await fixture.RunAsync(
            "-Version", "1.2.3",
            "-AcceptanceFeedPath", fixture.FeedDirectory,
            "-OutputDirectory", fixture.OutputDirectory,
            "-OtaAcceptanceBuild",
            "-AllowUnsignedLocal");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("empty", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("do-not-overwrite", await File.ReadAllTextAsync(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory));
    }

    [Fact]
    public async Task RejectsVersionAlreadyPresentInPreviousReleaseFeedWithoutCreatingOutput()
    {
        using var fixture = ScriptFixture.Create();
        Directory.CreateDirectory(fixture.PreviousReleaseDirectory);
        var feed = new
        {
            Assets = new[]
            {
                new
                {
                    PackageId = "SuperPan.BaiduBatchTransfer.OtaAcceptance",
                    Version = "1.2.3",
                    Type = "Full",
                    FileName = "SuperPan.BaiduBatchTransfer.OtaAcceptance-1.2.3-full.nupkg",
                    SHA256 = new string('a', 64),
                    Size = 123,
                },
            },
        };
        await File.WriteAllTextAsync(
            Path.Combine(fixture.PreviousReleaseDirectory, "releases.win-x64-ota-acceptance.json"),
            JsonSerializer.Serialize(feed));

        var result = await fixture.RunAsync(
            "-Version", "1.2.3",
            "-AcceptanceFeedPath", fixture.FeedDirectory,
            "-OutputDirectory", fixture.OutputDirectory,
            "-PreviousReleaseDirectory", fixture.PreviousReleaseDirectory,
            "-OtaAcceptanceBuild",
            "-AllowUnsignedLocal");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("already exists", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
    }

    [Fact]
    public void ScriptUsesRecordedArgumentArraysAndLockedAcceptancePipeline()
    {
        var script = ReadScript();

        Assert.DoesNotContain("Invoke-Expression", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("function Invoke-RecordedCommand", script, StringComparison.Ordinal);
        Assert.Contains("& $FilePath @Arguments", script, StringComparison.Ordinal);
        Assert.Contains("stdout", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stderr", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exitCode", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$toolRestoreArguments = @('tool', 'restore')", script, StringComparison.Ordinal);
        Assert.Contains("'restore', $appProject", script, StringComparison.Ordinal);
        Assert.Contains("'--locked-mode'", script, StringComparison.Ordinal);
        Assert.Contains("'-r', 'win-x64'", script, StringComparison.Ordinal);
        Assert.Contains("'-p:RuntimeIdentifier=win-x64'", script, StringComparison.Ordinal);
        Assert.Contains("'-p:SelfContained=true'", script, StringComparison.Ordinal);
        Assert.Contains("'--self-contained', 'true'", script, StringComparison.Ordinal);
        Assert.Contains("'-p:UpdateFeedUrl='", script, StringComparison.Ordinal);
        Assert.Contains("'-p:OtaAcceptanceBuild=true'", script, StringComparison.Ordinal);
        Assert.Contains("'-p:UpdateChannel=win-x64-ota-acceptance'", script, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(
            script,
            Regex.Escape("\"-p:PackageId=$acceptancePackageId\""),
            RegexOptions.CultureInvariant).Count);
        Assert.Contains("verify_managed_browser_helper_package.ps1", script, StringComparison.Ordinal);
        Assert.Contains("$vpkArguments = @(", script, StringComparison.Ordinal);
        Assert.Contains("'tool', 'run', 'vpk', 'pack'", script, StringComparison.Ordinal);
        Assert.Contains("'--packId', $acceptancePackageId", script, StringComparison.Ordinal);
        Assert.Contains("'--channel', $acceptanceChannel", script, StringComparison.Ordinal);
        Assert.Contains("'--runtime', 'win-x64'", script, StringComparison.Ordinal);
        Assert.Contains("'--releaseNotes', $effectiveReleaseNotesPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--signParams", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScriptVerifiesPublishedMetadataFeedAssetsAndCreatesManifestWithoutUploading()
    {
        var script = ReadScript();

        Assert.Contains("GetCustomAttributesData", script, StringComparison.Ordinal);
        Assert.Contains("OtaAcceptanceFeedPath", script, StringComparison.Ordinal);
        Assert.Contains("OtaAcceptanceBuild", script, StringComparison.Ordinal);
        Assert.Contains("releases.$acceptanceChannel.json", script, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-Json", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains("upload-manifest.sha256", script, StringComparison.Ordinal);
        Assert.Contains("本机 OTA 验收", script, StringComparison.Ordinal);

        var forbiddenCommands = new[]
        {
            @"(?im)^\s*(?:ssh|scp|sftp|ftp|azcopy|aws|rclone)\b",
            @"(?im)^\s*Invoke-(?:WebRequest|RestMethod)\b",
            @"(?im)^\s*Start-BitsTransfer\b",
        };
        foreach (var pattern in forbiddenCommands)
            Assert.DoesNotMatch(new Regex(pattern, RegexOptions.CultureInvariant), script);
    }

    private static string ReadScript()
    {
        var path = ScriptPath();
        Assert.True(File.Exists(path), $"OTA release script is missing: {path}");
        return File.ReadAllText(path);
    }

    private static string ScriptPath() => Path.Combine(
        FindRepositoryRoot(),
        "scripts",
        "build_ota_release.ps1");

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class ScriptFixture : IDisposable
    {
        private ScriptFixture(string root)
        {
            Root = root;
            FeedDirectory = Path.Combine(root, "feed");
            OutputDirectory = Path.Combine(root, "release");
            PreviousReleaseDirectory = Path.Combine(root, "previous");
        }

        public string Root { get; }
        public string FeedDirectory { get; }
        public string OutputDirectory { get; }
        public string PreviousReleaseDirectory { get; }

        public static ScriptFixture Create()
        {
            var root = Path.Combine(
                FindRepositoryRoot(),
                ".artifacts",
                $"ota-acceptance-script-tests-{Guid.NewGuid():N}");
            return new ScriptFixture(root);
        }

        public Task<ScriptResult> RunAsync(params string[] arguments) =>
            RunPowerShellAsync(ScriptPath(), arguments);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<ScriptResult> RunPowerShellAsync(string script, string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start PowerShell.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ScriptTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited while timeout handling started.
            }

            await process.WaitForExitAsync();
            throw new TimeoutException($"OTA release script exceeded {ScriptTimeout}.");
        }

        return new ScriptResult(
            process.ExitCode,
            string.Join(Environment.NewLine, await standardOutput, await standardError));
    }

    private sealed record ScriptResult(int ExitCode, string Output);
}
