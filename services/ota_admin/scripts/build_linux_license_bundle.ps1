[CmdletBinding()]
param(
    [string] $SourceCommit,
    [switch] $FunctionsOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$LicenseFunctionsOnly = [bool] $FunctionsOnly
$BaseBuilderScript = Join-Path $PSScriptRoot 'build_linux_amd64.ps1'
. $BaseBuilderScript -FunctionsOnly
$FunctionsOnly = $LicenseFunctionsOnly
$LicenseBootstrapScript = Join-Path $ModuleRoot 'scripts\bootstrap_go_toolchain.ps1'

$LicenseOutputDirectory = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot 'license-production'))
$licenseInvocationId = [guid]::NewGuid().ToString('N')
$licenseStagingId = $licenseInvocationId.Substring(0, 12)
$LicenseStagingDirectory = [System.IO.Path]::GetFullPath((Join-Path $SystemTempRoot "spota-$licenseStagingId"))
$LicenseSnapshotDirectory = Join-Path $LicenseStagingDirectory 's'
$LicenseStagedPublishDirectory = Join-Path $LicenseStagingDirectory 'p'
$LicenseTestTempDirectory = Join-Path $LicenseStagingDirectory 't'
$LicenseBackupDirectory = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot ".license-production-backup-$licenseInvocationId"))
$LicenseBundleNames = @(
    'ota-admin'
    'ota-admin.sha256'
    'source-commit.txt'
    'source-manifest.sha256'
    'source-manifest.sha256.sha256'
    'superpan-license'
    'superpan-license.sha256'
)

function Invoke-LicenseToolchainBootstrap {
    param([string[]] $Arguments = @())

    if (-not (Test-Path -LiteralPath $LicenseBootstrapScript -PathType Leaf)) {
        throw "Pinned Go bootstrap script is missing: $LicenseBootstrapScript"
    }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $LicenseBootstrapScript @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Pinned Go bootstrap failed.' }
}

