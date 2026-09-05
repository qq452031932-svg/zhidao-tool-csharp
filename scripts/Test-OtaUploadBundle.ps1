[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BundlePath,
    [string]$PublisherCertificatePath = (Join-Path $PSScriptRoot '..\eng\ota\SuperPan.OtaPublisher.cer'),
    [string]$ExpectedVersion,
    [string]$ExpectedPublisherSpkiSha256 = 'f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff',
    [string]$OutputReportPath,
    [switch]$RequireTimestamp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageId = 'SuperPan.BaiduBatchTransfer'
$channel = 'win-x64-stable'
$manifestName = 'upload-manifest.sha256'
$signatureName = 'upload-manifest.sha256.sig'
$releaseFeedName = "releases.$channel.json"
$assetCatalogName = "assets.$channel.json"
$releasesName = "RELEASES-$channel"
$utf8Strict = [Text.UTF8Encoding]::new($false, $true)
$signatureEvidence = [Collections.Generic.List[object]]::new()

Add-Type -AssemblyName System.Security.Cryptography.Pkcs

function Get-LowerHash([Security.Cryptography.HashAlgorithm]$Algorithm, [IO.Stream]$Stream) {
    try { return ([Convert]::ToHexString($Algorithm.ComputeHash($Stream))).ToLowerInvariant() }
    finally { $Algorithm.Dispose(); $Stream.Dispose() }
}

function Get-FileSha256([string]$Path) {
    return Get-LowerHash ([Security.Cryptography.SHA256]::Create()) ([IO.File]::OpenRead($Path))
}

function Get-BytesSha256([byte[]]$Bytes) {
    return ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))).ToLowerInvariant()
}

