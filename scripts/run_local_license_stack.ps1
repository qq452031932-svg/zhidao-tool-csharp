[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RuntimeRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.tmp\license-local'))
$ExpectedRuntimeRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.tmp\license-local'))
$ManifestPath = Join-Path $RuntimeRoot 'pid-manifest.json'
$ReadyPath = Join-Path $RuntimeRoot 'ready.json'
$ClientPath = Join-Path $RuntimeRoot 'client.json'
$ExecutablePath = Join-Path (Split-Path -Parent $RuntimeRoot) 'license-admin-local.exe'
$OwnerPath = Join-Path $RuntimeRoot 'instance-token'
$GoExecutable = Join-Path $RepositoryRoot '.tools\go-1.27.1\go\bin\go.exe'
$AdminAddress = '127.0.0.1:19090'
$LicenseAddress = '127.0.0.1:19091'
$startedProcess = $null
$startedProcessStartTicks = 0L
$completed = $false
$ownsRuntime = $false
$builtExecutable = $false
$startupMutex = $null
$startupMutexAcquired = $false
$tokenBytes = New-Object byte[] 16
$tokenGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $tokenGenerator.GetBytes($tokenBytes) }
finally { $tokenGenerator.Dispose() }
$InstanceToken = ([BitConverter]::ToString($tokenBytes).Replace('-', '')).ToLowerInvariant()