function Assert-LicenseBundleSourceState {
    param([Parameter(Mandatory)][string] $SourceCommit)

    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$') {
        throw 'SourceCommit must be one explicit lowercase 40-character Git commit SHA.'
    }
    [string[]] $headLines = @(& git -C $RepositoryRoot rev-parse --verify 'HEAD^{commit}')
    if ($LASTEXITCODE -ne 0 -or $headLines.Count -ne 1 -or $headLines[0] -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve exactly one clean HEAD commit.'
    }
    if ($SourceCommit -cne $headLines[0]) {
        throw "SourceCommit must exactly equal HEAD ($($headLines[0]))."
    }
    [string[]] $statusLines = @(& git -C $RepositoryRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'git status failed while verifying the source worktree.' }
    if ($statusLines.Count -ne 0) {
        throw "The source worktree is dirty; commit or remove every tracked and untracked change before building.`n$($statusLines -join "`n")"
    }
}

function Get-LicenseBundleSourcePaths {
    param([Parameter(Mandatory)][string] $SourceCommit)

    $modulePrefix = 'services/ota_admin/'
    [string[]] $trackedPaths = @(& git -C $RepositoryRoot ls-tree -r --name-only $SourceCommit -- services/ota_admin)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-tree failed while selecting license bundle source inputs.' }

    [string[]] $sourcePaths = @($trackedPaths | Where-Object {
        if (-not $_.StartsWith($modulePrefix, [System.StringComparison]::Ordinal)) { return $false }
        $relativePath = $_.Substring($modulePrefix.Length)
        if ($relativePath -ceq 'go.mod' -or $relativePath -ceq 'go.sum') { return $true }
        if ($relativePath.StartsWith('cmd/ota-admin/', [System.StringComparison]::Ordinal) -or
            $relativePath.StartsWith('cmd/license-service/', [System.StringComparison]::Ordinal)) {
            return $relativePath.EndsWith('.go', [System.StringComparison]::Ordinal) -and
                -not $relativePath.EndsWith('_test.go', [System.StringComparison]::Ordinal)
        }
        if ($relativePath.StartsWith('internal/', [System.StringComparison]::Ordinal) -and
            $relativePath.EndsWith('.go', [System.StringComparison]::Ordinal) -and
            -not $relativePath.EndsWith('_test.go', [System.StringComparison]::Ordinal)) {
            return $true
        }
        return $relativePath.StartsWith('internal/webui/static/', [System.StringComparison]::Ordinal) -and
            ($relativePath.EndsWith('.html', [System.StringComparison]::Ordinal) -or
                $relativePath.EndsWith('.css', [System.StringComparison]::Ordinal) -or
                $relativePath.EndsWith('.js', [System.StringComparison]::Ordinal))
    })
    [System.Array]::Sort($sourcePaths, [System.StringComparer]::Ordinal)
    foreach ($requiredPath in @(
        'services/ota_admin/go.mod',
        'services/ota_admin/go.sum',
        'services/ota_admin/cmd/ota-admin/main.go',
        'services/ota_admin/cmd/license-service/main.go'
    )) {
        if (-not $sourcePaths.Contains($requiredPath)) {
            throw "License bundle source selection is missing $requiredPath."
        }
    }
    return $sourcePaths
}

function New-LicenseBundleSourceSnapshot {
    param(
        [Parameter(Mandatory)][string[]] $SourcePaths,
        [Parameter(Mandatory)][string] $DestinationRoot,
        [Parameter(Mandatory)][string] $SourceCommit
    )

    $modulePrefix = 'services/ota_admin/'
    $DestinationRoot = [System.IO.Path]::GetFullPath($DestinationRoot)
    [string[]] $moduleRelativePaths = @($SourcePaths | ForEach-Object {
        if (-not $_.StartsWith($modulePrefix, [System.StringComparison]::Ordinal)) {
            throw "Source path is outside services/ota_admin: $_"
        }
        $_.Substring($modulePrefix.Length)
    })
    [System.Array]::Sort($moduleRelativePaths, [System.StringComparer]::Ordinal)
    $snapshotArchivePath = Join-Path (Split-Path -Parent $DestinationRoot) 'license-snapshot-from-commit.zip'
    if ((Test-Path -LiteralPath $DestinationRoot) -or (Test-Path -LiteralPath $snapshotArchivePath)) {
        throw 'License Git-object snapshot target unexpectedly exists.'
    }

    $treeish = "${SourceCommit}:services/ota_admin"
    & git -C $RepositoryRoot -c core.autocrlf=false -c core.eol=lf archive --format=zip --output=$snapshotArchivePath $treeish -- @moduleRelativePaths
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed while materializing the committed license source snapshot.' }
    New-Item -ItemType Directory -Path $DestinationRoot | Out-Null
    try {
        Expand-Archive -LiteralPath $snapshotArchivePath -DestinationPath $DestinationRoot
    }
    finally {
        if (Test-Path -LiteralPath $snapshotArchivePath) { Remove-Item -LiteralPath $snapshotArchivePath -Force }
    }

    [string[]] $actualPaths = @(Get-ChildItem -LiteralPath $DestinationRoot -File -Recurse | ForEach-Object {
        $_.FullName.Substring($DestinationRoot.TrimEnd('\').Length + 1).Replace('\', '/')
    })
    [System.Array]::Sort($actualPaths, [System.StringComparer]::Ordinal)
    if ([string]::Join("`n", $actualPaths) -cne [string]::Join("`n", $moduleRelativePaths)) {
        throw 'License Git-object snapshot file set differs from the exact production allowlist.'
    }
}

function Write-DeterministicSourceManifest {
    param(
        [Parameter(Mandatory)][string] $SnapshotRoot,
        [Parameter(Mandatory)][string] $ManifestPath
    )

    $SnapshotRoot = [System.IO.Path]::GetFullPath($SnapshotRoot)
    [string[]] $lines = @(Get-ChildItem -LiteralPath $SnapshotRoot -File -Recurse | ForEach-Object {
        if (($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Snapshot contains a reparse point: $($_.FullName)"
        }
        $relativePath = $_.FullName.Substring($SnapshotRoot.TrimEnd('\').Length + 1).Replace('\', '/')
        $hash = Get-LicenseFileSha256 -Path $_.FullName
        "$hash  $relativePath"
    })
    [System.Array]::Sort($lines, [System.StringComparer]::Ordinal)
    if ($lines.Count -eq 0) { throw 'Cannot write an empty source manifest.' }
    [System.IO.File]::WriteAllText($ManifestPath, ($lines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Get-LicenseFileSha256 {
    param([Parameter(Mandatory)][string] $Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Write-LicenseSha256CheckFile {
    param(
        [Parameter(Mandatory)][string] $PayloadPath,
        [Parameter(Mandatory)][string] $CheckFilePath
    )

    $hash = Get-LicenseFileSha256 -Path $PayloadPath
    [System.IO.File]::WriteAllText($CheckFilePath, "$hash  $([System.IO.Path]::GetFileName($PayloadPath))`n", [System.Text.UTF8Encoding]::new($false))
    return $hash
}

