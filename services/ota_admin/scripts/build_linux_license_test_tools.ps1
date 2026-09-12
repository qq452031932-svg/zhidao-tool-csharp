[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SourceCommit,
    [Parameter(Mandatory)][string] $BundleDirectory,
    [string] $OutputDirectory = "$PSScriptRoot\..\..\.artifacts\license-test-tools",
    [switch] $FunctionsOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$TestToolsCommit,$TestToolsBundle,$TestToolsOutput,$TestToolsFunctionsOnly = $SourceCommit,$BundleDirectory,$OutputDirectory,[bool]$FunctionsOnly
. (Join-Path $PSScriptRoot 'build_linux_license_bundle.ps1') -FunctionsOnly
$SourceCommit,$BundleDirectory,$OutputDirectory,$FunctionsOnly = $TestToolsCommit,$TestToolsBundle,$TestToolsOutput,$TestToolsFunctionsOnly
$RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))

function Assert-LicenseTestToolSourceState {
    param([string]$SourceCommit,[string]$BundleDirectory)
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'SourceCommit must be one lowercase 40-character SHA.' }
    [string[]]$head = @(& git -C $RepositoryRoot rev-parse --verify 'HEAD^{commit}')
    if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $SourceCommit -cne $head[0]) { throw 'SourceCommit must exactly equal HEAD.' }
    [string[]]$status = @(& git -C $RepositoryRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) { throw 'The source worktree must be clean.' }
    $bundleCommit = Join-Path ([System.IO.Path]::GetFullPath($BundleDirectory)) 'source-commit.txt'
    if (-not (Test-Path -LiteralPath $bundleCommit -PathType Leaf) -or ([System.IO.File]::ReadAllText($bundleCommit)).Trim() -cne $SourceCommit) { throw 'Test tools and deployment bundle must use the same source commit.' }
}

function Get-LicenseTestToolSourcePaths {
    param([string]$SourceCommit)
    [string[]]$tracked = @(& git -C $RepositoryRoot ls-tree -r --name-only $SourceCommit -- services/ota_admin)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-tree failed.' }
    [string[]]$selected = @($tracked | Where-Object { $_ -ceq 'services/ota_admin/go.mod' -or $_ -ceq 'services/ota_admin/go.sum' -or ($_.StartsWith('services/ota_admin/',[System.StringComparison]::Ordinal) -and $_.EndsWith('.go',[System.StringComparison]::Ordinal) -and -not $_.EndsWith('_test.go',[System.StringComparison]::Ordinal)) })
    [System.Array]::Sort($selected,[System.StringComparer]::Ordinal)
    foreach ($required in @('services/ota_admin/cmd/license-loadtest/main.go','services/ota_admin/cmd/license-dbcheck/main.go')) { if (-not $selected.Contains($required)) { throw "Missing immutable source $required" } }
    return $selected
}

function New-LicenseTestToolSourceSnapshot {
    param([string[]]$SourcePaths,[string]$DestinationRoot,[string]$SourceCommit)
    # New-LicenseBundleSourceSnapshot materializes each selected Git blob from the explicit commit, never the worktree.
    New-LicenseBundleSourceSnapshot -SourcePaths $SourcePaths -DestinationRoot $DestinationRoot -SourceCommit $SourceCommit
}

if ($FunctionsOnly) { return }
Assert-LicenseTestToolSourceState -SourceCommit $SourceCommit -BundleDirectory $BundleDirectory
$paths = @(Get-LicenseTestToolSourcePaths -SourceCommit $SourceCommit)
$savedEnvironment = Save-LicenseBuildEnvironment
$ExpectedArchiveSha256 = 'a3911b5e0e1b1053f25ed0675f4c1c6aad1e2bfcf253df2b9be4caabd2edd95d'
$ExpectedGoIdentity = 'go version go1.27.1 windows/amd64'
$stagingRoot = Join-Path $SystemTempRoot ("sp-license-test-tools-"+[guid]::NewGuid().ToString('N').Substring(0,12))
$snapshot,$publish = (Join-Path $stagingRoot 'snapshot'),(Join-Path $stagingRoot 'publish')
try {
    Set-LicensePreBootstrapEnvironment
    Invoke-LicenseToolchainBootstrap -SourceCommit $SourceCommit
    New-Item -ItemType Directory -Path $stagingRoot | Out-Null
    New-LicenseTestToolSourceSnapshot -SourcePaths $paths -DestinationRoot $snapshot -SourceCommit $SourceCommit
    $fingerprint = Get-SnapshotFingerprint -SnapshotRoot $snapshot
    New-Item -ItemType Directory -Path $publish | Out-Null
    Set-SealedLicenseBuildEnvironment
    [System.Environment]::SetEnvironmentVariable('GOOS','linux',[System.EnvironmentVariableTarget]::Process)
    [System.Environment]::SetEnvironmentVariable('GOARCH','amd64',[System.EnvironmentVariableTarget]::Process)
    [System.Environment]::SetEnvironmentVariable('CGO_ENABLED','0',[System.EnvironmentVariableTarget]::Process)
    Assert-PinnedLicenseGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity
    & $GoExe -C $snapshot mod verify
    if ($LASTEXITCODE -ne 0) { throw 'go mod verify failed.' }
    foreach ($name in @('license-loadtest','license-dbcheck')) {
        & $GoExe -C $snapshot build -trimpath -buildvcs=false -o (Join-Path $publish $name) ("./cmd/"+$name)
        if ($LASTEXITCODE -ne 0) { throw "$name build failed." }
        $hash = (Get-FileHash -LiteralPath (Join-Path $publish $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        [System.IO.File]::WriteAllText((Join-Path $publish ($name+'.sha256')),"$hash  $name`n",[System.Text.UTF8Encoding]::new($false))
    }
    if ((Get-SnapshotFingerprint -SnapshotRoot $snapshot) -cne $fingerprint) { throw 'Immutable source snapshot changed during build.' }
    [System.IO.File]::WriteAllText((Join-Path $publish 'source-commit.txt'),"$SourceCommit`n",[System.Text.UTF8Encoding]::new($false))
    $output = [System.IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) { throw 'Output directory already exists.' }
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null
    [System.IO.Directory]::Move($publish,$output)
}
finally {
    Restore-LicenseBuildEnvironment -SavedEnvironment $savedEnvironment
    if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
}
Write-Output "Built immutable license test tools from $SourceCommit"
