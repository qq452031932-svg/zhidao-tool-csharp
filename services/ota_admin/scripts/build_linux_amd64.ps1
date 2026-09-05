[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$GoVersion = '1.27.1'
$ExpectedArchiveSha256 = 'a3911b5e0e1b1053f25ed0675f4c1c6aad1e2bfcf253df2b9be4caabd2edd95d'
$ExpectedGoIdentity = 'go version go1.27.1 windows/amd64'
$RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$ModuleRoot = Join-Path $RepositoryRoot 'services\ota_admin'
$GoArchive = Join-Path $RepositoryRoot ".tools\downloads\go$GoVersion.windows-amd64.zip"
$GoExe = Join-Path $RepositoryRoot ".tools\go-$GoVersion\go\bin\go.exe"
$ArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.artifacts'))
$OutputDirectory = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot 'ota-admin'))
$invocationId = [guid]::NewGuid().ToString('N')
$StagingDirectory = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot ".ota-admin-staging-$invocationId"))
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
    if (-not (Test-Path -LiteralPath $GoArchive -PathType Leaf)) {
        throw "Pinned Go archive is missing. Run $PSScriptRoot\bootstrap_go_toolchain.ps1 first."
    }
    $archiveSha256 = (Get-FileHash -LiteralPath $GoArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($archiveSha256 -ne $ExpectedArchiveSha256) {
        throw "Pinned Go archive SHA-256 mismatch. Run $PSScriptRoot\bootstrap_go_toolchain.ps1 again."
    }
    if (-not (Test-Path -LiteralPath $GoExe -PathType Leaf)) {
        throw "Pinned Go executable is missing. Run $PSScriptRoot\bootstrap_go_toolchain.ps1 first."
    }
    $goIdentity = (& $GoExe version) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $goIdentity -cne $ExpectedGoIdentity) {
        throw "Go executable identity mismatch; expected go version go1.27.1 windows/amd64."
    }
}

