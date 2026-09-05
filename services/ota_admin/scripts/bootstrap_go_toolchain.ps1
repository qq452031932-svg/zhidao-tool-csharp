[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$GoVersion = '1.27.1'
$ExpectedArchiveSha256 = 'a3911b5e0e1b1053f25ed0675f4c1c6aad1e2bfcf253df2b9be4caabd2edd95d'
$ArchiveUri = "https://go.dev/dl/go$GoVersion.windows-amd64.zip"
$RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$ToolsRoot = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.tools'))
$DownloadsRoot = Join-Path $ToolsRoot 'downloads'
$ArchivePath = Join-Path $DownloadsRoot "go$GoVersion.windows-amd64.zip"
$InstallRoot = [System.IO.Path]::GetFullPath((Join-Path $ToolsRoot "go-$GoVersion"))
$StagingRoot = [System.IO.Path]::GetFullPath((Join-Path $ToolsRoot "go-$GoVersion.extracting"))
$GoExe = Join-Path $InstallRoot 'go\bin\go.exe'

function Assert-RepositoryLocalPath {
    param([Parameter(Mandatory)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $requiredPrefix = $RepositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the repository: $fullPath"
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

Assert-RepositoryLocalPath -Path $ToolsRoot
Assert-RepositoryLocalPath -Path $ArchivePath
Assert-RepositoryLocalPath -Path $InstallRoot
Assert-RepositoryLocalPath -Path $StagingRoot

New-Item -ItemType Directory -Path $DownloadsRoot -Force | Out-Null

$archiveIsVerified = (Test-Path -LiteralPath $ArchivePath) -and ((Get-Sha256 -Path $ArchivePath) -eq $ExpectedArchiveSha256)
if (-not $archiveIsVerified) {
    if (Test-Path -LiteralPath $ArchivePath) {
        Remove-Item -LiteralPath $ArchivePath -Force
    }

    $partialPath = "$ArchivePath.partial"
    Assert-RepositoryLocalPath -Path $partialPath
    if (Test-Path -LiteralPath $partialPath) {
        Remove-Item -LiteralPath $partialPath -Force
    }

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $ArchiveUri -OutFile $partialPath -UseBasicParsing
    $actualSha256 = Get-Sha256 -Path $partialPath
    if ($actualSha256 -ne $ExpectedArchiveSha256) {
        Remove-Item -LiteralPath $partialPath -Force
        throw "Go archive SHA-256 mismatch: expected $ExpectedArchiveSha256, got $actualSha256"
    }
    Move-Item -LiteralPath $partialPath -Destination $ArchivePath
}

if ((Get-Sha256 -Path $ArchivePath) -ne $ExpectedArchiveSha256) {
    throw 'The cached Go archive failed verification immediately before extraction.'
}

# Always extract from the pinned archive. This deliberately avoids trusting an
# existing local executable or an unverifiable marker from a previous run.
if (Test-Path -LiteralPath $StagingRoot) {
    Remove-Item -LiteralPath $StagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $StagingRoot | Out-Null
Expand-Archive -LiteralPath $ArchivePath -DestinationPath $StagingRoot

$StagedGoExe = Join-Path $StagingRoot 'go\bin\go.exe'
if (-not (Test-Path -LiteralPath $StagedGoExe)) {
    throw "Extracted toolchain is missing $StagedGoExe"
}
$versionOutput = (& $StagedGoExe version) -join "`n"
if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch '^go version go1\.27\.1 windows/amd64$') {
    throw "Unexpected extracted Go toolchain identity: $versionOutput"
}

if (Test-Path -LiteralPath $InstallRoot) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
}
Move-Item -LiteralPath $StagingRoot -Destination $InstallRoot

[pscustomobject]@{
    GoExecutable = $GoExe
    Version = $versionOutput
    ArchiveSha256 = (Get-Sha256 -Path $ArchivePath)
} | ConvertTo-Json -Compress
