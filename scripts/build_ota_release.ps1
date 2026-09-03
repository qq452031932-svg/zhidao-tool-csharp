[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$AcceptanceFeedPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$PreviousReleaseDirectory,
    [string]$ReleaseNotesPath,
    [switch]$OtaAcceptanceBuild,
    [switch]$AllowUnsignedLocal
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$acceptancePackageId = 'SuperPan.BaiduBatchTransfer.OtaAcceptance'
$acceptanceChannel = 'win-x64-ota-acceptance'
$productionPackageId = 'SuperPan.BaiduBatchTransfer'
$productionChannel = 'win-x64-stable'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.artifacts'))

# This is the first release gate. There is deliberately no production or signing bypass.
if (-not $OtaAcceptanceBuild -or -not $AllowUnsignedLocal) {
    throw 'Both -OtaAcceptanceBuild and -AllowUnsignedLocal are required for the unsigned local OTA acceptance pipeline.'
}
if ($acceptancePackageId -ceq $productionPackageId -or
    $acceptanceChannel -ceq $productionChannel) {
    throw 'The OTA acceptance package ID and channel must remain distinct from production.'
}

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version -cnotmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') {
    throw 'Version must be a canonical major.minor.patch value.'
}
$versionParts = @($Version.Split('.') | ForEach-Object { [int]::Parse($_) })
if (@($versionParts | Where-Object { $_ -gt 65534 }).Count -ne 0) {
    throw 'Version parts must be between 0 and 65534.'
}

function Resolve-AbsolutePath([string]$Value, [string]$ParameterName) {
    $isDriveAbsolute = $Value -match '^[A-Za-z]:[\\/]'
    $isUncAbsolute = $Value -match '^\\\\[^\\/]+[\\/][^\\/]+'
    if ([string]::IsNullOrWhiteSpace($Value) -or
        (-not $isDriveAbsolute -and -not $isUncAbsolute)) {
        throw "$ParameterName must be an absolute local path."
    }
    try {
        return [IO.Path]::GetFullPath($Value)
    }
    catch {
        throw "$ParameterName is not a valid local path."
    }
}

function Test-PathWithin([string]$Candidate, [string]$Parent) {
    $parentPrefix = $Parent.TrimEnd([IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $Candidate.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-ExistingPathIsNotReparsePoint([string]$Path, [string]$Description) {
    if (Test-Path -LiteralPath $Path) {
        $attributes = [IO.File]::GetAttributes($Path)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description must not be a reparse point."
        }
    }
}

function Assert-NonReparseExistingAncestors([string]$Path, [string]$Boundary) {
    if (-not $Path.Equals($Boundary, [StringComparison]::OrdinalIgnoreCase) -and
        -not (Test-PathWithin $Path $Boundary)) {
        throw 'OutputDirectory is outside its verified artifact boundary.'
    }
    $current = $Path
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "OutputDirectory has a reparse-point ancestor: $current"
            }
        }
        if ($current.Equals($Boundary, [StringComparison]::OrdinalIgnoreCase)) { return }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent)) {
            throw 'OutputDirectory did not reach its verified artifact boundary.'
        }
        $current = $parent
    }
}

