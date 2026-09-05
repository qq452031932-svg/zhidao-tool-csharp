[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$ModuleRoot = Join-Path $RepositoryRoot 'services\ota_admin'
$GoExe = Join-Path $RepositoryRoot '.tools\go-1.27.1\go\bin\go.exe'
$OutputDirectory = Join-Path $RepositoryRoot '.artifacts\ota-admin'
$OutputPath = Join-Path $OutputDirectory 'superpan-ota-admin-linux-amd64'
$OutputHashPath = Join-Path $OutputDirectory 'superpan-ota-admin-linux-amd64.sha256'
$SourceBundlePath = Join-Path $OutputDirectory 'superpan-ota-admin-source.zip'
$SourceBundleHashPath = Join-Path $OutputDirectory 'superpan-ota-admin-source.zip.sha256'
$FixedZipTimestamp = [System.DateTimeOffset]::Parse('1980-01-01T00:00:00Z')

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
    param([Parameter(Mandatory)][string] $DestinationPath)

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
            $moduleRelativePath.EndsWith('.service', [System.StringComparison]::Ordinal)
    })
    [System.Array]::Sort($sourcePaths, [System.StringComparer]::Ordinal)
    if ($sourcePaths.Count -eq 0 -or
        -not $sourcePaths.Contains('services/ota_admin/go.mod') -or
        -not $sourcePaths.Contains('services/ota_admin/go.sum')) {
        throw 'Tracked Go source selection is incomplete.'
    }

    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }
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
            foreach ($repositoryRelativePath in $sourcePaths) {
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

if (-not (Test-Path -LiteralPath $GoExe)) {
    throw "Pinned Go toolchain is missing. Run $PSScriptRoot\bootstrap_go_toolchain.ps1 first."
}

& $GoExe -C $ModuleRoot mod verify
if ($LASTEXITCODE -ne 0) { throw 'go mod verify failed.' }

& $GoExe -C $ModuleRoot test ./...
if ($LASTEXITCODE -ne 0) { throw 'go test failed.' }

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$env:CGO_ENABLED = '0'
$env:GOOS = 'linux'
$env:GOARCH = 'amd64'
& $GoExe -C $ModuleRoot build -trimpath -ldflags '-s -w' -o $OutputPath ./cmd/ota-admin
if ($LASTEXITCODE -ne 0) { throw 'static linux/amd64 build failed.' }
if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
    throw "Build did not create $OutputPath"
}

New-DeterministicSourceBundle -DestinationPath $SourceBundlePath
$hash = Write-Sha256CheckFile -PayloadPath $OutputPath -CheckFilePath $OutputHashPath
$sourceHash = Write-Sha256CheckFile -PayloadPath $SourceBundlePath -CheckFilePath $SourceBundleHashPath
Write-Output "Built $OutputPath"
Write-Output "SHA256 $hash"
Write-Output "Source bundle $SourceBundlePath"
Write-Output "Source SHA256 $sourceHash"