function Assert-LicenseBundleCheckFile {
    param(
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)][string] $PayloadName,
        [Parameter(Mandatory)][string] $CheckName
    )

    $payloadPath = Join-Path $Directory $PayloadName
    $checkPath = Join-Path $Directory $CheckName
    $actualHash = Get-LicenseFileSha256 -Path $payloadPath
    $checkText = [System.IO.File]::ReadAllText($checkPath, [System.Text.UTF8Encoding]::new($false, $true))
    $expectedText = "$actualHash  $PayloadName`n"
    if ($checkText -cne $expectedText) {
        throw "$CheckName is not the canonical lowercase SHA-256 authentication record for $PayloadName."
    }
}

function Assert-LicenseBundleArtifacts {
    param(
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)][string] $SourceCommit
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw 'License bundle staging directory is missing.' }
    if (@(Get-ChildItem -LiteralPath $Directory -Directory -Force).Count -ne 0) { throw 'License bundle must not contain directories.' }
    [string[]] $actualNames = @(Get-ChildItem -LiteralPath $Directory -File -Force | ForEach-Object Name)
    [System.Array]::Sort($actualNames, [System.StringComparer]::Ordinal)
    [string[]] $expectedNames = @($LicenseBundleNames)
    [System.Array]::Sort($expectedNames, [System.StringComparer]::Ordinal)
    if ([string]::Join("`n", $actualNames) -cne [string]::Join("`n", $expectedNames)) {
        throw 'License bundle is incomplete or contains unexpected files.'
    }

    Assert-LicenseBundleCheckFile -Directory $Directory -PayloadName 'ota-admin' -CheckName 'ota-admin.sha256'
    Assert-LicenseBundleCheckFile -Directory $Directory -PayloadName 'superpan-license' -CheckName 'superpan-license.sha256'
    Assert-LicenseBundleCheckFile -Directory $Directory -PayloadName 'source-manifest.sha256' -CheckName 'source-manifest.sha256.sha256'

    $commitText = [System.IO.File]::ReadAllText((Join-Path $Directory 'source-commit.txt'), [System.Text.UTF8Encoding]::new($false, $true))
    if ($commitText -cne "$SourceCommit`n") { throw 'source-commit.txt does not contain the exact canonical source commit.' }

    $manifestText = [System.IO.File]::ReadAllText((Join-Path $Directory 'source-manifest.sha256'), [System.Text.UTF8Encoding]::new($false, $true))
    if ($manifestText.Contains("`r") -or -not $manifestText.EndsWith("`n") -or $manifestText.Length -eq 1) {
        throw 'Source manifest is not canonical UTF-8 with LF line endings.'
    }
    [string[]] $manifestLines = @($manifestText.Substring(0, $manifestText.Length - 1).Split("`n"))
    [string[]] $sortedManifestLines = @($manifestLines)
    [System.Array]::Sort($sortedManifestLines, [System.StringComparer]::Ordinal)
    if ([string]::Join("`n", $manifestLines) -cne [string]::Join("`n", $sortedManifestLines)) {
        throw 'Source manifest entries are not sorted ordinally.'
    }
    $seenPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($line in $manifestLines) {
        if ($line -cnotmatch '^([0-9a-f]{64})  ([^\\\r\n]+)$') { throw "Malformed source manifest line: $line" }
        $relativePath = $Matches[2]
        if (-not $seenPaths.Add($relativePath)) { throw "Duplicate source manifest path: $relativePath" }
        if ($relativePath.StartsWith('cmd/', [System.StringComparison]::Ordinal) -and
            -not $relativePath.StartsWith('cmd/ota-admin/', [System.StringComparison]::Ordinal) -and
            -not $relativePath.StartsWith('cmd/license-service/', [System.StringComparison]::Ordinal)) {
            throw "Non-production command entered the source manifest: $relativePath"
        }
        if ($relativePath -cmatch '(?i)(^|/)(acceptance|load-generator)(/|$)|\.(key|pem|p12|pfx|db|sqlite|json)$') {
            throw "Forbidden source entered the source manifest: $relativePath"
        }
    }
}

function Publish-LicenseBundle {
    param(
        [Parameter(Mandatory)][string] $StagedPath,
        [Parameter(Mandatory)][string] $PublishedPath,
        [Parameter(Mandatory)][string] $PreviousPath
    )

    Publish-StagedArtifacts -StagedPath $StagedPath -PublishedPath $PublishedPath -PreviousPath $PreviousPath
}

if ($FunctionsOnly) {
    return
}

if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
    throw 'SourceCommit is required for a production license bundle build.'
}
Assert-LicenseBundleSourceState -SourceCommit $SourceCommit
[string[]] $licenseSourcePaths = @(Get-LicenseBundleSourcePaths -SourceCommit $SourceCommit)

$ExpectedArchiveSha256 = 'a3911b5e0e1b1053f25ed0675f4c1c6aad1e2bfcf253df2b9be4caabd2edd95d'
$ExpectedGoIdentity = 'go version go1.27.1 windows/amd64'
Invoke-LicenseToolchainBootstrap
Assert-PinnedGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity

