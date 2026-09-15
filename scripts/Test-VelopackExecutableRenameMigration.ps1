[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OldAssets,
    [Parameter(Mandatory = $true)][string]$NewAssets,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [Parameter(Mandatory = $true)][ValidateSet('Setup', 'Portable')][string]$AssetKind,
    [Parameter(Mandatory = $true)][ValidateSet('Delta', 'Full')][string]$Path,
    [ValidateSet('None', 'DownloadInterrupted', 'ApplicationFailed')][string]$FailureInjection = 'None',
    [switch]$IUnderstandThisIsAnIsolatedWindowsProfile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

# This gate deliberately has no network client. Its only asset source is the two
# caller-supplied local directories copied into the run-owned evidence directory.
$script:OldMainExecutableName = 'BaiduBatchTransfer.App.exe'
# UTF-8 contract text: 知道综合工具.exe. Constructed from code points because Windows
# PowerShell 5.1 decodes UTF-8-without-BOM scripts through the active ANSI code page.
$script:ChineseDisplayName = "$([char]0x77E5)$([char]0x9053)$([char]0x7EFC)$([char]0x5408)$([char]0x5DE5)$([char]0x5177)"
$script:NewMainExecutableName = $script:ChineseDisplayName + '.exe'
$script:IsolationDirectoryName = '.velopack-rename-migration-isolation'
$script:FixtureName = 'migration-gate-fixture.json'
$script:OwnedPaths = New-Object System.Collections.Generic.List[string]

function Assert-StrictChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$Label = 'Path'
    )

    $canonicalRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $canonicalCandidate = [IO.Path]::GetFullPath($Candidate)
    if (-not $canonicalCandidate.StartsWith($canonicalRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must be a strict child of '$canonicalRoot': $canonicalCandidate"
    }
    return $canonicalCandidate
}

function Assert-NoReparsePoint {
    param([Parameter(Mandatory = $true)][string]$ExistingPath, [string]$Label = 'Path')

    $item = Get-Item -LiteralPath $ExistingPath -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must not be a reparse point: $ExistingPath"
    }
}

function Assert-NoReparseAncestors {
    param([Parameter(Mandatory = $true)][string]$ExistingPath, [string]$Label = 'Path')
    $current = Get-Item -LiteralPath ([IO.Path]::GetFullPath($ExistingPath)) -Force
    while ($current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label has a reparse-point ancestor: $($current.FullName)"
        }
        $current = $current.Parent
    }
}

function Assert-SafeInputDirectory {
    param([Parameter(Mandatory = $true)][string]$InputPath, [Parameter(Mandatory = $true)][string]$Label)

    if (-not [IO.Path]::IsPathRooted($InputPath)) { throw "$Label must be an absolute path." }
    $canonical = [IO.Path]::GetFullPath($InputPath).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $canonical -PathType Container)) { throw "$Label does not exist: $canonical" }
    Assert-NoReparseAncestors -ExistingPath $canonical -Label $Label
    Assert-NoReparsePoint -ExistingPath $canonical -Label $Label
    foreach ($entry in @(Get-ChildItem -LiteralPath $canonical -Force -Recurse)) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label contains a reparse point: $($entry.FullName)"
        }
    }
    return $canonical
}

function Assert-NewOwnedDirectory {
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)][string]$RunId)

    if (-not [IO.Path]::IsPathRooted($Directory)) { throw 'EvidenceDirectory must be an absolute path.' }
    $canonical = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    if (Test-Path -LiteralPath $canonical) { throw "EvidenceDirectory must not already exist: $canonical" }
    $parent = Split-Path -Parent $canonical
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw "EvidenceDirectory parent does not exist: $parent" }
    Assert-NoReparseAncestors -ExistingPath $parent -Label 'EvidenceDirectory parent'
    New-Item -ItemType Directory -Path $canonical | Out-Null
    $script:OwnedPaths.Add($canonical)
    $owner = [ordered]@{ schema_version = 1; run_id = $RunId; created_utc = [DateTimeOffset]::UtcNow.ToString('O') }
    Write-CanonicalJson -Value $owner -Destination (Join-Path $canonical 'ownership.json') -CreateNew
    return $canonical
}

