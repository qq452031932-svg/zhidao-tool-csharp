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
Add-Type -AssemblyName System.Security
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Text;
public sealed class MigrationBoundedProcessResult {
    public int ExitCode;
    public string StandardOutput;
    public string StandardError;
}
public static class MigrationBoundedProcessRunner {
    private static bool KillTree(int id) {
        try {
            using (var killer = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + id + " /T /F") {
                UseShellExecute = false, CreateNoWindow = true
            })) {
                return killer != null && killer.WaitForExit(10000) && killer.ExitCode == 0;
            }
        } catch { return false; }
    }
    public static MigrationBoundedProcessResult Run(string file, string arguments, string workingDirectory, int timeoutMs, int maximumBytes) {
        var info = new ProcessStartInfo(file, arguments) {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var process = new Process { StartInfo = info }) {
            var output = new StringBuilder(); var error = new StringBuilder();
            var sync = new object(); var bytes = 0; var overflow = false;
            DataReceivedEventHandler appendOutput = (sender, e) => {
                if (e.Data == null) return;
                lock (sync) {
                    bytes += Encoding.UTF8.GetByteCount(e.Data) + 1;
                    if (bytes > maximumBytes) overflow = true; else output.Append(e.Data).Append('\n');
                }
                if (overflow) KillTree(process.Id);
            };
            DataReceivedEventHandler appendError = (sender, e) => {
                if (e.Data == null) return;
                lock (sync) {
                    bytes += Encoding.UTF8.GetByteCount(e.Data) + 1;
                    if (bytes > maximumBytes) overflow = true; else error.Append(e.Data).Append('\n');
                }
                if (overflow) KillTree(process.Id);
            };
            process.OutputDataReceived += appendOutput; process.ErrorDataReceived += appendError;
            if (!process.Start()) throw new InvalidOperationException("Unable to start bounded process.");
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            if (!process.WaitForExit(timeoutMs)) {
                KillTree(process.Id);
                if (!process.WaitForExit(10000)) throw new InvalidOperationException("Timed-out process tree could not be terminated.");
                throw new TimeoutException("Bounded process timed out.");
            }
            process.WaitForExit();
            if (overflow) throw new InvalidOperationException("Bounded process output exceeded the byte limit.");
            return new MigrationBoundedProcessResult { ExitCode = process.ExitCode, StandardOutput = output.ToString(), StandardError = error.ToString() };
        }
    }
}
'@

# This gate deliberately has no network client. Its only asset source is the two
# caller-supplied local directories copied into the run-owned evidence directory.
$script:OldMainExecutableName = 'BaiduBatchTransfer.App.exe'
# UTF-8 contract text: 知道综合工具.exe. Constructed from code points because Windows
# PowerShell 5.1 decodes UTF-8-without-BOM scripts through the active ANSI code page.
$script:ChineseDisplayName = "$([char]0x77E5)$([char]0x9053)$([char]0x7EFC)$([char]0x5408)$([char]0x5DE5)$([char]0x5177)"
$script:NewMainExecutableName = $script:ChineseDisplayName + '.exe'
$script:IsolationDirectoryName = '.velopack-rename-migration-isolation'
$script:FixtureName = 'migration-gate-fixture.json'
$script:FixtureSignatureName = 'migration-gate-fixture.json.sig'
$script:ExpectedPublisherSpkiSha256 = 'f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff'
$script:PublisherCertificatePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'eng\ota\SuperPan.OtaPublisher.cer'
$script:MaximumFixtureBytes = 65536
$script:MaximumDriverOutputBytes = 1048576
$script:OwnedPaths = New-Object System.Collections.Generic.List[string]

function New-DerElement([byte]$Tag, [byte[]]$Content) {
    if ($Content.Length -lt 128) { [byte[]]$length = [byte]$Content.Length }
    else {
        $lengthBytes = New-Object Collections.Generic.List[byte]
        $remaining = [uint64]$Content.Length
        while ($remaining -gt 0) { $lengthBytes.Insert(0, [byte]($remaining -band 0xff)); $remaining = $remaining -shr 8 }
        [byte[]]$length = @([byte](0x80 -bor $lengthBytes.Count)) + $lengthBytes.ToArray()
    }
    $result = New-Object byte[] (1 + $length.Length + $Content.Length)
    $result[0] = $Tag
    [Array]::Copy($length, 0, $result, 1, $length.Length)
    [Array]::Copy($Content, 0, $result, 1 + $length.Length, $Content.Length)
    return ,$result
}

