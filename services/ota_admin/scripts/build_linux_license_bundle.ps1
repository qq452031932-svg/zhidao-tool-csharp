[CmdletBinding()]
param(
    [string] $SourceCommit,
    [switch] $FunctionsOnly
)

$LicensePreviousErrorActionPreference = $ErrorActionPreference
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
$LicenseBuildEnvironmentNames = @(
    'GOENV'
    'GOFLAGS'
    'GOWORK'
    'GOEXPERIMENT'
    'GOAMD64'
    'GOTOOLCHAIN'
    'GOROOT'
    'GOFIPS140'
    'GODEBUG'
    'GO111MODULE'
    'GOCACHEPROG'
    'GO_EXTLINK_ENABLED'
    'GOPRIVATE'
    'GONOPROXY'
    'GONOSUMDB'
    'GOINSECURE'
    'GOAUTH'
    'GOPROXY'
    'GOSUMDB'
    'GOVCS'
    'CC'
    'CXX'
    'PKG_CONFIG'
    'TEMP'
    'TMP'
    'GOTMPDIR'
    'GOCACHE'
    'GOMODCACHE'
    'CGO_ENABLED'
    'GOOS'
    'GOARCH'
    'SOURCE_DATE_EPOCH'
)

function Save-LicenseBuildEnvironment {
    $saved = @{}
    foreach ($name in $LicenseBuildEnvironmentNames) {
        $saved[$name] = [System.Environment]::GetEnvironmentVariable($name, [System.EnvironmentVariableTarget]::Process)
    }
    return $saved
}

function Set-SealedLicenseBuildEnvironment {
    $pinnedGoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $GoExe))).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $sealed = @{
        GOENV = 'off'
        GOFLAGS = ''
        GOWORK = 'off'
        GOEXPERIMENT = ''
        GOAMD64 = 'v1'
        GOTOOLCHAIN = 'local'
        GOROOT = $pinnedGoRoot
        GOFIPS140 = 'off'
        GODEBUG = ''
        GO111MODULE = 'on'
        GOCACHEPROG = ''
        GO_EXTLINK_ENABLED = '0'
        GOPRIVATE = ''
        GONOPROXY = ''
        GONOSUMDB = ''
        GOINSECURE = ''
        GOAUTH = 'off'
        GOPROXY = 'https://proxy.golang.org,direct'
        GOSUMDB = 'sum.golang.org'
        GOVCS = 'public:git|hg,private:off'
        CC = ''
        CXX = ''
        PKG_CONFIG = ''
    }
    foreach ($name in $sealed.Keys) {
        [System.Environment]::SetEnvironmentVariable($name, [string] $sealed[$name], [System.EnvironmentVariableTarget]::Process)
    }
}