function Get-TrackedSourcePaths {
    $modulePrefix = 'services/ota_admin/'
    [string[]] $trackedPaths = @(& git -C $RepositoryRoot ls-files -- services/ota_admin)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed while selecting source bundle inputs.' }

    [string[]] $sourcePaths = @($trackedPaths | Where-Object {
        if (-not $_.StartsWith($modulePrefix, [System.StringComparison]::Ordinal)) { return $false }
        $moduleRelativePath = $_.Substring($modulePrefix.Length)
        return $moduleRelativePath -eq 'go.mod' -or
            $moduleRelativePath -eq 'go.sum' -or
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

function Assert-SourcePathsClean {
    param([Parameter(Mandatory)][string[]] $SourcePaths)

    & git -C $RepositoryRoot diff --quiet HEAD -- @SourcePaths
    if ($LASTEXITCODE -eq 1) {
        throw 'Selected OTA admin source inputs are dirty relative to HEAD; commit them before building artifacts.'
    }
    if ($LASTEXITCODE -ne 0) {
        throw 'git diff failed while verifying source bundle inputs.'
    }
}

function Write-Sha256CheckFile {
    param(
        [Parameter(Mandatory)][string] $PayloadPath,
        [Parameter(Mandatory)][string] $CheckFilePath
    )

    $hash = (Get-FileHash -LiteralPath $PayloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $line = "$hash  $([System.IO.Path]::GetFileName($PayloadPath))`n"
    [System.IO.File]::WriteAllText($CheckFilePath, $line, [System.Text.UTF8Encoding]::new($false))
    return $hash
}

function New-DeterministicSourceBundle {
    param(
        [Parameter(Mandatory)][string] $DestinationPath,
        [Parameter(Mandatory)][string[]] $SourcePaths
    )

    $modulePrefix = 'services/ota_admin/'
    Add-Type -AssemblyName System.IO.Compression
    $outputStream = [System.IO.File]::Open(
        $DestinationPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $outputStream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            foreach ($repositoryRelativePath in $SourcePaths) {
                $moduleRelativePath = $repositoryRelativePath.Substring($modulePrefix.Length)
                $entry = $archive.CreateEntry($moduleRelativePath, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $FixedZipTimestamp
                $entryStream = $entry.Open()
                $sourceStream = [System.IO.File]::OpenRead((Join-Path $RepositoryRoot $repositoryRelativePath))
                try {
                    $sourceStream.CopyTo($entryStream)
                }
                finally {
                    $sourceStream.Dispose()
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $outputStream.Dispose()
    }
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
    if (Test-Path -LiteralPath $PreviousPath) {
        throw 'Artifact backup path unexpectedly exists.'
    }

    $movedPrevious = $false
    if (Test-Path -LiteralPath $PublishedPath) {
        $publishedItem = Get-Item -LiteralPath $PublishedPath -Force
        if (($publishedItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or -not $publishedItem.PSIsContainer) {
            throw 'Published artifact path is not a regular directory.'
        }
        Move-Item -LiteralPath $PublishedPath -Destination $PreviousPath
        $movedPrevious = $true
    }

    try {
        Move-Item -LiteralPath $StagedPath -Destination $PublishedPath
    }
    catch {
        if ($movedPrevious -and -not (Test-Path -LiteralPath $PublishedPath)) {
            Move-Item -LiteralPath $PreviousPath -Destination $PublishedPath
        }
        throw
    }

    if ($movedPrevious) {
        Remove-SafeArtifactDirectory -Path $PreviousPath
    }
}

New-Item -ItemType Directory -Path $ArtifactsRoot -Force | Out-Null
Assert-SafeArtifactDirectory -Path $OutputDirectory
Assert-SafeArtifactDirectory -Path $StagingDirectory
Assert-SafeArtifactDirectory -Path $BackupDirectory
$sourcePaths = @(Get-TrackedSourcePaths)
Assert-SourcePathsClean -SourcePaths $sourcePaths
$env:GOTOOLCHAIN = 'local'

Assert-PinnedGoToolchain
& $GoExe -C $ModuleRoot mod verify
if ($LASTEXITCODE -ne 0) { throw 'go mod verify failed.' }

Assert-PinnedGoToolchain
& $GoExe -C $ModuleRoot test ./...
if ($LASTEXITCODE -ne 0) { throw 'go test failed.' }

New-Item -ItemType Directory -Path $StagingDirectory | Out-Null
$stagedOutputPath = Join-Path $StagingDirectory 'superpan-ota-admin-linux-amd64'
$stagedOutputHashPath = Join-Path $StagingDirectory 'superpan-ota-admin-linux-amd64.sha256'
$stagedSourceBundlePath = Join-Path $StagingDirectory 'superpan-ota-admin-source.zip'
$stagedSourceBundleHashPath = Join-Path $StagingDirectory 'superpan-ota-admin-source.zip.sha256'

try {
    $env:CGO_ENABLED = '0'
    $env:GOOS = 'linux'
    $env:GOARCH = 'amd64'
    Assert-PinnedGoToolchain
    & $GoExe -C $ModuleRoot build -buildvcs=false -trimpath -ldflags '-s -w' -o $stagedOutputPath ./cmd/ota-admin
    if ($LASTEXITCODE -ne 0) { throw 'static linux/amd64 build failed.' }
    if (-not (Test-Path -LiteralPath $stagedOutputPath -PathType Leaf)) {
        throw "Build did not create $stagedOutputPath"
    }

    Assert-SourcePathsClean -SourcePaths $sourcePaths
    New-DeterministicSourceBundle -DestinationPath $stagedSourceBundlePath -SourcePaths $sourcePaths
    $hash = Write-Sha256CheckFile -PayloadPath $stagedOutputPath -CheckFilePath $stagedOutputHashPath
    $sourceHash = Write-Sha256CheckFile -PayloadPath $stagedSourceBundlePath -CheckFilePath $stagedSourceBundleHashPath
    [string[]] $stagedNames = @(Get-ChildItem -LiteralPath $StagingDirectory -File | ForEach-Object Name)
    [System.Array]::Sort($stagedNames, [System.StringComparer]::Ordinal)
    [string[]] $expectedNames = @(
        'superpan-ota-admin-linux-amd64',
        'superpan-ota-admin-linux-amd64.sha256',
        'superpan-ota-admin-source.zip',
        'superpan-ota-admin-source.zip.sha256'
    )
    if ([string]::Join("`n", $stagedNames) -cne [string]::Join("`n", $expectedNames)) {
        throw 'Staged artifact set is incomplete or contains unexpected files.'
    }

    Publish-StagedArtifacts -StagedPath $StagingDirectory -PublishedPath $OutputDirectory -PreviousPath $BackupDirectory
}
finally {
    if (Test-Path -LiteralPath $StagingDirectory) {
        Remove-SafeArtifactDirectory -Path $StagingDirectory
    }
    if (Test-Path -LiteralPath $BackupDirectory) {
        Remove-SafeArtifactDirectory -Path $BackupDirectory
    }
}

$OutputPath = Join-Path $OutputDirectory 'superpan-ota-admin-linux-amd64'
$SourceBundlePath = Join-Path $OutputDirectory 'superpan-ota-admin-source.zip'
Write-Output "Built $OutputPath"
Write-Output "SHA256 $hash"
Write-Output "Source bundle $SourceBundlePath"
Write-Output "Source SHA256 $sourceHash"