function Assert-IsolatedWindowsProfile {
    if (-not $IUnderstandThisIsAnIsolatedWindowsProfile) {
        throw 'The explicit isolation confirmation switch is required.'
    }
    $profile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $identity.User) { throw 'The current Windows identity has no SID.' }
    $markerDirectory = Join-Path $profile $script:IsolationDirectoryName
    $markerPath = Join-Path $markerDirectory 'marker.json'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "The profile-bound isolation marker is missing: $markerPath"
    }
    Assert-NoReparsePoint -ExistingPath $markerDirectory -Label 'Isolation marker directory'
    Assert-NoReparsePoint -ExistingPath $markerPath -Label 'Isolation marker'
    try { $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "The profile-bound isolation marker is malformed: $($_.Exception.Message)" }
    $canonicalProfile = [IO.Path]::GetFullPath($profile).TrimEnd('\')
    if (-not [string]::Equals([IO.Path]::GetFullPath([string]$marker.profile_path).TrimEnd('\'), $canonicalProfile, [StringComparison]::OrdinalIgnoreCase) -or
        [string]$marker.sid -cne $identity.User.Value) {
        throw 'The profile-bound isolation marker does not match the current profile and SID.'
    }
    try { $nonce = [Convert]::FromBase64String([string]$marker.nonce) }
    catch { throw 'The profile-bound isolation marker nonce is malformed.' }
    if ($nonce.Length -lt 32) { throw 'The profile-bound isolation marker nonce must contain at least 256 bits.' }
    foreach ($securedPath in @($markerDirectory, $markerPath)) {
        $acl = Get-Acl -LiteralPath $securedPath
        if (-not $acl.AreAccessRulesProtected) { throw 'The profile-bound isolation marker ACL must disable inheritance.' }
        $owner = New-Object Security.Principal.NTAccount -ArgumentList ([string]$acl.Owner)
        $ownerSid = $owner.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($ownerSid -cne 'S-1-5-18' -and $ownerSid -cne 'S-1-5-32-544') {
            throw "The profile-bound isolation marker owner must be SYSTEM or Administrators, actual SID '$ownerSid'."
        }
        $writeProbe = $null
        try {
            if ($securedPath -ceq $markerDirectory) {
                $writeProbe = Join-Path $securedPath ("write-probe-$([Guid]::NewGuid().ToString('N')).tmp")
                $stream = [IO.File]::Open($writeProbe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            } else {
                $stream = [IO.File]::Open($securedPath, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
            }
            $stream.Dispose()
            throw 'The profile-bound isolation marker or its directory is writable by the isolated user.'
        } catch [UnauthorizedAccessException] {
            # Expected: marker was provisioned by the VM administrator and is read-only to this user.
        } finally {
            if ($writeProbe -and (Test-Path -LiteralPath $writeProbe)) {
                Remove-Item -LiteralPath $writeProbe -Force
            }
        }
    }
    return $canonicalProfile
}

function New-RunId {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    return (-join @($bytes | ForEach-Object { $_.ToString('x2') }))
}

function ConvertTo-CanonicalNode {
    param($Value)
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { return $Value }
    if ($Value -is [Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object)) { $ordered[[string]$key] = ConvertTo-CanonicalNode $Value[$key] }
        return $ordered
    }
    if ($Value -is [Collections.IEnumerable]) {
        return @($Value | ForEach-Object { ConvertTo-CanonicalNode $_ })
    }
    $object = [ordered]@{}
    foreach ($property in @($Value.PSObject.Properties | Sort-Object Name)) {
        $object[$property.Name] = ConvertTo-CanonicalNode $property.Value
    }
    return $object
}

function ConvertTo-CanonicalJson {
    param([Parameter(Mandatory = $true)]$Value)
    return ((ConvertTo-CanonicalNode $Value) | ConvertTo-Json -Depth 20 -Compress)
}

function Write-CanonicalJson {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Destination,
        [switch]$CreateNew
    )
    $json = ConvertTo-CanonicalJson $Value
    $mode = if ($CreateNew) { [IO.FileMode]::CreateNew } else { [IO.FileMode]::Create }
    $stream = [IO.File]::Open($Destination, $mode, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json + "`n")
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
}

function Copy-LocalAssetTree {
    param([string]$Source, [string]$Destination)
    New-Item -ItemType Directory -Path $Destination | Out-Null
    foreach ($entry in @(Get-ChildItem -LiteralPath $Source -Force -Recurse)) {
        $relative = $entry.FullName.Substring($Source.Length).TrimStart('\')
        $target = Assert-StrictChildPath -Candidate (Join-Path $Destination $relative) -Root $Destination -Label 'Staged asset'
        if ($entry.PSIsContainer) { New-Item -ItemType Directory -Path $target -Force | Out-Null }
        else {
            $parent = Split-Path -Parent $target
            if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
            Copy-Item -LiteralPath $entry.FullName -Destination $target
        }
    }
}

function Resolve-FixturePath {
    param([string]$Root, [string]$RelativePath, [string]$Label, [switch]$Directory, [switch]$AllowRoot)
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "$Label must be a non-empty relative path."
    }
    $candidate = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    if ($AllowRoot -and [string]::Equals($candidate.TrimEnd('\'), [IO.Path]::GetFullPath($Root).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        $resolved = $candidate
    } else {
        $resolved = Assert-StrictChildPath -Candidate $candidate -Root $Root -Label $Label
    }
    $pathType = if ($Directory) { 'Container' } else { 'Leaf' }
    if (-not (Test-Path -LiteralPath $resolved -PathType $pathType)) { throw "$Label does not exist: $resolved" }
    Assert-NoReparsePoint -ExistingPath $resolved -Label $Label
    return $resolved
}

function Assert-FileSha256 {
    param([string]$File, [string]$Expected, [string]$Label)
    if ($Expected -cnotmatch '^[0-9a-f]{64}$') { throw "$Label SHA-256 is not canonical lowercase hex." }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $File).Hash.ToLowerInvariant()
    if ($actual -cne $Expected) { throw "$Label SHA-256 mismatch." }
}

function Assert-AuthenticodeSamePublisher {
    param([string]$Driver, [string]$ApplicationExecutable)
    $driverSignature = Get-AuthenticodeSignature -LiteralPath $Driver
    $applicationSignature = Get-AuthenticodeSignature -LiteralPath $ApplicationExecutable
    if ($driverSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $applicationSignature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        -not $driverSignature.SignerCertificate -or -not $applicationSignature.SignerCertificate) {
        throw 'The state driver and production application must both have valid Authenticode signatures.'
    }
    if ($driverSignature.SignerCertificate.Thumbprint -cne $applicationSignature.SignerCertificate.Thumbprint) {
        throw 'The state driver is not signed by the production application publisher.'
    }
}

function Assert-PeMachineAmd64 {
    param([string]$Executable, [string]$Label)
    $stream = [IO.File]::OpenRead($Executable)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "$Label is not a PE executable." }
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0x40 -or $peOffset -gt $stream.Length - 6) { throw "$Label has an invalid PE header offset." }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw "$Label is not an x64 PE executable."
        }
    } finally { $reader.Dispose(); $stream.Dispose() }
}