function Assert-LicenseGoRoot {
    param(
        [Parameter(Mandatory)][string] $GoExecutable,
        [Parameter(Mandatory)][string] $ExpectedGoRoot
    )

    $expectedCanonical = [System.IO.Path]::GetFullPath($ExpectedGoRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $sealedGoRoot = [System.Environment]::GetEnvironmentVariable('GOROOT', [System.EnvironmentVariableTarget]::Process)
    if ([string]::IsNullOrWhiteSpace($sealedGoRoot)) { throw 'Sealed GOROOT is missing.' }
    $sealedCanonical = [System.IO.Path]::GetFullPath($sealedGoRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if (-not $sealedCanonical.Equals($expectedCanonical, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Sealed GOROOT does not match the pinned Go executable root.'
    }
    if (-not (Test-Path -LiteralPath $expectedCanonical -PathType Container)) {
        throw 'Pinned GOROOT directory is missing after bootstrap.'
    }

    [string[]] $goRootLines = @(& $GoExecutable env GOROOT)
    if ($LASTEXITCODE -ne 0 -or $goRootLines.Count -ne 1 -or [string]::IsNullOrWhiteSpace($goRootLines[0])) {
        throw 'Pinned Go executable could not report GOROOT.'
    }
    $reportedCanonical = [System.IO.Path]::GetFullPath($goRootLines[0]).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    if (-not $reportedCanonical.Equals($expectedCanonical, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Pinned Go executable reported an unexpected GOROOT.'
    }
}

function Assert-PinnedLicenseGoToolchain {
    param(
        [Parameter(Mandatory)][string] $ExpectedSha256,
        [Parameter(Mandatory)][string] $ExpectedIdentity
    )

    Assert-PinnedGoToolchain -ExpectedSha256 $ExpectedSha256 -ExpectedIdentity $ExpectedIdentity
    $expectedGoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $GoExe))).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    Assert-LicenseGoRoot -GoExecutable $GoExe -ExpectedGoRoot $expectedGoRoot
}

function Restore-LicenseBuildEnvironment {
    param([Parameter(Mandatory)][hashtable] $SavedEnvironment)

    foreach ($name in $LicenseBuildEnvironmentNames) {
        [System.Environment]::SetEnvironmentVariable($name, $SavedEnvironment[$name], [System.EnvironmentVariableTarget]::Process)
    }
}

function Get-LicenseGitBlobObjectId {
    param(
        [Parameter(Mandatory)][byte[]] $Bytes,
        [Parameter(Mandatory)][ValidateSet('sha1', 'sha256')][string] $ObjectFormat
    )

    $header = [System.Text.Encoding]::ASCII.GetBytes("blob $($Bytes.Length)`0")
    [byte[]] $objectBytes = New-Object byte[] ($header.Length + $Bytes.Length)
    [System.Buffer]::BlockCopy($header, 0, $objectBytes, 0, $header.Length)
    [System.Buffer]::BlockCopy($Bytes, 0, $objectBytes, $header.Length, $Bytes.Length)
    $algorithm = if ($ObjectFormat -ceq 'sha1') {
        [System.Security.Cryptography.SHA1]::Create()
    }
    else {
        [System.Security.Cryptography.SHA256]::Create()
    }
    try {
        return ([System.BitConverter]::ToString($algorithm.ComputeHash($objectBytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function ConvertTo-LicenseCanonicalGitBytes {
    param([Parameter(Mandatory)][byte[]] $WorkingTreeBytes)

    $canonical = New-Object System.IO.MemoryStream
    try {
        for ($index = 0; $index -lt $WorkingTreeBytes.Length; $index++) {
            $value = $WorkingTreeBytes[$index]
            if ($value -eq 13) {
                if ($index + 1 -ge $WorkingTreeBytes.Length -or $WorkingTreeBytes[$index + 1] -ne 10) {
                    throw 'Pinned Go bootstrap contains a non-canonical carriage return.'
                }
                continue
            }
            $canonical.WriteByte($value)
        }
        return $canonical.ToArray()
    }
    finally {
        $canonical.Dispose()
    }
}

function Invoke-LicenseToolchainBootstrap {
    param(
        [Parameter(Mandatory)][string] $SourceCommit,
        [string[]] $Arguments = @()
    )

    if (-not (Test-Path -LiteralPath $LicenseBootstrapScript -PathType Leaf)) {
        throw "Pinned Go bootstrap script is missing: $LicenseBootstrapScript"
    }
    $bootstrapItem = Get-Item -LiteralPath $LicenseBootstrapScript -Force
    if (($bootstrapItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Pinned Go bootstrap script must not be a reparse point.'
    }
    $expectedBootstrapPath = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'services\ota_admin\scripts\bootstrap_go_toolchain.ps1'))
    if (-not ([System.IO.Path]::GetFullPath($LicenseBootstrapScript)).Equals($expectedBootstrapPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Pinned Go bootstrap script path is outside the expected repository location.'
    }

    [string[]] $objectFormatLines = @(& git -C $RepositoryRoot rev-parse --show-object-format)
    if ($LASTEXITCODE -ne 0 -or $objectFormatLines.Count -ne 1 -or $objectFormatLines[0] -cnotmatch '^(sha1|sha256)$') {
        throw 'Unable to resolve the repository Git object format.'
    }
    $objectFormat = $objectFormatLines[0]
    $bootstrapObjectSpec = "${SourceCommit}:services/ota_admin/scripts/bootstrap_go_toolchain.ps1"
    [string[]] $expectedObjectLines = @(& git -C $RepositoryRoot rev-parse --verify $bootstrapObjectSpec)
    $expectedObjectLength = if ($objectFormat -ceq 'sha1') { 40 } else { 64 }
    if ($LASTEXITCODE -ne 0 -or $expectedObjectLines.Count -ne 1 -or
        $expectedObjectLines[0] -cnotmatch "^[0-9a-f]{$expectedObjectLength}$") {
        throw 'Unable to resolve the committed pinned Go bootstrap blob.'
    }

    $bootstrapStream = [System.IO.File]::Open(
        $LicenseBootstrapScript,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read
    )
    try {
        if ($bootstrapStream.Length -le 0 -or $bootstrapStream.Length -gt 1048576) {
            throw 'Pinned Go bootstrap script has an invalid length.'
        }
        [byte[]] $workingTreeBytes = New-Object byte[] ([int] $bootstrapStream.Length)
        $offset = 0
        while ($offset -lt $workingTreeBytes.Length) {
            $read = $bootstrapStream.Read($workingTreeBytes, $offset, $workingTreeBytes.Length - $offset)
            if ($read -le 0) { throw 'Pinned Go bootstrap script could not be read completely.' }
            $offset += $read
        }
        [byte[]] $bootstrapBytes = ConvertTo-LicenseCanonicalGitBytes -WorkingTreeBytes $workingTreeBytes
        $actualObjectId = Get-LicenseGitBlobObjectId -Bytes $bootstrapBytes -ObjectFormat $objectFormat
        if ($actualObjectId -cne $expectedObjectLines[0]) {
            throw 'Pinned Go bootstrap script does not match its committed Git blob.'
        }

        & powershell -NoProfile -ExecutionPolicy Bypass -File $LicenseBootstrapScript @Arguments
        if ($LASTEXITCODE -ne 0) { throw 'Pinned Go bootstrap failed.' }
    }
    finally {
        $bootstrapStream.Dispose()
    }
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
    [byte[]] $manifestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`n") + "`n")
    [System.IO.File]::WriteAllBytes($ManifestPath, $manifestBytes)
    return [System.Convert]::ToBase64String($manifestBytes)
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

function Get-LicenseBytesSha256 {
    param([Parameter(Mandatory)][byte[]] $Bytes)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha256.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Write-LicenseSha256CheckFile {
    param(
        [Parameter(Mandatory)][string] $PayloadPath,
        [Parameter(Mandatory)][string] $CheckFilePath,
        [string] $ExpectedSha256
    )

    $hash = if ([string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        Get-LicenseFileSha256 -Path $PayloadPath
    }
    else {
        if ($ExpectedSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Expected SHA-256 must be lowercase hexadecimal.' }
        $ExpectedSha256
    }
    [System.IO.File]::WriteAllText($CheckFilePath, "$hash  $([System.IO.Path]::GetFileName($PayloadPath))`n", [System.Text.UTF8Encoding]::new($false))
    return $hash
}

function New-LicenseBundleExpectedState {
    param(
        [Parameter(Mandatory)][string] $OtaAdminSha256,
        [Parameter(Mandatory)][string] $LicenseServiceSha256,
        [Parameter(Mandatory)][string] $SourceManifestBase64,
        [Parameter(Mandatory)][string] $SourceManifestSha256,
        [Parameter(Mandatory)][string] $SourceCommit
    )

    foreach ($hash in @($OtaAdminSha256, $LicenseServiceSha256, $SourceManifestSha256)) {
        if ($hash -cnotmatch '^[0-9a-f]{64}$') { throw 'Expected artifact SHA-256 values must be lowercase hexadecimal.' }
    }
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'Expected source commit must be lowercase hexadecimal.' }
    try { [byte[]] $manifestBytes = [System.Convert]::FromBase64String($SourceManifestBase64) }
    catch { throw 'Expected source manifest bytes are not valid base64.' }
    if ((Get-LicenseBytesSha256 -Bytes $manifestBytes) -cne $SourceManifestSha256) {
        throw 'Expected source manifest bytes do not match their captured SHA-256.'
    }

    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $expectedText = @{
        'ota-admin.sha256' = "$OtaAdminSha256  ota-admin`n"
        'superpan-license.sha256' = "$LicenseServiceSha256  superpan-license`n"
        'source-manifest.sha256.sha256' = "$SourceManifestSha256  source-manifest.sha256`n"
        'source-commit.txt' = "$SourceCommit`n"
    }
    $contentSha256 = @{
        'ota-admin' = $OtaAdminSha256
        'superpan-license' = $LicenseServiceSha256
        'source-manifest.sha256' = $SourceManifestSha256
    }
    foreach ($name in $expectedText.Keys) {
        $contentSha256[$name] = Get-LicenseBytesSha256 -Bytes ($utf8.GetBytes($expectedText[$name]))
    }
    return [pscustomobject]@{
        Names = [string[]] @($LicenseBundleNames)
        ContentSha256 = $contentSha256
        SourceManifestBase64 = $SourceManifestBase64
        SourceCommitText = $expectedText['source-commit.txt']
    }
}

function Assert-LicenseBundleArtifacts {
    param(
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)][psobject] $ExpectedState
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw 'License bundle staging directory is missing.' }
    if (@(Get-ChildItem -LiteralPath $Directory -Directory -Force).Count -ne 0) { throw 'License bundle must not contain directories.' }
    [string[]] $actualNames = @(Get-ChildItem -LiteralPath $Directory -File -Force | ForEach-Object Name)
    [System.Array]::Sort($actualNames, [System.StringComparer]::Ordinal)
    [string[]] $expectedNames = @($ExpectedState.Names)
    [System.Array]::Sort($expectedNames, [System.StringComparer]::Ordinal)
    if ([string]::Join("`n", $actualNames) -cne [string]::Join("`n", $expectedNames)) {
        throw 'License bundle is incomplete or contains unexpected files.'
    }

    foreach ($name in $expectedNames) {
        $actualHash = Get-LicenseFileSha256 -Path (Join-Path $Directory $name)
        $expectedHash = [string] $ExpectedState.ContentSha256[$name]
        if ($expectedHash -cnotmatch '^[0-9a-f]{64}$' -or $actualHash -cne $expectedHash) {
            throw "License bundle artifact differs from captured expected bytes: $name"
        }
    }

    $commitText = [System.IO.File]::ReadAllText((Join-Path $Directory 'source-commit.txt'), [System.Text.UTF8Encoding]::new($false, $true))
    if ($commitText -cne $ExpectedState.SourceCommitText) { throw 'source-commit.txt does not contain the captured canonical source commit.' }

    [byte[]] $manifestBytes = [System.IO.File]::ReadAllBytes((Join-Path $Directory 'source-manifest.sha256'))
    if ([System.Convert]::ToBase64String($manifestBytes) -cne $ExpectedState.SourceManifestBase64) {
        throw 'Source manifest differs from the captured canonical bytes.'
    }
    $manifestText = [System.Text.UTF8Encoding]::new($false, $true).GetString($manifestBytes)
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

function Publish-VerifiedLicenseBundle {
    param(
        [Parameter(Mandatory)][string] $StagedPath,
        [Parameter(Mandatory)][string] $PublishedPath,
        [Parameter(Mandatory)][string] $PreviousPath,
        [Parameter(Mandatory)][psobject] $ExpectedState
    )

    Assert-SafeStagedPublishDirectory -Path $StagedPath
    Assert-SafeArtifactDirectory -Path $PublishedPath
    Assert-SafeArtifactDirectory -Path $PreviousPath
    Assert-SameVolumeArtifactPublish -StagedPath $StagedPath -PublishedPath $PublishedPath
    if (Test-Path -LiteralPath $PreviousPath) { throw 'License bundle backup path unexpectedly exists.' }

    $movedPrevious = $false
    if (Test-Path -LiteralPath $PublishedPath) {
        $publishedItem = Get-Item -LiteralPath $PublishedPath -Force
        if (($publishedItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or -not $publishedItem.PSIsContainer) {
            throw 'Published license bundle path is not a regular directory.'
        }
        Move-Item -LiteralPath $PublishedPath -Destination $PreviousPath
        $movedPrevious = $true
    }

    try {
        Move-Item -LiteralPath $StagedPath -Destination $PublishedPath
    }
    catch {
        $publishError = $_
        if ($movedPrevious) {
            if (Test-Path -LiteralPath $PublishedPath) {
                throw "License bundle publication failed with an unexpected live path; backup preserved at $PreviousPath. Publish error: $publishError"
            }
            try { Move-Item -LiteralPath $PreviousPath -Destination $PublishedPath }
            catch { throw "License bundle publication and rollback failed; backup preserved at $PreviousPath. Publish error: $publishError Rollback error: $_" }
        }
        throw $publishError
    }

    try {
        Assert-LicenseBundleArtifacts -Directory $PublishedPath -ExpectedState $ExpectedState
    }
    catch {
        $verificationError = $_
        $invalidRemovalError = $null
        if (Test-Path -LiteralPath $PublishedPath) {
            try { Remove-SafeArtifactDirectory -Path $PublishedPath }
            catch { $invalidRemovalError = $_ }
        }

        if ($null -ne $invalidRemovalError) {
            if (-not $movedPrevious) {
                try {
                    Move-Item -LiteralPath $PublishedPath -Destination $PreviousPath
                    throw "Published license bundle verification failed; invalid output quarantined at $PreviousPath. Verification error: $verificationError"
                }
                catch {
                    if (Test-Path -LiteralPath $PreviousPath) { throw }
                    throw "Published license bundle verification failed and invalid output could not be removed or quarantined. Verification error: $verificationError Removal error: $invalidRemovalError Quarantine error: $_"
                }
            }
            throw "Published license bundle verification failed and invalid live output could not be removed; old backup preserved at $PreviousPath. Verification error: $verificationError Removal error: $invalidRemovalError"
        }

        if ($movedPrevious) {
            try { Move-Item -LiteralPath $PreviousPath -Destination $PublishedPath }
            catch { throw "Published license bundle verification failed and rollback failed; old backup preserved at $PreviousPath. Verification error: $verificationError Rollback error: $_" }
        }
        throw $verificationError
    }

    if ($movedPrevious) {
        try { Remove-SafeArtifactDirectory -Path $PreviousPath }
        catch { throw "Verified license bundle is live, but the old backup could not be removed; backup preserved at $PreviousPath. Error: $_" }
    }
}

if ($FunctionsOnly) {
    $ErrorActionPreference = $LicensePreviousErrorActionPreference
    return
}

$savedLicenseBuildEnvironment = Save-LicenseBuildEnvironment
try {
if ([string]::IsNullOrWhiteSpace($SourceCommit)) {
    throw 'SourceCommit is required for a production license bundle build.'
}
Assert-LicenseBundleSourceState -SourceCommit $SourceCommit
[string[]] $licenseSourcePaths = @(Get-LicenseBundleSourcePaths -SourceCommit $SourceCommit)

$ExpectedArchiveSha256 = 'a3911b5e0e1b1053f25ed0675f4c1c6aad1e2bfcf253df2b9be4caabd2edd95d'
$ExpectedGoIdentity = 'go version go1.27.1 windows/amd64'
Set-SealedLicenseBuildEnvironment
Invoke-LicenseToolchainBootstrap -SourceCommit $SourceCommit
Assert-PinnedLicenseGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity

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

    Assert-PinnedLicenseGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity
    & $GoExe -C $SnapshotDirectory mod verify
    if ($LASTEXITCODE -ne 0) { throw 'go mod verify failed for the immutable license snapshot.' }

    $otaAdminPath = Join-Path $StagedPublishDirectory 'ota-admin'
    $licenseServicePath = Join-Path $StagedPublishDirectory 'superpan-license'
    Assert-PinnedLicenseGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity
    & $GoExe -C $SnapshotDirectory build -buildvcs=false -trimpath -ldflags '-s -w' -o $otaAdminPath ./cmd/ota-admin
    if ($LASTEXITCODE -ne 0) { throw 'ota-admin static linux/amd64 build failed.' }
    Assert-PinnedLicenseGoToolchain -ExpectedSha256 $ExpectedArchiveSha256 -ExpectedIdentity $ExpectedGoIdentity
    & $GoExe -C $SnapshotDirectory build -buildvcs=false -trimpath -ldflags '-s -w' -o $licenseServicePath ./cmd/license-service
    if ($LASTEXITCODE -ne 0) { throw 'superpan-license static linux/amd64 build failed.' }
    if (-not (Test-Path -LiteralPath $otaAdminPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $licenseServicePath -PathType Leaf)) {
        throw 'One or both production binaries are missing after build.'
    }
    if ((Get-SnapshotFingerprint -SnapshotRoot $SnapshotDirectory) -cne $snapshotFingerprint) {
        throw 'The immutable license source snapshot changed during the build.'
    }

    $otaAdminHash = Get-LicenseFileSha256 -Path $otaAdminPath
    $licenseServiceHash = Get-LicenseFileSha256 -Path $licenseServicePath
    $sourceManifestPath = Join-Path $StagedPublishDirectory 'source-manifest.sha256'
    $sourceManifestBase64 = Write-DeterministicSourceManifest -SnapshotRoot $SnapshotDirectory -ManifestPath $sourceManifestPath
    if ((Get-SnapshotFingerprint -SnapshotRoot $SnapshotDirectory) -cne $snapshotFingerprint) {
        throw 'The immutable license source snapshot changed during manifest capture.'
    }
    [byte[]] $sourceManifestBytes = [System.Convert]::FromBase64String($sourceManifestBase64)
    $sourceManifestHash = Get-LicenseBytesSha256 -Bytes $sourceManifestBytes
    $expectedBundleState = New-LicenseBundleExpectedState -OtaAdminSha256 $otaAdminHash -LicenseServiceSha256 $licenseServiceHash -SourceManifestBase64 $sourceManifestBase64 -SourceManifestSha256 $sourceManifestHash -SourceCommit $SourceCommit

    Write-LicenseSha256CheckFile -PayloadPath $otaAdminPath -CheckFilePath (Join-Path $StagedPublishDirectory 'ota-admin.sha256') -ExpectedSha256 $otaAdminHash | Out-Null
    Write-LicenseSha256CheckFile -PayloadPath $licenseServicePath -CheckFilePath (Join-Path $StagedPublishDirectory 'superpan-license.sha256') -ExpectedSha256 $licenseServiceHash | Out-Null
    Write-LicenseSha256CheckFile -PayloadPath $sourceManifestPath -CheckFilePath (Join-Path $StagedPublishDirectory 'source-manifest.sha256.sha256') -ExpectedSha256 $sourceManifestHash | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $StagedPublishDirectory 'source-commit.txt'), "$SourceCommit`n", [System.Text.UTF8Encoding]::new($false))

    Assert-LicenseBundleArtifacts -Directory $StagedPublishDirectory -ExpectedState $expectedBundleState
    Publish-VerifiedLicenseBundle -StagedPath $StagedPublishDirectory -PublishedPath $OutputDirectory -PreviousPath $BackupDirectory -ExpectedState $expectedBundleState
}
finally {
    if (Test-Path -LiteralPath $StagingDirectory) {
        Remove-SafeStagingDirectory -Path $StagingDirectory
    }
}
}
finally {
    Restore-LicenseBuildEnvironment -SavedEnvironment $savedLicenseBuildEnvironment
    $ErrorActionPreference = $LicensePreviousErrorActionPreference
}

Write-Output "Built immutable production license bundle $OutputDirectory"
Write-Output "Source commit $SourceCommit"
