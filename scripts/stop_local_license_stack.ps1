[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RuntimeRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.tmp\license-local'))
$ExpectedRuntimeRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.tmp\license-local'))
$ManifestPath = Join-Path $RuntimeRoot 'pid-manifest.json'
$ReadyPath = Join-Path $RuntimeRoot 'ready.json'
$OwnerPath = Join-Path $RuntimeRoot 'instance-token'
$LicenseExecutablePath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $RuntimeRoot) 'license-service-local.exe'))
$AdminExecutablePath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $RuntimeRoot) 'license-admin-local.exe'))

function Remove-LocalArtifactWithRetry {
    param([Parameter(Mandatory = $true)][string]$LiteralPath, [switch]$Recurse)
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try {
            if (Test-Path -LiteralPath $LiteralPath) { Remove-Item -LiteralPath $LiteralPath -Force -Recurse:$Recurse -ErrorAction Stop }
            return
        }
        catch [IO.IOException] { if ($attempt -eq 49) { throw }; Start-Sleep -Milliseconds 100 }
        catch [UnauthorizedAccessException] { if ($attempt -eq 49) { throw }; Start-Sleep -Milliseconds 100 }
    }
}

function Get-LocalFileSha256 {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)
    $stream = [IO.File]::Open($LiteralPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $hashAlgorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hashAlgorithm.ComputeHash($stream)).Replace('-', '')).ToLowerInvariant() }
    finally { $hashAlgorithm.Dispose(); $stream.Dispose() }
}

function Get-ExpectedExecutable {
    param([Parameter(Mandatory = $true)][string]$Role)
    if ($Role -eq 'license-service') { return $LicenseExecutablePath }
    if ($Role -eq 'license-admin-local') { return $AdminExecutablePath }
    throw 'Local PID manifest contains an unknown child role.'
}

