[CmdletBinding()]
param([switch] $FunctionsOnly)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$GoVersion = '1.27.1'
$RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$ModuleRoot = Join-Path $RepositoryRoot 'services\ota_admin'
$BootstrapScript = Join-Path $ModuleRoot 'scripts\bootstrap_go_toolchain.ps1'
$GoArchive = Join-Path $RepositoryRoot ".tools\downloads\go$GoVersion.windows-amd64.zip"
$GoExe = Join-Path $RepositoryRoot ".tools\go-$GoVersion\go\bin\go.exe"
$ArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.artifacts'))
$OutputDirectory = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot 'ota-admin'))
$invocationId = [guid]::NewGuid().ToString('N')
$stagingId = $invocationId.Substring(0, 12)
# Keep the immutable Windows test snapshot short. Several security tests use
# additional nested temporary paths, and the previous descriptive staging
# names exceeded legacy Win32 path budgets before the Linux cross-build began.
$StagingDirectory = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot ".o-$stagingId"))
$SnapshotDirectory = Join-Path $StagingDirectory 's'
$StagedPublishDirectory = Join-Path $StagingDirectory 'p'
$TestTempDirectory = Join-Path $StagingDirectory 't'
$BackupDirectory = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot ".ota-admin-backup-$invocationId"))
$FixedZipTimestamp = [System.DateTimeOffset]::Parse('1980-01-01T00:00:00Z')

