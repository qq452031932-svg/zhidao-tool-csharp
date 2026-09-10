[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RuntimeRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.tmp\license-local'))
$ExpectedRuntimeRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.tmp\license-local'))
$ManifestPath = Join-Path $RuntimeRoot 'pid-manifest.json'
$ReadyPath = Join-Path $RuntimeRoot 'ready.json'
$ClientPath = Join-Path $RuntimeRoot 'client.json'
$LicenseConfigPath = Join-Path $RuntimeRoot 'license-service.json'
$OwnerPath = Join-Path $RuntimeRoot 'instance-token'
$LicenseExecutablePath = Join-Path (Split-Path -Parent $RuntimeRoot) 'license-service-local.exe'
$AdminExecutablePath = Join-Path (Split-Path -Parent $RuntimeRoot) 'license-admin-local.exe'
$GoExecutable = Join-Path $RepositoryRoot '.tools\go-1.27.1\go\bin\go.exe'
$AdminAddress = '127.0.0.1:19090'
$LicenseAddress = '127.0.0.1:19091'
$LicenseBaseUrl = "http://$LicenseAddress"
$startedChildren = New-Object Collections.Generic.List[object]
$completed = $false
$ownsRuntime = $false
$startupMutex = $null
$startupMutexAcquired = $false
$tokenBytes = New-Object byte[] 16
$tokenGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $tokenGenerator.GetBytes($tokenBytes) }
finally { $tokenGenerator.Dispose() }
$InstanceToken = ([BitConverter]::ToString($tokenBytes).Replace('-', '')).ToLowerInvariant()