function Confirm-ExactRuntimeRoot {
    if (-not [string]::Equals($RuntimeRoot, $ExpectedRuntimeRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Local runtime path failed its exact-root safety check.'
    }
}

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

function Write-TextFileAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$LiteralPath,
        [Parameter(Mandatory = $true)][string]$Text
    )

    $temporaryPath = $LiteralPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $stream = $null
    try {
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($Text)
        $stream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        $stream.Dispose()
        $stream = $null
        [IO.File]::Move($temporaryPath, $LiteralPath)
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
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

try {
    Confirm-ExactRuntimeRoot
    [IO.Directory]::CreateDirectory((Split-Path -Parent $RuntimeRoot)) | Out-Null
    $startupMutex = New-Object Threading.Mutex($false, 'Local\SuperPanLicenseLocalAcceptance')
    $startupMutexAcquired = $startupMutex.WaitOne(0)
    if (-not $startupMutexAcquired) {
        throw 'Another local acceptance stack is starting.'
    }
    if (Test-Path -LiteralPath $RuntimeRoot) {
        throw 'The local runtime directory already exists; no data was overwritten.'
    }
    if (Test-Path -LiteralPath $ExecutablePath) {
        throw 'The local acceptance executable already exists; no file was overwritten.'
    }
    if (-not (Test-Path -LiteralPath $GoExecutable -PathType Leaf)) {
        throw 'The bundled Go toolchain is unavailable.'
    }

    Push-Location (Join-Path $RepositoryRoot 'services\ota_admin')
    try {
        & $GoExecutable build -tags license_local_acceptance -o $ExecutablePath ./cmd/license-admin-local
        if ($LASTEXITCODE -ne 0) { throw 'Building the local acceptance host failed.' }
        $builtExecutable = $true
    }
    finally {
        Pop-Location
    }
    $executableHash = Get-LocalFileSha256 -LiteralPath $ExecutablePath

    if ($RuntimeRoot.IndexOf('"', [StringComparison]::Ordinal) -ge 0) {
        throw 'The local runtime path contains an unsupported quote character.'
    }
    $QuotedRuntimeRoot = '"' + $RuntimeRoot + '"'
    $arguments = @(
        '--root', $QuotedRuntimeRoot,
        '--admin-listen', $AdminAddress,
        '--license-listen', $LicenseAddress,
        '--instance-token', $InstanceToken
    )
    $startedProcess = Start-Process -FilePath $ExecutablePath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $null = $startedProcess.Handle
    $startedProcessStartTicks = [long]$startedProcess.StartTime.ToUniversalTime().Ticks

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($startedProcess.HasExited) { throw 'The local acceptance host exited during startup.' }
        if ((Test-Path -LiteralPath $ReadyPath) -and (Test-Path -LiteralPath $ClientPath) -and (Test-Path -LiteralPath $OwnerPath)) { break }
        Start-Sleep -Milliseconds 100
        $startedProcess.Refresh()
    }
    if (-not (Test-Path -LiteralPath $ReadyPath) -or -not (Test-Path -LiteralPath $ClientPath) -or -not (Test-Path -LiteralPath $OwnerPath)) {
        throw 'The local acceptance host did not become ready in time.'
    }

    $ready = Get-Content -LiteralPath $ReadyPath -Raw -Encoding utf8 | ConvertFrom-Json
    $ownsRuntime = [string]::Equals((Get-Content -LiteralPath $OwnerPath -Raw -Encoding ascii).Trim(), $InstanceToken, [StringComparison]::Ordinal)
    $live = Get-Process -Id $startedProcess.Id -ErrorAction Stop
    $null = $live.Handle
    $processRecord = Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId = {0}" -f $startedProcess.Id) -ErrorAction Stop
    $actualCommandLine = [string]$processRecord.CommandLine
    if ([int]$ready.pid -ne [int]$startedProcess.Id -or
        -not [string]::Equals([string]$ready.instance_token, $InstanceToken, [StringComparison]::Ordinal) -or
        -not $ownsRuntime -or
        -not [string]::Equals([IO.Path]::GetFullPath([string]$live.Path), [IO.Path]::GetFullPath($ExecutablePath), [StringComparison]::OrdinalIgnoreCase) -or
        [string]::IsNullOrWhiteSpace($actualCommandLine) -or
        $actualCommandLine.IndexOf($RuntimeRoot, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $actualCommandLine.IndexOf($AdminAddress, [StringComparison]::Ordinal) -lt 0 -or
        $actualCommandLine.IndexOf($LicenseAddress, [StringComparison]::Ordinal) -lt 0 -or
        $actualCommandLine.IndexOf($InstanceToken, [StringComparison]::Ordinal) -lt 0) {
        throw 'The started process did not match the expected executable and command line.'
    }

    $manifest = [ordered]@{
        schema_version = 1
        runtime_root = $RuntimeRoot
        process = [ordered]@{
            pid = [int]$startedProcess.Id
            executable_path = [IO.Path]::GetFullPath([string]$live.Path)
            executable_sha256 = $executableHash
            start_time_utc_ticks = [long]$live.StartTime.ToUniversalTime().Ticks
            command_line = $actualCommandLine
            instance_token = $InstanceToken
        }
    }
    Write-TextFileAtomic -LiteralPath $ManifestPath -Text (($manifest | ConvertTo-Json -Depth 4) + "`n")

    Invoke-RestMethod -Method Get -Uri "http://$LicenseAddress/health" -TimeoutSec 5 | Out-Null
    Invoke-RestMethod -Method Get -Uri "http://$AdminAddress/admin/health" -TimeoutSec 5 | Out-Null
    $completed = $true
    Write-Output "Local license admin: http://$AdminAddress/admin/"
    Write-Output "One-time admin password file: $(Join-Path $RuntimeRoot 'admin-password.txt')"
    Write-Output "Acceptance client settings: $ClientPath"
}
finally {
    try {
        if (-not $completed) {
            Confirm-ExactRuntimeRoot
            if (-not $ownsRuntime -and (Test-Path -LiteralPath $OwnerPath -PathType Leaf)) {
                $ownsRuntime = [string]::Equals((Get-Content -LiteralPath $OwnerPath -Raw -Encoding ascii).Trim(), $InstanceToken, [StringComparison]::Ordinal)
            }
            $cleanupAllowed = $true
            if ($null -ne $startedProcess) {
                $startedProcess.Refresh()
                if ($startedProcess.HasExited) {
                    if (-not $startedProcess.WaitForExit(10000)) { $cleanupAllowed = $false }
                }
                else {
                    try {
                        $failureProcessRecord = Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId = {0}" -f $startedProcess.Id) -ErrorAction Stop
                        $failureCommandLine = [string]$failureProcessRecord.CommandLine
                        $failureStartTicks = [long]$startedProcess.StartTime.ToUniversalTime().Ticks
                        $failureIdentityMatches =
                            [string]::Equals([IO.Path]::GetFullPath([string]$startedProcess.Path), [IO.Path]::GetFullPath($ExecutablePath), [StringComparison]::OrdinalIgnoreCase) -and
                            $failureStartTicks -eq $startedProcessStartTicks -and
                            -not [string]::IsNullOrWhiteSpace($failureCommandLine) -and
                            $failureCommandLine.IndexOf($RuntimeRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                            $failureCommandLine.IndexOf($AdminAddress, [StringComparison]::Ordinal) -ge 0 -and
                            $failureCommandLine.IndexOf($LicenseAddress, [StringComparison]::Ordinal) -ge 0 -and
                            $failureCommandLine.IndexOf($InstanceToken, [StringComparison]::Ordinal) -ge 0
                        if ($failureIdentityMatches) {
                            try { Stop-Process -InputObject $startedProcess -Force -ErrorAction Stop }
                            catch {
                                $startedProcess.Refresh()
                                if (-not $startedProcess.HasExited) { throw }
                            }
                            if (-not $startedProcess.WaitForExit(10000)) { $cleanupAllowed = $false }
                        }
                        else {
                            $cleanupAllowed = $false
                        }
                    }
                    catch {
                        $startedProcess.Refresh()
                        if (-not $startedProcess.HasExited) { $cleanupAllowed = $false }
                    }
                }
            }
            if (-not $cleanupAllowed) {
                throw 'Startup cleanup refused because the child process identity could not be verified.'
            }
            $cleanupFailures = New-Object Collections.Generic.List[string]
            if ($ownsRuntime -and (Test-Path -LiteralPath $RuntimeRoot)) {
                try { Remove-LocalArtifactWithRetry -LiteralPath $RuntimeRoot -Recurse }
                catch { $cleanupFailures.Add($_.Exception.Message) }
            }
            if ($builtExecutable -and (Test-Path -LiteralPath $ExecutablePath)) {
                try { Remove-LocalArtifactWithRetry -LiteralPath $ExecutablePath }
                catch { $cleanupFailures.Add($_.Exception.Message) }
            }
            if ($cleanupFailures.Count -gt 0) {
                throw ('Startup cleanup failed: ' + ($cleanupFailures -join '; '))
            }
        }
    }
    finally {
        if ($startupMutexAcquired) { $startupMutex.ReleaseMutex() }
        if ($null -ne $startupMutex) { $startupMutex.Dispose() }
    }
}