function Convert-OidToDerValue([string]$Oid) {
    if ($Oid -cnotmatch '^[0-2](\.(0|[1-9][0-9]*)){1,}$') { throw 'Certificate public-key OID is invalid.' }
    [uint64[]]$parts = @($Oid.Split('.') | ForEach-Object { [uint64]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
    if ($parts[0] -lt 2 -and $parts[1] -gt 39) { throw 'Certificate public-key OID is invalid.' }
    [uint64[]]$values = @([uint64](40 * $parts[0] + $parts[1])) + @($parts | Select-Object -Skip 2)
    $encoded = New-Object Collections.Generic.List[byte]
    foreach ($value in $values) {
        $component = New-Object Collections.Generic.List[byte]
        $remaining = [uint64]$value
        do { $component.Insert(0, [byte]($remaining -band 0x7f)); $remaining = $remaining -shr 7 } while ($remaining -gt 0)
        for ($index = 0; $index -lt $component.Count - 1; $index++) { $component[$index] = [byte]($component[$index] -bor 0x80) }
        $encoded.AddRange($component)
    }
    return ,$encoded.ToArray()
}

function Get-CertificateSubjectPublicKeyInfo([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    [byte[]]$oid = New-DerElement 0x06 (Convert-OidToDerValue $Certificate.PublicKey.Oid.Value)
    [byte[]]$parameters = $Certificate.PublicKey.EncodedParameters.RawData
    [byte[]]$algorithm = New-DerElement 0x30 ([byte[]]($oid + $parameters))
    [byte[]]$keyValue = $Certificate.PublicKey.EncodedKeyValue.RawData
    [byte[]]$bitString = New-DerElement 0x03 ([byte[]](@(0) + $keyValue))
    return New-DerElement 0x30 ([byte[]]($algorithm + $bitString))
}

function Get-CertificateSpkiSha256 {
    param([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash((Get-CertificateSubjectPublicKeyInfo $Certificate)) | ForEach-Object { $_.ToString('x2') }) -join '') }
    finally { $sha.Dispose() }
}

function Import-P256SubjectPublicKeyInfo([byte[]]$Der) {
    [byte[]]$prefix=0x30,0x59,0x30,0x13,0x06,0x07,0x2a,0x86,0x48,0xce,0x3d,0x02,0x01,0x06,0x08,0x2a,0x86,0x48,0xce,0x3d,0x03,0x01,0x07,0x03,0x42,0x00,0x04
    if($Der.Length-ne 91){throw 'License SPKI length is invalid.'}
    for($index=0;$index-lt $prefix.Length;$index++){if($Der[$index]-ne $prefix[$index]){throw 'License SPKI is not canonical P-256.'}}
    [byte[]]$blob=0x45,0x43,0x53,0x31,0x20,0,0,0
    [Array]::Resize([ref]$blob,72);[Array]::Copy($Der,27,$blob,8,64)
    return [Security.Cryptography.CngKey]::Import($blob,[Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
}

function Copy-DerEcdsaInteger([byte[]]$Signature,[ref]$Offset,[byte[]]$Output,[int]$TargetOffset) {
    if($Offset.Value+2-gt $Signature.Length-or $Signature[$Offset.Value]-ne 0x02){throw 'ECDSA signature integer is invalid.'}
    $length=[int]$Signature[$Offset.Value+1];$Offset.Value+=2
    if($length-lt 1-or $length-gt 33-or $Offset.Value+$length-gt $Signature.Length-or ($Signature[$Offset.Value]-band 0x80)-ne 0){throw 'ECDSA signature integer is invalid.'}
    if($Signature[$Offset.Value]-eq 0){$Offset.Value++;$length--};if($length-gt 32){throw 'ECDSA signature integer is invalid.'}
    [Array]::Copy($Signature,$Offset.Value,$Output,$TargetOffset+32-$length,$length);$Offset.Value+=$length
}

function Convert-DerEcdsaSignatureToP1363([byte[]]$Signature) {
    if($Signature.Length-lt 8-or $Signature[0]-ne 0x30-or $Signature[1]-ge 0x80-or $Signature[1]-ne $Signature.Length-2){throw 'ECDSA signature encoding is invalid.'}
    $output=New-Object byte[] 64;$offset=2
    Copy-DerEcdsaInteger $Signature ([ref]$offset) $output 0;Copy-DerEcdsaInteger $Signature ([ref]$offset) $output 32
    if($offset-ne $Signature.Length){throw 'ECDSA signature encoding is invalid.'};return ,$output
}

function Assert-AssetFileName {
    param([string]$FileName, [string]$Label)
    if ([string]::IsNullOrWhiteSpace($FileName) -or [IO.Path]::IsPathRooted($FileName) -or
        [IO.Path]::GetFileName($FileName) -cne $FileName -or $FileName -in @('.', '..')) {
        throw "$Label must be one top-level local asset file name."
    }
    return $FileName
}

function Assert-CanonicalSignedFixture {
    param([string]$FixturePath, [string]$SignaturePath, [string]$CertificatePath, [string]$ExpectedSpki)
    foreach ($file in @($FixturePath, $SignaturePath, $CertificatePath)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Signed fixture input is missing: $file" }
        Assert-NoReparsePoint -ExistingPath $file -Label 'Signed fixture input'
    }
    $bytes = [IO.File]::ReadAllBytes($FixturePath)
    if ($bytes.Length -lt 3 -or $bytes.Length -gt 65536 -or
        ($bytes.Length -ge 3 -and $bytes[0] -eq 0xef -and $bytes[1] -eq 0xbb -and $bytes[2] -eq 0xbf) -or
        $bytes[$bytes.Length - 1] -ne 0x0a -or $bytes -contains 0x0d) {
        throw 'Migration fixture must be bounded canonical UTF-8 with LF and no BOM.'
    }
    $utf8 = New-Object Text.UTF8Encoding($false, $true)
    try { $json = $utf8.GetString($bytes) } catch { throw 'Migration fixture is not canonical UTF-8.' }
    try { $fixture = $json | ConvertFrom-Json } catch { throw 'Migration fixture JSON is malformed.' }
    $expectedProperties = @('license_signing_key_id','license_signing_spki_base64','new_delta_file','new_full_file','old_full_file','old_portable_file','old_setup_file','schema_version','state_driver_file','state_driver_sha256')
    $actualProperties = @($fixture.PSObject.Properties.Name | Sort-Object)
    if (($actualProperties -join "`n") -cne ($expectedProperties -join "`n") -or $fixture.schema_version -ne 1) {
        throw 'Migration fixture schema is not exactly version 1.'
    }
    foreach ($name in @('new_delta_file','new_full_file','old_full_file','old_portable_file','old_setup_file','state_driver_file')) {
        Assert-AssetFileName -FileName ([string]$fixture.$name) -Label $name | Out-Null
    }
    if ([string]$fixture.state_driver_sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'state_driver_sha256 is invalid.' }
    if ([string]$fixture.license_signing_key_id -cnotmatch '^[A-Za-z0-9._-]{1,128}$') { throw 'license_signing_key_id is invalid.' }
    try { $licenseSpki=[Convert]::FromBase64String([string]$fixture.license_signing_spki_base64) } catch { throw 'license_signing_spki_base64 is invalid.' }
    if ($licenseSpki.Length -ne 91 -or [Convert]::ToBase64String($licenseSpki) -cne [string]$fixture.license_signing_spki_base64) { throw 'license signing SPKI must be canonical P-256 SPKI DER base64.' }
    $licenseKey=$null
    try { $licenseKey=Import-P256SubjectPublicKeyInfo $licenseSpki } finally { if($licenseKey){$licenseKey.Dispose()} }
    $canonical = ConvertTo-CanonicalJson $fixture
    $canonicalBytes = $utf8.GetBytes($canonical + "`n")
    if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$bytes, [byte[]]$canonicalBytes)) { throw 'Migration fixture JSON is not canonical.' }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([IO.File]::ReadAllBytes($CertificatePath))
    try {
        if ((Get-CertificateSpkiSha256 $certificate) -cne $ExpectedSpki) { throw 'Migration fixture publisher certificate SPKI mismatch.' }
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
        try {
            $signature = [IO.File]::ReadAllBytes($SignaturePath)
            if (-not $rsa -or $signature.Length -ne ($rsa.KeySize / 8) -or
                -not $rsa.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
                throw 'Migration fixture detached RSA SHA-256/PKCS1 signature is invalid.'
            }
        } finally { if ($rsa) { $rsa.Dispose() } }
    } finally { $certificate.Dispose() }
    return $fixture
}

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

function Assert-NoNonLoopbackNetwork {
    $adapters = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {
        $_.ifIndex -ne 1 -and [string]$_.InterfaceDescription -notmatch '(?i)loopback'
    })
    $enabled = @($adapters | Where-Object { [string]$_.Status -notin @('Disabled','Not Present') })
    if ($enabled.Count -ne 0) { throw 'Every non-loopback network adapter must be disabled in the isolated migration environment.' }
    $reachableRoutes = @(
        Get-NetRoute -AddressFamily IPv4 -ErrorAction Stop
        Get-NetRoute -AddressFamily IPv6 -ErrorAction Stop
    ) | Where-Object { $_.ifIndex -ne 1 -and [string]$_.State -notin @('Invalid','Unreachable') }
    if (@($reachableRoutes).Count -ne 0) { throw 'The isolated migration environment must have no reachable IPv4 or IPv6 non-loopback route.' }
    return [ordered]@{ NonLoopbackAdapters=$adapters.Count; ReachableRoutes=0; VerifiedUtc=[DateTimeOffset]::UtcNow.ToString('O') }
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
    $principal = New-Object Security.Principal.WindowsPrincipal $identity
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The migration gate must run as a non-Administrators isolated user token.'
    }
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

function New-RunChallenge {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash=(($sha.ComputeHash($bytes)|ForEach-Object{$_.ToString('x2')}) -join '') } finally { $sha.Dispose() }
    return [ordered]@{ Base64=[Convert]::ToBase64String($bytes); Sha256=$hash }
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

function Get-StreamingSha256 {
    param([string]$File)
    $stream = [IO.File]::Open($File, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) -join '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Get-Utf8Sha256 {
    param([string]$Text)
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash((New-Object Text.UTF8Encoding($false,$true)).GetBytes($Text))|ForEach-Object{$_.ToString('x2')}) -join '') }
    finally { $sha.Dispose() }
}

