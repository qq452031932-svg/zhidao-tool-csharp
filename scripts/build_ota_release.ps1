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
$requiredVpkVersion = '1.2.0'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.artifacts'))

# This is the first release gate. There is deliberately no production or signing bypass.
if (-not $OtaAcceptanceBuild -or -not $AllowUnsignedLocal) {
    throw 'Both -OtaAcceptanceBuild and -AllowUnsignedLocal are required for the unsigned local OTA acceptance pipeline.'
}
$inheritedVpkVariables = @(Get-ChildItem Env: | Where-Object {
        $_.Name.StartsWith('VPK_', [StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::IsNullOrWhiteSpace([string]$_.Value)
    })
if ($inheritedVpkVariables.Count -ne 0) {
    $unsafeNames = @($inheritedVpkVariables | ForEach-Object { $_.Name } | Sort-Object) -join ', '
    throw "Inherited VPK_* environment variables are not allowed: $unsafeNames"
}
if ($acceptancePackageId -ceq $productionPackageId -or
    $acceptanceChannel -ceq $productionChannel) {
    throw 'The OTA acceptance package ID and channel must remain distinct from production.'
}

$rawPathInputs = [ordered]@{
    AcceptanceFeedPath = $AcceptanceFeedPath
    OutputDirectory = $OutputDirectory
    PreviousReleaseDirectory = $PreviousReleaseDirectory
    ReleaseNotesPath = $ReleaseNotesPath
}
foreach ($entry in $rawPathInputs.GetEnumerator()) {
    if (-not [string]::IsNullOrWhiteSpace([string]$entry.Value) -and
        [string]$entry.Value -match '^\\\\') {
        throw "$($entry.Key) must be an absolute local filesystem path, not a UNC path."
    }
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

function Assert-LocalDrivePath([string]$Path, [string]$Description) {
    $root = [IO.Path]::GetPathRoot($Path)
    if ([string]::IsNullOrWhiteSpace($root) -or $root -match '^\\\\') {
        throw "$Description must be on a local filesystem drive."
    }
    try { $drive = [IO.DriveInfo]::new($root) }
    catch { throw "$Description must be on an available local filesystem drive." }
    if ($drive.DriveType -eq [IO.DriveType]::Network) {
        throw "$Description must not use a mapped network drive."
    }
}

function Assert-NoReparsePointAncestors([string]$Path, [string]$Description) {
    $root = [IO.Path]::GetPathRoot($Path).TrimEnd('\', '/')
    $current = $Path.TrimEnd('\', '/')
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description has a reparse-point ancestor: $current"
            }
        }
        if ($current.Equals($root, [StringComparison]::OrdinalIgnoreCase)) { return }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or
            $parent.Equals($current, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Description did not reach its local drive root."
        }
        $current = $parent.TrimEnd('\', '/')
    }
    throw "$Description did not reach its local drive root."
}

function Get-LowerSha256([string]$Path) {
    $stream = [IO.File]::Open(
        $Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($stream) }
    finally { $algorithm.Dispose(); $stream.Dispose() }
    return ([BitConverter]::ToString($hash).Replace('-', '')).ToLowerInvariant()
}

function Test-SafeNupkgFileName([string]$FileName) {
    if ([string]::IsNullOrWhiteSpace($FileName) -or $FileName.Length -gt 255 -or
        $FileName -cne [IO.Path]::GetFileName($FileName) -or
        [IO.Path]::GetExtension($FileName) -cne '.nupkg' -or
        $FileName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $FileName.EndsWith(' ') -or $FileName.EndsWith('.')) {
        return $false
    }
    $stem = $FileName.Split('.')[0].ToUpperInvariant()
    return $stem -notin @(
        'CON', 'PRN', 'AUX', 'NUL',
        'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9',
        'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9')
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
Assert-LocalDrivePath $acceptanceFeed 'AcceptanceFeedPath'
Assert-NoReparsePointAncestors $acceptanceFeed 'AcceptanceFeedPath'
if (-not (Test-Path -LiteralPath $acceptanceFeed -PathType Container)) {
    throw 'AcceptanceFeedPath must be an existing local filesystem directory.'
}
$outputPath = Resolve-AbsolutePath $OutputDirectory 'OutputDirectory'
Assert-LocalDrivePath $outputPath 'OutputDirectory'
Assert-NoReparsePointAncestors $outputPath 'OutputDirectory'
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
$validatedPreviousAssets = @()
if (-not [string]::IsNullOrWhiteSpace($PreviousReleaseDirectory)) {
    $previousPath = Resolve-AbsolutePath $PreviousReleaseDirectory 'PreviousReleaseDirectory'
    Assert-LocalDrivePath $previousPath 'PreviousReleaseDirectory'
    Assert-NoReparsePointAncestors $previousPath 'PreviousReleaseDirectory'
    if (-not (Test-Path -LiteralPath $previousPath -PathType Container)) {
        throw 'PreviousReleaseDirectory must be an existing directory.'
    }
    Assert-ExistingPathIsNotReparsePoint $previousPath 'PreviousReleaseDirectory'
    if ($previousPath.Equals($outputPath, [StringComparison]::OrdinalIgnoreCase) -or
        (Test-PathWithin $previousPath $outputPath) -or
        (Test-PathWithin $outputPath $previousPath)) {
        throw 'PreviousReleaseDirectory and OutputDirectory must be separate directories.'
    }

    $expectedPreviousFeedName = "releases.$acceptanceChannel.json"
    $previousFeedFiles = @([IO.Directory]::EnumerateFiles(
            $previousPath, 'releases.*.json', [IO.SearchOption]::TopDirectoryOnly))
    if ($previousFeedFiles.Count -ne 1 -or
        [IO.Path]::GetFileName($previousFeedFiles[0]) -cne $expectedPreviousFeedName) {
        throw "PreviousReleaseDirectory must contain only the acceptance release feed $expectedPreviousFeedName."
    }
    $previousFeedPath = $previousFeedFiles[0]
    Assert-ExistingPathIsNotReparsePoint $previousFeedPath 'Previous acceptance release feed'
    try {
        $previousFeed = [IO.File]::ReadAllText($previousFeedPath) | ConvertFrom-Json
    }
    catch {
        throw 'Previous acceptance release feed is not valid JSON.'
    }
    if ($null -eq $previousFeed.PSObject.Properties['Assets']) {
        throw 'Previous acceptance release feed has no Assets property.'
    }
    $previousFeedAssets = @($previousFeed.Assets)
    if ($previousFeedAssets.Count -eq 0) {
        throw 'Previous acceptance release feed contains no assets.'
    }
    $referencedPreviousPackages = New-Object `
        'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    $containsRequestedVersion = $false
    foreach ($asset in $previousFeedAssets) {
        foreach ($propertyName in @('PackageId', 'Version', 'FileName', 'SHA256', 'Size')) {
            if ($null -eq $asset -or $null -eq $asset.PSObject.Properties[$propertyName]) {
                throw "Previous acceptance release feed asset is missing $propertyName."
            }
        }
        if ([string]$asset.PackageId -cne $acceptancePackageId) {
            throw 'Previous acceptance release feed contains a foreign package ID.'
        }
        $fileName = [string]$asset.FileName
        if (-not (Test-SafeNupkgFileName $fileName)) {
            throw 'Previous acceptance release feed contains an unsafe package file name.'
        }
        if ($referencedPreviousPackages.ContainsKey($fileName)) {
            throw "Previous acceptance release feed contains duplicate package file name: $fileName"
        }
        $declaredHash = [string]$asset.SHA256
        if ($declaredHash -cnotmatch '^[0-9A-Fa-f]{64}$') {
            throw "Previous acceptance release feed contains an invalid SHA-256: $fileName"
        }
        try { $declaredSize = [long]$asset.Size }
        catch { throw "Previous acceptance release feed contains an invalid size: $fileName" }
        if ($declaredSize -lt 0) {
            throw "Previous acceptance release feed contains an invalid size: $fileName"
        }
        $packagePath = Join-Path $previousPath $fileName
        if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
            throw "Previous acceptance release feed referenced package is missing: $fileName"
        }
        Assert-ExistingPathIsNotReparsePoint $packagePath 'Previous acceptance package'
        $packageInfo = [IO.FileInfo]::new($packagePath)
        if ($packageInfo.Length -ne $declaredSize) {
            throw "Previous acceptance package size does not match its feed: $fileName"
        }
        $actualHash = Get-LowerSha256 $packagePath
        if ($actualHash -cne $declaredHash.ToLowerInvariant()) {
            throw "Previous acceptance package SHA-256 does not match its feed: $fileName"
        }
        $referencedPreviousPackages.Add($fileName, [pscustomobject]@{
                FileName = $fileName
                FullPath = $packagePath
            })
        if ([string]$asset.Version -ceq $Version) { $containsRequestedVersion = $true }
    }
    foreach ($packageFile in [IO.Directory]::EnumerateFiles(
            $previousPath, '*.nupkg', [IO.SearchOption]::TopDirectoryOnly)) {
        Assert-ExistingPathIsNotReparsePoint $packageFile 'Previous acceptance package'
        $actualName = [IO.Path]::GetFileName($packageFile)
        if (-not $referencedPreviousPackages.ContainsKey($actualName)) {
            throw "PreviousReleaseDirectory contains an unreferenced nupkg: $actualName"
        }
        if ($actualName -cne [string]$referencedPreviousPackages[$actualName].FileName) {
            throw "Previous acceptance package casing differs from its feed: $actualName"
        }
    }
    if ($containsRequestedVersion) {
        throw "Version $Version already exists in the previous release feed."
    }
    $validatedPreviousAssets = @($previousFeedPath) + @(
        $referencedPreviousPackages.Values |
            Sort-Object FileName |
            ForEach-Object { $_.FullPath })
}

$releaseNotes = $null
if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
    $releaseNotes = Resolve-AbsolutePath $ReleaseNotesPath 'ReleaseNotesPath'
    Assert-LocalDrivePath $releaseNotes 'ReleaseNotesPath'
    Assert-NoReparsePointAncestors $releaseNotes 'ReleaseNotesPath'
    if (-not (Test-Path -LiteralPath $releaseNotes -PathType Leaf)) {
        throw 'ReleaseNotesPath must name an existing file.'
    }
    Assert-ExistingPathIsNotReparsePoint $releaseNotes 'ReleaseNotesPath'
}

$appProject = Join-Path $repoRoot 'src\BaiduBatchTransfer.App\BaiduBatchTransfer.App.csproj'
$toolManifestPath = Join-Path $repoRoot '.config\dotnet-tools.json'
$helperVerificationScript = Join-Path $repoRoot 'scripts\verify_managed_browser_helper_package.ps1'
$helperManifestSource = Join-Path $repoRoot 'tools\managed_browser_helper\helper-manifest.json'
$helperSourceDirectory = Join-Path $repoRoot 'tools\managed_browser_helper'
foreach ($requiredInput in @($appProject, $toolManifestPath, $helperVerificationScript, $helperManifestSource)) {
    if (-not (Test-Path -LiteralPath $requiredInput -PathType Leaf)) {
        throw "Required release input is missing: $requiredInput"
    }
}
try { $toolManifest = [IO.File]::ReadAllText($toolManifestPath) | ConvertFrom-Json }
catch { throw 'The local dotnet tool manifest is not valid JSON.' }
if ([string]$toolManifest.tools.vpk.version -cne $requiredVpkVersion) {
    throw "The local vpk tool manifest must pin exactly version $requiredVpkVersion."
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
    $exitCode = $null
    $status = 'Exception'
    $invocationException = $null
    $originalLocation = Get-Location
    $originalCurrentDirectory = [Environment]::CurrentDirectory
    $originalErrorActionPreference = $ErrorActionPreference
    try {
        Set-Location -LiteralPath $repoRoot
        [Environment]::CurrentDirectory = $repoRoot
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = $null
        & $FilePath @Arguments 1> $stdoutPath 2> $stderrPath
        $commandSucceeded = $?
        $exitCode = $LASTEXITCODE
        if ($null -eq $exitCode) {
            if (-not $commandSucceeded) {
                throw "Release command '$Name' could not be started."
            }
            $exitCode = 0
        }
        $status = if ($exitCode -eq 0) { 'Succeeded' } else { 'Failed' }
    }
    catch {
        if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
            $exitCode = $LASTEXITCODE
            $status = 'Failed'
        }
        else {
            $invocationException = $_
            $status = 'Exception'
        }
    }
    finally {
        $ErrorActionPreference = $originalErrorActionPreference
        Set-Location -LiteralPath $originalLocation.Path
        [Environment]::CurrentDirectory = $originalCurrentDirectory
        $stdout = if (Test-Path -LiteralPath $stdoutPath) {
            [IO.File]::ReadAllText($stdoutPath)
        } else { '' }
        $stderr = if (Test-Path -LiteralPath $stderrPath) {
            [IO.File]::ReadAllText($stderrPath)
        } else { '' }
        if ($null -ne $invocationException) {
            $exceptionText = [string]$invocationException
            if ([string]::IsNullOrWhiteSpace($stderr)) { $stderr = $exceptionText }
            else { $stderr = $stderr.TrimEnd() + "`n" + $exceptionText }
        }
        $record = [ordered]@{
            name = $Name
            executable = $FilePath
            arguments = @($Arguments)
            WorkingDirectory = $repoRoot
            startedUtc = $startedUtc.ToString('O')
            status = $status
            exitCode = $exitCode
            stdout = $stdout
            stderr = $stderr
        }
        [IO.File]::AppendAllText(
            $commandLogPath,
            (($record | ConvertTo-Json -Compress -Depth 4) + "`n"),
            $utf8NoBom)
    }
    if (-not [string]::IsNullOrEmpty($stdout)) { Write-Verbose $stdout.TrimEnd() }
    if (-not [string]::IsNullOrEmpty($stderr)) { Write-Warning $stderr.TrimEnd() }
    if ($null -ne $invocationException) { throw $invocationException }
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

$vpkRootHelpArguments = @('tool', 'run', 'vpk', '--', '--help')
$vpkRootHelp = Invoke-RecordedCommand 'vpk-root-help' 'dotnet' $vpkRootHelpArguments
$vpkRootHelpOutput = $vpkRootHelp.Stdout + "`n" + $vpkRootHelp.Stderr
$vpkDescriptionMarkers = [Text.RegularExpressions.Regex]::Matches(
    $vpkRootHelpOutput,
    'Velopack CLI[ \t]+')
$requiredVpkTokenMatches = [Text.RegularExpressions.Regex]::Matches(
    $vpkRootHelpOutput,
    ('Velopack CLI[ \t]+' + [Text.RegularExpressions.Regex]::Escape($requiredVpkVersion) + '(?=$|[\s,])'))
if ($vpkDescriptionMarkers.Count -ne 1 -or $requiredVpkTokenMatches.Count -ne 1) {
    throw "The restored vpk executable root help must report exactly Velopack CLI $requiredVpkVersion."
}

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
[void](Invoke-RecordedCommand 'verify-managed-browser-helper' 'powershell' $helperGateArguments)

if ($null -ne $previousPath) {
    foreach ($previousAsset in $validatedPreviousAssets) {
        $name = [IO.Path]::GetFileName($previousAsset)
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
$releaseFeedFiles = @([IO.Directory]::EnumerateFiles(
        $outputPath, 'releases.*.json', [IO.SearchOption]::TopDirectoryOnly))
if ($releaseFeedFiles.Count -ne 1 -or
    [IO.Path]::GetFileName($releaseFeedFiles[0]) -cne [IO.Path]::GetFileName($releaseFeedPath) -or
    -not (Test-Path -LiteralPath $releaseFeedPath -PathType Leaf)) {
    throw "Expected release feed is missing: $releaseFeedPath"
}
Assert-ExistingPathIsNotReparsePoint $releaseFeedPath 'Release feed'
try { $releaseFeed = [IO.File]::ReadAllText($releaseFeedPath) | ConvertFrom-Json }
catch { throw 'Generated release feed is not valid JSON.' }
if ($null -eq $releaseFeed.PSObject.Properties['Assets']) {
    throw 'Generated release feed has no Assets property.'
}
$assets = @($releaseFeed.Assets)
if ($assets.Count -eq 0) { throw 'Release feed contains no assets.' }
$referencedReleasePackages = New-Object `
    'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
$targetAssetCount = 0
$hasFullPackage = $false
foreach ($asset in $assets) {
    foreach ($propertyName in @('PackageId', 'Version', 'Type', 'FileName', 'SHA256', 'Size')) {
        if ($null -eq $asset -or $null -eq $asset.PSObject.Properties[$propertyName]) {
            throw "Release feed asset is missing $propertyName."
        }
    }
    if ([string]$asset.PackageId -cne $acceptancePackageId) {
        throw 'Release feed contains a foreign package ID.'
    }
    $fileName = [string]$asset.FileName
    if (-not (Test-SafeNupkgFileName $fileName)) {
        throw 'Release feed contains an unsafe asset file name.'
    }
    if ($referencedReleasePackages.ContainsKey($fileName)) {
        throw "Release feed contains duplicate asset file name: $fileName"
    }
    $assetPath = Join-Path $outputPath $fileName
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Release feed asset is missing: $fileName"
    }
    Assert-ExistingPathIsNotReparsePoint $assetPath 'Release asset'
    $assetInfo = [IO.FileInfo]::new($assetPath)
    $declaredHash = [string]$asset.SHA256
    if ($declaredHash -cnotmatch '^[0-9A-Fa-f]{64}$') {
        throw "Release feed contains an invalid SHA-256: $fileName"
    }
    try { $declaredSize = [long]$asset.Size }
    catch { throw "Release feed contains an invalid size: $fileName" }
    if ($declaredSize -lt 0 -or $declaredSize -ne $assetInfo.Length) {
        throw "Release feed size differs from the asset: $fileName"
    }
    $actualHash = Get-LowerSha256 $assetPath
    if ($declaredHash.ToLowerInvariant() -cne $actualHash) {
        throw "Release feed SHA-256 differs from the asset: $fileName"
    }
    $referencedReleasePackages.Add($fileName, [pscustomobject]@{
            FileName = $fileName
            FullPath = $assetPath
        })
    if ([string]$asset.Version -ceq $Version) {
        $targetAssetCount++
        if ([string]$asset.Type -match '(?i)full') { $hasFullPackage = $true }
    }
}
if ($targetAssetCount -eq 0) {
    throw 'Release feed does not contain the requested acceptance package and version.'
}
foreach ($packageFile in [IO.Directory]::EnumerateFiles(
        $outputPath, '*.nupkg', [IO.SearchOption]::TopDirectoryOnly)) {
    $actualName = [IO.Path]::GetFileName($packageFile)
    if (-not $referencedReleasePackages.ContainsKey($actualName)) {
        throw "Output contains an unreferenced nupkg: $actualName"
    }
    if ($actualName -cne [string]$referencedReleasePackages[$actualName].FileName) {
        throw "Output package casing differs from the release feed: $actualName"
    }
}
if (-not $hasFullPackage) { throw 'Release feed does not contain a full package for this version.' }

$setupFiles = @([IO.Directory]::EnumerateFiles($outputPath, "$acceptancePackageId*Setup*.exe",
    [IO.SearchOption]::TopDirectoryOnly))
$portableFiles = @([IO.Directory]::EnumerateFiles($outputPath, "$acceptancePackageId*Portable*.zip",
    [IO.SearchOption]::TopDirectoryOnly))
if ($setupFiles.Count -eq 0 -or $portableFiles.Count -eq 0) {
    throw 'Setup, Portable ZIP, or full nupkg acceptance output is missing.'
}

$manifestAssetPaths = @(
    @($releaseFeedPath) +
    @($referencedReleasePackages.Values | ForEach-Object { $_.FullPath }) +
    @($setupFiles) +
    @($portableFiles) |
        Sort-Object { [IO.Path]::GetFileName($_) }
)
$manifestAssetSet = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($assetPath in $manifestAssetPaths) {
    if (-not $manifestAssetSet.Add([IO.Path]::GetFullPath($assetPath))) {
        throw "Duplicate upload asset path: $assetPath"
    }
}
foreach ($candidate in [IO.Directory]::EnumerateFiles(
        $outputPath, '*', [IO.SearchOption]::TopDirectoryOnly)) {
    if ([IO.Path]::GetExtension($candidate) -notin @('.json', '.nupkg', '.exe', '.zip')) { continue }
    if (-not $manifestAssetSet.Contains([IO.Path]::GetFullPath($candidate))) {
        throw "Output contains a foreign asset that is excluded from the upload manifest: $candidate"
    }
}
$manifestLines = foreach ($assetPath in $manifestAssetPaths) {
    Assert-ExistingPathIsNotReparsePoint $assetPath 'Upload asset'
    $hash = Get-LowerSha256 $assetPath
    "$hash  $([IO.Path]::GetFileName($assetPath))"
}
$manifestPath = Join-Path $outputPath 'upload-manifest.sha256'
try {
    [IO.File]::WriteAllText($manifestPath, (($manifestLines -join "`n") + "`n"), $utf8NoBom)
}
catch {
    throw "Unable to write upload manifest '$manifestPath': $($_.Exception.Message)"
}

$readmePath = Join-Path $outputPath 'LOCAL-OTA-ACCEPTANCE-README.zh-CN.txt'
$readmeLabels = ('{' +
    '"heading":"\u672c\u673a OTA \u9a8c\u6536\u5305",' +
    '"version":"\u7248\u672c",' +
    '"package":"\u5305 ID",' +
    '"channel":"\u6e20\u9053",' +
    '"feed":"\u66f4\u65b0\u6e90",' +
    '"warning":"\u6b64\u76ee\u5f55\u4ec5\u7528\u4e8e\u672a\u7b7e\u540d\u7684\u672c\u673a OTA \u9a8c\u6536\uff0c\u4e0d\u662f\u751f\u4ea7\u53d1\u5e03\u7269\u3002",' +
    '"noUpload":"\u811a\u672c\u4e0d\u4f1a\u8fde\u63a5\u6216\u4e0a\u4f20\u5230\u4efb\u4f55\u670d\u52a1\u5668\u3002\u4e0a\u4f20\u524d\u5e94\u518d\u6b21\u6838\u5bf9 upload-manifest.sha256\u3002",' +
    '"log":"\u547d\u4ee4\u6267\u884c\u8bb0\u5f55"' +
    '}') | ConvertFrom-Json
$readme = @"
$($readmeLabels.heading)

$($readmeLabels.version): $Version
$($readmeLabels.package): $acceptancePackageId
$($readmeLabels.channel): $acceptanceChannel
$($readmeLabels.feed): $acceptanceFeed

$($readmeLabels.warning)
$($readmeLabels.noUpload)
$($readmeLabels.log): .build\logs\commands.jsonl
"@
try { [IO.File]::WriteAllText($readmePath, $readme.Replace("`r`n", "`n"), $utf8NoBom) }
catch { throw "Unable to write release description '$readmePath': $($_.Exception.Message)" }

Write-Output "Local OTA acceptance release created: $outputPath"