function Stop-OwnedProcessTree {
    param([int]$RootProcessId)
    $all = @(Get-CimInstance Win32_Process)
    $ids = New-Object System.Collections.Generic.List[int]
    $ids.Add($RootProcessId)
    do {
        $added = $false
        foreach ($process in $all) {
            if ($ids.Contains([int]$process.ParentProcessId) -and -not $ids.Contains([int]$process.ProcessId)) {
                $ids.Add([int]$process.ProcessId); $added = $true
            }
        }
    } while ($added)
    foreach ($id in @($ids | Sort-Object -Descending)) {
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-CheckedProcess {
    param([string]$File, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutSeconds = 120, [switch]$ExpectFailure)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $File
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Arguments = ($Arguments | ForEach-Object {
        $argument = [string]$_
        if ($argument.IndexOf([char]0) -ge 0) { throw 'A process argument contains a NUL character.' }
        # CommandLineToArgvW-compatible quoting, including trailing backslashes.
        '"' + [regex]::Replace($argument, '(\\*)"', '$1$1\"') +
            ([regex]::Match($argument, '\\+$').Value) + '"'
    }) -join ' '
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Unable to start $File" }
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-OwnedProcessTree -RootProcessId $process.Id
            throw "Process timed out after $TimeoutSeconds seconds: $File"
        }
        $output = $stdoutTask.GetAwaiter().GetResult() + $stderrTask.GetAwaiter().GetResult()
        if (-not $ExpectFailure -and $process.ExitCode -ne 0) { throw "Process failed ($($process.ExitCode)): $File`n$output" }
        if ($ExpectFailure -and $process.ExitCode -eq 0) { throw "Injected failure unexpectedly succeeded: $File" }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output }
    } finally { $process.Dispose() }
}