function Get-FileHardLinkCount {
    param([string]$File)
    $output = @(& fsutil.exe hardlink list $File 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Unable to inspect hard links for '$File'." }
    $links = @($output | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($links.Count -ne 1) { throw "State file has $($links.Count) hard links: $File" }
    return 1
}

function Assert-ExactProfileFile {
    param([string]$Actual, [string]$Expected, [long]$MaximumBytes, [string]$Label)
    $canonicalActual = [IO.Path]::GetFullPath($Actual)
    $canonicalExpected = [IO.Path]::GetFullPath($Expected)
    if (-not [string]::Equals($canonicalActual, $canonicalExpected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label path is not the fixed isolated-profile path."
    }
    if (-not (Test-Path -LiteralPath $canonicalActual -PathType Leaf)) { throw "$Label is missing." }
    Assert-NoReparseAncestors -ExistingPath $canonicalActual -Label $Label
    $info = Get-Item -LiteralPath $canonicalActual -Force
    if ($info.Length -lt 1 -or $info.Length -gt $MaximumBytes) { throw "$Label size is outside the allowed bound." }
    Get-FileHardLinkCount -File $canonicalActual | Out-Null
    return $info
}

function Assert-StateEvidence {
    param([string]$StateJson, [string]$StateJsonPath, [string]$ProfileRoot, [string]$RunId, [string]$ExpectedChallengeSha256, [DateTimeOffset]$WindowStartedUtc, [DateTimeOffset]$WindowCompletedUtc)
    if ([string]::IsNullOrWhiteSpace($StateJson)) {
        if ([string]::IsNullOrWhiteSpace($StateJsonPath)) { throw 'State evidence JSON is missing.' }
        $StateJson = Get-Content -LiteralPath $StateJsonPath -Raw -Encoding UTF8
    }
    try { $state = $StateJson | ConvertFrom-Json } catch { throw 'State evidence JSON is malformed.' }
    if ([string]$state.run_id -cne $RunId -or [string]$state.challenge_sha256 -cne $ExpectedChallengeSha256) { throw 'State query run id or challenge does not match this invocation.' }
    $queryTime=[DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact([string]$state.query_completed_utc,'O',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$queryTime) -or
        $queryTime -lt $WindowStartedUtc -or $queryTime -gt $WindowCompletedUtc) { throw 'State query time is outside this invocation window.' }
    # The fixed-production-signed, signed-fixture-hash-pinned driver is the TCB for
    # SQLite row selection. This detects replay/bugs, not a malicious trusted TCB.
    foreach ($kind in @('account','settings','task')) {
        $markerName = $kind + '_marker'
        $countName = $kind + '_marker_count'
        $hashName = $kind + '_row_sha256'
        $rowName = $kind + '_row'
        $canonicalRow = ConvertTo-CanonicalJson $state.$rowName
        if (-not $state.$rowName -or [string]$state.$markerName -cne $RunId -or [string]$state.$rowName.marker -cne $RunId -or [int]$state.$countName -ne 1 -or
            [string]$state.$hashName -cne (Get-Utf8Sha256 $canonicalRow)) {
            throw "$kind marker must be one unique canonical row bound to the current run id and row hash."
        }
    }
    $expectedDatabase = Join-Path $ProfileRoot 'AppData\Local\BaiduBatchTransfer\data\baidu-batch-transfer.db'
    $expectedLicense = Join-Path $ProfileRoot 'AppData\Local\SuperPan\Licensing\v1\licenses.dat'
    $database = Assert-ExactProfileFile -Actual ([string]$state.database_path) -Expected $expectedDatabase -MaximumBytes 536870912 -Label 'Database state file'
    $license = Assert-ExactProfileFile -Actual ([string]$state.license_path) -Expected $expectedLicense -MaximumBytes 1048576 -Label 'License state file'
    $databaseHash = Get-StreamingSha256 -File $database.FullName
    $licenseHash = Get-StreamingSha256 -File $license.FullName
    if ([string]$state.database_file_sha256 -cne $databaseHash -or [long]$state.database_length -ne $database.Length) {
        throw 'Database state file hash or length is not independently bound to driver evidence.'
    }
    if ([string]$state.license_file_sha256 -cne $licenseHash -or [long]$state.license_length -ne $license.Length) {
        throw 'License state file hash or length is not independently bound to driver evidence.'
    }
    if ($state.production_trust_chain -ne $true -or [string]$state.signed_payload_sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'The trusted driver did not verify the production-chain license signature.'
    }
    $expiry = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact([string]$state.expires_at_utc, 'O', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$expiry) -or
        $expiry -le [DateTimeOffset]::UtcNow) { throw 'The production-chain isolation license is expired or malformed.' }
    $activation = [Guid]::Empty
    if (-not [Guid]::TryParseExact([string]$state.activation_request_id, 'D', [ref]$activation)) { throw 'activation_request_id is invalid.' }
    if ($state.environment_authorization_is_auxiliary -ne $true -or $state.network_accessed -ne $false -or
        [string]$state.license_source -cne 'preissued-local-production-chain-license') {
        throw 'License state evidence is not an offline preissued production-chain authorization.'
    }
    return $state
}

function Assert-StateEvidencePreserved {
    param($Before, $After)
    foreach ($field in @(
        'account_marker','account_marker_count','account_row_sha256','settings_marker','settings_marker_count','settings_row_sha256',
        'task_marker','task_marker_count','task_row_sha256','database_path','database_file_sha256','database_length',
        'license_path','license_file_sha256','license_length','signed_payload_sha256','expires_at_utc','activation_request_id','license_source')) {
        if ([string]$Before.$field -cne [string]$After.$field) { throw "Preserved state field changed: $field" }
    }
    if ($After.reactivation_required -ne $false) { throw 'The upgraded application required reactivation.' }
}

function Assert-IndependentLicenseAudit {
    param($State,[string]$ProfileRoot,$Fixture)
    $licensePath=Join-Path $ProfileRoot 'AppData\Local\SuperPan\Licensing\v1\licenses.dat'
    Assert-ExactProfileFile -Actual $licensePath -Expected $licensePath -MaximumBytes 1048576 -Label 'License state file'|Out-Null
    $entropy=[Security.Cryptography.SHA256]::Create()
    try{$purposeHash=$entropy.ComputeHash([Text.Encoding]::UTF8.GetBytes('SuperPan.Licensing.State.v1'))}finally{$entropy.Dispose()}
    try{$plaintext=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($licensePath),$purposeHash,[Security.Cryptography.DataProtectionScope]::CurrentUser)}catch{throw 'License DPAPI CurrentUser audit failed.'}
    try {
        if($plaintext.Length-lt 2-or $plaintext.Length-gt 786432){throw 'License plaintext size is invalid.'}
        $utf8=New-Object Text.UTF8Encoding($false,$true)
        try{$document=$utf8.GetString($plaintext)|ConvertFrom-Json}catch{throw 'License plaintext JSON is invalid.'}
        if($document.version-ne 1){throw 'License state version is invalid.'}
        $credentials=@($document.credentials|Where-Object{[string]$_.activation_request_id-ceq[string]$State.activation_request_id})
        if($credentials.Count-ne 1){throw 'License activation request id is not unique in DPAPI state.'}
        $leases=@($credentials[0].leases|Where-Object{[string]$_.software_id-ceq'8270e8d7-1791-487d-ad76-0da7a0cbed12'-and[string]$_.machine_hash-ceq[string]$State.machine_hash})
        if($leases.Count-ne 1){throw 'License ProductId/machine lease is not unique in DPAPI state.'}
        $lease=$leases[0];$expires=[DateTimeOffset]$lease.expires_at;$leaseUntil=[DateTimeOffset]$lease.lease_until
        if($expires-le[DateTimeOffset]::UtcNow-or $leaseUntil-le[DateTimeOffset]::UtcNow-or $leaseUntil-gt$expires-or $expires.ToString('O')-cne[string]$State.expires_at_utc){throw 'License expiry/lease audit failed.'}
        try{$envelope=[string]$lease.signed_payload|ConvertFrom-Json;$payload=[Convert]::FromBase64String([string]$envelope.payload);$signature=[Convert]::FromBase64String([string]$envelope.signature)}catch{throw 'Stored signed license envelope is malformed.'}
        if([string]$envelope.key_id-cne[string]$Fixture.license_signing_key_id-or (Get-Utf8Sha256 ([string]$lease.signed_payload))-cne[string]$State.signed_payload_sha256){throw 'Stored license key id or signed-envelope hash audit failed.'}
        $cng=$null;$ecdsa=$null
        try{$cng=Import-P256SubjectPublicKeyInfo ([Convert]::FromBase64String([string]$Fixture.license_signing_spki_base64));$ecdsa=[Security.Cryptography.ECDsaCng]::new($cng);$ecdsa.HashAlgorithm=[Security.Cryptography.CngAlgorithm]::Sha256;if(-not$ecdsa.VerifyData($payload,(Convert-DerEcdsaSignatureToP1363 $signature))){throw 'Stored license production P-256 signature is invalid.'}}
        finally{if($ecdsa){$ecdsa.Dispose()};if($cng){$cng.Dispose()}}
        $claims=$utf8.GetString($payload)|ConvertFrom-Json
        if([string]$claims.request_id-cne[string]$State.activation_request_id-or[string]$claims.software_id-cne'8270e8d7-1791-487d-ad76-0da7a0cbed12'-or[string]$claims.machine_hash-cne[string]$State.machine_hash-or[string]$claims.decision-cne'allow'-or[string]$claims.code-cne'ok'){throw 'Signed license claims bindings are invalid.'}
        return [ordered]@{KeyId=[string]$envelope.key_id;SoftwareId=[string]$claims.software_id;MachineHash=[string]$claims.machine_hash;RequestId=[string]$claims.request_id;ExpiresAtUtc=$expires.ToString('O');LeaseUntilUtc=$leaseUntil.ToString('O')}
    } finally { if($plaintext){[Array]::Clear($plaintext,0,$plaintext.Length)} }
}

function Assert-UpgradeEvidence {
    param(
        [string]$EvidenceJson, [string]$RunId, [string]$ExpectedChallengeSha256, [string]$Scenario,
        [string]$AssetKind, [string]$Path, [string]$FailureInjection, [string]$ExpectedAssetSha256,
        [long]$ExpectedAssetBytes, [string]$ExpectedUpdaterPath, [string]$ObservedUpdaterJson,
        [DateTimeOffset]$WindowStartedUtc, [DateTimeOffset]$WindowCompletedUtc, [string]$RunOwnedRoot
    )
    try { $upgrade = $EvidenceJson | ConvertFrom-Json } catch { throw 'Upgrade evidence JSON is malformed.' }
    if ([string]$upgrade.run_id -cne $RunId -or [string]$upgrade.challenge_sha256 -cne $ExpectedChallengeSha256) { throw 'Upgrade evidence run id or challenge does not match this invocation.' }
    if ([string]$upgrade.scenario -cne $Scenario -or [string]$upgrade.asset_kind -cne $AssetKind -or [string]$upgrade.path -cne $Path -or [string]$upgrade.failure_injection -cne $FailureInjection) { throw 'Upgrade evidence scenario fields do not match this invocation.' }
    if ([string]$upgrade.source_asset_sha256 -cne $ExpectedAssetSha256 -or [long]$upgrade.source_asset_length -ne $ExpectedAssetBytes) { throw 'Upgrade evidence is not bound to the selected local source asset.' }
    try { $observed=$ObservedUpdaterJson|ConvertFrom-Json } catch { throw 'ObservedUpdater evidence is malformed.' }
    if (-not $observed -or [int]$observed.Pid -le 0) { throw 'An independently observed updater process is required.' }
    if ([IO.Path]::GetFullPath([string]$observed.Path) -cne [IO.Path]::GetFullPath($ExpectedUpdaterPath) -or
        [int]$upgrade.updater_pid -ne [int]$observed.Pid -or [string]$upgrade.updater_path -cne [string]$observed.Path -or
        [string]$upgrade.updater_started_utc -cne [string]$observed.StartedUtc) { throw 'Driver updater evidence does not match the independently observed install-root Update.exe process.' }
    $started=[DateTimeOffset]::MinValue; $completed=[DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact([string]$upgrade.started_utc,'O',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$started) -or
        -not [DateTimeOffset]::TryParseExact([string]$upgrade.completed_utc,'O',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$completed) -or
        $started -lt $WindowStartedUtc -or $completed -gt $WindowCompletedUtc -or $completed -lt $started) { throw 'Upgrade timestamps are outside this invocation window.' }
    if ($upgrade.update_manager_invoked -ne $true -or [string]::IsNullOrWhiteSpace([string]$upgrade.update_manager_stage)) { throw 'The trusted driver did not invoke a named Velopack UpdateManager stage.' }
    if ($FailureInjection -ceq 'None') {
        if ($upgrade.updater_invoked -ne $true -or [int]$upgrade.updater_exit_code -ne 0 -or $upgrade.update_manager_succeeded -ne $true -or [string]$upgrade.update_manager_stage -cne 'apply-completed' -or [string]$upgrade.outcome -cne 'updated') {
            throw 'Successful migration did not execute the Velopack updater successfully.'
        }
    } elseif ($FailureInjection -ceq 'DownloadInterrupted') {
        $partial=Assert-StrictChildPath -Candidate ([string]$upgrade.partial_artifact_path) -Root $RunOwnedRoot -Label 'Partial download artifact'
        if (-not (Test-Path -LiteralPath $partial -PathType Leaf)) { throw 'Partial download artifact was not created.' }
        Assert-NoReparseAncestors -ExistingPath $partial -Label 'Partial download artifact'
        $partialInfo=Get-Item -LiteralPath $partial
        if ($partialInfo.Length -le 0 -or $partialInfo.Length -ge $ExpectedAssetBytes -or [long]$upgrade.bytes_consumed -ne $partialInfo.Length -or [string]$upgrade.partial_artifact_sha256 -cne (Get-StreamingSha256 $partial) -or
            $upgrade.fault_observed -ne $true -or $upgrade.asset_opened -ne $true -or $upgrade.updater_invoked -ne $true -or [int]$upgrade.updater_exit_code -eq 0 -or $upgrade.update_manager_succeeded -ne $false -or [string]$upgrade.update_manager_stage -cne 'download-failed' -or
            [string]$upgrade.old_version_after_failure -cne '1.1.0' -or [string]$upgrade.outcome -cne 'failed-and-rolled-back') {
            throw 'DownloadInterrupted did not prove a real updater/UpdateManager failure over an independently hashed partial artifact and 1.1.0 rollback.'
        }
    } else {
        $hook=Assert-StrictChildPath -Candidate ([string]$upgrade.candidate_hook_trace_path) -Root $RunOwnedRoot -Label 'Candidate failure hook trace'
        if (-not (Test-Path -LiteralPath $hook -PathType Leaf) -or [string]$upgrade.candidate_hook_challenge_sha256 -cne $ExpectedChallengeSha256 -or [string]$upgrade.candidate_hook_trace_sha256 -cne (Get-StreamingSha256 $hook) -or
            $upgrade.updater_invoked -ne $true -or [int]$upgrade.updater_exit_code -eq 0 -or $upgrade.update_manager_succeeded -ne $false -or [string]$upgrade.update_manager_stage -cne 'apply-failed' -or $upgrade.fault_observed -ne $true -or
            [string]$upgrade.old_version_after_failure -cne '1.1.0' -or [string]$upgrade.outcome -cne 'failed-and-rolled-back') {
            throw 'ApplicationFailed did not prove an observed updater nonzero exit, challenge-bound candidate hook, UpdateManager failure, and 1.1.0 rollback.'
        }
    }
    return $upgrade
}

function Assert-PublisherExecutable {
    param([string]$Executable, [string]$ExpectedSpki = $script:ExpectedPublisherSpkiSha256)
    $signature = Get-AuthenticodeSignature -LiteralPath $Executable
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or -not $signature.SignerCertificate) {
        throw "Executable is not validly Authenticode signed: $Executable"
    }
    if ((Get-CertificateSpkiSha256 $signature.SignerCertificate) -cne $ExpectedSpki) {
        throw "Executable signer SPKI does not match the fixed production OTA publisher: $Executable"
    }
    Assert-PeMachineAmd64 -Executable $Executable -Label ([IO.Path]::GetFileName($Executable))
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
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $remaining = @($ids | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        if ($remaining.Count -eq 0) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Owned process tree could not be terminated: $($remaining -join ',')"
}

function ConvertTo-NativeCommandLine {
    param([string[]]$Arguments)
    return (($Arguments | ForEach-Object {
        $argument = [string]$_
        if ($argument.IndexOf([char]0) -ge 0) { throw 'A process argument contains a NUL character.' }
        '"' + [regex]::Replace($argument, '(\\*)"', '$1$1\"') + ([regex]::Match($argument, '\\+$').Value) + '"'
    }) -join ' ')
}

function Invoke-MonitoredProcess {
    param([string]$File, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutSeconds, [string]$ExpectedUpdater, [switch]$RequireUpdaterObservation)
    $token = [Guid]::NewGuid().ToString('N')
    $stdoutPath = Join-Path $WorkingDirectory ".migration-$token.stdout"
    $stderrPath = Join-Path $WorkingDirectory ".migration-$token.stderr"
    $started = [DateTimeOffset]::UtcNow
    Assert-NoNonLoopbackNetwork | Out-Null
    $process = Start-Process -FilePath $File -ArgumentList (ConvertTo-NativeCommandLine $Arguments) -WorkingDirectory $WorkingDirectory -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -NoNewWindow -PassThru
    $observedUpdater = $null
    try {
        $deadline = $started.AddSeconds($TimeoutSeconds)
        while (-not $process.HasExited) {
            try { Assert-NoNonLoopbackNetwork | Out-Null } catch { Stop-OwnedProcessTree -RootProcessId $process.Id; throw }
            $snapshot=@(Get-CimInstance Win32_Process -ErrorAction Stop)
            $descendantIds=New-Object Collections.Generic.List[int]
            $descendantIds.Add($process.Id)
            do {
                $added=$false
                foreach($item in $snapshot) { if ($descendantIds.Contains([int]$item.ParentProcessId) -and -not $descendantIds.Contains([int]$item.ProcessId)) { $descendantIds.Add([int]$item.ProcessId); $added=$true } }
            } while($added)
            foreach ($candidate in $snapshot) {
                if ($RequireUpdaterObservation -and $descendantIds.Contains([int]$candidate.ProcessId) -and [int]$candidate.ProcessId -ne $process.Id -and $candidate.ExecutablePath -and
                    [string]::Equals([IO.Path]::GetFullPath([string]$candidate.ExecutablePath), [IO.Path]::GetFullPath($ExpectedUpdater), [StringComparison]::OrdinalIgnoreCase)) {
                    $creation=$candidate.CreationDate
                    $creationUtc=if($creation-is[DateTime]){([DateTime]$creation).ToUniversalTime()}else{[Management.ManagementDateTimeConverter]::ToDateTime([string]$creation).ToUniversalTime()}
                    $observedUpdater = [ordered]@{ Pid=[int]$candidate.ProcessId; Path=[IO.Path]::GetFullPath([string]$candidate.ExecutablePath); StartedUtc=$creationUtc.ToString('O') }
                }
            }
            foreach ($outputPath in @($stdoutPath,$stderrPath)) {
                if ((Test-Path -LiteralPath $outputPath) -and (Get-Item -LiteralPath $outputPath).Length -gt $script:MaximumDriverOutputBytes) { Stop-OwnedProcessTree -RootProcessId $process.Id; throw 'Bounded process output exceeded the byte limit.' }
            }
            if ([DateTimeOffset]::UtcNow -ge $deadline) { Stop-OwnedProcessTree -RootProcessId $process.Id; throw "Bounded process timed out after $TimeoutSeconds seconds." }
            Start-Sleep -Milliseconds 20
            $process.Refresh()
        }
        $process.WaitForExit()
        if ($RequireUpdaterObservation -and -not $observedUpdater) { throw 'The expected install-root Update.exe descendant was not independently observed.' }
        [byte[]]$stdoutBytes = [IO.File]::ReadAllBytes($stdoutPath)
        [byte[]]$stderrBytes = [IO.File]::ReadAllBytes($stderrPath)
        if ($stdoutBytes.Length + $stderrBytes.Length -gt $script:MaximumDriverOutputBytes) { throw 'Bounded process output exceeded the byte limit.' }
        $utf8 = New-Object Text.UTF8Encoding($false, $true)
        try { $stdout=$utf8.GetString($stdoutBytes); $stderr=$utf8.GetString($stderrBytes) } catch { throw 'Bounded process output is not strict UTF-8.' }
        $process.Refresh();$exitCode=[int]$process.ExitCode;$processId=[int]$process.Id
        return [pscustomobject]@{ ExitCode=$exitCode; StandardOutput=$stdout; StandardError=$stderr; ProcessId=$processId; StartedUtc=$started.ToString('O'); CompletedUtc=[DateTimeOffset]::UtcNow.ToString('O'); ObservedUpdater=$observedUpdater }
    } finally {
        $process.Dispose()
        foreach ($ownedOutput in @($stdoutPath,$stderrPath)) {
            $cleanupDeadline=[DateTimeOffset]::UtcNow.AddSeconds(5)
            while(Test-Path -LiteralPath $ownedOutput){try{Remove-Item -LiteralPath $ownedOutput -Force -ErrorAction Stop}catch [IO.IOException]{if([DateTimeOffset]::UtcNow-ge$cleanupDeadline){throw};Start-Sleep -Milliseconds 50}}
        }
        Assert-NoNonLoopbackNetwork | Out-Null
    }
}

function Invoke-BoundedDriver {
    param([string]$Driver, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutSeconds = 120, [string]$ExpectedUpdater, [switch]$RequireUpdaterObservation)
    $result = Invoke-MonitoredProcess -File $Driver -Arguments $Arguments -WorkingDirectory $WorkingDirectory -TimeoutSeconds $TimeoutSeconds -ExpectedUpdater $ExpectedUpdater -RequireUpdaterObservation:$RequireUpdaterObservation
    if ($result.ExitCode -ne 0) { throw "Trusted state driver failed with exit code $($result.ExitCode): $($result.StandardError)" }
    if (-not [string]::IsNullOrEmpty($result.StandardError)) { throw 'Trusted state driver wrote unexpected stderr.' }
    [byte[]]$outputBytes = (New-Object Text.UTF8Encoding($false, $true)).GetBytes($result.StandardOutput)
    if ($outputBytes.Length -lt 3 -or $outputBytes.Length -gt $script:MaximumDriverOutputBytes -or $outputBytes[$outputBytes.Length - 1] -ne 0x0a -or $outputBytes -contains 0x0d) { throw 'Trusted state driver stdout must be one bounded canonical UTF-8/LF JSON record.' }
    try { $record=$result.StandardOutput|ConvertFrom-Json } catch { throw 'Trusted state driver stdout is malformed JSON.' }
    if ((ConvertTo-CanonicalJson $record) + "`n" -cne $result.StandardOutput) { throw 'Trusted state driver stdout JSON is not canonical.' }
    return $result
}

function Invoke-CheckedProcess {
    param([string]$File, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutSeconds = 120, [switch]$ExpectFailure)
    $result = Invoke-MonitoredProcess -File $File -Arguments $Arguments -WorkingDirectory $WorkingDirectory -TimeoutSeconds $TimeoutSeconds
    if (-not $ExpectFailure -and $result.ExitCode -ne 0) { throw "Process failed ($($result.ExitCode)): $File`n$($result.StandardOutput)$($result.StandardError)" }
    if ($ExpectFailure -and $result.ExitCode -eq 0) { throw "Injected failure unexpectedly succeeded: $File" }
    return [pscustomobject]@{ ExitCode=$result.ExitCode; Output=($result.StandardOutput+$result.StandardError) }
}

function Resolve-AssetFile {
    param([string]$Root, [string]$FileName, [string]$Label)
    Assert-AssetFileName -FileName $FileName -Label $Label | Out-Null
    return Resolve-FixturePath -Root $Root -RelativePath $FileName -Label $Label
}

function Expand-LocalPortableAsset {
    param([string]$Archive, [string]$Destination)
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            $name = ([string]$entry.FullName).Replace('/', '\')
            if ([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::IsPathRooted($name)) { throw 'Portable archive contains an invalid path.' }
            $target = Assert-StrictChildPath -Candidate (Join-Path $Destination $name) -Root $Destination -Label 'Portable archive entry'
            if ([string]::IsNullOrEmpty($entry.Name)) {
                New-Item -ItemType Directory -Path $target -Force | Out-Null
            } else {
                $parent = Split-Path -Parent $target
                if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
            }
        }
    } finally { $zip.Dispose() }
    foreach ($item in @(Get-ChildItem -LiteralPath $Destination -Force -Recurse)) { Assert-NoReparsePoint -ExistingPath $item.FullName -Label 'Portable archive output' }
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
    param([string]$InstallRoot)
    $roots = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*')
    $matches = @($roots | ForEach-Object { Get-ItemProperty -Path $_ -ErrorAction SilentlyContinue } |
        Where-Object { [string]$_.DisplayName -ceq $script:ChineseDisplayName })
    if ($matches.Count -ne 1) { throw "Expected one exact Chinese UninstallEntry, found $($matches.Count)." }
    $entry = $matches[0]
    $expectedUpdater = Join-Path $InstallRoot 'Update.exe'
    if ([IO.Path]::GetFullPath([string]$entry.InstallLocation).TrimEnd('\') -cne [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')) {
        throw 'UninstallEntry InstallLocation is not the isolated product install root.'
    }
    $uninstallString = [string]$entry.UninstallString
    if ($uninstallString -notlike "*$expectedUpdater*" -or $uninstallString -notmatch '(?i)(^|\s|\")--?uninstall(\s|$|\")') {
        throw 'UninstallEntry UninstallString must invoke this install root Update.exe with uninstall semantics.'
    }
    return [ordered]@{ Key = [string]$entry.PSPath; DisplayName = [string]$entry.DisplayName; InstallLocation = [string]$entry.InstallLocation; UninstallString = $uninstallString }
}

function Assert-CleanSetupSurfaces {
    param([string]$Profile)
    foreach ($shortcut in @(
        (Join-Path $Profile "Desktop\$($script:ChineseDisplayName).lnk"),
        (Join-Path $Profile "Desktop\BaiduBatchTransfer.App.lnk"),
        (Join-Path $Profile "AppData\Roaming\Microsoft\Windows\Start Menu\Programs\$($script:ChineseDisplayName).lnk"),
        (Join-Path $Profile "AppData\Roaming\Microsoft\Windows\Start Menu\Programs\BaiduBatchTransfer.App.lnk"))) {
        if (Test-Path -LiteralPath $shortcut) { throw "The isolated profile contains a pre-existing product shortcut: $shortcut" }
    }
    $existing = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' |
        ForEach-Object { Get-ItemProperty -Path $_ -ErrorAction SilentlyContinue } |
        Where-Object { [string]$_.DisplayName -in @($script:ChineseDisplayName, 'BaiduBatchTransfer.App') })
    if ($existing.Count -ne 0) { throw 'The isolated profile contains a pre-existing product uninstall entry.' }
}