function Stop-ValidatedManifestChild {
    param([Parameter(Mandatory = $true)]$ProcessRecord)
    $pidValue = [int]$ProcessRecord.pid
    $manifestExecutable = [IO.Path]::GetFullPath([string]$ProcessRecord.executable_path)
    $manifestCommandLine = [string]$ProcessRecord.command_line
    $manifestStartTicks = [long]$ProcessRecord.start_time_utc_ticks
    $expectedExecutable = Get-ExpectedExecutable -Role ([string]$ProcessRecord.role)
    if ($pidValue -le 0 -or $manifestStartTicks -le 0 -or [string]::IsNullOrWhiteSpace($manifestCommandLine) -or
        -not [string]::Equals($manifestExecutable, $expectedExecutable, [StringComparison]::OrdinalIgnoreCase) -or
        $manifestCommandLine.IndexOf('serve', [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $manifestCommandLine.IndexOf($RuntimeRoot, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw 'Local PID manifest child identity is invalid.'
    }
    if ([string]$ProcessRecord.role -eq 'license-admin-local' -and
        ($manifestCommandLine.IndexOf('127.0.0.1:19090', [StringComparison]::Ordinal) -lt 0 -or
         $manifestCommandLine.IndexOf('127.0.0.1:19091', [StringComparison]::Ordinal) -lt 0 -or
         $manifestCommandLine.IndexOf([string]$ProcessRecord.instance_token, [StringComparison]::Ordinal) -lt 0)) {
        throw 'Local admin manifest command identity is invalid.'
    }
    $process = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }
    $null = $process.Handle
    $cimRecord = Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId = {0}" -f $pidValue) -ErrorAction Stop
    $actualExecutable = [IO.Path]::GetFullPath([string]$process.Path)
    $actualStartTicks = [long]$process.StartTime.ToUniversalTime().Ticks
    $actualCommandLine = [string]$cimRecord.CommandLine
    if (-not [string]::Equals($actualExecutable, $manifestExecutable, [StringComparison]::OrdinalIgnoreCase) -or
        $actualStartTicks -ne $manifestStartTicks -or [string]::IsNullOrWhiteSpace($actualCommandLine) -or
        -not [string]::Equals($manifestCommandLine, $actualCommandLine, [StringComparison]::Ordinal)) {
        throw 'The recorded PID now belongs to a different process; that process was not stopped.'
    }
    try { Stop-Process -InputObject $process -Force -ErrorAction Stop }
    catch { $process.Refresh(); if (-not $process.HasExited) { throw } }
    if (-not $process.WaitForExit(10000)) { throw 'A validated local process did not stop in time.' }
}

if (-not [string]::Equals($RuntimeRoot, $ExpectedRuntimeRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Local runtime path failed its exact-root safety check.' }
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw 'Validated local PID manifest not found; no process was stopped.' }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if ([int]$manifest.schema_version -ne 2 -or $null -eq $manifest.processes -or @($manifest.processes).Count -ne 2) { throw 'Local PID manifest has an unsupported structure.' }
$manifestRoot = [IO.Path]::GetFullPath([string]$manifest.runtime_root)
$manifestToken = [string]$manifest.instance_token
$ownerToken = if (Test-Path -LiteralPath $OwnerPath -PathType Leaf) { (Get-Content -LiteralPath $OwnerPath -Raw -Encoding ascii).Trim() } else { '' }
if (-not [string]::Equals($manifestRoot, $RuntimeRoot, [StringComparison]::OrdinalIgnoreCase) -or $manifestToken -notmatch '^[0-9a-f]{32}$' -or -not [string]::Equals($ownerToken, $manifestToken, [StringComparison]::Ordinal)) { throw 'Local PID manifest failed its ownership checks.' }
$roles = @($manifest.processes | ForEach-Object { [string]$_.role } | Sort-Object -Unique)
if ($roles.Count -ne 2 -or $roles[0] -ne 'license-admin-local' -or $roles[1] -ne 'license-service') { throw 'Local PID manifest child roles are invalid.' }
$adminRecord = @($manifest.processes | Where-Object { [string]$_.role -eq 'license-admin-local' })[0]
$ready = Get-Content -LiteralPath $ReadyPath -Raw -Encoding utf8 | ConvertFrom-Json
if ([int]$ready.pid -ne [int]$adminRecord.pid -or -not [string]::Equals([string]$ready.instance_token, $manifestToken, [StringComparison]::Ordinal) -or -not [string]::Equals([string]$adminRecord.instance_token, $manifestToken, [StringComparison]::Ordinal)) { throw 'The process-owned readiness proof does not match the PID manifest.' }
foreach ($processRecord in $manifest.processes) {
    $expectedExecutable = Get-ExpectedExecutable -Role ([string]$processRecord.role)
    if (-not (Test-Path -LiteralPath $expectedExecutable -PathType Leaf) -or [string]$processRecord.executable_sha256 -notmatch '^[0-9a-f]{64}$' -or
        -not [string]::Equals((Get-LocalFileSha256 -LiteralPath $expectedExecutable), [string]$processRecord.executable_sha256, [StringComparison]::Ordinal)) {
        throw 'A recorded local executable is missing or was replaced; no process was stopped.'
    }
}

$stopFailures = New-Object Collections.Generic.List[string]
try {
    foreach ($processRecord in $manifest.processes) {
        try { Stop-ValidatedManifestChild -ProcessRecord $processRecord }
        catch { $stopFailures.Add($_.Exception.Message) }
    }
}
finally {
    if ($stopFailures.Count -eq 0) {
        foreach ($processRecord in $manifest.processes) {
            $expectedExecutable = Get-ExpectedExecutable -Role ([string]$processRecord.role)
            if (-not [string]::Equals((Get-LocalFileSha256 -LiteralPath $expectedExecutable), [string]$processRecord.executable_sha256, [StringComparison]::Ordinal)) { throw 'A recorded executable changed while stopping; no artifacts were removed.' }
        }
        Remove-LocalArtifactWithRetry -LiteralPath $RuntimeRoot -Recurse
        Remove-LocalArtifactWithRetry -LiteralPath $AdminExecutablePath
        Remove-LocalArtifactWithRetry -LiteralPath $LicenseExecutablePath
    }
}
if ($stopFailures.Count -gt 0) { throw ('One or more local children failed exact-identity stop: ' + ($stopFailures -join '; ')) }
Write-Output 'Local license acceptance stack stopped; both exact children and disposable state removed.'