function Invoke-StateDriver {
    param([string]$Driver, [string]$Mode, [string]$RunId, [string]$ProfileRoot, [string]$FixturePath, [string]$OutputPath)
    Invoke-CheckedProcess -File $Driver -WorkingDirectory (Split-Path -Parent $Driver) -Arguments @(
        $Mode, '--run-id', $RunId, '--profile-root', $ProfileRoot, '--fixture', $FixturePath, '--output', $OutputPath
    ) | Out-Null
    if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) { throw "State driver did not write: $OutputPath" }
    return (Get-Content -LiteralPath $OutputPath -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Assert-StateObservation {
    param($State, [string]$RunId)
    foreach ($name in @('account_marker', 'settings_marker', 'task_marker')) {
        if ([string]$State.$name -cne $RunId) { throw "State observation '$name' does not contain the current run marker." }
    }
    if ([string]::IsNullOrWhiteSpace([string]$State.database_relative_path)) { throw 'database_relative_path is missing.' }
    if ([string]::IsNullOrWhiteSpace([string]$State.license_state_relative_path)) { throw 'license_state_relative_path is missing.' }
    if ($State.production_trust_chain -ne $true) { throw 'The isolation license was not verified by the production_trust_chain.' }
    if ([string]$State.signed_payload_sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'signed_payload_sha256 is invalid.' }
    $expires = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact([string]$State.expires_at_utc, 'O', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$expires)) {
        throw 'expires_at_utc is invalid.'
    }
    if ($expires -le [DateTimeOffset]::UtcNow) { throw 'The production-chain isolation license is expired.' }
    $activationId = [Guid]::Empty
    if (-not [Guid]::TryParseExact([string]$State.activation_request_id, 'D', [ref]$activationId)) { throw 'activation_request_id is invalid.' }
    if ($State.environment_authorization_is_auxiliary -ne $true) { throw 'environment_authorization_is_auxiliary must be acknowledged.' }
    if ($State.network_accessed -ne $false -or [string]$State.license_source -cne 'preissued-local-production-chain-license') {
        throw 'The license fixture must be preissued locally and the state driver must not access a network.'
    }
    return $State
}

function Assert-MigrationStatePreserved {
    param($Before, $After, [string]$RunId)
    Assert-StateObservation -State $Before -RunId $RunId | Out-Null
    Assert-StateObservation -State $After -RunId $RunId | Out-Null
    foreach ($field in @('account_marker','settings_marker','task_marker','database_relative_path','license_state_relative_path','signed_payload_sha256','expires_at_utc','activation_request_id','license_source')) {
        if ([string]$Before.$field -cne [string]$After.$field) { throw "Migration changed preserved state field '$field'." }
    }
    if ($After.reactivation_required -ne $false) { throw 'The upgraded application required reactivation.' }
}

function Get-ShortcutTarget {
    param([string]$Shortcut)
    $shell = New-Object -ComObject WScript.Shell
    return [IO.Path]::GetFullPath([string]$shell.CreateShortcut($Shortcut).TargetPath)
}

function Find-ExactProcessPath {
    param([string]$ExpectedPath, [DateTimeOffset]$NotBefore, [int]$TimeoutSeconds = 45)
    $canonical = [IO.Path]::GetFullPath($ExpectedPath)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
            try {
                if ($process.StartTime.ToUniversalTime() -ge $NotBefore.UtcDateTime -and
                    [string]::Equals([IO.Path]::GetFullPath($process.Path), $canonical, [StringComparison]::OrdinalIgnoreCase)) {
                    return $process
                }
            } catch { }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Expected process was not observed: $canonical"
}

function Assert-OldVersionStillLaunches {
    param([string]$OldExecutable, [string]$RunId)
    $started = [DateTimeOffset]::UtcNow
    $process = Start-Process -FilePath $OldExecutable -ArgumentList @('--migration-gate-rollback', $RunId) -WorkingDirectory (Split-Path -Parent $OldExecutable) -PassThru
    $observed = Find-ExactProcessPath -ExpectedPath $OldExecutable -NotBefore $started
    Stop-OwnedProcessTree -RootProcessId $observed.Id
    return $true
}

function Get-UninstallEntry {
    param([string]$PackageId)
    $roots = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*')
    $matches = @($roots | ForEach-Object { Get-ItemProperty -Path $_ -ErrorAction SilentlyContinue } |
        Where-Object { [string]$_.PSChildName -like "*$PackageId*" -or [string]$_.DisplayName -ceq $script:ChineseDisplayName })
    if ($matches.Count -ne 1) { throw "Expected one UninstallEntry for '$PackageId', found $($matches.Count)." }
    return [ordered]@{ Key = [string]$matches[0].PSPath; DisplayName = [string]$matches[0].DisplayName; UninstallString = [string]$matches[0].UninstallString }
}

