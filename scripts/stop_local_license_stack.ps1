[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RuntimeParent = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.tmp'))
$RuntimeRoot = [IO.Path]::GetFullPath((Join-Path $RuntimeParent 'license-local'))
$ExpectedRuntimeRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.tmp\license-local'))
$ManifestPath = Join-Path $RuntimeRoot 'pid-manifest.json'
$ReadyPath = Join-Path $RuntimeRoot 'ready.json'
$OwnerPath = Join-Path $RuntimeRoot 'instance-token'
$CleanupJournalPath = Join-Path $RuntimeParent 'license-local-cleanup.json'
$LicenseExecutablePath = [IO.Path]::GetFullPath((Join-Path $RuntimeParent 'license-service-local.exe'))
$AdminExecutablePath = [IO.Path]::GetFullPath((Join-Path $RuntimeParent 'license-admin-local.exe'))
$stopMutex = $null
$stopMutexAcquired = $false

function Remove-LocalArtifactWithRetry {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try {
            if (Test-Path -LiteralPath $LiteralPath) { Remove-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop }
            return
        }
        catch [IO.IOException] { if ($attempt -eq 49) { throw }; Start-Sleep -Milliseconds 100 }
        catch [UnauthorizedAccessException] { if ($attempt -eq 49) { throw }; Start-Sleep -Milliseconds 100 }
    }
}

