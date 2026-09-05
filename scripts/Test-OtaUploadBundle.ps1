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

function Assert-Authenticode([string]$Path, [string]$ExpectedSpki, [bool]$TimestampRequired) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($null -eq $signature.SignerCertificate -or
        $signature.Status -in @('NotSigned', 'HashMismatch', 'NotSupported', 'Incompatible', 'FileError')) {
        throw "Executable is not validly Authenticode signed: $([IO.Path]::GetFileName($Path)) ($($signature.Status))"
    }
    $actualSpki = Get-CertificateSpki $signature.SignerCertificate
    if ($actualSpki -cne $ExpectedSpki) {
        throw "Executable signer SPKI does not match the enrolled publisher: $([IO.Path]::GetFileName($Path))"
    }
    if ($TimestampRequired -and $null -eq $signature.TimeStamperCertificate) {
        throw "Executable is missing the required Authenticode timestamp: $([IO.Path]::GetFileName($Path))"
    }
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

    $report = [ordered]@{
        schemaVersion = 1
        verified = $true
        packageId = $packageId
        channel = $channel
        version = $currentVersion
        manifestSha256 = Get-BytesSha256 $manifestBytes
        publisherSpkiSha256 = $certificateSpki
        authenticodeTimestampRequired = $RequireTimestamp.IsPresent
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