$acceptanceFeed = Resolve-AbsolutePath $AcceptanceFeedPath 'AcceptanceFeedPath'
$outputPath = Resolve-AbsolutePath $OutputDirectory 'OutputDirectory'
if (-not (Test-PathWithin $outputPath $artifactRoot)) {
    throw 'OutputDirectory must be inside the repository .artifacts directory.'
}
Assert-NonReparseExistingAncestors $outputPath $artifactRoot
$relativeOutput = $outputPath.Substring(
    $artifactRoot.TrimEnd('\', '/').Length + 1).Replace('/', '\')
$acceptanceRootName = $relativeOutput.Split('\')[0]
if (-not $acceptanceRootName.StartsWith('ota-acceptance-', [StringComparison]::OrdinalIgnoreCase) -or
    $relativeOutput.IndexOf('\') -lt 1) {
    throw 'OutputDirectory must be a child of a dedicated .artifacts/ota-acceptance-* directory.'
}
if ($acceptanceFeed.Equals($outputPath, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-PathWithin $acceptanceFeed $outputPath) -or
    (Test-PathWithin $outputPath $acceptanceFeed)) {
    throw 'AcceptanceFeedPath and OutputDirectory must be separate directories.'
}
Assert-ExistingPathIsNotReparsePoint $acceptanceFeed 'AcceptanceFeedPath'
Assert-ExistingPathIsNotReparsePoint $outputPath 'OutputDirectory'
if (Test-Path -LiteralPath $outputPath) {
    if (-not (Test-Path -LiteralPath $outputPath -PathType Container)) {
        throw 'OutputDirectory must be a directory.'
    }
    if (@([IO.Directory]::EnumerateFileSystemEntries($outputPath)).Count -ne 0) {
        throw 'OutputDirectory must be empty; existing files are never overwritten.'
    }
}

$previousPath = $null
if (-not [string]::IsNullOrWhiteSpace($PreviousReleaseDirectory)) {
    $previousPath = Resolve-AbsolutePath $PreviousReleaseDirectory 'PreviousReleaseDirectory'
    if (-not (Test-Path -LiteralPath $previousPath -PathType Container)) {
        throw 'PreviousReleaseDirectory must be an existing directory.'
    }
    Assert-ExistingPathIsNotReparsePoint $previousPath 'PreviousReleaseDirectory'
    if ($previousPath.Equals($outputPath, [StringComparison]::OrdinalIgnoreCase) -or
        (Test-PathWithin $previousPath $outputPath) -or
        (Test-PathWithin $outputPath $previousPath)) {
        throw 'PreviousReleaseDirectory and OutputDirectory must be separate directories.'
    }

    $escapedVersion = [Text.RegularExpressions.Regex]::Escape($Version)
    foreach ($feedFile in [IO.Directory]::EnumerateFiles($previousPath, 'releases.*.json',
            [IO.SearchOption]::TopDirectoryOnly)) {
        Assert-ExistingPathIsNotReparsePoint $feedFile 'Previous release feed'
        $feedText = [IO.File]::ReadAllText($feedFile)
        if ($feedText -match ('(?i)"version"\s*:\s*"' + $escapedVersion + '"')) {
            throw "Version $Version already exists in the previous release feed."
        }
    }
    foreach ($packageFile in [IO.Directory]::EnumerateFiles($previousPath, '*.nupkg',
            [IO.SearchOption]::TopDirectoryOnly)) {
        Assert-ExistingPathIsNotReparsePoint $packageFile 'Previous release package'
        if ([IO.Path]::GetFileName($packageFile) -match
            ('(?i)(?:^|[.-])' + $escapedVersion + '(?:[.-]|$)')) {
            throw "Version $Version already exists in the previous release assets."
        }
    }
}

$releaseNotes = $null
if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
    $releaseNotes = Resolve-AbsolutePath $ReleaseNotesPath 'ReleaseNotesPath'
    if (-not (Test-Path -LiteralPath $releaseNotes -PathType Leaf)) {
        throw 'ReleaseNotesPath must name an existing file.'
    }
    Assert-ExistingPathIsNotReparsePoint $releaseNotes 'ReleaseNotesPath'
}

$appProject = Join-Path $repoRoot 'src\BaiduBatchTransfer.App\BaiduBatchTransfer.App.csproj'
$helperVerificationScript = Join-Path $repoRoot 'scripts\verify_managed_browser_helper_package.ps1'
$helperManifestSource = Join-Path $repoRoot 'tools\managed_browser_helper\helper-manifest.json'
$helperSourceDirectory = Join-Path $repoRoot 'tools\managed_browser_helper'
foreach ($requiredInput in @($appProject, $helperVerificationScript, $helperManifestSource)) {
    if (-not (Test-Path -LiteralPath $requiredInput -PathType Leaf)) {
        throw "Required release input is missing: $requiredInput"
    }
}

[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$workingDirectory = Join-Path $outputPath '.build'
$publishDirectory = Join-Path $workingDirectory 'publish'
$logDirectory = Join-Path $workingDirectory 'logs'
[IO.Directory]::CreateDirectory($publishDirectory) | Out-Null
[IO.Directory]::CreateDirectory($logDirectory) | Out-Null
$commandLogPath = Join-Path $logDirectory 'commands.jsonl'
$utf8NoBom = New-Object Text.UTF8Encoding($false)

function Invoke-RecordedCommand(
    [string]$Name,
    [string]$FilePath,
    [string[]]$Arguments) {
    $safeName = $Name -replace '[^A-Za-z0-9_.-]', '_'
    $stdoutPath = Join-Path $logDirectory "$safeName.stdout.log"
    $stderrPath = Join-Path $logDirectory "$safeName.stderr.log"
    $startedUtc = [DateTime]::UtcNow
    & $FilePath @Arguments 1> $stdoutPath 2> $stderrPath
    $exitCode = $LASTEXITCODE
    $stdout = if (Test-Path -LiteralPath $stdoutPath) {
        [IO.File]::ReadAllText($stdoutPath)
    } else { '' }
    $stderr = if (Test-Path -LiteralPath $stderrPath) {
        [IO.File]::ReadAllText($stderrPath)
    } else { '' }
    $record = [ordered]@{
        name = $Name
        executable = $FilePath
        arguments = @($Arguments)
        startedUtc = $startedUtc.ToString('O')
        exitCode = $exitCode
        stdout = $stdout
        stderr = $stderr
    }
    [IO.File]::AppendAllText(
        $commandLogPath,
        (($record | ConvertTo-Json -Compress -Depth 4) + "`n"),
        $utf8NoBom)
    if (-not [string]::IsNullOrEmpty($stdout)) { Write-Output $stdout.TrimEnd() }
    if (-not [string]::IsNullOrEmpty($stderr)) { Write-Warning $stderr.TrimEnd() }
    if ($exitCode -ne 0) {
        throw "Release command '$Name' failed with exit code $exitCode. See $commandLogPath"
    }
    return [pscustomobject]@{ Stdout = $stdout; Stderr = $stderr; ExitCode = $exitCode }
}

if ($null -eq $releaseNotes) {
    $releaseNotes = Join-Path $workingDirectory 'release-notes.md'
    [IO.File]::WriteAllText(
        $releaseNotes,
        "# $Version`n`n本机 OTA 验收构建。`n",
        $utf8NoBom)
}
$effectiveReleaseNotesPath = $releaseNotes

$toolRestoreArguments = @('tool', 'restore')
[void](Invoke-RecordedCommand 'dotnet-tool-restore' 'dotnet' $toolRestoreArguments)

$vpkHelpArguments = @('tool', 'run', 'vpk', '--', 'pack', '--help')
$vpkHelp = Invoke-RecordedCommand 'vpk-pack-help' 'dotnet' $vpkHelpArguments
foreach ($requiredOption in @('--packId', '--packVersion', '--packDir', '--mainExe',
        '--channel', '--runtime', '--outputDir', '--releaseNotes')) {
    if ($vpkHelp.Stdout.IndexOf($requiredOption, [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
        $vpkHelp.Stderr.IndexOf($requiredOption, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Locked vpk 1.2.0 help does not advertise required option $requiredOption."
    }
}

$restoreArguments = @(
    'restore', $appProject,
    '--locked-mode',
    '-r', 'win-x64',
    '-p:RuntimeIdentifier=win-x64',
    '-p:SelfContained=true',
    "-p:VersionPrefix=$Version",
    '-p:UpdateFeedUrl=',
    '-p:OtaAcceptanceBuild=true',
    "-p:OtaAcceptanceFeedPath=$acceptanceFeed",
    "-p:PackageId=$acceptancePackageId",
    '-p:UpdateChannel=win-x64-ota-acceptance'
)
[void](Invoke-RecordedCommand 'dotnet-restore' 'dotnet' $restoreArguments)

$publishArguments = @(
    'publish', $appProject,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '-o', $publishDirectory,
    "-p:VersionPrefix=$Version",
    '-p:RuntimeIdentifier=win-x64',
    '-p:SelfContained=true',
    '-p:UpdateFeedUrl=',
    '-p:OtaAcceptanceBuild=true',
    "-p:OtaAcceptanceFeedPath=$acceptanceFeed",
    "-p:PackageId=$acceptancePackageId",
    '-p:UpdateChannel=win-x64-ota-acceptance'
)
[void](Invoke-RecordedCommand 'dotnet-publish' 'dotnet' $publishArguments)

$publishedAssembly = Join-Path $publishDirectory 'BaiduBatchTransfer.App.dll'
if (-not (Test-Path -LiteralPath $publishedAssembly -PathType Leaf)) {
    throw 'Published application assembly is missing.'
}
$metadataProjectDirectory = Join-Path $workingDirectory 'metadata-verifier'
[IO.Directory]::CreateDirectory($metadataProjectDirectory) | Out-Null
$metadataProject = Join-Path $metadataProjectDirectory 'MetadataVerifier.csproj'
$metadataProgram = Join-Path $metadataProjectDirectory 'Program.cs'
[IO.File]::WriteAllText($metadataProject, @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@, $utf8NoBom)
[IO.File]::WriteAllText($metadataProgram, @'
using System.Diagnostics;
using System.Reflection;

if (args.Length != 5) return 2;
var assemblyPath = Path.GetFullPath(args[0]);
var expectedVersion = args[1];
var expectedFeed = Path.GetFullPath(args[2]);
var expectedChannel = args[3];
var expectedAcceptance = args[4];
var assembly = Assembly.LoadFrom(assemblyPath);
var metadata = assembly.GetCustomAttributesData()
    .Where(a => a.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute")
    .ToDictionary(
        a => (string?)a.ConstructorArguments[0].Value ?? string.Empty,
        a => (string?)a.ConstructorArguments[1].Value ?? string.Empty,
        StringComparer.Ordinal);
var productVersion = FileVersionInfo.GetVersionInfo(assemblyPath).ProductVersion;
var valid = string.Equals(productVersion, expectedVersion, StringComparison.Ordinal)
    && metadata.TryGetValue("UpdateFeedUrl", out var productionFeed) && productionFeed.Length == 0
    && metadata.TryGetValue("OtaAcceptanceFeedPath", out var acceptanceFeed)
    && string.Equals(Path.GetFullPath(acceptanceFeed), expectedFeed, StringComparison.OrdinalIgnoreCase)
    && metadata.TryGetValue("UpdateChannel", out var channel)
    && string.Equals(channel, expectedChannel, StringComparison.Ordinal)
    && metadata.TryGetValue("OtaAcceptanceBuild", out var acceptance)
    && string.Equals(acceptance, expectedAcceptance, StringComparison.Ordinal);
if (!valid)
{
    Console.Error.WriteLine("Published assembly metadata did not match the requested OTA acceptance build.");
    return 1;
}
Console.WriteLine("Published assembly metadata verified.");
return 0;
'@, $utf8NoBom)
$metadataArguments = @(
    'run', '--project', $metadataProject,
    '-c', 'Release',
    '--', $publishedAssembly, $Version, $acceptanceFeed, $acceptanceChannel, 'true'
)
[void](Invoke-RecordedCommand 'verify-published-metadata' 'dotnet' $metadataArguments)

$publishedHelperDirectory = Join-Path $publishDirectory 'managed-browser-helper'
$helperGateArguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', $helperVerificationScript,
    '-ManifestSource', $helperManifestSource,
    '-PackageDirectory', $publishedHelperDirectory,
    '-HelperSourceDirectory', $helperSourceDirectory
)
[void](Invoke-RecordedCommand 'verify-managed-browser-helper' 'powershell.exe' $helperGateArguments)

if ($null -ne $previousPath) {
    foreach ($previousAsset in [IO.Directory]::EnumerateFiles($previousPath, '*',
            [IO.SearchOption]::TopDirectoryOnly)) {
        $name = [IO.Path]::GetFileName($previousAsset)
        if ($name -notmatch '^(?:releases\.[A-Za-z0-9.-]+\.json|[A-Za-z0-9._-]+\.nupkg)$') {
            continue
        }
        Assert-ExistingPathIsNotReparsePoint $previousAsset 'Previous release asset'
        $destination = Join-Path $outputPath $name
        if (Test-Path -LiteralPath $destination) {
            throw "Previous release asset would overwrite an output file: $name"
        }
        [IO.File]::Copy($previousAsset, $destination, $false)
    }
}

$vpkArguments = @(
    'tool', 'run', 'vpk', 'pack',
    '--packId', $acceptancePackageId,
    '--packVersion', $Version,
    '--packDir', $publishDirectory,
    '--mainExe', 'BaiduBatchTransfer.App.exe',
    '--channel', $acceptanceChannel,
    '--runtime', 'win-x64',
    '--outputDir', $outputPath,
    '--releaseNotes', $effectiveReleaseNotesPath
)
[void](Invoke-RecordedCommand 'vpk-pack' 'dotnet' $vpkArguments)

$releaseFeedPath = Join-Path $outputPath "releases.$acceptanceChannel.json"
if (-not (Test-Path -LiteralPath $releaseFeedPath -PathType Leaf)) {
    throw "Expected release feed is missing: $releaseFeedPath"
}
$releaseFeed = [IO.File]::ReadAllText($releaseFeedPath) | ConvertFrom-Json
$assets = @($releaseFeed.Assets)
if ($assets.Count -eq 0) { throw 'Release feed contains no assets.' }
$targetAssets = @($assets | Where-Object {
    [string]$_.PackageId -ceq $acceptancePackageId -and [string]$_.Version -ceq $Version
})
if ($targetAssets.Count -eq 0) {
    throw 'Release feed does not contain the requested acceptance package and version.'
}
$hasFullPackage = $false
foreach ($asset in $targetAssets) {
    $fileName = [string]$asset.FileName
    if ([string]::IsNullOrWhiteSpace($fileName) -or
        $fileName -cne [IO.Path]::GetFileName($fileName)) {
        throw 'Release feed contains an unsafe asset file name.'
    }
    $assetPath = Join-Path $outputPath $fileName
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Release feed asset is missing: $fileName"
    }
    Assert-ExistingPathIsNotReparsePoint $assetPath 'Release asset'
    $assetInfo = [IO.FileInfo]::new($assetPath)
    if ([long]$asset.Size -ne $assetInfo.Length) {
        throw "Release feed size differs from the asset: $fileName"
    }
    $actualHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]$asset.SHA256 -cne $actualHash) {
        throw "Release feed SHA-256 differs from the asset: $fileName"
    }
    if ([string]$asset.Type -match '(?i)full') { $hasFullPackage = $true }
}
if (-not $hasFullPackage) { throw 'Release feed does not contain a full package for this version.' }

$setupFiles = @([IO.Directory]::EnumerateFiles($outputPath, '*Setup*.exe',
    [IO.SearchOption]::TopDirectoryOnly))
$portableFiles = @([IO.Directory]::EnumerateFiles($outputPath, '*Portable*.zip',
    [IO.SearchOption]::TopDirectoryOnly))
$fullPackages = @([IO.Directory]::EnumerateFiles($outputPath, '*full*.nupkg',
    [IO.SearchOption]::TopDirectoryOnly))
if ($setupFiles.Count -eq 0 -or $portableFiles.Count -eq 0 -or $fullPackages.Count -eq 0) {
    throw 'Setup, Portable ZIP, or full nupkg acceptance output is missing.'
}

$uploadAssets = @([IO.Directory]::EnumerateFiles($outputPath, '*',
        [IO.SearchOption]::TopDirectoryOnly) | Where-Object {
        $_ -match '\.(?:json|nupkg|exe|zip)$'
    } | Sort-Object { [IO.Path]::GetFileName($_) })
if ($uploadAssets.Count -eq 0) { throw 'No upload assets were generated.' }
$manifestLines = foreach ($assetPath in $uploadAssets) {
    Assert-ExistingPathIsNotReparsePoint $assetPath 'Upload asset'
    $hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($assetPath))"
}
$manifestPath = Join-Path $outputPath 'upload-manifest.sha256'
[IO.File]::WriteAllText($manifestPath, (($manifestLines -join "`n") + "`n"), $utf8NoBom)

$readmePath = Join-Path $outputPath '本地验收包说明.txt'
$readme = @"
本机 OTA 验收包

版本：$Version
包 ID：$acceptancePackageId
渠道：$acceptanceChannel
更新源：$acceptanceFeed

此目录仅用于未签名的本机 OTA 验收，不是生产发布物。
脚本不会连接或上传到任何服务器。上传前应再次核对 upload-manifest.sha256。
命令执行记录：.build\logs\commands.jsonl
"@
[IO.File]::WriteAllText($readmePath, $readme.Replace("`r`n", "`n"), $utf8NoBom)

Write-Output "Local OTA acceptance release created: $outputPath"