function Move-LocalDirectoryAtomicallyWithRetry {
    param([Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Destination)
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try {
            if (-not (Test-Path -LiteralPath $Source -PathType Container) -or (Test-Path -LiteralPath $Destination)) { throw 'Atomic runtime move precondition failed.' }
            [IO.Directory]::Move($Source, $Destination)
            return
        }
        catch [IO.IOException] {
            if ((Test-Path -LiteralPath $Destination -PathType Container) -and -not (Test-Path -LiteralPath $Source)) { return }
            if ($attempt -eq 49 -or -not (Test-Path -LiteralPath $Source -PathType Container) -or (Test-Path -LiteralPath $Destination)) { throw }
            Start-Sleep -Milliseconds 100
        }
        catch [UnauthorizedAccessException] {
            if ($attempt -eq 49 -or -not (Test-Path -LiteralPath $Source -PathType Container) -or (Test-Path -LiteralPath $Destination)) { throw }
            Start-Sleep -Milliseconds 100
        }
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

function Get-LocalArtifactInventory {
    param([Parameter(Mandatory = $true)][string]$Root)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $rootItem = Get-Item -LiteralPath $Root -Force -ErrorAction Stop
    if (-not $rootItem.PSIsContainer -or (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Runtime inventory root is not a bound ordinary directory.' }
    $pending = New-Object Collections.Generic.Stack[string]
    $inventory = New-Object Collections.Generic.List[object]
    $pending.Push([IO.Path]::GetFullPath($Root))
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($item in @(Get-ChildItem -LiteralPath $current -Force | Sort-Object FullName)) {
            $full = [IO.Path]::GetFullPath($item.FullName)
            if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime inventory escaped its private root.' }
            $entryType = if ($item.PSIsContainer) { 'directory' } else { 'file' }
            $isReparsePoint = (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
            $sha256 = if ($entryType -eq 'file' -and -not $isReparsePoint) { Get-LocalFileSha256 -LiteralPath $full } else { $null }
            $inventory.Add([ordered]@{ relative_path = $full.Substring($rootFull.Length).Replace('\', '/'); entry_type = $entryType; is_reparse_point = [bool]$isReparsePoint; sha256 = $sha256 })
            if ($entryType -eq 'directory' -and -not $isReparsePoint) { $pending.Push($full) }
        }
    }
    return @($inventory | Sort-Object { [string]$_.relative_path })
}

function Get-ExpectedExecutable {
    param([Parameter(Mandatory = $true)][string]$Role)
    if ($Role -eq 'license-service') { return $LicenseExecutablePath }
    if ($Role -eq 'license-admin-local') { return $AdminExecutablePath }
    throw 'Local PID manifest contains an unknown child role.'
}

function Invoke-TestHook {
    param([Parameter(Mandatory = $true)][string]$Name)
    if ([string]::IsNullOrWhiteSpace($env:LICENSE_LOCAL_TEST_HOOK_ROOT)) { return }
    if (-not [string]::IsNullOrWhiteSpace($env:LICENSE_LOCAL_TEST_HOOK_NAME) -and -not [string]::Equals($env:LICENSE_LOCAL_TEST_HOOK_NAME, $Name, [StringComparison]::Ordinal)) { return }
    $hookRoot = [IO.Path]::GetFullPath($env:LICENSE_LOCAL_TEST_HOOK_ROOT)
    $expectedHookRoot = [IO.Path]::GetFullPath((Join-Path $RuntimeParent 'license-local-test-hooks'))
    if (-not [string]::Equals($hookRoot, $expectedHookRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Local cleanup test hook path rejected.' }
    [IO.Directory]::CreateDirectory($hookRoot) | Out-Null
    [IO.File]::WriteAllText((Join-Path $hookRoot ($Name + '.ready')), 'ready')
    $release = Join-Path $hookRoot ($Name + '.release')
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $release -PathType Leaf) { return }
        Start-Sleep -Milliseconds 20
    }
    throw 'Local cleanup test hook timed out.'
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
        $manifestCommandLine.IndexOf($RuntimeRoot, [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw 'Local PID manifest child identity is invalid.' }
    if ([string]$ProcessRecord.role -eq 'license-admin-local' -and
        ($manifestCommandLine.IndexOf('127.0.0.1:19090', [StringComparison]::Ordinal) -lt 0 -or
         $manifestCommandLine.IndexOf('127.0.0.1:19091', [StringComparison]::Ordinal) -lt 0 -or
         $manifestCommandLine.IndexOf([string]$ProcessRecord.instance_token, [StringComparison]::Ordinal) -lt 0)) { throw 'Local admin manifest command identity is invalid.' }
    $process = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }
    $null = $process.Handle
    $cimRecord = Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId = {0}" -f $pidValue) -ErrorAction Stop
    $actualExecutable = [IO.Path]::GetFullPath([string]$process.Path)
    $actualStartTicks = [long]$process.StartTime.ToUniversalTime().Ticks
    $actualCommandLine = [string]$cimRecord.CommandLine
    if (-not [string]::Equals($actualExecutable, $manifestExecutable, [StringComparison]::OrdinalIgnoreCase) -or
        $actualStartTicks -ne $manifestStartTicks -or [string]::IsNullOrWhiteSpace($actualCommandLine) -or
        -not [string]::Equals($manifestCommandLine, $actualCommandLine, [StringComparison]::Ordinal)) { throw 'The recorded PID now belongs to a different process; that process was not stopped.' }
    try { Stop-Process -InputObject $process -Force -ErrorAction Stop }
    catch { $process.Refresh(); if (-not $process.HasExited) { throw } }
    if (-not $process.WaitForExit(10000)) { throw 'A validated local process did not stop in time.' }
}

function Assert-CleanupJournal {
    param([Parameter(Mandatory = $true)]$Journal)
    $token = [string]$Journal.instance_token
    $expectedQuarantine = [IO.Path]::GetFullPath((Join-Path $RuntimeParent ("license-local-quarantine-" + $token)))
    if ([int]$Journal.schema_version -ne 2 -or $token -notmatch '^[0-9a-f]{32}$' -or
        -not [string]::Equals([IO.Path]::GetFullPath([string]$Journal.runtime_root), $RuntimeRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetFullPath([string]$Journal.quarantine_root), $expectedQuarantine, [StringComparison]::OrdinalIgnoreCase) -or
        [string]$Journal.evidence_name -notin @('pid-manifest.json', 'startup-cleanup.json') -or @($Journal.executables).Count -ne 2 -or @($Journal.runtime_files).Count -lt 1) { throw 'Cleanup journal identity rejected.' }
    $journalRoles = @($Journal.executables | ForEach-Object { [string]$_.role } | Sort-Object -Unique)
    if ($journalRoles.Count -ne 2 -or $journalRoles[0] -ne 'license-admin-local' -or $journalRoles[1] -ne 'license-service') { throw 'Cleanup journal executable roles rejected.' }
    foreach ($entry in $Journal.executables) {
        $expectedExecutable = Get-ExpectedExecutable -Role ([string]$entry.role)
        $expectedStagedName = '.cleanup-' + $token + '-' + [string]$entry.role + '.exe'
        if (-not [string]::Equals([IO.Path]::GetFullPath([string]$entry.source_path), $expectedExecutable, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string]$entry.staged_name, $expectedStagedName, [StringComparison]::Ordinal) -or [string]$entry.sha256 -notmatch '^[0-9a-f]{64}$') { throw 'Cleanup journal executable identity rejected.' }
    }
    return $expectedQuarantine
}

function Assert-QuarantineInventory {
    param([Parameter(Mandatory = $true)]$Journal, [Parameter(Mandatory = $true)][string]$QuarantineRoot)
    $rootItem = Get-Item -LiteralPath $QuarantineRoot -Force -ErrorAction Stop
    if (-not $rootItem.PSIsContainer -or (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Cleanup root is not a bound ordinary directory and was preserved.' }
    $rootPrefix = [IO.Path]::GetFullPath($QuarantineRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $expected = @{}
    foreach ($entry in $Journal.runtime_files) {
        $relative = ([string]$entry.relative_path).Replace('\', '/')
        $entryType = [string]$entry.entry_type
        $isReparsePoint = [bool]$entry.is_reparse_point
        $sha256 = [string]$entry.sha256
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.($|/)' -or
            $entryType -notin @('file', 'directory') -or $isReparsePoint -or
            ($entryType -eq 'file' -and $sha256 -notmatch '^[0-9a-f]{64}$') -or
            ($entryType -eq 'directory' -and -not [string]::IsNullOrEmpty($sha256)) -or $expected.ContainsKey($relative)) { throw 'Cleanup journal runtime inventory rejected.' }
        $candidate = [IO.Path]::GetFullPath((Join-Path $QuarantineRoot $relative))
        if (-not $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup journal runtime path escaped quarantine.' }
        $expected[$relative] = [ordered]@{ entry_type = $entryType; is_reparse_point = $isReparsePoint; sha256 = $sha256 }
    }
    foreach ($entry in $Journal.executables) { $expected[[string]$entry.staged_name] = [ordered]@{ entry_type = 'file'; is_reparse_point = $false; sha256 = [string]$entry.sha256 } }
    $actual = @{}
    foreach ($entry in (Get-LocalArtifactInventory -Root $QuarantineRoot)) { $actual[[string]$entry.relative_path] = $entry }
    foreach ($relative in $actual.Keys) {
        if (-not $expected.ContainsKey($relative)) { throw 'Quarantine contains a changed or unowned artifact and was preserved.' }
        $actualEntry = $actual[$relative]
        $expectedEntry = $expected[$relative]
        if (-not [string]::Equals([string]$actualEntry.entry_type, [string]$expectedEntry.entry_type, [StringComparison]::Ordinal) -or
            [bool]$actualEntry.is_reparse_point -ne [bool]$expectedEntry.is_reparse_point -or
            ([string]$actualEntry.entry_type -eq 'file' -and -not [string]::Equals([string]$actualEntry.sha256, [string]$expectedEntry.sha256, [StringComparison]::Ordinal))) { throw 'Quarantine contains a changed or unowned artifact and was preserved.' }
    }
}

function Remove-VerifiedQuarantineTree {
    param([Parameter(Mandatory = $true)]$Journal, [Parameter(Mandatory = $true)][string]$QuarantineRoot)
    $rootPrefix = [IO.Path]::GetFullPath($QuarantineRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $files = @($Journal.runtime_files | Where-Object { [string]$_.entry_type -eq 'file' } | Sort-Object { ([string]$_.relative_path).Length } -Descending)
    $directories = @($Journal.runtime_files | Where-Object { [string]$_.entry_type -eq 'directory' } | Sort-Object { ([string]$_.relative_path).Length } -Descending)
    foreach ($entry in @($files + $directories)) {
        $relative = ([string]$entry.relative_path).Replace('\', '/')
        $candidate = [IO.Path]::GetFullPath((Join-Path $QuarantineRoot $relative))
        if (-not $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup journal runtime path escaped quarantine.' }
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
        $expectedType = [string]$entry.entry_type
        $actualType = if ($item.PSIsContainer) { 'directory' } else { 'file' }
        $isReparsePoint = (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        if (-not [string]::Equals($actualType, $expectedType, [StringComparison]::Ordinal) -or $isReparsePoint) { throw 'A runtime directory identity changed before deletion and was preserved.' }
        if ($actualType -eq 'file' -and -not [string]::Equals((Get-LocalFileSha256 -LiteralPath $candidate), [string]$entry.sha256, [StringComparison]::Ordinal)) { throw 'A runtime file changed before deletion and was preserved.' }
        if ($actualType -eq 'directory' -and @(Get-ChildItem -LiteralPath $candidate -Force).Count -ne 0) { throw 'A runtime directory was not empty at deletion and was preserved.' }
        Remove-LocalArtifactWithRetry -LiteralPath $candidate
    }
    $rootItem = Get-Item -LiteralPath $QuarantineRoot -Force -ErrorAction Stop
    if (-not $rootItem.PSIsContainer -or (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or @(Get-ChildItem -LiteralPath $QuarantineRoot -Force).Count -ne 0) { throw 'Cleanup root identity changed before deletion and was preserved.' }
    Remove-LocalArtifactWithRetry -LiteralPath $QuarantineRoot
}

function Resume-QuarantineCleanup {
    param([Parameter(Mandatory = $true)]$Journal)
    $quarantineRoot = Assert-CleanupJournal -Journal $Journal
    if ((Test-Path -LiteralPath $RuntimeRoot) -and (Test-Path -LiteralPath $quarantineRoot)) { throw 'Both live and quarantine runtime roots exist; cleanup refused.' }
    if (Test-Path -LiteralPath $RuntimeRoot) {
        Invoke-TestHook -Name 'before-stage'
        foreach ($entry in $Journal.executables) {
            $source = [IO.Path]::GetFullPath([string]$entry.source_path)
            $staged = Join-Path $RuntimeRoot ([string]$entry.staged_name)
            if (Test-Path -LiteralPath $source) {
                if (Test-Path -LiteralPath $staged) { throw 'Both source and staged executable exist.' }
                [IO.File]::Move($source, $staged)
            }
            if (-not (Test-Path -LiteralPath $staged -PathType Leaf) -or -not [string]::Equals((Get-LocalFileSha256 -LiteralPath $staged), [string]$entry.sha256, [StringComparison]::Ordinal)) { throw 'A staged executable failed post-move hash verification and was preserved.' }
        }
        Move-LocalDirectoryAtomicallyWithRetry -Source $RuntimeRoot -Destination $quarantineRoot
    }
    if (-not (Test-Path -LiteralPath $quarantineRoot -PathType Container)) { throw 'Cleanup quarantine is missing.' }
    $owner = Join-Path $quarantineRoot 'instance-token'
    $manifest = Join-Path $quarantineRoot ([string]$Journal.evidence_name)
    if ((Test-Path -LiteralPath $owner) -and (-not (Test-Path -LiteralPath $owner -PathType Leaf) -or
        -not [string]::Equals((Get-Content -LiteralPath $owner -Raw -Encoding ascii).Trim(), [string]$Journal.instance_token, [StringComparison]::Ordinal))) { throw 'Quarantine ownership evidence rejected.' }
    if ((Test-Path -LiteralPath $manifest) -and -not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'Quarantine ownership evidence rejected.' }
    Assert-QuarantineInventory -Journal $Journal -QuarantineRoot $quarantineRoot
    foreach ($entry in $Journal.executables) {
        $staged = Join-Path $quarantineRoot ([string]$entry.staged_name)
        if (Test-Path -LiteralPath $staged) {
            if (-not [string]::Equals((Get-LocalFileSha256 -LiteralPath $staged), [string]$entry.sha256, [StringComparison]::Ordinal)) { throw 'A quarantined executable changed and was preserved.' }
            Invoke-TestHook -Name ('after-executable-verify-' + [string]$entry.role)
            if (-not (Test-Path -LiteralPath $staged -PathType Leaf) -or -not [string]::Equals((Get-LocalFileSha256 -LiteralPath $staged), [string]$entry.sha256, [StringComparison]::Ordinal)) { throw 'A quarantined executable changed before deletion and was preserved.' }
            if ([string]::Equals($env:LICENSE_LOCAL_TEST_FAIL_DELETE_ROLE, [string]$entry.role, [StringComparison]::Ordinal)) { throw 'Injected executable deletion failure.' }
            Remove-LocalArtifactWithRetry -LiteralPath $staged
        }
    }
    Invoke-TestHook -Name 'before-quarantine-delete'
    if ((Test-Path -LiteralPath $owner) -and -not [string]::Equals((Get-Content -LiteralPath $owner -Raw -Encoding ascii).Trim(), [string]$Journal.instance_token, [StringComparison]::Ordinal)) { throw 'Quarantine ownership changed before deletion.' }
    Assert-QuarantineInventory -Journal $Journal -QuarantineRoot $quarantineRoot
    Remove-VerifiedQuarantineTree -Journal $Journal -QuarantineRoot $quarantineRoot
    Remove-LocalArtifactWithRetry -LiteralPath $CleanupJournalPath
}

try {
    $stopMutex = New-Object Threading.Mutex($false, 'Local\SuperPanLicenseLocalAcceptanceStop')
    $stopMutexAcquired = $stopMutex.WaitOne(30000)
    if (-not $stopMutexAcquired) { throw 'Timed out waiting for serialized local cleanup.' }
    if (Test-Path -LiteralPath $CleanupJournalPath -PathType Leaf) {
        $journal = Get-Content -LiteralPath $CleanupJournalPath -Raw -Encoding utf8 | ConvertFrom-Json
        Resume-QuarantineCleanup -Journal $journal
        Write-Output 'Local license acceptance cleanup recovery completed.'
        return
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
            -not [string]::Equals((Get-LocalFileSha256 -LiteralPath $expectedExecutable), [string]$processRecord.executable_sha256, [StringComparison]::Ordinal)) { throw 'A recorded local executable is missing or was replaced; no process was stopped.' }
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
            $executables = @($manifest.processes | ForEach-Object { [ordered]@{ role = [string]$_.role; source_path = [IO.Path]::GetFullPath([string]$_.executable_path); staged_name = ('.cleanup-' + $manifestToken + '-' + [string]$_.role + '.exe'); sha256 = [string]$_.executable_sha256 } })
            $runtimeFiles = Get-LocalArtifactInventory -Root $RuntimeRoot
            $journal = [ordered]@{ schema_version = 2; instance_token = $manifestToken; runtime_root = $RuntimeRoot; quarantine_root = [IO.Path]::GetFullPath((Join-Path $RuntimeParent ("license-local-quarantine-" + $manifestToken))); evidence_name = 'pid-manifest.json'; executables = $executables; runtime_files = $runtimeFiles }
            Write-TextFileAtomic -LiteralPath $CleanupJournalPath -Text (($journal | ConvertTo-Json -Depth 5) + "`n")
            Resume-QuarantineCleanup -Journal $journal
        }
    }
    if ($stopFailures.Count -gt 0) { throw ('One or more local children failed exact-identity stop: ' + ($stopFailures -join '; ')) }
    Write-Output 'Local license acceptance stack stopped through verified private quarantine.'
}
finally {
    if ($stopMutexAcquired) { $stopMutex.ReleaseMutex() }
    if ($null -ne $stopMutex) { $stopMutex.Dispose() }
}