New-Item -ItemType Directory -Path $ArtifactsRoot -Force | Out-Null
$OutputDirectory = $LicenseOutputDirectory
$StagingDirectory = $LicenseStagingDirectory
$SnapshotDirectory = $LicenseSnapshotDirectory
$StagedPublishDirectory = $LicenseStagedPublishDirectory
$TestTempDirectory = $LicenseTestTempDirectory
$BackupDirectory = $LicenseBackupDirectory
Assert-SafeArtifactDirectory -Path $OutputDirectory
Assert-SafeStagingDirectory -Path $StagingDirectory
Assert-SafeArtifactDirectory -Path $BackupDirectory

New-SafeStagingDirectory -Path $StagingDirectory
try {
    New-Item -ItemType Directory -Path $TestTempDirectory | Out-Null
    New-LicenseBundleSourceSnapshot -SourcePaths $licenseSourcePaths -DestinationRoot $SnapshotDirectory -SourceCommit $SourceCommit
    $snapshotFingerprint = Get-SnapshotFingerprint -SnapshotRoot $SnapshotDirectory
    New-Item -ItemType Directory -Path $StagedPublishDirectory | Out-Null

    $env:TEMP = $TestTempDirectory
    $env:TMP = $TestTempDirectory
    $env:GOTMPDIR = $TestTempDirectory
    $env:GOTOOLCHAIN = 'local'
    $env:GOCACHE = Join-Path $StagingDirectory 'g'
    $env:GOMODCACHE = Join-Path $StagingDirectory 'm'
    $env:CGO_ENABLED = '0'
    $env:GOOS = 'linux'
    $env:GOARCH = 'amd64'
    $env:SOURCE_DATE_EPOCH = '0'

    Assert-PinnedGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity
    & $GoExe -C $SnapshotDirectory mod verify
    if ($LASTEXITCODE -ne 0) { throw 'go mod verify failed for the immutable license snapshot.' }

    $otaAdminPath = Join-Path $StagedPublishDirectory 'ota-admin'
    $licenseServicePath = Join-Path $StagedPublishDirectory 'superpan-license'
    Assert-PinnedGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity
    & $GoExe -C $SnapshotDirectory build -buildvcs=false -trimpath -ldflags '-s -w' -o $otaAdminPath ./cmd/ota-admin
    if ($LASTEXITCODE -ne 0) { throw 'ota-admin static linux/amd64 build failed.' }
    Assert-PinnedGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity
    & $GoExe -C $SnapshotDirectory build -buildvcs=false -trimpath -ldflags '-s -w' -o $licenseServicePath ./cmd/license-service
    if ($LASTEXITCODE -ne 0) { throw 'superpan-license static linux/amd64 build failed.' }
    if (-not (Test-Path -LiteralPath $otaAdminPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $licenseServicePath -PathType Leaf)) {
        throw 'One or both production binaries are missing after build.'
    }
    if ((Get-SnapshotFingerprint -SnapshotRoot $SnapshotDirectory) -cne $snapshotFingerprint) {
        throw 'The immutable license source snapshot changed during the build.'
    }

    Write-LicenseSha256CheckFile -PayloadPath $otaAdminPath -CheckFilePath (Join-Path $StagedPublishDirectory 'ota-admin.sha256') | Out-Null
    Write-LicenseSha256CheckFile -PayloadPath $licenseServicePath -CheckFilePath (Join-Path $StagedPublishDirectory 'superpan-license.sha256') | Out-Null
    $sourceManifestPath = Join-Path $StagedPublishDirectory 'source-manifest.sha256'
    Write-DeterministicSourceManifest -SnapshotRoot $SnapshotDirectory -ManifestPath $sourceManifestPath
    Write-LicenseSha256CheckFile -PayloadPath $sourceManifestPath -CheckFilePath (Join-Path $StagedPublishDirectory 'source-manifest.sha256.sha256') | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $StagedPublishDirectory 'source-commit.txt'), "$SourceCommit`n", [System.Text.UTF8Encoding]::new($false))

    Assert-LicenseBundleArtifacts -Directory $StagedPublishDirectory -SourceCommit $SourceCommit
    Publish-LicenseBundle -StagedPath $StagedPublishDirectory -PublishedPath $OutputDirectory -PreviousPath $BackupDirectory
}
finally {
    if (Test-Path -LiteralPath $StagingDirectory) {
        Remove-SafeStagingDirectory -Path $StagingDirectory
    }
}

Write-Output "Built immutable production license bundle $OutputDirectory"
Write-Output "Source commit $SourceCommit"
