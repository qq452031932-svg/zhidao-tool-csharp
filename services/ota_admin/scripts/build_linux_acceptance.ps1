[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'build_linux_amd64.ps1') -FunctionsOnly

$AcceptanceOutputDirectory = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot 'ota-admin-acceptance'))
$acceptanceInvocationId = [guid]::NewGuid().ToString('N')
$AcceptanceStagingDirectory = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot ".ota-admin-acceptance-staging-$acceptanceInvocationId"))
$AcceptanceSnapshotDirectory = Join-Path $AcceptanceStagingDirectory 'snapshot'
$AcceptancePublishDirectory = Join-Path $AcceptanceStagingDirectory 'publish'
$AcceptanceBackupDirectory = [System.IO.Path]::GetFullPath((Join-Path $ArtifactsRoot ".ota-admin-acceptance-backup-$acceptanceInvocationId"))

New-Item -ItemType Directory -Path $ArtifactsRoot -Force | Out-Null
Assert-SafeArtifactDirectory -Path $AcceptanceOutputDirectory
Assert-SafeArtifactDirectory -Path $AcceptanceStagingDirectory
Assert-SafeArtifactDirectory -Path $AcceptanceBackupDirectory
Assert-NoUntrackedModuleFiles

function Get-AcceptanceSourcePaths {
    [string[]] $paths = @(Get-TrackedSourcePaths)
    $fixtures = [ordered]@{
        'services/ota_admin/cmd/ota-admin-acceptance/testdata/valid-upload.zip' = @('e79d1fa48360d28fb662fb56f57435e144f3784b', '30b96617dfb888b5ce6daf0e8d3b7bd2b1a94c09d37021919d0b313720148916')
        'services/ota_admin/cmd/ota-admin-acceptance/testdata/publisher.cer' = @('96f6d3166172998f62621f93a1f12932b245a1a9', 'eed6657913e5bf86caac9b8f23928648b83a50b9b06f1e2c738cbb2a13dbc2d5')
    }
    foreach ($path in $fixtures.Keys) {
        $blob = (& git -C $RepositoryRoot rev-parse "HEAD:$path") -join "`n"
        if ($LASTEXITCODE -ne 0 -or $blob -cne $fixtures[$path][0]) { throw "Acceptance fixture Git blob mismatch: $path" }
        if (-not $paths.Contains($path)) { $paths += $path }
    }
    [System.Array]::Sort($paths, [System.StringComparer]::Ordinal)
    return $paths
}

$acceptanceSourcePaths = @(Get-AcceptanceSourcePaths)
Assert-SourcePathsClean -SourcePaths $acceptanceSourcePaths