function Assert-CleanSetupSurfaces {
    param([string]$Profile, [string]$PackageId)
    foreach ($shortcut in @(
        (Join-Path $Profile "Desktop\$($script:ChineseDisplayName).lnk"),
        (Join-Path $Profile "Desktop\BaiduBatchTransfer.App.lnk"),
        (Join-Path $Profile "AppData\Roaming\Microsoft\Windows\Start Menu\Programs\$($script:ChineseDisplayName).lnk"),
        (Join-Path $Profile "AppData\Roaming\Microsoft\Windows\Start Menu\Programs\BaiduBatchTransfer.App.lnk"))) {
        if (Test-Path -LiteralPath $shortcut) { throw "The isolated profile contains a pre-existing product shortcut: $shortcut" }
    }
    $existing = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' |
        ForEach-Object { Get-ItemProperty -Path $_ -ErrorAction SilentlyContinue } |
        Where-Object { [string]$_.PSChildName -like "*$PackageId*" -or [string]$_.DisplayName -in @($script:ChineseDisplayName, 'BaiduBatchTransfer.App') })
    if ($existing.Count -ne 0) { throw 'The isolated profile contains a pre-existing product uninstall entry.' }
}

function Assert-SetupSurfaces {
    param([string]$InstallRoot, [string]$CurrentMain, [string]$RootLauncher, [string]$LegacyLauncher, [string]$Profile, [string]$PackageId)
    $desktopShortcut = Join-Path $Profile ("Desktop\$($script:ChineseDisplayName).lnk")
    $startMenuShortcut = Join-Path $Profile ("AppData\Roaming\Microsoft\Windows\Start Menu\Programs\$($script:ChineseDisplayName).lnk")
    foreach ($required in @($CurrentMain, $RootLauncher, $LegacyLauncher, $desktopShortcut, $startMenuShortcut)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required Setup migration surface is missing: $required" }
    }
    $allowedTargets = @([IO.Path]::GetFullPath($RootLauncher), [IO.Path]::GetFullPath($CurrentMain))
    $desktopTarget = Get-ShortcutTarget $desktopShortcut
    $startMenuTarget = Get-ShortcutTarget $startMenuShortcut
    if ($desktopTarget -notin $allowedTargets -or $startMenuTarget -notin $allowedTargets) { throw 'Chinese shortcuts do not target the renamed launcher.' }
    $uninstall = Get-UninstallEntry -PackageId $PackageId
    if ([string]$uninstall.DisplayName -cne $script:ChineseDisplayName) { throw 'UninstallEntry does not use the Chinese product name.' }
    return [ordered]@{
        InstallRoot = $InstallRoot; CurrentDirectory = (Split-Path -Parent $CurrentMain)
        InstallRootLauncher = $RootLauncher; CurrentMainExecutable = $CurrentMain
        LegacyCompatibilityLauncher = $LegacyLauncher
        DesktopShortcut = [ordered]@{ Path = $desktopShortcut; Target = $desktopTarget }
        StartMenuShortcut = [ordered]@{ Path = $startMenuShortcut; Target = $startMenuTarget }
        UninstallEntry = $uninstall
    }
}

function Get-FileInventory {
    param([string[]]$Roots)
    $records = New-Object System.Collections.Generic.List[object]
    foreach ($root in $Roots) {
        foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName)) {
            $records.Add([ordered]@{ Path = $file.FullName; Length = $file.Length; Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
        }
    }
    return @($records)
}

$runId = New-RunId
$startedUtc = [DateTimeOffset]::UtcNow
$evidenceRoot = $null
$evidence = [ordered]@{
    SchemaVersion = 1; RunId = $runId; StartedUtc = $startedUtc.ToString('O'); FinishedUtc = $null
    AssetSource = 'caller-supplied-local-directories'; AssetKind = $AssetKind; Path = $Path
    Scenario = "$AssetKind-$Path"; FailureInjection = $FailureInjection; Succeeded = $false
    FailureRollbackVerified = $false; VelopackUpdaterVersion = $null
    Surfaces = $null; BeforeState = $null; AfterState = $null; Error = $null
}