function Test-CanonicalVersion([string]$Value) {
    if ($Value -cnotmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') { return $false }
    foreach ($part in $Value.Split('.')) { if ([int]$part -gt 65534) { return $false } }
    return $true
}

function Compare-Version([string]$Left, [string]$Right) {
    $a = @($Left.Split('.') | ForEach-Object { [int]$_ })
    $b = @($Right.Split('.') | ForEach-Object { [int]$_ })
    for ($i = 0; $i -lt 3; $i++) {
        if ($a[$i] -lt $b[$i]) { return -1 }
        if ($a[$i] -gt $b[$i]) { return 1 }
    }
    return 0
}

function Test-FlatAsciiName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 255 -or
        $Name -cne [IO.Path]::GetFileName($Name) -or $Name.Contains('/') -or $Name.Contains('\') -or
        $Name.EndsWith(' ') -or $Name.EndsWith('.') -or
        $Name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { return $false }
    foreach ($character in $Name.ToCharArray()) {
        if ([int]$character -lt 0x21 -or [int]$character -gt 0x7e) { return $false }
    }
    $stem = $Name.Split('.')[0].ToUpperInvariant()
    return $stem -notin @('CON','PRN','AUX','NUL','COM1','COM2','COM3','COM4','COM5','COM6','COM7','COM8','COM9','LPT1','LPT2','LPT3','LPT4','LPT5','LPT6','LPT7','LPT8','LPT9')
}

function Test-AllowedName([string]$Name) {
    if ($Name -cin @($manifestName, $signatureName, $releaseFeedName, $assetCatalogName, $releasesName,
            "$packageId-Setup.exe", "$packageId-Portable.zip")) { return $true }
    return $Name -cmatch ('^' + [Regex]::Escape($packageId) + '-(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})-(full|delta)\.nupkg$')
}

function Read-LimitedStream([IO.Stream]$Stream, [long]$Limit, [string]$Description) {
    $buffer = [byte[]]::new(65536)
    $memory = [IO.MemoryStream]::new()
    try {
        while (($read = $Stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($memory.Length + $read -gt $Limit) { throw "$Description exceeds the allowed size." }
            $memory.Write($buffer, 0, $read)
        }
        return $memory.ToArray()
    }
    finally { $memory.Dispose(); $Stream.Dispose() }
}

function Get-CertificateSpki([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($Certificate)
    if ($null -eq $rsa -or $rsa.KeySize -ne 3072) { throw 'Publisher certificate must use RSA-3072.' }
    try { return Get-BytesSha256 $rsa.ExportSubjectPublicKeyInfo() }
    finally { $rsa.Dispose() }
}

function Add-AuthenticodeHashRange(
    [Security.Cryptography.IncrementalHash]$Hash,
    [IO.FileStream]$Stream,
    [long]$Offset,
    [long]$Count) {
    if ($Count -lt 0 -or $Offset -lt 0 -or $Offset + $Count -gt $Stream.Length) { throw 'Authenticode image range is invalid.' }
    $Stream.Position = $Offset
    $buffer = [byte[]]::new(1048576)
    while ($Count -gt 0) {
        $read = $Stream.Read($buffer, 0, [int][Math]::Min($buffer.Length, $Count))
        if ($read -le 0) { throw 'Authenticode image ended unexpectedly.' }
        $Hash.AppendData($buffer, 0, $read)
        $Count -= $read
    }
}

function Get-AuthenticodeTimestampUtc([Security.Cryptography.Pkcs.SignerInfo]$SignerInfo) {
    foreach ($counterSigner in $SignerInfo.CounterSignerInfos) {
        $counterSigner.CheckSignature($true)
        foreach ($attribute in $counterSigner.SignedAttributes) {
            if ($attribute.Oid.Value -ceq '1.2.840.113549.1.9.5' -and $attribute.Values.Count -eq 1) {
                return ([Security.Cryptography.Pkcs.Pkcs9SigningTime]::new($attribute.Values[0].RawData)).SigningTime.ToUniversalTime()
            }
        }
    }
    foreach ($attribute in $SignerInfo.UnsignedAttributes) {
        if ($attribute.Oid.Value -cne '1.3.6.1.4.1.311.3.3.1') { continue }
        foreach ($value in $attribute.Values) {
            $timestampCms = [Security.Cryptography.Pkcs.SignedCms]::new()
            $timestampCms.Decode($value.RawData)
            $timestampCms.CheckSignature($true)
            $reader = [System.Formats.Asn1.AsnReader]::new(
                $timestampCms.ContentInfo.Content,
                [System.Formats.Asn1.AsnEncodingRules]::DER)
            $sequence = $reader.ReadSequence()
            [void]$sequence.ReadInteger()
            [void]$sequence.ReadObjectIdentifier()
            $messageImprint = $sequence.ReadSequence()
            $imprintAlgorithm = $messageImprint.ReadSequence()
            $imprintOid = $imprintAlgorithm.ReadObjectIdentifier()
            if ($imprintAlgorithm.HasData) { $imprintAlgorithm.ReadNull() }
            $expectedImprint = $messageImprint.ReadOctetString()
            if ($imprintOid -cne '2.16.840.1.101.3.4.2.1' -or $messageImprint.HasData) {
                throw 'Authenticode timestamp uses an unsupported message imprint.'
            }
            $actualImprint = [Security.Cryptography.SHA256]::HashData($SignerInfo.GetSignature())
            if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($actualImprint, $expectedImprint)) {
                throw 'Authenticode timestamp is not bound to the executable signature.'
            }
            [void]$sequence.ReadInteger()
            return $sequence.ReadGeneralizedTime().UtcDateTime
        }
    }
    return $null
}

function Get-VerifiedAuthenticode([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::ASCII, $true)
    try {
        if ($stream.Length -lt 256 -or $reader.ReadUInt16() -ne 0x5A4D) { throw 'Executable is not a PE image.' }
        $stream.Position = 0x3c
        $peOffset = [long]$reader.ReadUInt32()
        if ($peOffset -lt 0x40 -or $peOffset + 256 -gt $stream.Length) { throw 'Executable PE header is invalid.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Executable PE signature is invalid.' }
        $optionalOffset = $peOffset + 24
        $stream.Position = $optionalOffset
        $magic = $reader.ReadUInt16()
        $dataDirectoryOffset = if ($magic -eq 0x20b) { $optionalOffset + 112 } elseif ($magic -eq 0x10b) { $optionalOffset + 96 } else { throw 'Executable optional header is invalid.' }
        $checksumOffset = $optionalOffset + 64
        $securityDirectoryOffset = $dataDirectoryOffset + 32
        $stream.Position = $securityDirectoryOffset
        $certificateOffset = [long]$reader.ReadUInt32()
        $certificateSize = [long]$reader.ReadUInt32()
        if ($certificateOffset -le $securityDirectoryOffset + 8 -or $certificateSize -lt 8 -or
            $certificateOffset + $certificateSize -gt $stream.Length) { throw 'Executable has no valid Authenticode certificate table.' }
        $stream.Position = $certificateOffset
        $certificateLength = [long]$reader.ReadUInt32()
        $revision = $reader.ReadUInt16()
        $certificateType = $reader.ReadUInt16()
        if ($certificateLength -lt 9 -or $certificateLength -gt $certificateSize -or $revision -notin @(0x0100,0x0200) -or $certificateType -ne 2) {
            throw 'Executable Authenticode certificate entry is invalid.'
        }
        $pkcs7Bytes = $reader.ReadBytes([int]($certificateLength - 8))
        if ($pkcs7Bytes.Length -ne $certificateLength - 8) { throw 'Executable Authenticode certificate entry is truncated.' }

        $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $cms.Decode($pkcs7Bytes)
        $cms.CheckSignature($true)
        if ($cms.SignerInfos.Count -ne 1 -or $null -eq $cms.SignerInfos[0].Certificate) { throw 'Executable Authenticode signer is ambiguous.' }

        $asn = [System.Formats.Asn1.AsnReader]::new($cms.ContentInfo.Content, [System.Formats.Asn1.AsnEncodingRules]::BER)
        $root = $asn.ReadSequence()
        [void]$root.ReadEncodedValue()
        $digestInfo = $root.ReadSequence()
        $algorithm = $digestInfo.ReadSequence()
        $algorithmOid = $algorithm.ReadObjectIdentifier()
        if ($algorithm.HasData) { $algorithm.ReadNull() }
        $expectedDigest = $digestInfo.ReadOctetString()
        if ($algorithmOid -cne '2.16.840.1.101.3.4.2.1' -or $digestInfo.HasData -or $root.HasData -or $asn.HasData) {
            throw 'Executable Authenticode image digest metadata is unsupported.'
        }
        $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
        try {
            Add-AuthenticodeHashRange $hash $stream 0 $checksumOffset
            Add-AuthenticodeHashRange $hash $stream ($checksumOffset + 4) ($securityDirectoryOffset - ($checksumOffset + 4))
            Add-AuthenticodeHashRange $hash $stream ($securityDirectoryOffset + 8) ($certificateOffset - ($securityDirectoryOffset + 8))
            if ($certificateOffset + $certificateSize -lt $stream.Length) {
                Add-AuthenticodeHashRange $hash $stream ($certificateOffset + $certificateSize) ($stream.Length - ($certificateOffset + $certificateSize))
            }
            $actualDigest = $hash.GetHashAndReset()
        }
        finally { $hash.Dispose() }
        if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($actualDigest, $expectedDigest)) {
            throw 'Executable Authenticode image digest does not match the signed digest.'
        }
        return [pscustomobject]@{ Cms=$cms; Signer=$cms.SignerInfos[0] }
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}

function Assert-Authenticode([string]$Path, [string]$ExpectedSpki, [bool]$TimestampRequired) {
    try { $verified = Get-VerifiedAuthenticode $Path }
    catch { throw "Executable is not validly Authenticode signed: $([IO.Path]::GetFileName($Path)) ($($_.Exception.Message))" }
    $actualSpki = Get-CertificateSpki $verified.Signer.Certificate
    if ($actualSpki -cne $ExpectedSpki) {
        throw "Executable signer SPKI does not match the enrolled publisher: $([IO.Path]::GetFileName($Path))"
    }
    $authenticodeStatus = Get-AuthenticodeSignature -LiteralPath $Path
    $timestampUtc = Get-AuthenticodeTimestampUtc $verified.Signer
    if ($TimestampRequired -and ($null -eq $authenticodeStatus.TimeStamperCertificate -or $null -eq $timestampUtc)) {
        throw "Executable is missing the required Authenticode timestamp: $([IO.Path]::GetFileName($Path))"
    }
    $signatureEvidence.Add([ordered]@{
        file = [IO.Path]::GetFileName($Path)
        signerSpkiSha256 = $actualSpki
        imageDigestVerified = $true
        platformStatus = [string]$authenticodeStatus.Status
        timestampPresent = ($null -ne $timestampUtc)
        timestampUtc = if ($null -eq $timestampUtc) { $null } else { $timestampUtc.ToString('O') }
    })
}

$resolvedBundle = [IO.Path]::GetFullPath($BundlePath)
$resolvedCertificate = [IO.Path]::GetFullPath($PublisherCertificatePath)
if (-not (Test-Path -LiteralPath $resolvedBundle)) { throw 'BundlePath does not exist.' }
if (-not (Test-Path -LiteralPath $resolvedCertificate -PathType Leaf)) { throw 'Publisher certificate does not exist.' }
if ($ExpectedPublisherSpkiSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Expected publisher SPKI must be lowercase SHA-256.' }
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and -not (Test-CanonicalVersion $ExpectedVersion)) {
    throw 'ExpectedVersion must be a canonical major.minor.patch value.'
}

$publisherCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($resolvedCertificate)
$archive = $null
$temporaryRoot = $null
try {
    $certificateSpki = Get-CertificateSpki $publisherCertificate
    if ($certificateSpki -cne $ExpectedPublisherSpkiSha256) { throw 'Publisher certificate SPKI pin does not match.' }

    $entries = [Collections.Generic.List[object]]::new()
    if (Test-Path -LiteralPath $resolvedBundle -PathType Container) {
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($resolvedBundle, '*', [IO.SearchOption]::TopDirectoryOnly)) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Upload bundle contains a directory entry.' }
            if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Upload bundle contains a reparse-point file.' }
            $entries.Add([pscustomobject]@{ Name = [IO.Path]::GetFileName($path); Length = ([IO.FileInfo]$path).Length; Path = $path; ZipEntry = $null })
        }
    }
    else {
        if ([IO.Path]::GetExtension($resolvedBundle) -cne '.zip') { throw 'BundlePath must be a directory or ZIP file.' }
        if ([IO.FileInfo]::new($resolvedBundle).Length -gt 2GB) { throw 'Upload ZIP exceeds the 2 GiB compressed limit.' }
        $archive = [IO.Compression.ZipFile]::OpenRead($resolvedBundle)
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name) -or $entry.FullName -cne $entry.Name) { throw 'Upload ZIP contains a directory or nested entry.' }
            $unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
            if ($unixType -eq 0xA000 -or ($entry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Upload ZIP contains a symbolic-link or reparse-point entry.'
            }
            $entries.Add([pscustomobject]@{ Name = $entry.Name; Length = $entry.Length; Path = $null; ZipEntry = $entry })
        }
    }
    if ($entries.Count -lt 7 -or $entries.Count -gt 256) { throw 'Upload bundle has an invalid file count.' }
    $expandedBytes = [long]0
    $compressedBytes = [long]0
    foreach ($entry in $entries) {
        if ($entry.Length -lt 0 -or $entry.Length -gt 2GB) { throw "Upload asset exceeds the 2 GiB per-file limit: $($entry.Name)" }
        if ($entry.Length -gt ([long]::MaxValue - $expandedBytes)) { throw 'Upload bundle expanded size overflowed.' }
        $expandedBytes += [long]$entry.Length
        if ($expandedBytes -gt 8GB) { throw 'Upload bundle exceeds the 8 GiB expanded limit.' }
        if ($null -ne $entry.ZipEntry) {
            $compressed = [long]$entry.ZipEntry.CompressedLength
            if ($compressed -gt ([long]::MaxValue - $compressedBytes)) { throw 'Upload bundle compressed size overflowed.' }
            $compressedBytes += $compressed
            if ($entry.Length -gt 0 -and ($compressed -le 0 -or ([double]$entry.Length / [double]$compressed) -gt 100.0)) {
                throw "Upload asset exceeds the 100:1 compression ratio limit: $($entry.Name)"
            }
        }
    }
    if ($compressedBytes -gt 0 -and ([double]$expandedBytes / [double]$compressedBytes) -gt 100.0) {
        throw 'Upload bundle exceeds the 100:1 aggregate compression ratio limit.'
    }

    $names = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        if (-not (Test-FlatAsciiName $entry.Name)) { throw "Upload bundle contains an unknown or unsafe file: $($entry.Name)" }
        if ($names.ContainsKey($entry.Name)) { throw "Upload bundle contains a duplicate or case-colliding file: $($entry.Name)" }
        if (-not (Test-AllowedName $entry.Name)) { throw "Upload bundle contains an unknown or unsafe file: $($entry.Name)" }
        $names.Add($entry.Name, $entry.Name)
    }
    foreach ($required in @($manifestName, $signatureName, $releaseFeedName, $assetCatalogName, $releasesName,
            "$packageId-Setup.exe", "$packageId-Portable.zip")) {
        if (-not $names.ContainsKey($required) -or $names[$required] -cne $required) { throw "Upload bundle is missing required file: $required" }
    }

    function Read-EntryBytes([string]$Name, [long]$Limit) {
        $entry = @($entries | Where-Object { $_.Name -ceq $Name })[0]
        if ($null -ne $entry.Path) {
            if ($entry.Length -gt $Limit) { throw "$Name exceeds the allowed size." }
            return [IO.File]::ReadAllBytes($entry.Path)
        }
        return Read-LimitedStream $entry.ZipEntry.Open() $Limit $Name
    }

    function Get-EntrySha256([object]$Entry) {
        if ($null -ne $Entry.Path) { return Get-FileSha256 $Entry.Path }
        return Get-LowerHash ([Security.Cryptography.SHA256]::Create()) $Entry.ZipEntry.Open()
    }

    function Extract-Entry([string]$Name, [string]$Destination) {
        $entry = @($entries | Where-Object { $_.Name -ceq $Name })[0]
        if ($null -ne $entry.Path) { [IO.File]::Copy($entry.Path, $Destination, $false); return }
        $input = $entry.ZipEntry.Open()
        $output = [IO.File]::Create($Destination)
        try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
    }

    function Test-SafeInnerPath([string]$Name) {
        if ([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 1024 -or
            $Name.StartsWith('/') -or $Name.StartsWith('\') -or $Name.Contains('\') -or
            $Name.Contains([char]0) -or $Name -match '^[A-Za-z]:') { return $false }
        $parts = @($Name.Split('/'))
        return $parts.Count -gt 0 -and @($parts | Where-Object { $_ -in @('', '.', '..') }).Count -eq 0
    }

    function Assert-InnerArchive(
        [string]$ArchivePath,
        [string]$Description,
        [string[]]$RequiredPublisherExecutables,
        [string[]]$AllowedThirdPartyExecutables = @()) {
        $inner = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
        try {
            if ($inner.Entries.Count -eq 0 -or $inner.Entries.Count -gt 4096) { throw "$Description has an invalid entry count." }
            $innerNames = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
            $innerExpanded = [long]0
            $innerCompressed = [long]0
            foreach ($innerEntry in $inner.Entries) {
                if ([string]::IsNullOrEmpty($innerEntry.Name) -or -not (Test-SafeInnerPath $innerEntry.FullName)) { throw "$Description contains an unsafe path." }
                if ($innerNames.ContainsKey($innerEntry.FullName)) { throw "$Description contains duplicate or case-colliding paths." }
                $innerNames.Add($innerEntry.FullName, $innerEntry.FullName)
                $unixType = (($innerEntry.ExternalAttributes -shr 16) -band 0xF000)
                if ($unixType -eq 0xA000 -or ($innerEntry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "$Description contains a symbolic-link or reparse-point entry."
                }
                if ($innerEntry.Length -lt 0 -or $innerEntry.Length -gt 2GB) { throw "$Description contains an oversized entry." }
                if ($innerEntry.Length -gt ([long]::MaxValue - $innerExpanded)) { throw "$Description expanded size overflowed." }
                $innerExpanded += $innerEntry.Length
                if ($innerExpanded -gt 8GB) { throw "$Description exceeds the expanded size limit." }
                $innerCompressed += $innerEntry.CompressedLength
                if ($innerEntry.Length -gt 0 -and ($innerEntry.CompressedLength -le 0 -or
                    ([double]$innerEntry.Length / [double]$innerEntry.CompressedLength) -gt 100.0)) {
                    throw "$Description contains an excessive compression ratio."
                }
            }
            if ($innerCompressed -gt 0 -and ([double]$innerExpanded / [double]$innerCompressed) -gt 100.0) {
                throw "$Description has an excessive aggregate compression ratio."
            }
            $executableIndex = 0
            foreach ($requiredName in $RequiredPublisherExecutables) {
                if (-not $innerNames.ContainsKey($requiredName) -or $innerNames[$requiredName] -cne $requiredName) {
                    throw "$Description is missing required publisher executable: $requiredName"
                }
            }
            $expectedExecutables = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($name in $RequiredPublisherExecutables) { [void]$expectedExecutables.Add($name) }
            foreach ($name in $AllowedThirdPartyExecutables) { [void]$expectedExecutables.Add($name) }
            $actualExecutables = @($inner.Entries | Where-Object { $_.FullName.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase) })
            foreach ($executableEntry in $actualExecutables) {
                if (-not $expectedExecutables.Contains($executableEntry.FullName)) {
                    throw "$Description contains an unexpected executable: $($executableEntry.FullName)"
                }
            }
            # Every executable carried by an inner archive must be explicitly
            # classified; every publisher-owned executable is then independently
            # checked against its Authenticode image digest and publisher SPKI.
            foreach ($requiredName in $RequiredPublisherExecutables) {
                $requiredEntry = $inner.GetEntry($requiredName)
                if ($null -eq $requiredEntry -or $requiredEntry.Length -le 0) { throw "$Description has an empty required publisher executable." }
                $destination = Join-Path $temporaryRoot ("inner-$executableIndex.exe")
                $executableIndex++
                $input = $requiredEntry.Open(); $output = [IO.File]::Create($destination)
                try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
                Assert-Authenticode $destination $ExpectedPublisherSpkiSha256 $RequireTimestamp.IsPresent
            }
        }
        finally { $inner.Dispose() }
    }

    $manifestBytes = Read-EntryBytes $manifestName 81788
    $signatureBytes = Read-EntryBytes $signatureName 384
    if ($signatureBytes.Length -ne 384) { throw 'Manifest signature must be one raw RSA-3072 signature.' }
    if ($manifestBytes.Length -eq 0 -or $manifestBytes[0] -eq 0xef) { throw 'Manifest must be non-empty UTF-8 without BOM.' }
    try { $manifestText = $utf8Strict.GetString($manifestBytes) }
    catch { throw 'Manifest is not strict UTF-8.' }
    if ($manifestText.Contains("`r") -or -not $manifestText.EndsWith("`n") -or $manifestText.EndsWith("`n`n")) { throw 'Manifest must use LF and exactly one final LF.' }
    $lines = @($manifestText.Substring(0, $manifestText.Length - 1).Split("`n"))
    if ($lines.Count -ne $entries.Count - 2) { throw 'Manifest does not cover the complete ZIP closure.' }
    $manifestEntries = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $manifestNameCases = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $previousName = $null
    foreach ($line in $lines) {
        if ($line -cnotmatch '^(?<hash>[0-9a-f]{64})  (?<name>[!-~]+)$') { throw 'Manifest contains a non-canonical line.' }
        $name = [string]$Matches.name
        if (-not (Test-FlatAsciiName $name) -or $name -cin @($manifestName, $signatureName) -or -not (Test-AllowedName $name)) { throw "Manifest contains an unsafe file name: $name" }
        if ($null -ne $previousName -and [StringComparer]::Ordinal.Compare($previousName, $name) -ge 0) { throw 'Manifest entries are not in ordinal UTF-8 filename order.' }
        if ($manifestEntries.ContainsKey($name)) { throw "Manifest contains a duplicate or case-colliding file: $name" }
        $manifestEntries.Add($name, [string]$Matches.hash)
        $manifestNameCases.Add($name, $name)
        $previousName = $name
    }
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($publisherCertificate)
    try {
        if (-not $rsa.VerifyData($manifestBytes, $signatureBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
            throw 'Manifest signature verification failed.'
        }
    }
    finally { $rsa.Dispose() }

    $actualFiles = [ordered]@{}
    foreach ($entry in $entries) {
        if ($entry.Name -cin @($manifestName, $signatureName)) { continue }
        if (-not $manifestEntries.ContainsKey($entry.Name) -or $manifestNameCases[$entry.Name] -cne $entry.Name) { throw 'Manifest casing mismatch.' }
        $actualHash = Get-EntrySha256 $entry
        if ($actualHash -cne $manifestEntries[$entry.Name]) { throw "Manifest SHA-256 differs from file: $($entry.Name)" }
        $actualFiles[$entry.Name] = [pscustomobject]@{ hash = $actualHash; size = [long]$entry.Length }
    }

    try { $releaseFeed = $utf8Strict.GetString((Read-EntryBytes $releaseFeedName 1048576)) | ConvertFrom-Json }
    catch { throw 'Production release JSON is invalid.' }
    if ($null -eq $releaseFeed.PSObject.Properties['Assets']) { throw 'Production release JSON has no Assets array.' }
    $assets = @($releaseFeed.Assets)
    if ($assets.Count -eq 0 -or $assets.Count -gt 254) { throw 'Production release JSON has an invalid asset count.' }
    $releaseFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $fullFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $versions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($asset in $assets) {
        foreach ($property in @('PackageId','Version','Type','FileName','SHA256','Size')) { if ($null -eq $asset.PSObject.Properties[$property]) { throw "Release asset is missing $property." } }
        $version = [string]$asset.Version
        $type = [string]$asset.Type
        $fileName = [string]$asset.FileName
        if ([string]$asset.PackageId -cne $packageId -or -not (Test-CanonicalVersion $version) -or $type -cnotin @('Full','Delta')) { throw 'Release asset identity, version, or type is invalid.' }
        $expectedFileName = "$packageId-$version-$($type.ToLowerInvariant()).nupkg"
        if ($fileName -cne $expectedFileName -or -not $actualFiles.Contains($fileName) -or -not $releaseFiles.Add($fileName)) { throw "Release asset reference is invalid: $fileName" }
        if (-not [string]::Equals([string]$asset.SHA256, [string]$actualFiles[$fileName].hash, [StringComparison]::OrdinalIgnoreCase) -or
            [long]$asset.Size -ne $actualFiles[$fileName].size) { throw "Release asset hash or size differs: $fileName" }
        if ($type -ceq 'Full') { [void]$fullFiles.Add($fileName) }
        [void]$versions.Add($version)
    }
    foreach ($fileName in @($actualFiles.Keys | Where-Object { $_ -clike '*.nupkg' })) { if (-not $releaseFiles.Contains($fileName)) { throw "Unreferenced package file: $fileName" } }
    $currentVersion = @($versions | Sort-Object { [version]$_ } -Descending)[0]
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $currentVersion -cne $ExpectedVersion) { throw 'Production release version does not match ExpectedVersion.' }

    try { $catalog = $utf8Strict.GetString((Read-EntryBytes $assetCatalogName 1048576)) | ConvertFrom-Json }
    catch { throw 'Production asset catalog is invalid.' }
    if ($catalog -isnot [array]) { throw 'Production asset catalog must be a JSON array.' }
    $expectedCatalog = [ordered]@{ Installer = "$packageId-Setup.exe"; Portable = "$packageId-Portable.zip"; Full = "$packageId-$currentVersion-full.nupkg" }
    if ($releaseFiles.Contains("$packageId-$currentVersion-delta.nupkg")) { $expectedCatalog.Delta = "$packageId-$currentVersion-delta.nupkg" }
    $seenTypes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in @($catalog)) {
        $properties = @($item.PSObject.Properties.Name)
        if ($properties.Count -ne 2 -or -not ($properties -ccontains 'Type') -or -not ($properties -ccontains 'RelativeFileName')) { throw 'Asset catalog properties are invalid.' }
        $type = [string]$item.Type; $name = [string]$item.RelativeFileName
        if (-not $expectedCatalog.Contains($type) -or $name -cne $expectedCatalog[$type] -or -not $seenTypes.Add($type) -or -not $actualFiles.Contains($name)) { throw "Asset catalog reference is invalid: $name" }
    }
    if ($seenTypes.Count -ne $expectedCatalog.Count) { throw 'Asset catalog is incomplete.' }

    try { $releaseLines = @($utf8Strict.GetString((Read-EntryBytes $releasesName 1048576)).TrimEnd("`n").Split("`n")) }
    catch { throw 'RELEASES index is invalid.' }
    if ($releaseLines.Count -ne $fullFiles.Count) { throw 'RELEASES does not cover every Full package exactly once.' }
    $indexed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in $releaseLines) {
        if ($line -cnotmatch '^(?<sha1>[0-9A-F]{40}) (?<name>[^\s]+) (?<size>[0-9]+)$') { throw 'RELEASES contains a non-canonical line.' }
        $name = [string]$Matches.name
        if (-not $fullFiles.Contains($name) -or -not $indexed.Add($name) -or [long]$Matches.size -ne $actualFiles[$name].size) { throw "RELEASES reference is invalid: $name" }
        $entry = @($entries | Where-Object { $_.Name -ceq $name })[0]
        if ($null -ne $entry.Path) { $sha1 = Get-LowerHash ([Security.Cryptography.SHA1]::Create()) ([IO.File]::OpenRead($entry.Path)) }
        else { $sha1 = Get-LowerHash ([Security.Cryptography.SHA1]::Create()) $entry.ZipEntry.Open() }
        if ($sha1.ToUpperInvariant() -cne [string]$Matches.sha1) { throw "RELEASES SHA-1 differs: $name" }
    }

    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("superpan-ota-verify-" + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $setupName = "$packageId-Setup.exe"
    $setupPath = Join-Path $temporaryRoot $setupName
    Extract-Entry $setupName $setupPath
    Assert-Authenticode $setupPath $ExpectedPublisherSpkiSha256 $RequireTimestamp.IsPresent

    $portableName = "$packageId-Portable.zip"
    $portablePath = Join-Path $temporaryRoot $portableName
    Extract-Entry $portableName $portablePath
    Assert-InnerArchive $portablePath 'Portable archive' @(
        "$packageId.exe",
        'Update.exe',
        'current/BaiduBatchTransfer.App.exe') @(
        'current/managed-browser-helper/python.exe',
        'current/managed-browser-helper/pythonw.exe')

    $packageIndex = 0
    foreach ($asset in $assets) {
        $packageName = [string]$asset.FileName
        $packagePath = Join-Path $temporaryRoot ("package-$packageIndex.nupkg")
        $packageIndex++
        Extract-Entry $packageName $packagePath
        $requiredPackageExecutables = if ([string]$asset.Type -ceq 'Full') {
            @('lib/app/BaiduBatchTransfer.App_ExecutionStub.exe', 'lib/app/BaiduBatchTransfer.App.exe', 'lib/app/Squirrel.exe')
        }
        else { @() }
        $allowedPackageExecutables = if ([string]$asset.Type -ceq 'Full') {
            @('lib/app/managed-browser-helper/python.exe', 'lib/app/managed-browser-helper/pythonw.exe')
        }
        else { @() }
        Assert-InnerArchive $packagePath "Package $packageName" $requiredPackageExecutables $allowedPackageExecutables
    }

    $report = [ordered]@{
        schemaVersion = 1
        verified = $true
        packageId = $packageId
        channel = $channel
        version = $currentVersion
        manifestSha256 = Get-BytesSha256 $manifestBytes
        publisherSpkiSha256 = $certificateSpki
        authenticodeTimestampRequired = $RequireTimestamp.IsPresent
        executableSignatures = @($signatureEvidence)
        fileCount = $entries.Count
        bundleSha256 = if (Test-Path -LiteralPath $resolvedBundle -PathType Leaf) { Get-FileSha256 $resolvedBundle } else { $null }
        files = @($actualFiles.GetEnumerator() | Sort-Object Key | ForEach-Object { [ordered]@{ name = $_.Key; size = $_.Value.size; sha256 = $_.Value.hash } })
    }
    $json = $report | ConvertTo-Json -Depth 6
    if (-not [string]::IsNullOrWhiteSpace($OutputReportPath)) {
        $reportPath = [IO.Path]::GetFullPath($OutputReportPath)
        $parent = [IO.Path]::GetDirectoryName($reportPath)
        if (-not [string]::IsNullOrWhiteSpace($parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
        [IO.File]::WriteAllText($reportPath, $json + "`n", [Text.UTF8Encoding]::new($false))
    }
    Write-Output $json
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    $publisherCertificate.Dispose()
    if ($null -ne $temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