function Assert-SetupSurfaces {
    param([string]$InstallRoot, [string]$CurrentMain, [string]$RootLauncher, [string]$LegacyLauncher, [string]$Profile)
    $desktopShortcut = Join-Path $Profile ("Desktop\$($script:ChineseDisplayName).lnk")
    $startMenuShortcut = Join-Path $Profile ("AppData\Roaming\Microsoft\Windows\Start Menu\Programs\$($script:ChineseDisplayName).lnk")
    foreach ($required in @($CurrentMain, $RootLauncher, $LegacyLauncher, $desktopShortcut, $startMenuShortcut)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required Setup migration surface is missing: $required" }
    }
    $desktopTarget = Get-ShortcutTarget $desktopShortcut
    $startMenuTarget = Get-ShortcutTarget $startMenuShortcut
    $exactCurrent = [IO.Path]::GetFullPath($CurrentMain)
    if ($desktopTarget -cne $exactCurrent -or $startMenuTarget -cne $exactCurrent) { throw 'Chinese shortcuts must target the exact current Chinese executable.' }
    $uninstall = Get-UninstallEntry -InstallRoot $InstallRoot
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
    SchemaVersion = 2; RunId = $runId; StartedUtc = $startedUtc.ToString('O'); FinishedUtc = $null
    AssetSource = 'caller-supplied-local-directories'; AssetKind = $AssetKind; Path = $Path
    Scenario = "$AssetKind-$Path"; FailureInjection = $FailureInjection; Succeeded = $false
    FailureRollbackVerified = $false; VelopackUpdaterVersion = $null; Upgrade = $null
    Surfaces = $null; BeforeState = $null; AfterState = $null; BeforeLicenseAudit=$null; AfterLicenseAudit=$null; Error = $null
}

