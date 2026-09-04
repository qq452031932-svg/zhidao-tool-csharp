using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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
    [InlineData("VPK_SIGN_PARAMS")]
    [InlineData("VPK_SIGN_TEMPLATE")]
    [InlineData("VPK_PACK_ARGS")]
    public async Task RejectsInheritedVpkOptionInjectionBeforeAnyOutput(string variableName)
    {
        using var fixture = ScriptFixture.Create();

        var result = await fixture.RunWithEnvironmentAsync(
            new Dictionary<string, string?> { [variableName] = "untrusted-option" },
            "-Version", "not-a-version",
            "-AcceptanceFeedPath", fixture.FeedDirectory,
            "-OutputDirectory", fixture.OutputDirectory,
            "-OtaAcceptanceBuild",
            "-AllowUnsignedLocal");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(variableName, result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
    }

    [Theory]
    [InlineData("AcceptanceFeedPath")]
    [InlineData("OutputDirectory")]
    [InlineData("PreviousReleaseDirectory")]
    [InlineData("ReleaseNotesPath")]
    public async Task RejectsUncForEveryPathInputBeforeAnyFilesystemAccess(string parameterName)
    {
        using var fixture = ScriptFixture.Create();
        var arguments = new List<string>
        {
            "-Version", "not-a-version",
            "-AcceptanceFeedPath", fixture.FeedDirectory,
            "-OutputDirectory", fixture.OutputDirectory,
            "-OtaAcceptanceBuild",
            "-AllowUnsignedLocal",
        };
        var index = arguments.IndexOf($"-{parameterName}");
        if (index >= 0)
            arguments[index + 1] = @"\\server\share\path";
        else
        {
            arguments.Insert(arguments.Count - 2, $"-{parameterName}");
            arguments.Insert(arguments.Count - 2, @"\\server\share\path");
        }

        var result = await fixture.RunAsync(arguments.ToArray());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(parameterName, result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local filesystem", result.Output, StringComparison.OrdinalIgnoreCase);
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
    public async Task RejectsUncAcceptanceFeedAsNonLocalBeforeCreatingOutput()
    {
        using var fixture = ScriptFixture.Create();
        var outside = Path.Combine(Path.GetTempPath(), $"bbt-ota-output-{Guid.NewGuid():N}");

        var result = await fixture.RunAsync(
            "-Version", "1.2.3",
            "-AcceptanceFeedPath", @"\\server\share\feed",
            "-OutputDirectory", outside,
            "-OtaAcceptanceBuild",
            "-AllowUnsignedLocal");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("local", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(outside));
    }

    [Fact]
    public async Task RejectsAcceptanceFeedThatIsAnExistingFileBeforeCreatingOutput()
    {
        using var fixture = ScriptFixture.Create();
        Directory.Delete(fixture.FeedDirectory);
        await File.WriteAllTextAsync(fixture.FeedDirectory, "not-a-directory");
        Directory.CreateDirectory(fixture.OutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.OutputDirectory, "keep.txt"), "sentinel");

        var result = await fixture.RunAsync(
            "-Version", "1.2.3",
            "-AcceptanceFeedPath", fixture.FeedDirectory,
            "-OutputDirectory", fixture.OutputDirectory,
            "-OtaAcceptanceBuild",
            "-AllowUnsignedLocal");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("directory", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory));
    }

    [Fact]
    public async Task RequiresAcceptanceFeedDirectoryToExistBeforeCreatingOutput()
    {
        using var fixture = ScriptFixture.Create();
        Directory.Delete(fixture.FeedDirectory);
        Directory.CreateDirectory(fixture.OutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(fixture.OutputDirectory, "keep.txt"), "sentinel");

        var result = await fixture.RunAsync(
            "-Version", "1.2.3",
            "-AcceptanceFeedPath", fixture.FeedDirectory,
            "-OutputDirectory", fixture.OutputDirectory,
            "-OtaAcceptanceBuild",
            "-AllowUnsignedLocal");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("existing", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("directory", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory));
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
        var bytes = new byte[] { 11, 12, 13 };
        var fileName = "SuperPan.BaiduBatchTransfer.OtaAcceptance-1.2.3-full.nupkg";
        await File.WriteAllBytesAsync(Path.Combine(fixture.PreviousReleaseDirectory, fileName), bytes);
        await WriteFeedAsync(
            fixture.PreviousReleaseDirectory,
            "releases.win-x64-ota-acceptance.json",
            "SuperPan.BaiduBatchTransfer.OtaAcceptance",
            "1.2.3",
            fileName,
            LowerSha256(bytes),
            bytes.Length);

        var result = await fixture.RunAsync(
            "-Version", "1.2.3",
            "-AcceptanceFeedPath", fixture.FeedDirectory,
            "-OutputDirectory", fixture.OutputDirectory,
            "-PreviousReleaseDirectory", fixture.PreviousReleaseDirectory,
            "-OtaAcceptanceBuild",
            "-AllowUnsignedLocal");

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(
            result.Output.Contains("already exists", StringComparison.OrdinalIgnoreCase),
            result.Output);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
    }

    [Fact]
    public async Task RejectsProductionFeedInPreviousReleaseDirectory()
    {
        using var fixture = ScriptFixture.Create();
        Directory.CreateDirectory(fixture.PreviousReleaseDirectory);
        await WriteFeedAsync(
            fixture.PreviousReleaseDirectory,
            "releases.win-x64-stable.json",
            "SuperPan.BaiduBatchTransfer",
            "1.2.3",
            "SuperPan.BaiduBatchTransfer-1.2.3-full.nupkg",
            new string('a', 64),
            1);

        var result = await fixture.RunWithPreviousAsync("1.2.3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("acceptance release feed", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
    }

    [Fact]
    public async Task RejectsForeignPackageIdentityInAcceptanceNamedFeed()
    {
        using var fixture = ScriptFixture.Create();
        Directory.CreateDirectory(fixture.PreviousReleaseDirectory);
        var bytes = new byte[] { 1, 2, 3 };
        var fileName = "Foreign.Package-1.2.3-full.nupkg";
        await File.WriteAllBytesAsync(Path.Combine(fixture.PreviousReleaseDirectory, fileName), bytes);
        await WriteFeedAsync(
            fixture.PreviousReleaseDirectory,
            "releases.win-x64-ota-acceptance.json",
            "Foreign.Package",
            "1.2.3",
            fileName,
            LowerSha256(bytes),
            bytes.Length);

        var result = await fixture.RunWithPreviousAsync("1.2.3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("package ID", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
    }

    [Theory]
    [InlineData(false, false, "missing")]
    [InlineData(true, true, "SHA-256")]
    public async Task RejectsMissingOrMismatchedPackageReferencedByPreviousFeed(
        bool createPackage,
        bool useWrongHash,
        string expectedMessage)
    {
        using var fixture = ScriptFixture.Create();
        Directory.CreateDirectory(fixture.PreviousReleaseDirectory);
        var bytes = new byte[] { 4, 5, 6 };
        var fileName = "SuperPan.BaiduBatchTransfer.OtaAcceptance-1.2.3-full.nupkg";
        if (createPackage)
            await File.WriteAllBytesAsync(Path.Combine(fixture.PreviousReleaseDirectory, fileName), bytes);
        await WriteFeedAsync(
            fixture.PreviousReleaseDirectory,
            "releases.win-x64-ota-acceptance.json",
            "SuperPan.BaiduBatchTransfer.OtaAcceptance",
            "1.2.3",
            fileName,
            useWrongHash ? new string('a', 64) : LowerSha256(bytes),
            bytes.Length);

        var result = await fixture.RunWithPreviousAsync("1.2.3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedMessage, result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
    }

    [Fact]
    public async Task RejectsUnreferencedNupkgInPreviousReleaseDirectory()
    {
        using var fixture = ScriptFixture.Create();
        Directory.CreateDirectory(fixture.PreviousReleaseDirectory);
        var bytes = new byte[] { 7, 8, 9 };
        var fileName = "SuperPan.BaiduBatchTransfer.OtaAcceptance-1.2.3-full.nupkg";
        await File.WriteAllBytesAsync(Path.Combine(fixture.PreviousReleaseDirectory, fileName), bytes);
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.PreviousReleaseDirectory, "Foreign.Package-9.9.9-full.nupkg"),
            new byte[] { 10 });
        await WriteFeedAsync(
            fixture.PreviousReleaseDirectory,
            "releases.win-x64-ota-acceptance.json",
            "SuperPan.BaiduBatchTransfer.OtaAcceptance",
            "1.2.3",
            fileName,
            LowerSha256(bytes),
            bytes.Length);

        var result = await fixture.RunWithPreviousAsync("1.2.3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unreferenced", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OutputDirectory));
    }

    [Fact]
    public async Task RejectsUnsafePackageFileNameInPreviousFeed()
    {
        using var fixture = ScriptFixture.Create();
        Directory.CreateDirectory(fixture.PreviousReleaseDirectory);
        await WriteFeedAsync(
            fixture.PreviousReleaseDirectory,
            "releases.win-x64-ota-acceptance.json",
            "SuperPan.BaiduBatchTransfer.OtaAcceptance",
            "1.2.3",
            "CON.nupkg",
            LowerSha256(Array.Empty<byte>()),
            0);

        var result = await fixture.RunWithPreviousAsync("1.2.3");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unsafe", result.Output, StringComparison.OrdinalIgnoreCase);
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
        Assert.DoesNotContain("-p:PackageId=", script, StringComparison.Ordinal);
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
        Assert.Contains("[Security.Cryptography.SHA256]::Create()", script, StringComparison.Ordinal);
        Assert.Contains("upload-manifest.sha256", script, StringComparison.Ordinal);
        Assert.Contains("$validatedPreviousAssets", script, StringComparison.Ordinal);
        Assert.Contains("$manifestAssetPaths", script, StringComparison.Ordinal);
        Assert.Contains("LOCAL-OTA-ACCEPTANCE-README.zh-CN.txt", script, StringComparison.Ordinal);

        var forbiddenCommands = new[]
        {
            @"(?im)^\s*(?:ssh|scp|sftp|ftp|azcopy|aws|rclone)\b",
            @"(?im)^\s*Invoke-(?:WebRequest|RestMethod)\b",
            @"(?im)^\s*Start-BitsTransfer\b",
        };
        foreach (var pattern in forbiddenCommands)
            Assert.DoesNotMatch(new Regex(pattern, RegexOptions.CultureInvariant), script);
    }

    [Fact]
    public void ScriptPinsToolVersionWorkingDirectoryLocalDrivesAndAllInputAncestors()
    {
        var script = ReadScript();

        Assert.Contains("$requiredVpkVersion = '1.2.0'", script, StringComparison.Ordinal);
        Assert.Contains("dotnet-tools.json", script, StringComparison.Ordinal);
        Assert.Contains("$vpkRootHelpArguments = @('tool', 'run', 'vpk', '--', '--help')", script, StringComparison.Ordinal);
        Assert.Contains("Velopack CLI", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'--version'", script, StringComparison.Ordinal);
        Assert.Contains("WorkingDirectory = $repoRoot", script, StringComparison.Ordinal);
        Assert.Contains("[IO.DriveType]::Network", script, StringComparison.Ordinal);
        foreach (var marker in new[]
                 {
                     "Assert-NoReparsePointAncestors $acceptanceFeed 'AcceptanceFeedPath'",
                     "Assert-NoReparsePointAncestors $outputPath 'OutputDirectory'",
                     "Assert-NoReparsePointAncestors $previousPath 'PreviousReleaseDirectory'",
                     "Assert-NoReparsePointAncestors $releaseNotes 'ReleaseNotesPath'",
                 })
            Assert.Contains(marker, script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FakeToolchainCompletesReleaseFromOutsideRepositoryWithExactPipeline()
    {
        using var fixture = ScriptFixture.Create();
        await fixture.InstallFakeToolchainAsync();

        var result = await fixture.RunFakePipelineAsync(tamperHash: false);

        Assert.True(
            result.ExitCode == 0,
            result.Output + Environment.NewLine + "Fake commands:" + Environment.NewLine +
            (File.Exists(fixture.FakeCommandLog)
                ? await File.ReadAllTextAsync(fixture.FakeCommandLog)
                : "<none>"));
        var commands = await File.ReadAllLinesAsync(fixture.FakeCommandLog);
        Assert.Equal(
            new[]
            {
                "dotnet|tool restore",
                "dotnet|tool run vpk -- --help",
                "dotnet|tool run vpk -- pack --help",
                "dotnet|restore",
                "dotnet|publish",
                "dotnet|run --project",
                "powershell|-NoProfile -ExecutionPolicy Bypass -File",
                "dotnet|tool run vpk pack",
            },
            commands.Select(ParseCommandPrefix));
        var repositoryRoot = FindRepositoryRoot();
        Assert.All(commands, command =>
            Assert.Equal(repositoryRoot, command.Split('\t')[1], ignoreCase: true));
        var combinedCommands = string.Join(Environment.NewLine, commands);
        Assert.Contains("--locked-mode", combinedCommands, StringComparison.Ordinal);
        Assert.Contains("--self-contained" + UnitSeparator + "true", combinedCommands, StringComparison.Ordinal);
        Assert.Contains("-p:OtaAcceptanceBuild=true", combinedCommands, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:PackageId=", combinedCommands, StringComparison.Ordinal);
        Assert.Contains("--packId" + UnitSeparator + "SuperPan.BaiduBatchTransfer.OtaAcceptance", combinedCommands, StringComparison.Ordinal);
        Assert.Contains("--channel" + UnitSeparator + "win-x64-ota-acceptance", combinedCommands, StringComparison.Ordinal);

        var manifestPath = Path.Combine(fixture.OutputDirectory, "upload-manifest.sha256");
        Assert.True(File.Exists(manifestPath));
        var manifestLines = await File.ReadAllLinesAsync(manifestPath);
        Assert.Equal(6, manifestLines.Length);
        Assert.Contains(
            manifestLines,
            line => line.EndsWith("  assets.win-x64-ota-acceptance.json", StringComparison.Ordinal));
        Assert.Contains(
            manifestLines,
            line => line.EndsWith("  RELEASES-win-x64-ota-acceptance", StringComparison.Ordinal));
        foreach (var line in manifestLines)
        {
            var pieces = line.Split("  ", 2, StringSplitOptions.None);
            Assert.Equal(2, pieces.Length);
            var asset = Path.Combine(fixture.OutputDirectory, pieces[1]);
            Assert.True(File.Exists(asset), asset);
            Assert.Equal(LowerSha256(await File.ReadAllBytesAsync(asset)), pieces[0]);
        }
        Assert.True(File.Exists(Path.Combine(fixture.OutputDirectory, "LOCAL-OTA-ACCEPTANCE-README.zh-CN.txt")));
    }

    [Fact]
    public async Task FakeToolchainAcceptsCurrentDeltaCatalogAndFullOnlyMultiVersionReleasesIndex()
    {
        using var fixture = ScriptFixture.Create();
        await fixture.InstallFakeToolchainAsync();
        await fixture.PreparePreviousReleaseAsync("1.2.2");

        var result = await fixture.RunFakePipelineAsync(
            tamperHash: false,
            additionalEnvironment: new Dictionary<string, string?>
            {
                ["FAKE_OTA_MULTI_VERSION"] = "1",
            },
            includePreviousRelease: true);

        Assert.True(result.ExitCode == 0, result.Output);
        var manifestLines = await File.ReadAllLinesAsync(
            Path.Combine(fixture.OutputDirectory, "upload-manifest.sha256"));
        Assert.Equal(8, manifestLines.Length);
        Assert.Contains(manifestLines, line => line.EndsWith("-delta.nupkg", StringComparison.Ordinal));

        using var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(fixture.OutputDirectory, "assets.win-x64-ota-acceptance.json")));
        Assert.Equal(
            new[] { "Delta", "Full", "Installer", "Portable" },
            catalog.RootElement.EnumerateArray()
                .Select(asset => asset.GetProperty("Type").GetString())
                .Order(StringComparer.Ordinal));
        var releasesLines = await File.ReadAllLinesAsync(
            Path.Combine(fixture.OutputDirectory, "RELEASES-win-x64-ota-acceptance"));
        Assert.Equal(2, releasesLines.Length);
        Assert.All(releasesLines, line => Assert.DoesNotContain("-delta.nupkg", line, StringComparison.Ordinal));
        Assert.Contains(releasesLines, line => line.Contains("-1.2.2-full.nupkg", StringComparison.Ordinal));
        Assert.Contains(releasesLines, line => line.Contains("-1.2.3-full.nupkg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FakeToolchainRejectsHistoricalFullFromCurrentAssetCatalog()
    {
        using var fixture = ScriptFixture.Create();
        await fixture.InstallFakeToolchainAsync();
        await fixture.PreparePreviousReleaseAsync("1.2.2");

        var result = await fixture.RunFakePipelineAsync(
            tamperHash: false,
            additionalEnvironment: new Dictionary<string, string?>
            {
                ["FAKE_OTA_MULTI_VERSION"] = "1",
                ["FAKE_OTA_ASSETS_MODE"] = "historical-full",
            },
            includePreviousRelease: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("asset catalog", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.OutputDirectory, "upload-manifest.sha256")));
    }

    [Fact]
    public async Task FakeToolchainRejectsDeltaFromFullOnlyReleasesIndex()
    {
        using var fixture = ScriptFixture.Create();
        await fixture.InstallFakeToolchainAsync();
        await fixture.PreparePreviousReleaseAsync("1.2.2");

        var result = await fixture.RunFakePipelineAsync(
            tamperHash: false,
            additionalEnvironment: new Dictionary<string, string?>
            {
                ["FAKE_OTA_MULTI_VERSION"] = "1",
                ["FAKE_OTA_RELEASES_MODE"] = "includes-delta",
            },
            includePreviousRelease: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("RELEASES", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.OutputDirectory, "upload-manifest.sha256")));
    }

    [Fact]
    public async Task FakeToolchainUsesBomNormalizedCopyAndPreservesNonAsciiReleaseNotes()
    {
        using var fixture = ScriptFixture.Create();
        await fixture.InstallFakeToolchainAsync();
        var sourceNotesPath = Path.Combine(fixture.Root, "user-release-notes.md");
        const string notes = "# 自定义更新说明\n\n本机 OTA 验收构建。\n";
        var originalBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(notes);
        await File.WriteAllBytesAsync(sourceNotesPath, originalBytes);

        var result = await fixture.RunFakePipelineAsync(
            tamperHash: false,
            releaseNotesPath: sourceNotesPath);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourceNotesPath));
        var effectiveNotesPath = Path.Combine(
            fixture.OutputDirectory,
            ".build",
            "release-notes.vpk.md");
        var effectiveBytes = await File.ReadAllBytesAsync(effectiveNotesPath);
        Assert.True(effectiveBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        using var releaseFeed = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(fixture.OutputDirectory, "releases.win-x64-ota-acceptance.json")));
        Assert.All(
            releaseFeed.RootElement.GetProperty("Assets").EnumerateArray().ToArray(),
            asset => Assert.Contains("本机 OTA 验收构建。", asset.GetProperty("NotesMarkdown").GetString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task FakeToolchainRejectsAssetCatalogThatReferencesForeignOutput()
    {
        using var fixture = ScriptFixture.Create();
        await fixture.InstallFakeToolchainAsync();

        var result = await fixture.RunFakePipelineAsync(
            tamperHash: false,
            additionalEnvironment: new Dictionary<string, string?>
            {
                ["FAKE_OTA_ASSETS_MODE"] = "foreign-reference",
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("asset catalog", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.OutputDirectory, "upload-manifest.sha256")));
    }

    [Fact]
    public async Task FakeToolchainRejectsReleasesIndexWithTamperedSha1()
    {
        using var fixture = ScriptFixture.Create();
        await fixture.InstallFakeToolchainAsync();

        var result = await fixture.RunFakePipelineAsync(
            tamperHash: false,
            additionalEnvironment: new Dictionary<string, string?>
            {
                ["FAKE_OTA_RELEASES_MODE"] = "bad-hash",
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("RELEASES SHA-1", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.OutputDirectory, "upload-manifest.sha256")));
    }

    [Theory]
    [InlineData("Velopack CLI 1.2.1, for distributing applications.")]
    [InlineData("Velopack CLI 1.2.0.1, for distributing applications.")]
    [InlineData("Velopack CLI 1.2.0, for distributing applications.\nVelopack CLI 1.2.0, duplicate.")]
    [InlineData("Velopack CLI 1.2.0, for distributing applications.\nVelopack CLI 1.2.1, conflicting.")]
    public async Task FakeToolchainRejectsInvalidOrMultipleVpkRootHelpVersionsBeforePackHelp(
        string rootHelp)
    {
        using var fixture = ScriptFixture.Create();
        await fixture.InstallFakeToolchainAsync();

        var result = await fixture.RunFakePipelineAsync(
            tamperHash: false,
            additionalEnvironment: new Dictionary<string, string?>
            {
                ["FAKE_OTA_VPK_ROOT_HELP"] = rootHelp,
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("exactly Velopack CLI 1.2.0", result.Output, StringComparison.Ordinal);
        var commands = await File.ReadAllLinesAsync(fixture.FakeCommandLog);
        Assert.Equal(
            new[]
            {
                "dotnet|tool restore",
                "dotnet|tool run vpk -- --help",
            },
            commands.Select(ParseCommandPrefix));
        Assert.False(File.Exists(Path.Combine(fixture.OutputDirectory, "upload-manifest.sha256")));
    }

    [Fact]
    public async Task FakeToolchainTamperedFeedHashFailsBeforeManifest()
    {
        using var fixture = ScriptFixture.Create();
        await fixture.InstallFakeToolchainAsync();

        var result = await fixture.RunFakePipelineAsync(tamperHash: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SHA-256", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.OutputDirectory, "upload-manifest.sha256")));
    }

    [Fact]
    public async Task MissingExecutableIsRecordedAsExceptionBeforeRethrow()
    {
        using var fixture = ScriptFixture.Create();

        var result = await fixture.RunFakePipelineAsync(
            tamperHash: false,
            installPathOnly: true);

        Assert.NotEqual(0, result.ExitCode);
        var commandLog = Path.Combine(fixture.OutputDirectory, ".build", "logs", "commands.jsonl");
        Assert.True(File.Exists(commandLog), result.Output);
        var record = JsonDocument.Parse((await File.ReadAllLinesAsync(commandLog)).Single()).RootElement;
        Assert.Equal("Exception", record.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("exitCode").ValueKind);
    }

    [Fact]
    public async Task FailingExecutableRecordsExitCodeAndFailureStatus()
    {
        using var fixture = ScriptFixture.Create();
        await fixture.InstallFakeToolchainAsync();

        var result = await fixture.RunFakePipelineAsync(
            tamperHash: false,
            additionalEnvironment: new Dictionary<string, string?>
            {
                ["FAKE_OTA_FAIL_PREFIX"] = "tool restore",
            });

        Assert.NotEqual(0, result.ExitCode);
        var commandLog = Path.Combine(fixture.OutputDirectory, ".build", "logs", "commands.jsonl");
        var record = JsonDocument.Parse((await File.ReadAllLinesAsync(commandLog)).Single()).RootElement;
        Assert.True(
            record.GetProperty("status").GetString() == "Failed",
            record.GetRawText() + Environment.NewLine + result.Output);
        Assert.Equal(23, record.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task RejectsFeedBelowJunctionAncestorWhenJunctionsAreAvailable()
    {
        using var fixture = ScriptFixture.Create();
        var target = Path.Combine(fixture.Root, "junction-target");
        var link = Path.Combine(fixture.Root, "junction-link");
        Directory.CreateDirectory(Path.Combine(target, "feed"));
        if (!TryCreateJunction(link, target)) return;
        try
        {
            var outside = Path.Combine(Path.GetTempPath(), $"bbt-ota-output-{Guid.NewGuid():N}");
            var result = await fixture.RunAsync(
                "-Version", "1.2.3",
                "-AcceptanceFeedPath", Path.Combine(link, "feed"),
                "-OutputDirectory", outside,
                "-OtaAcceptanceBuild",
                "-AllowUnsignedLocal");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("reparse-point ancestor", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    private const char UnitSeparator = (char)31;

    private static string ParseCommandPrefix(string line)
    {
        var fields = line.Split('\t');
        var arguments = fields[2].Split(UnitSeparator);
        var prefixLength = fields[0] == "powershell"
            ? Math.Min(4, arguments.Length)
            : arguments.Length >= 2 && arguments[0] == "tool" && arguments[1] == "restore"
                ? 2
            : arguments.Length >= 2 && arguments[0] == "run" && arguments[1] == "--project"
                ? 2
            : arguments.Length >= 4 && arguments[0] == "tool" && arguments[1] == "run" && arguments[2] == "vpk"
                ? Math.Min(arguments[3] == "--" && arguments.Length > 4 && arguments[4] == "pack" ? 6 : arguments[3] == "--" ? 5 : 4, arguments.Length)
                : 1;
        return fields[0] + "|" + string.Join(' ', arguments.Take(prefixLength));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
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

    private static async Task WriteFeedAsync(
        string directory,
        string feedName,
        string packageId,
        string version,
        string fileName,
        string sha256,
        long size)
    {
        var feed = new
        {
            Assets = new[]
            {
                new { PackageId = packageId, Version = version, Type = "Full", FileName = fileName, SHA256 = sha256, Size = size },
            },
        };
        await File.WriteAllTextAsync(Path.Combine(directory, feedName), JsonSerializer.Serialize(feed));
    }

    private static string LowerSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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
            FakeBinDirectory = Path.Combine(root, "fake-bin");
            OutsideWorkingDirectory = Path.Combine(root, "outside-working-directory");
            FakeCommandLog = Path.Combine(root, "fake-commands.log");
        }

        public string Root { get; }
        public string FeedDirectory { get; }
        public string OutputDirectory { get; }
        public string PreviousReleaseDirectory { get; }
        public string FakeBinDirectory { get; }
        public string OutsideWorkingDirectory { get; }
        public string FakeCommandLog { get; }

        public static ScriptFixture Create()
        {
            var root = Path.Combine(
                FindRepositoryRoot(),
                ".artifacts",
                $"ota-acceptance-script-tests-{Guid.NewGuid():N}");
            var fixture = new ScriptFixture(root);
            Directory.CreateDirectory(fixture.FeedDirectory);
            Directory.CreateDirectory(fixture.FakeBinDirectory);
            Directory.CreateDirectory(fixture.OutsideWorkingDirectory);
            return fixture;
        }

        public Task<ScriptResult> RunAsync(params string[] arguments) =>
            RunPowerShellAsync(ScriptPath(), arguments, null);

        public Task<ScriptResult> RunWithEnvironmentAsync(
            IReadOnlyDictionary<string, string?> environment,
            params string[] arguments) =>
            RunPowerShellAsync(ScriptPath(), arguments, environment);

        public Task<ScriptResult> RunWithPreviousAsync(string version) => RunAsync(
            "-Version", version,
            "-AcceptanceFeedPath", FeedDirectory,
            "-OutputDirectory", OutputDirectory,
            "-PreviousReleaseDirectory", PreviousReleaseDirectory,
            "-OtaAcceptanceBuild",
            "-AllowUnsignedLocal");

        public async Task InstallFakeToolchainAsync()
        {
            var systemPowerShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var implementation = Path.Combine(FakeBinDirectory, "fake-tool.ps1");
            await File.WriteAllTextAsync(implementation, FakeToolImplementation);
            foreach (var command in new[] { "dotnet", "powershell" })
            {
                var shim = $"@echo off\r\nif defined FAKE_OTA_FAIL_PREFIX exit /b 23\r\n\"{systemPowerShell}\" -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-tool.ps1\" {command} %*\r\nexit /b %ERRORLEVEL%\r\n";
                await File.WriteAllTextAsync(Path.Combine(FakeBinDirectory, command + ".cmd"), shim);
            }
        }

        public async Task PreparePreviousReleaseAsync(string version)
        {
            Directory.CreateDirectory(PreviousReleaseDirectory);
            var packageName = $"SuperPan.BaiduBatchTransfer.OtaAcceptance-{version}-full.nupkg";
            var packageBytes = new byte[] { 91, 92, 93, 94 };
            await File.WriteAllBytesAsync(
                Path.Combine(PreviousReleaseDirectory, packageName),
                packageBytes);
            await WriteFeedAsync(
                PreviousReleaseDirectory,
                "releases.win-x64-ota-acceptance.json",
                "SuperPan.BaiduBatchTransfer.OtaAcceptance",
                version,
                packageName,
                LowerSha256(packageBytes),
                packageBytes.Length);
        }

        public Task<ScriptResult> RunFakePipelineAsync(
            bool tamperHash,
            bool installPathOnly = false,
            IReadOnlyDictionary<string, string?>? additionalEnvironment = null,
            bool includePreviousRelease = false,
            string? releaseNotesPath = null)
        {
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = FakeBinDirectory,
                ["FAKE_OTA_COMMAND_LOG"] = FakeCommandLog,
                ["FAKE_OTA_TAMPER_HASH"] = tamperHash ? "1" : "0",
            };
            if (installPathOnly)
            {
                foreach (var file in Directory.EnumerateFiles(FakeBinDirectory)) File.Delete(file);
            }
            if (additionalEnvironment is not null)
            {
                foreach (var pair in additionalEnvironment) environment[pair.Key] = pair.Value;
            }
            var arguments = new List<string>
            {
                "-Version", "1.2.3",
                "-AcceptanceFeedPath", FeedDirectory,
                "-OutputDirectory", OutputDirectory,
                "-OtaAcceptanceBuild",
                "-AllowUnsignedLocal",
            };
            if (includePreviousRelease)
            {
                arguments.Add("-PreviousReleaseDirectory");
                arguments.Add(PreviousReleaseDirectory);
            }
            if (releaseNotesPath is not null)
            {
                arguments.Add("-ReleaseNotesPath");
                arguments.Add(releaseNotesPath);
            }
            return RunPowerShellAsync(
                ScriptPath(),
                arguments.ToArray(),
                environment,
                OutsideWorkingDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<ScriptResult> RunPowerShellAsync(
        string script,
        string[] arguments,
        IReadOnlyDictionary<string, string?>? environment,
        string? workingDirectory = null)
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
        if (workingDirectory is not null) startInfo.WorkingDirectory = workingDirectory;
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var inheritedName in startInfo.Environment.Keys
                     .Where(name => name.StartsWith("VPK_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            startInfo.Environment.Remove(inheritedName);
        if (environment is not null)
        {
            foreach (var pair in environment)
                startInfo.Environment[pair.Key] = pair.Value;
        }

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

    private const string FakeToolImplementation = """
$ErrorActionPreference = 'Stop'
$Tool = [string]$args[0]
$ToolArgs = @($args | Select-Object -Skip 1)
$normalizedArguments = New-Object Collections.Generic.List[string]
for ($argumentIndex = 0; $argumentIndex -lt $ToolArgs.Count; $argumentIndex++) {
    if ($ToolArgs[$argumentIndex] -ceq '-p' -and $argumentIndex + 1 -lt $ToolArgs.Count) {
        $normalizedArguments.Add('-p:' + $ToolArgs[$argumentIndex + 1])
        $argumentIndex++
    }
    else { $normalizedArguments.Add([string]$ToolArgs[$argumentIndex]) }
}
$ToolArgs = @($normalizedArguments)

function Write-FakeLog {
    $line = $Tool + "`t" + (Get-Location).Path + "`t" + ($ToolArgs -join [char]31) + "`n"
    [IO.File]::AppendAllText($env:FAKE_OTA_COMMAND_LOG, $line)
}

function Get-ArgumentValue([string]$Name) {
    for ($index = 0; $index -lt $ToolArgs.Count - 1; $index++) {
        if ($ToolArgs[$index] -ceq $Name) { return $ToolArgs[$index + 1] }
    }
    throw "Missing fake argument: $Name"
}

function Get-LowerSha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($stream) }
    finally { $algorithm.Dispose(); $stream.Dispose() }
    return ([BitConverter]::ToString($hash).Replace('-', '')).ToLowerInvariant()
}

function Get-UpperSha1([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA1]::Create()
    try { $hash = $algorithm.ComputeHash($stream) }
    finally { $algorithm.Dispose(); $stream.Dispose() }
    return [BitConverter]::ToString($hash).Replace('-', '')
}

Write-FakeLog
$joined = $ToolArgs -join ' '
if (-not [string]::IsNullOrWhiteSpace($env:FAKE_OTA_FAIL_PREFIX) -and
    $joined.StartsWith($env:FAKE_OTA_FAIL_PREFIX, [StringComparison]::Ordinal)) {
    exit 23
}
if ($Tool -ceq 'powershell') {
    Write-Output 'fake helper gate passed'
    exit 0
}
if ($joined -ceq 'tool restore') { Write-Output 'fake tool restore'; exit 0 }
if ($joined -ceq 'tool run vpk -- --help') {
    $rootHelp = if ([string]::IsNullOrWhiteSpace($env:FAKE_OTA_VPK_ROOT_HELP)) {
        'Velopack CLI 1.2.0, for distributing applications.'
    }
    else { $env:FAKE_OTA_VPK_ROOT_HELP }
    Write-Output $rootHelp
    exit 0
}
if ($joined -ceq 'tool run vpk -- pack --help') {
    Write-Output '--packId --packVersion --packDir --mainExe --channel --runtime --outputDir --releaseNotes'
    exit 0
}
if ($ToolArgs[0] -ceq 'restore') { Write-Output 'fake locked restore'; exit 0 }
if ($ToolArgs[0] -ceq 'publish') {
    $publishDirectory = Get-ArgumentValue '-o'
    [IO.Directory]::CreateDirectory($publishDirectory) | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $publishDirectory 'BaiduBatchTransfer.App.dll'), [byte[]](1, 2, 3))
    [IO.Directory]::CreateDirectory((Join-Path $publishDirectory 'managed-browser-helper')) | Out-Null
    Write-Output 'fake publish'
    exit 0
}
if ($ToolArgs[0] -ceq 'run' -and $ToolArgs[1] -ceq '--project') {
    Write-Output 'fake metadata verified'
    exit 0
}
if ($joined.StartsWith('tool run vpk pack ', [StringComparison]::Ordinal)) {
    $outputDirectory = Get-ArgumentValue '--outputDir'
    $packageId = Get-ArgumentValue '--packId'
    $version = Get-ArgumentValue '--packVersion'
    $channel = Get-ArgumentValue '--channel'
    $notesText = [IO.File]::ReadAllText((Get-ArgumentValue '--releaseNotes')).TrimEnd()
    $packageName = "$packageId-$version-full.nupkg"
    $packagePath = Join-Path $outputDirectory $packageName
    $deltaName = "$packageId-$version-delta.nupkg"
    $deltaPath = Join-Path $outputDirectory $deltaName
    $setupName = "$packageId-Setup.exe"
    $portableName = "$packageId-Portable.zip"
    $previousAssets = @()
    if ($env:FAKE_OTA_MULTI_VERSION -ceq '1') {
        $previousFeedPath = Join-Path $outputDirectory "releases.$channel.json"
        $previousFeed = [IO.File]::ReadAllText($previousFeedPath) | ConvertFrom-Json
        $previousAssets = @($previousFeed.Assets)
    }
    [IO.File]::WriteAllBytes($packagePath, [byte[]](10, 20, 30, 40))
    if ($env:FAKE_OTA_MULTI_VERSION -ceq '1') {
        [IO.File]::WriteAllBytes($deltaPath, [byte[]](11, 21, 31))
    }
    [IO.File]::WriteAllBytes((Join-Path $outputDirectory $setupName), [byte[]](50, 60))
    [IO.File]::WriteAllBytes((Join-Path $outputDirectory $portableName), [byte[]](70, 80))
    $hash = if ($env:FAKE_OTA_TAMPER_HASH -ceq '1') { 'a' * 64 } else { Get-LowerSha256 $packagePath }
    $sha1 = Get-UpperSha1 $packagePath
    $currentFull = [ordered]@{
        PackageId = $packageId
        Version = $version
        Type = 'Full'
        FileName = $packageName
        SHA1 = $sha1
        SHA256 = $hash
        Size = ([IO.FileInfo]::new($packagePath)).Length
        NotesMarkdown = $notesText
    }
    $feedAssets = @($currentFull)
    if ($env:FAKE_OTA_MULTI_VERSION -ceq '1') {
        $feedAssets += [ordered]@{
            PackageId = $packageId
            Version = $version
            Type = 'Delta'
            FileName = $deltaName
            SHA1 = (Get-UpperSha1 $deltaPath)
            SHA256 = (Get-LowerSha256 $deltaPath)
            Size = ([IO.FileInfo]::new($deltaPath)).Length
            NotesMarkdown = $notesText
        }
        $feedAssets += $previousAssets
    }
    $feed = [ordered]@{
        Assets = $feedAssets
    }
    [IO.File]::WriteAllText(
        (Join-Path $outputDirectory "releases.$channel.json"),
        ($feed | ConvertTo-Json -Depth 4))
    $catalogPortableName = if ($env:FAKE_OTA_ASSETS_MODE -ceq 'foreign-reference') {
        'foreign.zip'
    }
    else { $portableName }
    $catalog = @()
    if ($env:FAKE_OTA_MULTI_VERSION -ceq '1') {
        $catalog += [ordered]@{ RelativeFileName = $deltaName; Type = 'Delta' }
    }
    $catalog += [ordered]@{ RelativeFileName = $setupName; Type = 'Installer' }
    $catalog += [ordered]@{ RelativeFileName = $packageName; Type = 'Full' }
    $catalog += [ordered]@{ RelativeFileName = $catalogPortableName; Type = 'Portable' }
    if ($env:FAKE_OTA_ASSETS_MODE -ceq 'historical-full') {
        $catalog += [ordered]@{
            RelativeFileName = [string]$previousAssets[0].FileName
            Type = 'Full'
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $outputDirectory "assets.$channel.json"),
        ($catalog | ConvertTo-Json -Depth 3 -Compress))
    $releaseLines = @()
    foreach ($fullAsset in @($feedAssets | Where-Object { [string]$_.Type -ceq 'Full' })) {
        $fullPath = Join-Path $outputDirectory ([string]$fullAsset.FileName)
        $fullSha1 = if ($env:FAKE_OTA_RELEASES_MODE -ceq 'bad-hash' -and
            [string]$fullAsset.Version -ceq $version) { '0' * 40 } else { Get-UpperSha1 $fullPath }
        $releaseLines += "$fullSha1 $([string]$fullAsset.FileName) $([IO.FileInfo]::new($fullPath).Length)"
    }
    if ($env:FAKE_OTA_RELEASES_MODE -ceq 'includes-delta') {
        $releaseLines += "$(Get-UpperSha1 $deltaPath) $deltaName $([IO.FileInfo]::new($deltaPath).Length)"
    }
    [IO.File]::WriteAllText(
        (Join-Path $outputDirectory "RELEASES-$channel"),
        (($releaseLines -join "`n") + "`n"))
    Write-Output 'fake vpk pack'
    exit 0
}
[Console]::Error.WriteLine("Unexpected fake command: $Tool $joined")
exit 44
""";
}
