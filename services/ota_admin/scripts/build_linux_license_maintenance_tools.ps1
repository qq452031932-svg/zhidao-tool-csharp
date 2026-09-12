[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SourceCommit,
    [string] $OutputDirectory = "$PSScriptRoot\..\..\.artifacts\license-maintenance",
    [switch] $FunctionsOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$MaintenanceSourceCommit = $SourceCommit
$MaintenanceOutputDirectory = $OutputDirectory
$MaintenanceFunctionsOnly = [bool] $FunctionsOnly
$BundleBuilder = Join-Path $PSScriptRoot 'build_linux_license_bundle.ps1'
. $BundleBuilder -FunctionsOnly
$SourceCommit = $MaintenanceSourceCommit
$OutputDirectory = $MaintenanceOutputDirectory
$FunctionsOnly = $MaintenanceFunctionsOnly
$RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))

function Assert-LicenseMaintenanceSourceState {
    param([Parameter(Mandatory)][string] $SourceCommit)
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'SourceCommit must be one explicit lowercase 40-character Git commit SHA.' }
    [string[]] $head = @(& git -C $RepositoryRoot rev-parse --verify 'HEAD^{commit}')
    if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $head[0] -cnotmatch '^[0-9a-f]{40}$') { throw 'Unable to resolve exactly one clean HEAD commit.' }
    if ($SourceCommit -cne $head[0]) { throw "SourceCommit must exactly equal HEAD ($($head[0]))." }
    [string[]] $status = @(& git -C $RepositoryRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'git status failed while verifying maintenance source state.' }
    if ($status.Count -ne 0) { throw "The source worktree is dirty; commit or remove all changes.`n$($status -join "`n")" }
}

function Get-LicenseMaintenanceSourcePaths {
    param([Parameter(Mandatory)][string] $SourceCommit)
    $prefix = 'services/ota_admin/'
    [string[]] $tracked = @(& git -C $RepositoryRoot ls-tree -r --name-only $SourceCommit -- services/ota_admin)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-tree failed while selecting maintenance sources.' }
    [string[]] $selected = @($tracked | Where-Object {
        if (-not $_.StartsWith($prefix, [System.StringComparison]::Ordinal)) { return $false }
        $relative = $_.Substring($prefix.Length)
        return $relative -ceq 'go.mod' -or $relative -ceq 'go.sum' -or
            ($relative.StartsWith('cmd/license-db-snapshot/', [System.StringComparison]::Ordinal) -and $relative.EndsWith('.go', [System.StringComparison]::Ordinal) -and -not $relative.EndsWith('_test.go', [System.StringComparison]::Ordinal))
    })
    [System.Array]::Sort($selected, [System.StringComparer]::Ordinal)
    foreach ($required in @('services/ota_admin/go.mod', 'services/ota_admin/go.sum', 'services/ota_admin/cmd/license-db-snapshot/main.go')) {
        if (-not $selected.Contains($required)) { throw "Maintenance source selection is missing $required." }
    }
    return $selected
}

function New-LicenseMaintenanceSourceSnapshot {
    param([Parameter(Mandatory)][string[]] $SourcePaths, [Parameter(Mandatory)][string] $DestinationRoot, [Parameter(Mandatory)][string] $SourceCommit)
    # This helper uses git -C $RepositoryRoot archive ${SourceCommit}:services/ota_admin.
    New-LicenseBundleSourceSnapshot -SourcePaths $SourcePaths -DestinationRoot $DestinationRoot -SourceCommit $SourceCommit
}

function Publish-LicenseMaintenanceTools {
    param([Parameter(Mandatory)][string] $StagedPublishDirectory, [Parameter(Mandatory)][string] $OutputDirectory)
    $output = [System.IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) { throw 'Maintenance output directory already exists; refusing replacement.' }
    [System.IO.Directory]::Move($StagedPublishDirectory, $output)
}

if ($FunctionsOnly) { return }

Assert-LicenseMaintenanceSourceState -SourceCommit $SourceCommit
[string[]] $sourcePaths = @(Get-LicenseMaintenanceSourcePaths -SourceCommit $SourceCommit)
$savedEnvironment = Save-LicenseBuildEnvironment
$ExpectedArchiveSha256 = 'a3911b5e0e1b1053f25ed0675f4c1c6aad1e2bfcf253df2b9be4caabd2edd95d'
$ExpectedGoIdentity = 'go version go1.27.1 windows/amd64'
$invocation = [guid]::NewGuid().ToString('N').Substring(0, 12)
$stagingRoot = Join-Path $SystemTempRoot "sp-license-maintenance-$invocation"
$snapshot = Join-Path $stagingRoot 'snapshot'
$StagedPublishDirectory = Join-Path $stagingRoot 'publish'
try {
    Set-LicensePreBootstrapEnvironment
    Invoke-LicenseToolchainBootstrap -SourceCommit $SourceCommit
    New-Item -ItemType Directory -Path $stagingRoot | Out-Null
    New-LicenseMaintenanceSourceSnapshot -SourcePaths $sourcePaths -DestinationRoot $snapshot -SourceCommit $SourceCommit
    $snapshotFingerprint = Get-SnapshotFingerprint -SnapshotRoot $snapshot
    New-Item -ItemType Directory -Path $StagedPublishDirectory | Out-Null
    Set-SealedLicenseBuildEnvironment
	$buildTemp = Join-Path $stagingRoot 'tmp'
	New-Item -ItemType Directory -Path $buildTemp | Out-Null
	[System.Environment]::SetEnvironmentVariable('TEMP', $buildTemp, [System.EnvironmentVariableTarget]::Process)
	[System.Environment]::SetEnvironmentVariable('TMP', $buildTemp, [System.EnvironmentVariableTarget]::Process)
	[System.Environment]::SetEnvironmentVariable('GOTMPDIR', $buildTemp, [System.EnvironmentVariableTarget]::Process)
	[System.Environment]::SetEnvironmentVariable('GOCACHE', (Join-Path $stagingRoot 'gocache'), [System.EnvironmentVariableTarget]::Process)
	[System.Environment]::SetEnvironmentVariable('GOMODCACHE', (Join-Path $stagingRoot 'gomodcache'), [System.EnvironmentVariableTarget]::Process)
    [System.Environment]::SetEnvironmentVariable('GOOS', 'linux', [System.EnvironmentVariableTarget]::Process)
    [System.Environment]::SetEnvironmentVariable('GOARCH', 'amd64', [System.EnvironmentVariableTarget]::Process)
    [System.Environment]::SetEnvironmentVariable('CGO_ENABLED', '0', [System.EnvironmentVariableTarget]::Process)
    Assert-PinnedLicenseGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity
    $binary = Join-Path $StagedPublishDirectory 'license-db-snapshot'
    & $GoExe -C $snapshot mod verify
    if ($LASTEXITCODE -ne 0) { throw 'go mod verify failed for immutable maintenance snapshot.' }
    & $GoExe -C $snapshot build -trimpath -buildvcs=false -o $binary ./cmd/license-db-snapshot
    if ($LASTEXITCODE -ne 0) { throw 'maintenance tool build failed.' }
    if ((Get-SnapshotFingerprint -SnapshotRoot $snapshot) -cne $snapshotFingerprint) { throw 'The immutable maintenance source snapshot changed during build.' }
    $hash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText((Join-Path $StagedPublishDirectory 'license-db-snapshot.sha256'), "$hash  license-db-snapshot`n", [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $StagedPublishDirectory 'source-commit.txt'), "$SourceCommit`n", [System.Text.UTF8Encoding]::new($false))
	[System.IO.Directory]::CreateDirectory((Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputDirectory)))) | Out-Null
    Publish-LicenseMaintenanceTools -StagedPublishDirectory $StagedPublishDirectory -OutputDirectory $OutputDirectory
}
finally {
    Restore-LicenseBuildEnvironment -SavedEnvironment $savedEnvironment
    if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
}
Write-Output "Built immutable maintenance tools $OutputDirectory"
Write-Output "Source commit $SourceCommit"