try {
    $profile = Assert-IsolatedWindowsProfile
    Assert-NoNonLoopbackNetwork | Out-Null
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
    if ($evidence.Scenario -notin @('Setup-Delta','Setup-Full','Portable-Full')) { throw "Unsupported migration scenario '$($evidence.Scenario)'." }

    $stagedRoot = Join-Path $evidenceRoot 'staged-assets'
    New-Item -ItemType Directory -Path $stagedRoot | Out-Null
    $script:OwnedPaths.Add($stagedRoot)
    $stagedOld = Join-Path $stagedRoot 'old'
    $stagedNew = Join-Path $stagedRoot 'new'
    Copy-LocalAssetTree -Source $oldSource -Destination $stagedOld
    Copy-LocalAssetTree -Source $newSource -Destination $stagedNew

    $fixturePath = Resolve-AssetFile -Root $stagedOld -FileName $script:FixtureName -Label 'Migration fixture'
    $fixtureSignature = Resolve-AssetFile -Root $stagedOld -FileName $script:FixtureSignatureName -Label 'Migration fixture signature'
    $fixture = Assert-CanonicalSignedFixture -FixturePath $fixturePath -SignaturePath $fixtureSignature -CertificatePath $script:PublisherCertificatePath -ExpectedSpki $script:ExpectedPublisherSpkiSha256
    $driver = Resolve-AssetFile -Root $stagedOld -FileName ([string]$fixture.state_driver_file) -Label 'State driver'
    Assert-FileSha256 -File $driver -Expected ([string]$fixture.state_driver_sha256) -Label 'State driver'
    Assert-PublisherExecutable -Executable $driver

    # ProductIdentity.StablePackageId fixes this topology. Signed fixtures cannot redirect it.
    $installRoot = Join-Path $profile 'AppData\Local\SuperPan.BaiduBatchTransfer'
    $currentDirectory = Join-Path $installRoot 'current'
    $oldMain = Join-Path $currentDirectory $script:OldMainExecutableName
    $currentMain = Join-Path $currentDirectory $script:NewMainExecutableName
    $legacy = Join-Path $currentDirectory $script:OldMainExecutableName
    $rootLauncher = Join-Path $installRoot $script:NewMainExecutableName
    $updater = Join-Path $installRoot 'Update.exe'
    $dataRoot = Join-Path $profile 'AppData\Local\BaiduBatchTransfer'
    $licenseRoot = Join-Path $profile 'AppData\Local\SuperPan\Licensing\v1'
    foreach ($cleanPath in @($installRoot,$dataRoot,$licenseRoot)) {
        if (Test-Path -LiteralPath $cleanPath) { throw "The isolated profile is not clean: $cleanPath" }
    }
    Assert-CleanSetupSurfaces -Profile $profile

    if ($AssetKind -ceq 'Setup') {
        $oldSetup = Resolve-AssetFile -Root $stagedOld -FileName ([string]$fixture.old_setup_file) -Label 'Old Setup'
        Assert-PublisherExecutable -Executable $oldSetup
        Invoke-CheckedProcess -File $oldSetup -Arguments @('--silent') -WorkingDirectory $stagedOld | Out-Null
    } else {
        $oldPortable = Resolve-AssetFile -Root $stagedOld -FileName ([string]$fixture.old_portable_file) -Label 'Old Portable'
        New-Item -ItemType Directory -Path $installRoot | Out-Null
        $script:OwnedPaths.Add($installRoot)
        Expand-LocalPortableAsset -Archive $oldPortable -Destination $installRoot
        Assert-NoReparseAncestors -ExistingPath $installRoot -Label 'Portable install root'
    }

    if (-not (Test-Path -LiteralPath $oldMain -PathType Leaf) -or
        -not ([Diagnostics.FileVersionInfo]::GetVersionInfo($oldMain).ProductVersion.StartsWith('1.1.0', [StringComparison]::Ordinal))) {
        throw 'Old main executable must be the real fixed-path 1.1.0 executable.'
    }
    Assert-PublisherExecutable -Executable $oldMain
    if (-not (Test-Path -LiteralPath $updater -PathType Leaf)) { throw 'Velopack Update.exe is missing from the fixed install root.' }
    $evidence.VelopackUpdaterVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($updater).ProductVersion

    $commonDriverArguments = @('--run-id',$runId,'--profile-root',$profile,'--fixture',$fixturePath)
    $beforeChallenge=New-RunChallenge
    $beforeRun = Invoke-BoundedDriver -Driver $driver -WorkingDirectory $stagedOld -Arguments (@('seed-and-probe','--challenge',$beforeChallenge.Base64) + $commonDriverArguments)
    $before = Assert-StateEvidence -StateJson $beforeRun.StandardOutput -ProfileRoot $profile -RunId $runId -ExpectedChallengeSha256 $beforeChallenge.Sha256 -WindowStartedUtc $beforeRun.StartedUtc -WindowCompletedUtc $beforeRun.CompletedUtc
    $evidence.BeforeState = $before
    $evidence.BeforeLicenseAudit=Assert-IndependentLicenseAudit -State $before -ProfileRoot $profile -Fixture $fixture

    if ($Path -ceq 'Delta') {
        Resolve-AssetFile -Root $stagedOld -FileName ([string]$fixture.old_full_file) -Label 'Old full package' | Out-Null
        $selectedAsset = Resolve-AssetFile -Root $stagedNew -FileName ([string]$fixture.new_delta_file) -Label 'New delta package'
    } else {
        $selectedAsset = Resolve-AssetFile -Root $stagedNew -FileName ([string]$fixture.new_full_file) -Label 'New full package'
    }
    $selectedAssetHash = Get-StreamingSha256 -File $selectedAsset
    $selectedAssetBytes = (Get-Item -LiteralPath $selectedAsset).Length
    $restartStarted = [DateTimeOffset]::UtcNow
    $upgradeChallenge=New-RunChallenge
    $upgradeRun = Invoke-BoundedDriver -Driver $driver -WorkingDirectory $stagedOld -Arguments @(
        'perform-upgrade','--challenge',$upgradeChallenge.Base64,'--run-id',$runId,'--profile-root',$profile,'--fixture',$fixturePath,
        '--old-assets',$stagedOld,'--new-assets',$stagedNew,'--install-root',$installRoot,
        '--asset',$selectedAsset,'--asset-kind',$AssetKind,'--path',$Path,'--failure-injection',$FailureInjection
    ) -TimeoutSeconds 300 -ExpectedUpdater $updater -RequireUpdaterObservation
    $upgrade = Assert-UpgradeEvidence -EvidenceJson $upgradeRun.StandardOutput -RunId $runId -ExpectedChallengeSha256 $upgradeChallenge.Sha256 -Scenario $evidence.Scenario -AssetKind $AssetKind -Path $Path -FailureInjection $FailureInjection `
        -ExpectedAssetSha256 $selectedAssetHash -ExpectedAssetBytes $selectedAssetBytes -ExpectedUpdaterPath $updater -ObservedUpdaterJson (ConvertTo-CanonicalJson $upgradeRun.ObservedUpdater) `
        -WindowStartedUtc $upgradeRun.StartedUtc -WindowCompletedUtc $upgradeRun.CompletedUtc -RunOwnedRoot $evidenceRoot
    $evidence.Upgrade = $upgrade

    if ($FailureInjection -cne 'None') {
        if (-not ([Diagnostics.FileVersionInfo]::GetVersionInfo($oldMain).ProductVersion.StartsWith('1.1.0', [StringComparison]::Ordinal))) {
            throw 'Injected failure did not leave the exact old executable at version 1.1.0.'
        }
        Assert-OldVersionStillLaunches -OldExecutable $oldMain -RunId $runId | Out-Null
        $afterChallenge=New-RunChallenge
        $afterRun = Invoke-BoundedDriver -Driver $driver -WorkingDirectory $stagedOld -Arguments (@('probe','--challenge',$afterChallenge.Base64) + $commonDriverArguments)
        $after = Assert-StateEvidence -StateJson $afterRun.StandardOutput -ProfileRoot $profile -RunId $runId -ExpectedChallengeSha256 $afterChallenge.Sha256 -WindowStartedUtc $afterRun.StartedUtc -WindowCompletedUtc $afterRun.CompletedUtc
        $evidence.AfterLicenseAudit=Assert-IndependentLicenseAudit -State $after -ProfileRoot $profile -Fixture $fixture
        if((ConvertTo-CanonicalJson $evidence.BeforeLicenseAudit)-cne(ConvertTo-CanonicalJson $evidence.AfterLicenseAudit)){throw 'Independent license audit changed across failed upgrade.'}
        Assert-StateEvidencePreserved -Before $before -After $after
        $evidence.AfterState = $after
        $evidence.FailureRollbackVerified = $true
        $evidence.Succeeded = $true
    } else {
        if (-not (Test-Path -LiteralPath $currentMain -PathType Leaf) -or
            -not ([Diagnostics.FileVersionInfo]::GetVersionInfo($currentMain).ProductVersion.StartsWith('1.1.1', [StringComparison]::Ordinal))) {
            throw 'Current Chinese executable must be the real fixed-path 1.1.1 executable.'
        }
        foreach ($application in @($currentMain,$rootLauncher,$legacy)) { Assert-PublisherExecutable -Executable $application }
        $restart = Find-ExactProcessPath -ExpectedPath $currentMain -NotBefore $restartStarted
        $restartEvidence = [ordered]@{ Id=$restart.Id; Path=$restart.Path; StartTimeUtc=$restart.StartTime.ToUniversalTime().ToString('O') }
        Stop-OwnedProcessTree -RootProcessId $restart.Id
        if ($AssetKind -ceq 'Setup') {
            $surfaces = Assert-SetupSurfaces -InstallRoot $installRoot -CurrentMain $currentMain -RootLauncher $rootLauncher -LegacyLauncher $legacy -Profile $profile
        } else {
            $surfaces = [ordered]@{ InstallRoot=$installRoot; CurrentDirectory=$currentDirectory; InstallRootLauncher=$rootLauncher; CurrentMainExecutable=$currentMain; LegacyCompatibilityLauncher=$legacy; DesktopShortcut=$null; StartMenuShortcut=$null; UninstallEntry=$null }
        }
        $surfaces.UpgradeRestartProcess = $restartEvidence
        $compatStarted = [DateTimeOffset]::UtcNow
        $compatProcess = Start-Process -FilePath $legacy -ArgumentList @('--migration-gate-compat',$runId) -WorkingDirectory $currentDirectory -PassThru
        try { $compatTarget = Find-ExactProcessPath -ExpectedPath $currentMain -NotBefore $compatStarted }
        finally { if ($compatProcess -and -not $compatProcess.HasExited) { Stop-OwnedProcessTree -RootProcessId $compatProcess.Id } }
        Stop-OwnedProcessTree -RootProcessId $compatTarget.Id
        $surfaces.LegacyCompatibilityForwardTarget = $compatTarget.Path
        $afterChallenge=New-RunChallenge
        $afterRun = Invoke-BoundedDriver -Driver $driver -WorkingDirectory $stagedOld -Arguments (@('probe','--challenge',$afterChallenge.Base64) + $commonDriverArguments)
        $after = Assert-StateEvidence -StateJson $afterRun.StandardOutput -ProfileRoot $profile -RunId $runId -ExpectedChallengeSha256 $afterChallenge.Sha256 -WindowStartedUtc $afterRun.StartedUtc -WindowCompletedUtc $afterRun.CompletedUtc
        $evidence.AfterLicenseAudit=Assert-IndependentLicenseAudit -State $after -ProfileRoot $profile -Fixture $fixture
        if((ConvertTo-CanonicalJson $evidence.BeforeLicenseAudit)-cne(ConvertTo-CanonicalJson $evidence.AfterLicenseAudit)){throw 'Independent license audit changed across successful upgrade.'}
        Assert-StateEvidencePreserved -Before $before -After $after
        $evidence.Surfaces = $surfaces
        $evidence.AfterState = $after
        $evidence.Succeeded = $true
    }
    Assert-NoNonLoopbackNetwork | Out-Null
} catch {
    $evidence.Error = $_.Exception.Message
    throw
} finally {
    if ($evidenceRoot) {
        $evidence.FinishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Write-CanonicalJson -Value $evidence -Destination (Join-Path $evidenceRoot 'evidence.json')
        $hashes = @(Get-FileInventory -Roots @($evidenceRoot))
        Write-CanonicalJson -Value ([ordered]@{ SchemaVersion=1; RunId=$runId; Files=$hashes }) -Destination (Join-Path $evidenceRoot 'files.sha256.json')
    }
}

if (-not $evidence.Succeeded) { throw 'The migration gate did not complete successfully.' }
Write-Host "REAL_MIGRATION_GATE_PASS=$evidenceRoot"