function Confirm-ExactRuntimeRoot {
    if (-not [string]::Equals($RuntimeRoot, $ExpectedRuntimeRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Local runtime path failed its exact-root safety check.' }
}

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

function Write-TextFileAtomic {
    param([Parameter(Mandatory = $true)][string]$LiteralPath, [Parameter(Mandatory = $true)][string]$Text)
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
    try { return ([BitConverter]::ToString($hashAlgorithm.ComputeHash($stream)).Replace('-', '')).ToLowerInvariant() }
    finally { $hashAlgorithm.Dispose(); $stream.Dispose() }
}

function Get-ExactChildIdentity {
    param([Parameter(Mandatory = $true)][Diagnostics.Process]$Process, [Parameter(Mandatory = $true)][string]$ExpectedExecutable, [Parameter(Mandatory = $true)][string[]]$RequiredCommandFragments)
    $Process.Refresh()
    if ($Process.HasExited) { throw 'A local acceptance child exited before identity capture.' }
    $null = $Process.Handle
    $record = Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId = {0}" -f $Process.Id) -ErrorAction Stop
    $actualExecutable = [IO.Path]::GetFullPath([string]$Process.Path)
    $commandLine = [string]$record.CommandLine
    if (-not [string]::Equals($actualExecutable, [IO.Path]::GetFullPath($ExpectedExecutable), [StringComparison]::OrdinalIgnoreCase) -or [string]::IsNullOrWhiteSpace($commandLine)) { throw 'A local acceptance child executable identity did not match.' }
    foreach ($fragment in $RequiredCommandFragments) {
        if ($commandLine.IndexOf($fragment, [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw 'A local acceptance child command line did not match.' }
    }
    return [ordered]@{ pid = [int]$Process.Id; executable_path = $actualExecutable; executable_sha256 = Get-LocalFileSha256 -LiteralPath $actualExecutable; start_time_utc_ticks = [long]$Process.StartTime.ToUniversalTime().Ticks; command_line = $commandLine }
}

function Stop-StartedChildSafely {
    param([Parameter(Mandatory = $true)]$Child)
    $process = $Child.Process
    $process.Refresh()
    if ($process.HasExited) { $null = $process.WaitForExit(10000); return }
    $identity = Get-ExactChildIdentity -Process $process -ExpectedExecutable $Child.Executable -RequiredCommandFragments $Child.Fragments
    if ([long]$identity.start_time_utc_ticks -ne [long]$Child.StartTicks) { throw 'Startup cleanup refused a reused PID.' }
    try { Stop-Process -InputObject $process -Force -ErrorAction Stop }
    catch { $process.Refresh(); if (-not $process.HasExited) { throw } }
    if (-not $process.WaitForExit(10000)) { throw 'A local acceptance child did not stop in time.' }
}

try {
    Confirm-ExactRuntimeRoot
    [IO.Directory]::CreateDirectory((Split-Path -Parent $RuntimeRoot)) | Out-Null
    $startupMutex = New-Object Threading.Mutex($false, 'Local\SuperPanLicenseLocalAcceptance')
    $startupMutexAcquired = $startupMutex.WaitOne(0)
    if (-not $startupMutexAcquired) { throw 'Another local acceptance stack is starting.' }
    foreach ($path in @($RuntimeRoot, $LicenseExecutablePath, $AdminExecutablePath)) {
        if (Test-Path -LiteralPath $path) { throw 'Local acceptance artifacts already exist; no data was overwritten.' }
    }
    if (-not (Test-Path -LiteralPath $GoExecutable -PathType Leaf)) { throw 'The bundled Go toolchain is unavailable.' }

    Push-Location (Join-Path $RepositoryRoot 'services\ota_admin')
    try {
        & $GoExecutable build -o $LicenseExecutablePath ./cmd/license-service
        if ($LASTEXITCODE -ne 0) { throw 'Building the license service failed.' }
        & $GoExecutable build -tags license_local_acceptance -o $AdminExecutablePath ./cmd/license-admin-local
        if ($LASTEXITCODE -ne 0) { throw 'Building the tagged local admin failed.' }
    }
    finally { Pop-Location }

    & $AdminExecutablePath prepare --root $RuntimeRoot --admin-listen $AdminAddress --license-listen $LicenseAddress | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $LicenseConfigPath -PathType Leaf)) { throw 'Preparing local license state failed.' }
    $ownsRuntime = $true

    $quotedConfig = '"' + $LicenseConfigPath + '"'
    $licenseProcess = Start-Process -FilePath $LicenseExecutablePath -ArgumentList @('serve', '--config', $quotedConfig) -WindowStyle Hidden -PassThru
    $null = $licenseProcess.Handle
    $licenseChild = [pscustomobject]@{ Role = 'license-service'; Process = $licenseProcess; Executable = $LicenseExecutablePath; StartTicks = [long]$licenseProcess.StartTime.ToUniversalTime().Ticks; Fragments = @('serve', $LicenseConfigPath) }
    $startedChildren.Add($licenseChild)

    $secret = [IO.File]::ReadAllBytes((Join-Path $RuntimeRoot 'internal-admin.key'))
    try { $internalToken = ([Convert]::ToBase64String($secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')) }
    finally { [Array]::Clear($secret, 0, $secret.Length) }
    $licenseHeaders = @{ Authorization = "Bearer $internalToken" }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $licenseReady = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $licenseProcess.Refresh()
        if ($licenseProcess.HasExited) { throw 'The license service exited during startup.' }
        try {
            Invoke-RestMethod -Method Get -Uri "$LicenseBaseUrl/internal/v1/software?page=1&page_size=1" -Headers $licenseHeaders -TimeoutSec 2 | Out-Null
            $licenseReady = $true
            break
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    $internalToken = $null
    $licenseHeaders = $null
    if (-not $licenseReady) { throw 'The license service did not become ready in time.' }

    $quotedRoot = '"' + $RuntimeRoot + '"'
    $adminArguments = @('serve', '--root', $quotedRoot, '--admin-listen', $AdminAddress, '--license-base-url', $LicenseBaseUrl, '--instance-token', $InstanceToken)
    $adminProcess = Start-Process -FilePath $AdminExecutablePath -ArgumentList $adminArguments -WindowStyle Hidden -PassThru
    $null = $adminProcess.Handle
    $adminChild = [pscustomobject]@{ Role = 'license-admin-local'; Process = $adminProcess; Executable = $AdminExecutablePath; StartTicks = [long]$adminProcess.StartTime.ToUniversalTime().Ticks; Fragments = @('serve', $RuntimeRoot, $AdminAddress, $LicenseBaseUrl, $InstanceToken) }
    $startedChildren.Add($adminChild)

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        $adminProcess.Refresh()
        if ($adminProcess.HasExited) { throw 'The local admin exited during startup.' }
        if ((Test-Path -LiteralPath $ReadyPath) -and (Test-Path -LiteralPath $ClientPath) -and (Test-Path -LiteralPath $OwnerPath)) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $ReadyPath) -or -not (Test-Path -LiteralPath $OwnerPath)) { throw 'The local admin did not become ready in time.' }
    $ready = Get-Content -LiteralPath $ReadyPath -Raw -Encoding utf8 | ConvertFrom-Json
    $ownerToken = (Get-Content -LiteralPath $OwnerPath -Raw -Encoding ascii).Trim()
    if ([int]$ready.pid -ne [int]$adminProcess.Id -or -not [string]::Equals([string]$ready.instance_token, $InstanceToken, [StringComparison]::Ordinal) -or -not [string]::Equals($ownerToken, $InstanceToken, [StringComparison]::Ordinal)) { throw 'The local admin readiness identity did not match.' }

    $licenseIdentity = Get-ExactChildIdentity -Process $licenseProcess -ExpectedExecutable $LicenseExecutablePath -RequiredCommandFragments $licenseChild.Fragments
    $licenseIdentity.role = 'license-service'
    $adminIdentity = Get-ExactChildIdentity -Process $adminProcess -ExpectedExecutable $AdminExecutablePath -RequiredCommandFragments $adminChild.Fragments
    $adminIdentity.role = 'license-admin-local'
    $adminIdentity.instance_token = $InstanceToken
    $manifest = [ordered]@{ schema_version = 2; runtime_root = $RuntimeRoot; instance_token = $InstanceToken; processes = @($licenseIdentity, $adminIdentity) }
    Write-TextFileAtomic -LiteralPath $ManifestPath -Text (($manifest | ConvertTo-Json -Depth 5) + "`n")

    Invoke-RestMethod -Method Get -Uri "http://$AdminAddress/admin/health" -TimeoutSec 5 | Out-Null
    $completed = $true
    Write-Output "Local license admin: http://$AdminAddress/admin/"
    Write-Output "One-time admin password file: $(Join-Path $RuntimeRoot 'admin-password.txt')"
    Write-Output "Acceptance client settings: $ClientPath"
}
finally {
    try {
        if (-not $completed) {
            $cleanupFailures = New-Object Collections.Generic.List[string]
            for ($index = $startedChildren.Count - 1; $index -ge 0; $index--) {
                try { Stop-StartedChildSafely -Child $startedChildren[$index] }
                catch { $cleanupFailures.Add($_.Exception.Message) }
            }
            if ($cleanupFailures.Count -eq 0) {
                Confirm-ExactRuntimeRoot
                if ($ownsRuntime -and (Test-Path -LiteralPath $RuntimeRoot)) { Remove-LocalArtifactWithRetry -LiteralPath $RuntimeRoot -Recurse }
                foreach ($path in @($AdminExecutablePath, $LicenseExecutablePath)) { if (Test-Path -LiteralPath $path) { Remove-LocalArtifactWithRetry -LiteralPath $path } }
            }
            if ($cleanupFailures.Count -gt 0) { throw ('Startup cleanup failed safely: ' + ($cleanupFailures -join '; ')) }
        }
    }
    finally {
        if ($startupMutexAcquired) { $startupMutex.ReleaseMutex() }
        if ($null -ne $startupMutex) { $startupMutex.Dispose() }
    }
}