function Assert-SafeArtifactDirectory {
    param([Parameter(Mandatory)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $requiredPrefix = $ArtifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify an artifact path outside $ArtifactsRoot"
    }
}

function Remove-SafeArtifactDirectory {
    param([Parameter(Mandatory)][string] $Path)

    Assert-SafeArtifactDirectory -Path $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Assert-PinnedGoToolchain {
    param(
        [Parameter(Mandatory)][string] $ExpectedSha256,
        [Parameter(Mandatory)][string] $ExpectedIdentity
    )

    if (-not (Test-Path -LiteralPath $GoArchive -PathType Leaf)) { throw 'Pinned Go archive is missing after bootstrap.' }
    $archiveSha256 = (Get-FileHash -LiteralPath $GoArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($archiveSha256 -cne $ExpectedSha256) { throw 'Pinned Go archive SHA-256 mismatch after bootstrap.' }
    if (-not (Test-Path -LiteralPath $GoExe -PathType Leaf)) { throw 'Pinned Go executable is missing after bootstrap.' }
    $goIdentity = (& $GoExe version) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $goIdentity -cne $ExpectedIdentity) {
        throw "Go executable identity mismatch; expected $ExpectedIdentity."
    }
}

function Assert-AcceptanceHarnessExcluded {
    param(
        [Parameter(Mandatory)][string] $GoExecutable,
        [Parameter(Mandatory)][string] $SnapshotRoot
    )

    $acceptanceMain = Join-Path $SnapshotRoot 'cmd\ota-admin-acceptance\main_linux.go'
    if (-not (Test-Path -LiteralPath $acceptanceMain -PathType Leaf)) { throw 'Acceptance harness source is missing from the audited snapshot.' }
    $firstLine = Get-Content -LiteralPath $acceptanceMain -Encoding UTF8 -TotalCount 1
    if ($firstLine -cne '//go:build linux && ota_admin_acceptance') { throw 'Acceptance harness is not protected by its opt-in build tag.' }
    [string[]] $defaultPackages = @(& $GoExecutable -C $SnapshotRoot list -f '{{.ImportPath}}' ./...)
    if ($LASTEXITCODE -ne 0) { throw 'go list failed while proving acceptance harness isolation.' }
    if ($defaultPackages -ccontains 'fanfh.cn/superpan/ota-admin/cmd/ota-admin-acceptance') {
        throw 'Acceptance-only harness entered the production build graph.'
    }
}

function Get-TrackedSourcePaths {
    $modulePrefix = 'services/ota_admin/'
    [string[]] $trackedPaths = @(& git -C $RepositoryRoot ls-tree -r --name-only HEAD -- services/ota_admin)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-tree failed while selecting committed source inputs.' }

    [string[]] $sourcePaths = @($trackedPaths | Where-Object {
        if (-not $_.StartsWith($modulePrefix, [System.StringComparison]::Ordinal)) { return $false }
        $moduleRelativePath = $_.Substring($modulePrefix.Length)
        return $moduleRelativePath -eq 'go.mod' -or
            $moduleRelativePath -eq 'go.sum' -or
            $moduleRelativePath -ceq 'cmd/ota-admin-acceptance/testdata/valid-upload.zip' -or
            $moduleRelativePath -ceq 'cmd/ota-admin-acceptance/testdata/publisher.cer' -or
            $moduleRelativePath.EndsWith('.go', [System.StringComparison]::Ordinal) -or
            $moduleRelativePath.EndsWith('.ps1', [System.StringComparison]::Ordinal) -or
            $moduleRelativePath.EndsWith('.sh', [System.StringComparison]::Ordinal) -or
            $moduleRelativePath.EndsWith('.service', [System.StringComparison]::Ordinal) -or
            $moduleRelativePath.EndsWith('.html', [System.StringComparison]::Ordinal) -or
            $moduleRelativePath.EndsWith('.css', [System.StringComparison]::Ordinal) -or
            $moduleRelativePath.EndsWith('.js', [System.StringComparison]::Ordinal) -or
            $moduleRelativePath.EndsWith('.sql', [System.StringComparison]::Ordinal)
    })
    [System.Array]::Sort($sourcePaths, [System.StringComparer]::Ordinal)
    if ($sourcePaths.Count -eq 0 -or
        -not $sourcePaths.Contains('services/ota_admin/go.mod') -or
        -not $sourcePaths.Contains('services/ota_admin/go.sum')) {
        throw 'Tracked Go source selection is incomplete.'
    }
    return $sourcePaths
}

function Assert-NoUntrackedModuleFiles {
    [string[]] $untrackedPaths = @(& git -C $RepositoryRoot ls-files --others -- services/ota_admin)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files --others failed while checking the OTA admin module.' }
    if ($untrackedPaths.Count -ne 0) {
        throw "Untracked files exist under services/ota_admin; refusing to build:`n$($untrackedPaths -join "`n")"
    }
}

function Assert-SourcePathsClean {
    param([Parameter(Mandatory)][string[]] $SourcePaths)

    & git -C $RepositoryRoot diff --quiet HEAD -- @SourcePaths
    if ($LASTEXITCODE -eq 1) { throw 'Selected OTA admin source inputs are dirty relative to HEAD; commit them before building artifacts.' }
    if ($LASTEXITCODE -ne 0) { throw 'git diff failed while verifying source inputs.' }
}

function New-GitObjectSourceSnapshot {
    param(
        [Parameter(Mandatory)][string[]] $SourcePaths,
        [Parameter(Mandatory)][string] $DestinationRoot
    )

    $modulePrefix = 'services/ota_admin/'
    $DestinationRoot = [System.IO.Path]::GetFullPath($DestinationRoot)
    [string[]] $moduleRelativePaths = @($SourcePaths | ForEach-Object { $_.Substring($modulePrefix.Length) })
    $snapshotArchivePath = Join-Path (Split-Path -Parent $DestinationRoot) 'snapshot-from-head.zip'
    if ((Test-Path -LiteralPath $DestinationRoot) -or (Test-Path -LiteralPath $snapshotArchivePath)) {
        throw 'Git-object snapshot target unexpectedly exists.'
    }

    & git -C $RepositoryRoot -c core.autocrlf=false -c core.eol=lf archive --format=zip --output=$snapshotArchivePath 'HEAD:services/ota_admin' -- @moduleRelativePaths
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed while materializing the committed source snapshot.' }
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
    [System.Array]::Sort($moduleRelativePaths, [System.StringComparer]::Ordinal)
    if ([string]::Join("`n", $actualPaths) -cne [string]::Join("`n", $moduleRelativePaths)) {
        throw 'Git-object snapshot file set differs from the exact tracked allowlist.'
    }
}

function Get-SnapshotFingerprint {
    param([Parameter(Mandatory)][string] $SnapshotRoot)

    [string[]] $lines = @(Get-ChildItem -LiteralPath $SnapshotRoot -File -Recurse | ForEach-Object {
        if (($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Snapshot contains a reparse point: $($_.FullName)" }
        $relativePath = $_.FullName.Substring($SnapshotRoot.TrimEnd('\').Length + 1).Replace('\', '/')
        $fileHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$fileHash  $relativePath"
    })
    [System.Array]::Sort($lines, [System.StringComparer]::Ordinal)
    $manifest = [System.Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`n") + "`n")
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha256.ComputeHash($manifest))).Replace('-', '').ToLowerInvariant() }
    finally { $sha256.Dispose() }
}

function Write-Sha256CheckFile {
    param(
        [Parameter(Mandatory)][string] $PayloadPath,
        [Parameter(Mandatory)][string] $CheckFilePath
    )

    $hash = (Get-FileHash -LiteralPath $PayloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText($CheckFilePath, "$hash  $([System.IO.Path]::GetFileName($PayloadPath))`n", [System.Text.UTF8Encoding]::new($false))
    return $hash
}

function New-DeterministicSourceBundle {
    param(
        [Parameter(Mandatory)][string] $DestinationPath,
        [Parameter(Mandatory)][string] $SnapshotRoot
    )

    Add-Type -AssemblyName System.IO.Compression
    [System.IO.FileInfo[]] $sourceFiles = @(Get-ChildItem -LiteralPath $SnapshotRoot -File -Recurse)
    [System.Array]::Sort($sourceFiles, [System.Comparison[System.IO.FileInfo]]{
        param($left, $right)
        $leftRelative = $left.FullName.Substring($SnapshotRoot.TrimEnd('\').Length + 1).Replace('\', '/')
        $rightRelative = $right.FullName.Substring($SnapshotRoot.TrimEnd('\').Length + 1).Replace('\', '/')
        return [System.StringComparer]::Ordinal.Compare($leftRelative, $rightRelative)
    })
    $outputStream = [System.IO.File]::Open($DestinationPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($outputStream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($sourceFile in $sourceFiles) {
                if (($sourceFile.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Snapshot contains a reparse point: $($sourceFile.FullName)" }
                $relativePath = $sourceFile.FullName.Substring($SnapshotRoot.TrimEnd('\').Length + 1).Replace('\', '/')
                $entry = $archive.CreateEntry($relativePath, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $FixedZipTimestamp
                $entryStream = $entry.Open()
                $sourceStream = [System.IO.File]::OpenRead($sourceFile.FullName)
                try { $sourceStream.CopyTo($entryStream) }
                finally { $sourceStream.Dispose(); $entryStream.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $outputStream.Dispose() }
}

function Publish-StagedArtifacts {
    param(
        [Parameter(Mandatory)][string] $StagedPath,
        [Parameter(Mandatory)][string] $PublishedPath,
        [Parameter(Mandatory)][string] $PreviousPath
    )

    Assert-SafeArtifactDirectory -Path $StagedPath
    Assert-SafeArtifactDirectory -Path $PublishedPath
    Assert-SafeArtifactDirectory -Path $PreviousPath
    if (Test-Path -LiteralPath $PreviousPath) { throw 'Artifact backup path unexpectedly exists.' }

    $movedPrevious = $false
    if (Test-Path -LiteralPath $PublishedPath) {
        $publishedItem = Get-Item -LiteralPath $PublishedPath -Force
        if (($publishedItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or -not $publishedItem.PSIsContainer) { throw 'Published artifact path is not a regular directory.' }
        Move-Item -LiteralPath $PublishedPath -Destination $PreviousPath
        $movedPrevious = $true
    }

    try { Move-Item -LiteralPath $StagedPath -Destination $PublishedPath }
    catch {
        $publishError = $_
        if ($movedPrevious) {
            if (Test-Path -LiteralPath $PublishedPath) {
                throw "Artifact publication failed and rollback was blocked; backup preserved at $PreviousPath. Publish error: $publishError"
            }
            try { Move-Item -LiteralPath $PreviousPath -Destination $PublishedPath }
            catch { throw "Artifact publication and rollback failed; backup preserved at $PreviousPath. Publish error: $publishError Rollback error: $_" }
        }
        throw $publishError
    }

    if ($movedPrevious) {
        try { Remove-SafeArtifactDirectory -Path $PreviousPath }
        catch { throw "New artifacts were published, but the old artifact backup could not be removed; backup preserved at $PreviousPath. Error: $_" }
    }
}

if ($FunctionsOnly) {
    return
}

New-Item -ItemType Directory -Path $ArtifactsRoot -Force | Out-Null
Assert-SafeArtifactDirectory -Path $OutputDirectory
Assert-SafeArtifactDirectory -Path $StagingDirectory
Assert-SafeArtifactDirectory -Path $BackupDirectory
Assert-NoUntrackedModuleFiles
$sourcePaths = @(Get-TrackedSourcePaths)
Assert-SourcePathsClean -SourcePaths $sourcePaths

New-Item -ItemType Directory -Path $StagingDirectory | Out-Null
try {
    New-Item -ItemType Directory -Path $TestTempDirectory | Out-Null
    New-GitObjectSourceSnapshot -SourcePaths $sourcePaths -DestinationRoot $SnapshotDirectory
    $snapshotFingerprint = Get-SnapshotFingerprint -SnapshotRoot $SnapshotDirectory
    New-Item -ItemType Directory -Path $StagedPublishDirectory | Out-Null
    $stagedOutputPath = Join-Path $StagedPublishDirectory 'superpan-ota-admin-linux-amd64'
    $stagedOutputHashPath = Join-Path $StagedPublishDirectory 'superpan-ota-admin-linux-amd64.sha256'
    $stagedSourceBundlePath = Join-Path $StagedPublishDirectory 'superpan-ota-admin-source.zip'
    $stagedSourceBundleHashPath = Join-Path $StagedPublishDirectory 'superpan-ota-admin-source.zip.sha256'

    $env:TEMP = $TestTempDirectory
    $env:TMP = $TestTempDirectory
    $env:GOTMPDIR = $TestTempDirectory
    $env:GOTOOLCHAIN = 'local'
    $env:GOCACHE = Join-Path $StagingDirectory 'g'
    $env:GOMODCACHE = Join-Path $StagingDirectory 'm'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $BootstrapScript
    if ($LASTEXITCODE -ne 0) { throw 'Pinned Go bootstrap failed.' }
    $ExpectedArchiveSha256 = 'a3911b5e0e1b1053f25ed0675f4c1c6aad1e2bfcf253df2b9be4caabd2edd95d'
    $ExpectedGoIdentity = 'go version go1.27.1 windows/amd64'
    $env:CGO_ENABLED = '0'

    Assert-PinnedGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity
    & $GoExe -C $SnapshotDirectory mod verify
    if ($LASTEXITCODE -ne 0) { throw 'go mod verify failed.' }

    Assert-PinnedGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity
    # Windows package-level parallelism races temporary-directory security
    # probes with the two real-browser suites (including Chrome GPU caches).
    # Keep test-internal concurrency, but serialize packages so the audited
    # snapshot gate is deterministic on the production build host.
    & $GoExe -C $SnapshotDirectory test -p=1 ./...
    if ($LASTEXITCODE -ne 0) { throw 'go test failed.' }

    Assert-PinnedGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity
    & $GoExe -C $SnapshotDirectory vet ./...
    if ($LASTEXITCODE -ne 0) { throw 'go vet failed.' }

    $env:GOOS = 'linux'
    $env:GOARCH = 'amd64'
    Assert-PinnedGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity
    Assert-AcceptanceHarnessExcluded -GoExecutable $GoExe -SnapshotRoot $SnapshotDirectory
    & $GoExe -C $SnapshotDirectory build -buildvcs=false -trimpath -ldflags '-s -w' -o $stagedOutputPath ./cmd/ota-admin
    if ($LASTEXITCODE -ne 0) { throw 'static linux/amd64 build failed.' }
    if (-not (Test-Path -LiteralPath $stagedOutputPath -PathType Leaf)) { throw "Build did not create $stagedOutputPath" }

    if ((Get-SnapshotFingerprint -SnapshotRoot $SnapshotDirectory) -cne $snapshotFingerprint) { throw 'The immutable source snapshot changed during verification or build.' }
    New-DeterministicSourceBundle -SnapshotRoot $SnapshotDirectory -DestinationPath $stagedSourceBundlePath
    $hash = Write-Sha256CheckFile -PayloadPath $stagedOutputPath -CheckFilePath $stagedOutputHashPath
    $sourceHash = Write-Sha256CheckFile -PayloadPath $stagedSourceBundlePath -CheckFilePath $stagedSourceBundleHashPath

    [string[]] $stagedNames = @(Get-ChildItem -LiteralPath $StagedPublishDirectory -File | ForEach-Object Name)
    [System.Array]::Sort($stagedNames, [System.StringComparer]::Ordinal)
    [string[]] $expectedNames = @('superpan-ota-admin-linux-amd64', 'superpan-ota-admin-linux-amd64.sha256', 'superpan-ota-admin-source.zip', 'superpan-ota-admin-source.zip.sha256')
    if ([string]::Join("`n", $stagedNames) -cne [string]::Join("`n", $expectedNames)) { throw 'Staged artifact set is incomplete or contains unexpected files.' }

    Publish-StagedArtifacts -StagedPath $StagedPublishDirectory -PublishedPath $OutputDirectory -PreviousPath $BackupDirectory
}
finally {
    if (Test-Path -LiteralPath $StagingDirectory) {
        Remove-SafeArtifactDirectory -Path $StagingDirectory
    }
}

$OutputPath = Join-Path $OutputDirectory 'superpan-ota-admin-linux-amd64'
$SourceBundlePath = Join-Path $OutputDirectory 'superpan-ota-admin-source.zip'
Write-Output "Built $OutputPath"
Write-Output "SHA256 $hash"
Write-Output "Source bundle $SourceBundlePath"
Write-Output "Source SHA256 $sourceHash"