try {
    $profile = Assert-IsolatedWindowsProfile
    $oldSource = Assert-SafeInputDirectory -InputPath $OldAssets -Label 'OldAssets'
    $newSource = Assert-SafeInputDirectory -InputPath $NewAssets -Label 'NewAssets'
    if ([string]::Equals($oldSource, $newSource, [StringComparison]::OrdinalIgnoreCase)) { throw 'OldAssets and NewAssets must be distinct.' }
    $intendedEvidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd('\')
    foreach ($source in @($oldSource, $newSource)) {
        if ($intendedEvidenceRoot.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $source.StartsWith($intendedEvidenceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'EvidenceDirectory and input asset directories must not contain one another.'
        }
    }
    $evidenceRoot = Assert-NewOwnedDirectory -Directory $intendedEvidenceRoot -RunId $runId
    $supportedScenarios = @('Setup-Delta', 'Setup-Full', 'Portable-Full')
    if ($evidence.Scenario -notin $supportedScenarios) {
        throw "Unsupported migration scenario '$($evidence.Scenario)'. Supported: $($supportedScenarios -join ', ')."
    }
    $stagedRoot = Join-Path $evidenceRoot 'staged-assets'
    New-Item -ItemType Directory -Path $stagedRoot | Out-Null
    $script:OwnedPaths.Add($stagedRoot)
    $stagedOld = Join-Path $stagedRoot 'old'
    $stagedNew = Join-Path $stagedRoot 'new'
    Copy-LocalAssetTree -Source $oldSource -Destination $stagedOld
    Copy-LocalAssetTree -Source $newSource -Destination $stagedNew

    $fixturePath = Resolve-FixturePath -Root $stagedOld -RelativePath $script:FixtureName -Label 'Migration fixture'
    $fixture = Get-Content -LiteralPath $fixturePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($fixture.schema_version -ne 1 -or [string]$fixture.old_version -cne '1.1.0' -or [string]$fixture.new_version -cne '1.1.1') {
        throw 'Migration fixture must describe versions 1.1.0 to 1.1.1 and schema version 1.'
    }
    $driver = Resolve-FixturePath -Root $stagedOld -RelativePath ([string]$fixture.state_driver_relative_path) -Label 'State driver'
    Assert-FileSha256 -File $driver -Expected ([string]$fixture.state_driver_sha256) -Label 'State driver'

    $dataRoot = Join-Path $profile 'AppData\Local\BaiduBatchTransfer'
    $licenseRoot = Join-Path $profile 'AppData\Local\SuperPan\Licensing\v1'
    if ((Test-Path -LiteralPath $dataRoot) -or (Test-Path -LiteralPath $licenseRoot)) {
        throw 'The isolated profile is not clean; existing application data or license state would be at risk.'
    }

    $oldPortableRoot = $null
    if ($AssetKind -ceq 'Setup') {
        $oldSetup = Resolve-FixturePath -Root $stagedOld -RelativePath ([string]$fixture.old_setup_relative_path) -Label 'Old Setup'
        $localAppData = Join-Path $profile 'AppData\Local'
        $installRoot = Assert-StrictChildPath -Candidate (Join-Path $localAppData ([string]$fixture.install_root_relative_path)) -Root $localAppData -Label 'InstallRoot'
        if (Test-Path -LiteralPath $installRoot) { throw "The isolated profile contains a pre-existing InstallRoot: $installRoot" }
        Assert-CleanSetupSurfaces -Profile $profile -PackageId ([string]$fixture.package_id)
        Invoke-CheckedProcess -File $oldSetup -Arguments @('--silent') -WorkingDirectory $stagedOld | Out-Null
    } else {
        $oldPortable = Resolve-FixturePath -Root $stagedOld -RelativePath ([string]$fixture.old_portable_relative_path) -Label 'Old Portable'
        $oldPortableRoot = Join-Path $evidenceRoot 'portable-install'
        New-Item -ItemType Directory -Path $oldPortableRoot | Out-Null
        $script:OwnedPaths.Add($oldPortableRoot)
        [IO.Compression.ZipFile]::ExtractToDirectory($oldPortable, $oldPortableRoot)
        $installRoot = Resolve-FixturePath -Root $oldPortableRoot -RelativePath ([string]$fixture.portable_root_relative_path) -Label 'Portable root' -Directory -AllowRoot
    }
    $installRoot = [IO.Path]::GetFullPath($installRoot)
    $oldMain = Resolve-FixturePath -Root $installRoot -RelativePath ([string]$fixture.old_main_relative_path) -Label 'Old main executable'
    if ([IO.Path]::GetFileName($oldMain) -cne $script:OldMainExecutableName -or
        -not ([Diagnostics.FileVersionInfo]::GetVersionInfo($oldMain).ProductVersion.StartsWith('1.1.0', [StringComparison]::Ordinal))) {
        throw 'Old main executable must be the real 1.1.0 BaiduBatchTransfer.App.exe.'
    }
    Assert-AuthenticodeSamePublisher -Driver $driver -ApplicationExecutable $oldMain
    Assert-PeMachineAmd64 -Executable $oldMain -Label 'Old main executable'
    $updater = Resolve-FixturePath -Root $installRoot -RelativePath ([string]$fixture.updater_relative_path) -Label 'Velopack updater'
    $evidence.VelopackUpdaterVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($updater).ProductVersion
    $beforePath = Join-Path $evidenceRoot 'state-before.json'
    $before = Invoke-StateDriver -Driver $driver -Mode 'seed-and-probe' -RunId $runId -ProfileRoot $profile -FixturePath $fixturePath -OutputPath $beforePath
    Assert-StateObservation -State $before -RunId $runId | Out-Null
    $evidence.BeforeState = $before

    $package = $null
    if ($Path -ceq 'Delta') {
        $oldFull = Resolve-FixturePath -Root $stagedOld -RelativePath ([string]$fixture.old_full_relative_path) -Label 'Old full package'
        $delta = Resolve-FixturePath -Root $stagedNew -RelativePath ([string]$fixture.new_delta_relative_path) -Label 'New delta package'
        $package = Join-Path $evidenceRoot 'reconstructed-new-full.nupkg'
        Invoke-CheckedProcess -File $updater -Arguments @('patch','--old',$oldFull,'--delta',$delta,'--output',$package) -WorkingDirectory $installRoot | Out-Null
    } else {
        $full = Resolve-FixturePath -Root $stagedNew -RelativePath ([string]$fixture.new_full_relative_path) -Label 'New full package'
        $package = Join-Path $evidenceRoot 'selected-new-full.nupkg'
        Copy-Item -LiteralPath $full -Destination $package
    }

    if ($FailureInjection -ceq 'DownloadInterrupted') {
        $partial = Join-Path $evidenceRoot 'injected-partial-download.nupkg'
        $input = [IO.File]::OpenRead($package)
        $output = [IO.File]::Open($partial, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $remaining = [Math]::Max(1, [Math]::Floor($input.Length / 3))
            $buffer = New-Object byte[] 65536
            while ($remaining -gt 0) {
                $read = $input.Read($buffer, 0, [Math]::Min($buffer.Length, $remaining))
                if ($read -le 0) { break }
                $output.Write($buffer, 0, $read); $remaining -= $read
            }
        } finally { $input.Dispose(); $output.Dispose() }
        Assert-OldVersionStillLaunches -OldExecutable $oldMain -RunId $runId | Out-Null
        $after = Invoke-StateDriver -Driver $driver -Mode 'probe' -RunId $runId -ProfileRoot $profile -FixturePath $fixturePath -OutputPath (Join-Path $evidenceRoot 'state-after.json')
        Assert-MigrationStatePreserved -Before $before -After $after -RunId $runId
        $evidence.AfterState = $after; $evidence.FailureRollbackVerified = $true; $evidence.Succeeded = $true
    } elseif ($FailureInjection -ceq 'ApplicationFailed') {
        $corrupt = Join-Path $evidenceRoot 'injected-corrupt-application.nupkg'
        Copy-Item -LiteralPath $package -Destination $corrupt
        $stream = [IO.File]::Open($corrupt, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.SetLength([Math]::Max(1, [Math]::Floor($stream.Length / 2))) } finally { $stream.Dispose() }
        Invoke-CheckedProcess -File $updater -Arguments @('--rootDir',$installRoot,'apply','--norestart','--package',$corrupt) -WorkingDirectory $installRoot -ExpectFailure | Out-Null
        Assert-OldVersionStillLaunches -OldExecutable $oldMain -RunId $runId | Out-Null
        $after = Invoke-StateDriver -Driver $driver -Mode 'probe' -RunId $runId -ProfileRoot $profile -FixturePath $fixturePath -OutputPath (Join-Path $evidenceRoot 'state-after.json')
        Assert-MigrationStatePreserved -Before $before -After $after -RunId $runId
        $evidence.AfterState = $after; $evidence.FailureRollbackVerified = $true; $evidence.Succeeded = $true
    } else {
        $restartStarted = [DateTimeOffset]::UtcNow
        Invoke-CheckedProcess -File $updater -Arguments @('--rootDir',$installRoot,'apply','--package',$package,'--','--migration-gate-restarted',$runId) -WorkingDirectory $installRoot | Out-Null
        $currentMain = Resolve-FixturePath -Root $installRoot -RelativePath ([string]$fixture.new_main_relative_path) -Label 'CurrentMainExecutable'
        if ([IO.Path]::GetFileName($currentMain) -cne $script:NewMainExecutableName -or
            -not ([Diagnostics.FileVersionInfo]::GetVersionInfo($currentMain).ProductVersion.StartsWith('1.1.1', [StringComparison]::Ordinal))) {
            throw 'CurrentMainExecutable must be the real 1.1.1 Chinese executable.'
        }
        Assert-AuthenticodeSamePublisher -Driver $driver -ApplicationExecutable $currentMain
        Assert-PeMachineAmd64 -Executable $currentMain -Label 'CurrentMainExecutable'
        $restart = Find-ExactProcessPath -ExpectedPath $currentMain -NotBefore $restartStarted
        $restartEvidence = [ordered]@{ Id = $restart.Id; Path = $restart.Path; StartTimeUtc = $restart.StartTime.ToUniversalTime().ToString('O') }
        Stop-OwnedProcessTree -RootProcessId $restart.Id
        $rootLauncher = Resolve-FixturePath -Root $installRoot -RelativePath ([string]$fixture.root_launcher_relative_path) -Label 'InstallRootLauncher'
        $legacy = Resolve-FixturePath -Root $installRoot -RelativePath ([string]$fixture.legacy_launcher_relative_path) -Label 'LegacyCompatibilityLauncher'
        if ([IO.Path]::GetFileName($rootLauncher) -cne $script:NewMainExecutableName -or
            [IO.Path]::GetFileName($legacy) -cne $script:OldMainExecutableName) {
            throw 'The root launcher or legacy compatibility launcher has the wrong executable name.'
        }
        foreach ($launcher in @($rootLauncher, $legacy)) {
            Assert-AuthenticodeSamePublisher -Driver $driver -ApplicationExecutable $launcher
            Assert-PeMachineAmd64 -Executable $launcher -Label ([IO.Path]::GetFileName($launcher))
        }
        if ($AssetKind -ceq 'Setup') {
            $surfaces = Assert-SetupSurfaces -InstallRoot $installRoot -CurrentMain $currentMain -RootLauncher $rootLauncher -LegacyLauncher $legacy -Profile $profile -PackageId ([string]$fixture.package_id)
        } else {
            $surfaces = [ordered]@{ InstallRoot=$installRoot; CurrentDirectory=(Split-Path -Parent $currentMain); InstallRootLauncher=$rootLauncher; CurrentMainExecutable=$currentMain; LegacyCompatibilityLauncher=$legacy; DesktopShortcut=$null; StartMenuShortcut=$null; UninstallEntry=$null }
        }
        $surfaces.UpgradeRestartProcess = $restartEvidence
        $compatStarted = [DateTimeOffset]::UtcNow
        Start-Process -FilePath $legacy -ArgumentList @('--migration-gate-compat', $runId) -WorkingDirectory (Split-Path -Parent $legacy) | Out-Null
        $compatTarget = Find-ExactProcessPath -ExpectedPath $currentMain -NotBefore $compatStarted
        Stop-OwnedProcessTree -RootProcessId $compatTarget.Id
        $surfaces.LegacyCompatibilityForwardTarget = $currentMain
        $after = Invoke-StateDriver -Driver $driver -Mode 'probe' -RunId $runId -ProfileRoot $profile -FixturePath $fixturePath -OutputPath (Join-Path $evidenceRoot 'state-after.json')
        Assert-MigrationStatePreserved -Before $before -After $after -RunId $runId
        $evidence.Surfaces = $surfaces; $evidence.AfterState = $after; $evidence.Succeeded = $true
    }
} catch {
    $evidence.Error = $_.Exception.Message
    throw
} finally {
    if ($evidenceRoot) {
        $evidence.FinishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Write-CanonicalJson -Value $evidence -Destination (Join-Path $evidenceRoot 'evidence.json')
        $hashes = @(Get-FileInventory -Roots @($evidenceRoot))
        Write-CanonicalJson -Value ([ordered]@{ SchemaVersion = 1; RunId = $runId; Files = $hashes }) -Destination (Join-Path $evidenceRoot 'files.sha256.json')
    }
}

if (-not $evidence.Succeeded) { throw 'The migration gate did not complete successfully.' }
Write-Host "REAL_MIGRATION_GATE_PASS=$evidenceRoot"