New-Item -ItemType Directory -Path $AcceptanceStagingDirectory | Out-Null
try {
    New-GitObjectSourceSnapshot -SourcePaths $acceptanceSourcePaths -DestinationRoot $AcceptanceSnapshotDirectory
    $snapshotFixtures = [ordered]@{
        'cmd/ota-admin-acceptance/testdata/valid-upload.zip' = '30b96617dfb888b5ce6daf0e8d3b7bd2b1a94c09d37021919d0b313720148916'
        'cmd/ota-admin-acceptance/testdata/publisher.cer' = 'eed6657913e5bf86caac9b8f23928648b83a50b9b06f1e2c738cbb2a13dbc2d5'
    }
    foreach ($relativePath in $snapshotFixtures.Keys) {
        $fixturePath = Join-Path $AcceptanceSnapshotDirectory $relativePath
        if (-not (Test-Path -LiteralPath $fixturePath -PathType Leaf)) { throw "Acceptance fixture missing from snapshot: $relativePath" }
        $fixtureHash = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($fixtureHash -cne $snapshotFixtures[$relativePath]) { throw "Acceptance fixture SHA-256 mismatch: $relativePath" }
    }
    $acceptanceSnapshotFingerprint = Get-SnapshotFingerprint -SnapshotRoot $AcceptanceSnapshotDirectory
    New-Item -ItemType Directory -Path $AcceptancePublishDirectory | Out-Null
    $stagedAcceptancePath = Join-Path $AcceptancePublishDirectory 'superpan-ota-admin-acceptance-linux-amd64'
    $stagedAcceptanceHashPath = Join-Path $AcceptancePublishDirectory 'superpan-ota-admin-acceptance-linux-amd64.sha256'

    $env:GOTOOLCHAIN = 'local'
    $env:GOCACHE = Join-Path $AcceptanceStagingDirectory 'go-build-cache'
    $env:GOMODCACHE = Join-Path $AcceptanceStagingDirectory 'go-module-cache'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $BootstrapScript
    if ($LASTEXITCODE -ne 0) { throw 'Pinned Go bootstrap failed.' }
    $ExpectedArchiveSha256 = 'a3911b5e0e1b1053f25ed0675f4c1c6aad1e2bfcf253df2b9be4caabd2edd95d'
    $ExpectedGoIdentity = 'go version go1.27.1 windows/amd64'
    $env:CGO_ENABLED = '0'
    $env:GOOS = 'linux'
    $env:GOARCH = 'amd64'

    Assert-PinnedGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity
    & $GoExe -C $AcceptanceSnapshotDirectory mod verify
    if ($LASTEXITCODE -ne 0) { throw 'go mod verify failed.' }

    [string[]] $acceptancePackages = @(& $GoExe -C $AcceptanceSnapshotDirectory list -tags 'ota_admin_acceptance' -f '{{.ImportPath}}' ./cmd/ota-admin-acceptance)
    if ($LASTEXITCODE -ne 0 -or $acceptancePackages.Count -ne 1 -or $acceptancePackages[0] -cne 'fanfh.cn/superpan/ota-admin/cmd/ota-admin-acceptance') {
        throw 'Acceptance harness opt-in build graph is invalid.'
    }

    & $GoExe -C $AcceptanceSnapshotDirectory build -buildvcs=false -trimpath -tags 'ota_admin_acceptance' -ldflags '-s -w' -o $stagedAcceptancePath ./cmd/ota-admin-acceptance
    if ($LASTEXITCODE -ne 0) { throw 'acceptance-only linux/amd64 build failed.' }
    if (-not (Test-Path -LiteralPath $stagedAcceptancePath -PathType Leaf)) { throw "Build did not create $stagedAcceptancePath" }
    if ((Get-SnapshotFingerprint -SnapshotRoot $AcceptanceSnapshotDirectory) -cne $acceptanceSnapshotFingerprint) { throw 'The immutable source snapshot changed during the acceptance build.' }

    $acceptanceHash = Write-Sha256CheckFile -PayloadPath $stagedAcceptancePath -CheckFilePath $stagedAcceptanceHashPath
    [string[]] $stagedNames = @(Get-ChildItem -LiteralPath $AcceptancePublishDirectory -File | ForEach-Object Name)
    [System.Array]::Sort($stagedNames, [System.StringComparer]::Ordinal)
    [string[]] $expectedNames = @('superpan-ota-admin-acceptance-linux-amd64', 'superpan-ota-admin-acceptance-linux-amd64.sha256')
    if ([string]::Join("`n", $stagedNames) -cne [string]::Join("`n", $expectedNames)) { throw 'Acceptance artifact set is incomplete or contains unexpected files.' }

    Publish-StagedArtifacts -StagedPath $AcceptancePublishDirectory -PublishedPath $AcceptanceOutputDirectory -PreviousPath $AcceptanceBackupDirectory
}
finally {
    if (Test-Path -LiteralPath $AcceptanceStagingDirectory) {
        Remove-SafeArtifactDirectory -Path $AcceptanceStagingDirectory
    }
}

$AcceptanceOutputPath = Join-Path $AcceptanceOutputDirectory 'superpan-ota-admin-acceptance-linux-amd64'
Write-Output "Built acceptance-only harness $AcceptanceOutputPath"
Write-Output "SHA256 $acceptanceHash"
