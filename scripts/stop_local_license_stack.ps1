[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RuntimeRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.tmp\license-local'))
$ExpectedRuntimeRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.tmp\license-local'))
$ManifestPath = Join-Path $RuntimeRoot 'pid-manifest.json'
$ExpectedExecutablePath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $RuntimeRoot) 'license-admin-local.exe'))
$OwnerPath = Join-Path $RuntimeRoot 'instance-token'

function Remove-LocalArtifactWithRetry {
    param(
        [Parameter(Mandatory = $true)][string]$LiteralPath,
        [switch]$Recurse
    )

    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try {
            if (Test-Path -LiteralPath $LiteralPath) {
                Remove-Item -LiteralPath $LiteralPath -Force -Recurse:$Recurse -ErrorAction Stop
            }
            return
        }
        catch [IO.IOException] {
            if ($attempt -eq 49) { throw }
            Start-Sleep -Milliseconds 100
        }
        catch [UnauthorizedAccessException] {
            if ($attempt -eq 49) { throw }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Get-LocalFileSha256 {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    $stream = [IO.File]::Open($LiteralPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $hashAlgorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($hashAlgorithm.ComputeHash($stream)).Replace('-', '')).ToLowerInvariant()
    }
    finally {
        $hashAlgorithm.Dispose()
        $stream.Dispose()
    }
}

if (-not [string]::Equals($RuntimeRoot, $ExpectedRuntimeRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Local runtime path failed its exact-root safety check.'
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw 'Validated local PID manifest not found; no process was stopped.'
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if ([int]$manifest.schema_version -ne 1 -or $null -eq $manifest.process) {
    throw 'Local PID manifest has an unsupported structure.'
}
$pidValue = [int]$manifest.process.pid
$manifestRoot = [IO.Path]::GetFullPath([string]$manifest.runtime_root)
$manifestExecutable = [IO.Path]::GetFullPath([string]$manifest.process.executable_path)
$manifestCommandLine = [string]$manifest.process.command_line
$manifestStartTicks = [long]$manifest.process.start_time_utc_ticks
$manifestToken = [string]$manifest.process.instance_token
$manifestExecutableHash = [string]$manifest.process.executable_sha256
$ReadyPath = Join-Path $RuntimeRoot 'ready.json'
$ownerToken = if (Test-Path -LiteralPath $OwnerPath -PathType Leaf) { (Get-Content -LiteralPath $OwnerPath -Raw -Encoding ascii).Trim() } else { '' }
if ($pidValue -le 0 -or
    -not [string]::Equals($manifestRoot, $RuntimeRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals($manifestExecutable, $ExpectedExecutablePath, [StringComparison]::OrdinalIgnoreCase) -or
    $manifestStartTicks -le 0 -or $manifestToken -notmatch '^[0-9a-f]{32}$' -or
    $manifestExecutableHash -notmatch '^[0-9a-f]{64}$' -or
    $manifestCommandLine.IndexOf($RuntimeRoot, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
    $manifestCommandLine.IndexOf('127.0.0.1:19090', [StringComparison]::Ordinal) -lt 0 -or
    $manifestCommandLine.IndexOf('127.0.0.1:19091', [StringComparison]::Ordinal) -lt 0 -or
    $manifestCommandLine.IndexOf($manifestToken, [StringComparison]::Ordinal) -lt 0 -or
    -not [string]::Equals($ownerToken, $manifestToken, [StringComparison]::Ordinal)) {
    throw 'Local PID manifest failed its identity checks.'
}
$ready = Get-Content -LiteralPath $ReadyPath -Raw -Encoding utf8 | ConvertFrom-Json
if ([int]$ready.pid -ne $pidValue -or
    -not [string]::Equals([string]$ready.instance_token, $manifestToken, [StringComparison]::Ordinal)) {
    throw 'The process-owned readiness proof does not match the PID manifest.'
}
if (-not (Test-Path -LiteralPath $ExpectedExecutablePath -PathType Leaf)) {
    throw 'The recorded local executable is missing; no artifacts were removed.'
}
$actualExecutableHash = Get-LocalFileSha256 -LiteralPath $ExpectedExecutablePath
if (-not [string]::Equals($actualExecutableHash, $manifestExecutableHash, [StringComparison]::Ordinal)) {
    throw 'The recorded local executable was replaced; no artifacts were removed.'
}

$process = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
if ($null -ne $process) {
    $null = $process.Handle
    $processRecord = Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId = {0}" -f $pidValue) -ErrorAction Stop
    $actualExecutable = [IO.Path]::GetFullPath([string]$process.Path)
    $actualStartTicks = [long]$process.StartTime.ToUniversalTime().Ticks
    $actualCommandLine = [string]$processRecord.CommandLine
    if (-not [string]::Equals($actualExecutable, $manifestExecutable, [StringComparison]::OrdinalIgnoreCase) -or
        $actualStartTicks -ne $manifestStartTicks -or
        [string]::IsNullOrWhiteSpace($actualCommandLine) -or
        -not [string]::Equals($manifestCommandLine, $actualCommandLine, [StringComparison]::Ordinal)) {
        throw 'The recorded PID now belongs to a different process; nothing was stopped.'
    }
    try { Stop-Process -InputObject $process -Force -ErrorAction Stop }
    catch {
        $process.Refresh()
        if (-not $process.HasExited) { throw }
    }
    if (-not $process.WaitForExit(10000)) {
        throw 'The validated local process did not stop in time.'
    }
}

$actualExecutableHash = Get-LocalFileSha256 -LiteralPath $ExpectedExecutablePath
if (-not [string]::Equals($actualExecutableHash, $manifestExecutableHash, [StringComparison]::Ordinal)) {
    throw 'The recorded local executable changed while stopping; no artifacts were removed.'
}
Remove-LocalArtifactWithRetry -LiteralPath $ExpectedExecutablePath
Remove-LocalArtifactWithRetry -LiteralPath $RuntimeRoot -Recurse
Write-Output 'Local license acceptance stack stopped; disposable keys and databases removed.'
