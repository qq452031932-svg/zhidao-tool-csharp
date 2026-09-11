[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('PersonalNonCommercial')]
    [string]$DistributionMode,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$PackageCacheDirectory,

    [switch]$Offline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$pythonVersion = '3.14.0'
$pythonArchiveName = 'python-3.14.0-embed-amd64.zip'
$pythonArchiveUri = 'https://www.python.org/ftp/python/3.14.0/python-3.14.0-embed-amd64.zip'
$pythonArchiveSha256 = '8d4d3590c10449d78aa4375f534e6d5f3027d67fdc362dd1a882279db6f90fdf'
$manifestVersion = 1
$protocolVersion = 3
$executionEngineVersion = 'ManagedChromium-v3'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$helperSource = Join-Path $repoRoot 'tools\managed_browser_helper'
$requirementsPath = Join-Path $helperSource 'requirements.lock'
$embeddedManifestPath = Join-Path $helperSource 'helper-manifest.json'
$noticeSourcePath = Join-Path $helperSource 'THIRD_PARTY_NOTICES.txt'
$mplLicenseSourcePath = Join-Path $helperSource 'license-inputs\MPL-2.0.txt'
$mplLicenseSha256 = '3f3d9e0024b1921b067d6f7f88deb4a60cbe7a78e76c64e3f1d7fc3b779b9d04'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if ([string]::IsNullOrWhiteSpace($PackageCacheDirectory)) {
    $PackageCacheDirectory = Join-Path $repoRoot '.artifacts\managed-browser-helper-cache'
}
$cachePath = [IO.Path]::GetFullPath($PackageCacheDirectory)

if ($DistributionMode -ne 'PersonalNonCommercial') {
    throw 'DrissionPage commercial/distributable packaging is blocked until written authorization is recorded or the dependency is replaced.'
}
if (Test-Path -LiteralPath $outputPath) {
    throw "OutputDirectory must not already exist: $outputPath"
}
foreach ($requiredSource in @($requirementsPath, $noticeSourcePath,
        $mplLicenseSourcePath, (Join-Path $helperSource 'main.py'),
        (Join-Path $helperSource 'protocol.py'), (Join-Path $helperSource 'identity_query.py'),
        (Join-Path $helperSource 'transfer_source.py'), (Join-Path $helperSource 'transfer_source_listing.py'),
        (Join-Path $helperSource 'transfer_picker.py'),
        (Join-Path $helperSource 'transfer_directory_listing.py'),
        (Join-Path $helperSource 'transfer_directory_mutation.py'),
        (Join-Path $helperSource 'transfer_target_baseline.py'),
        (Join-Path $helperSource 'transfer_submission.py'),
        (Join-Path $helperSource 'share_submission.py'),
        (Join-Path $helperSource 'session_operations.py'))) {
    if (-not (Test-Path -LiteralPath $requiredSource -PathType Leaf)) {
        throw "Required helper input is missing: $requiredSource"
    }
}
if ((Get-FileHash -LiteralPath $mplLicenseSourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        $mplLicenseSha256) {
    throw 'The reviewed MPL-2.0 source input digest does not match the locked value.'
}
[IO.Directory]::CreateDirectory($cachePath) | Out-Null
if (([IO.File]::GetAttributes($cachePath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The package cache directory must not be a reparse point.'
}

function Get-LowerSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-LowerSha256Bytes([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($Bytes) }
    finally { $algorithm.Dispose() }
    return ([BitConverter]::ToString($hash).Replace('-', '')).ToLowerInvariant()
}

function Get-RelativeChildPath([string]$Parent, [string]$Child) {
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $childFull = [IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the expected parent: $childFull"
    }
    return $childFull.Substring($parentFull.Length)
}

function Test-SafeRelativePath([string]$RelativePath, [switch]$AllowTrailingSlash) {
    if ([string]::IsNullOrEmpty($RelativePath) -or $RelativePath.Length -gt 1024 -or
        $RelativePath.Contains('\') -or $RelativePath.Contains(':') -or
        $RelativePath.StartsWith('/')) { return $false }
    foreach ($character in $RelativePath.ToCharArray()) {
        if ([int]$character -lt 32 -or [int]$character -eq 127) { return $false }
    }
    $value = if ($AllowTrailingSlash) { $RelativePath.TrimEnd('/') } else { $RelativePath }
    if ([string]::IsNullOrEmpty($value) -or (-not $AllowTrailingSlash -and $value.EndsWith('/')) -or
        [IO.Path]::IsPathRooted($value.Replace('/', '\'))) { return $false }
    foreach ($segment in $value.Split('/')) {
        if ([string]::IsNullOrEmpty($segment) -or $segment.Length -gt 255 -or
            $segment -in @('.', '..') -or $segment.EndsWith(' ') -or $segment.EndsWith('.')) {
            return $false
        }
        $stem = $segment.Split('.')[0].ToUpperInvariant()
        if ($stem -in @('CON', 'PRN', 'AUX', 'NUL',
                'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9',
                'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9')) {
            return $false
        }
    }
    return $true
}

function Get-VerifiedDownload(
    [string]$Uri,
    [string]$Destination,
    [string]$ExpectedSha256) {
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        if (([IO.File]::GetAttributes($Destination) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Cached input must not be a reparse point: $Destination"
        }
        if ((Get-LowerSha256 $Destination) -eq $ExpectedSha256) { return $Destination }
        throw "Cached input digest mismatch: $Destination"
    }
    if ($Offline) { throw "Offline cache miss for SHA-256 $ExpectedSha256" }
    $partial = "$Destination.partial-$([Guid]::NewGuid().ToString('N'))"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $partial
        if ((Get-LowerSha256 $partial) -ne $ExpectedSha256) {
            throw "Downloaded input digest mismatch: $Uri"
        }
        [IO.File]::Move($partial, $Destination)
    }
    finally {
        if (Test-Path -LiteralPath $partial) { [IO.File]::Delete($partial) }
    }
    return $Destination
}

function Copy-VerifiedInputToPrivateStaging(
    [string]$Source,
    [string]$Destination,
    [string]$ExpectedSha256) {
    if (([IO.File]::GetAttributes($Source) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Build input must not be a reparse point: $Source"
    }
    $input = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $output = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $input.CopyTo($output)
        $output.Flush($true)
    }
    finally {
        $output.Dispose()
        $input.Dispose()
    }
    if ((Get-LowerSha256 $Destination) -ne $ExpectedSha256) {
        throw "Private staged build input digest mismatch: $Destination"
    }
    return $Destination
}

function Assert-NonReparseAncestors(
    [string]$Path,
    [string]$Boundary) {
    $boundaryFull = [IO.Path]::GetFullPath($Boundary).TrimEnd('\')
    $pathFull = [IO.Path]::GetFullPath($Path)
    $prefix = $boundaryFull + '\'
    if (-not $pathFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Private input is outside its staging boundary: $pathFull"
    }
    $currentPath = $pathFull
    while (-not [string]::IsNullOrEmpty($currentPath)) {
        $attributes = [IO.File]::GetAttributes($currentPath)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Private input path contains a reparse point: $currentPath"
        }
        if ($currentPath.TrimEnd('\').Equals(
                $boundaryFull, [StringComparison]::OrdinalIgnoreCase)) { return }
        $currentPath = [IO.Path]::GetDirectoryName($currentPath)
    }
    throw "Private input staging boundary was not reached: $pathFull"
}

function Open-VerifiedZipInput(
    [string]$Path,
    [string]$Boundary,
    [string]$ExpectedSha256) {
    Assert-NonReparseAncestors $Path $Boundary
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $actual = ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $algorithm.Dispose() }
        if ($actual -ne $ExpectedSha256) {
            throw "Pinned private build input digest mismatch: $Path"
        }
        $stream.Position = 0
        return $stream
    }
    catch {
        $stream.Dispose()
        throw
    }
}

function Find-OrDownloadWheel(
    [string]$Name,
    [string]$Version,
    [string]$ExpectedSha256) {
    foreach ($candidate in [IO.Directory]::EnumerateFiles($cachePath, '*.whl')) {
        if ((Get-LowerSha256 $candidate) -eq $ExpectedSha256) { return $candidate }
    }
    if ($Offline) { throw "Offline wheel cache miss for $Name==$Version ($ExpectedSha256)" }
    $metadataUri = "https://pypi.org/pypi/$Name/$Version/json"
    $metadata = Invoke-RestMethod -Uri $metadataUri
    $artifact = @($metadata.urls | Where-Object {
        $_.packagetype -eq 'bdist_wheel' -and
        $_.digests.sha256 -eq $ExpectedSha256
    })
    if ($artifact.Count -ne 1) {
        throw "The pinned wheel was not uniquely found for $Name==$Version."
    }
    $destination = Join-Path $cachePath ([string]$artifact[0].filename)
    return Get-VerifiedDownload ([string]$artifact[0].url) $destination $ExpectedSha256
}

function Expand-SafeZip(
    [IO.Stream]$ArchiveStream,
    [string]$Destination,
    [switch]$SkipPackageTests) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    $destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
    $ArchiveStream.Position = 0
    $zip = [IO.Compression.ZipArchive]::new($ArchiveStream,
        [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        foreach ($entry in $zip.Entries) {
            $relative = $entry.FullName
            if ([string]::IsNullOrEmpty($relative)) { continue }
            if (-not (Test-SafeRelativePath $relative -AllowTrailingSlash)) {
                throw "Unsafe archive entry: $relative"
            }
            $segments = @($relative.TrimEnd('/').Split('/'))
            if ($SkipPackageTests -and ($segments | Where-Object {
                    $_ -in @('__pycache__', 'test', 'tests') -or $_.EndsWith('.pyc')
                })) { continue }
            $target = [IO.Path]::GetFullPath((Join-Path $Destination ($relative.Replace('/', '\'))))
            if (-not $target.StartsWith($destinationRoot, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Archive entry escapes staging: $relative"
            }
            if ($relative.EndsWith('/')) {
                [IO.Directory]::CreateDirectory($target) | Out-Null
                continue
            }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            if (Test-Path -LiteralPath $target) { throw "Archive file collision: $relative" }
            $inputStream = $entry.Open()
            $outputStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

$requirements = @()
foreach ($line in [IO.File]::ReadAllLines($requirementsPath)) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
    if ($line -notmatch '^([A-Za-z0-9_.-]+)==([^ ]+) --hash=sha256:([0-9a-f]{64})$') {
        throw "requirements.lock contains a non-canonical line: $line"
    }
    $requirements += [pscustomobject]@{ Name = $Matches[1]; Version = $Matches[2]; Hash = $Matches[3] }
}
if ($requirements.Count -ne 18) { throw 'requirements.lock must contain exactly 18 pinned hashed distributions.' }

$pythonArchive = Get-VerifiedDownload $pythonArchiveUri (Join-Path $cachePath $pythonArchiveName) $pythonArchiveSha256
$wheels = @()
foreach ($requirement in $requirements) {
    $wheels += Find-OrDownloadWheel $requirement.Name $requirement.Version $requirement.Hash
}

$outputParent = [IO.Path]::GetDirectoryName($outputPath)
[IO.Directory]::CreateDirectory($outputParent) | Out-Null
$staging = Join-Path $outputParent ('.managed-helper-staging-' + [Guid]::NewGuid().ToString('N'))
$inputStaging = Join-Path $outputParent ('.managed-helper-inputs-' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    [IO.Directory]::CreateDirectory($inputStaging) | Out-Null
    Assert-NonReparseAncestors $staging $outputParent
    Assert-NonReparseAncestors $inputStaging $outputParent
    $pythonArchive = Copy-VerifiedInputToPrivateStaging $pythonArchive `
        (Join-Path $inputStaging $pythonArchiveName) $pythonArchiveSha256
    $privateWheels = @()
    for ($index = 0; $index -lt $wheels.Count; $index++) {
        $privateWheels += Copy-VerifiedInputToPrivateStaging $wheels[$index] `
            (Join-Path $inputStaging ([IO.Path]::GetFileName($wheels[$index]))) `
            $requirements[$index].Hash
    }
    $wheels = $privateWheels
    $pythonStream = Open-VerifiedZipInput $pythonArchive $inputStaging $pythonArchiveSha256
    try { Expand-SafeZip $pythonStream $staging }
    finally { $pythonStream.Dispose() }
    $sitePackages = Join-Path $staging 'site-packages'
    [IO.Directory]::CreateDirectory($sitePackages) | Out-Null
    for ($index = 0; $index -lt $wheels.Count; $index++) {
        $wheelStream = Open-VerifiedZipInput $wheels[$index] $inputStaging $requirements[$index].Hash
        try { Expand-SafeZip $wheelStream $sitePackages -SkipPackageTests }
        finally { $wheelStream.Dispose() }
    }

    foreach ($name in @('main.py', 'protocol.py', 'identity_query.py', 'transfer_source.py', 'transfer_source_listing.py', 'transfer_directory_listing.py', 'transfer_directory_mutation.py', 'transfer_picker.py', 'transfer_target_baseline.py', 'transfer_submission.py', 'share_submission.py', 'session_operations.py', 'requirements.lock')) {
        [IO.File]::Copy((Join-Path $helperSource $name), (Join-Path $staging $name), $false)
    }

    $pthPath = Join-Path $staging 'python314._pth'
    if (-not (Test-Path -LiteralPath $pthPath -PathType Leaf)) { throw 'CPython _pth file is missing.' }
    $pthLines = @([IO.File]::ReadAllLines($pthPath) | Where-Object { $_ -notmatch '^#?import site$' })
    $pthLines += '.'
    $pthLines += 'site-packages'
    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($pthPath, (($pthLines -join "`n") + "`n"), $utf8NoBom)

    $noticeParts = New-Object Collections.Generic.List[string]
    $noticeHeader = [IO.File]::ReadAllText($noticeSourcePath).Replace("`r`n", "`n")
    $generatedNoticeMarker = "`n===== CPython $pythonVersion bundled LICENSE.txt =====`n"
    $markerOffset = $noticeHeader.IndexOf($generatedNoticeMarker, [StringComparison]::Ordinal)
    if ($markerOffset -ge 0) { $noticeHeader = $noticeHeader.Substring(0, $markerOffset) }
    $noticeParts.Add($noticeHeader.TrimEnd() + "`n")
    $pythonLicense = Join-Path $staging 'LICENSE.txt'
    if (-not (Test-Path -LiteralPath $pythonLicense -PathType Leaf)) {
        throw 'The CPython license file is missing.'
    }
    $noticeParts.Add($generatedNoticeMarker)
    $noticeParts.Add(([IO.File]::ReadAllText($pythonLicense).Replace("`r`n", "`n")))
    $declaredLicenseFiles = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($requirement in $requirements) {
        $normalizedRequiredName = ($requirement.Name -replace '[-_.]+', '-').ToLowerInvariant()
        $matchingMetadataDirectory = $null
        $matchingMetadataText = $null
        foreach ($metadataFile in [IO.Directory]::EnumerateFiles(
                $sitePackages, 'METADATA', [IO.SearchOption]::AllDirectories)) {
            if (-not ([IO.Path]::GetDirectoryName($metadataFile).EndsWith('.dist-info',
                        [StringComparison]::OrdinalIgnoreCase))) { continue }
            $metadataText = [IO.File]::ReadAllText($metadataFile)
            $nameMatch = [Text.RegularExpressions.Regex]::Match(
                $metadataText, '^Name:\s*(.+?)\s*$', [Text.RegularExpressions.RegexOptions]::Multiline)
            $versionMatch = [Text.RegularExpressions.Regex]::Match(
                $metadataText, '^Version:\s*(.+?)\s*$', [Text.RegularExpressions.RegexOptions]::Multiline)
            if (-not $nameMatch.Success -or -not $versionMatch.Success) { continue }
            $normalizedMetadataName = ($nameMatch.Groups[1].Value -replace '[-_.]+', '-').ToLowerInvariant()
            if ($normalizedMetadataName -eq $normalizedRequiredName -and
                $versionMatch.Groups[1].Value -eq $requirement.Version) {
                if ($null -ne $matchingMetadataDirectory) {
                    throw "Duplicate distribution metadata for $($requirement.Name)==$($requirement.Version)."
                }
                $matchingMetadataDirectory = [IO.Path]::GetDirectoryName($metadataFile)
                $matchingMetadataText = $metadataText
            }
        }
        if ($null -eq $matchingMetadataDirectory) {
            throw "Distribution metadata is missing for $($requirement.Name)==$($requirement.Version)."
        }
        $licenseDeclarations = @([Text.RegularExpressions.Regex]::Matches(
            $matchingMetadataText, '^License-File:\s*(.+?)\s*$',
            [Text.RegularExpressions.RegexOptions]::Multiline) | ForEach-Object {
                $_.Groups[1].Value
            })
        if ($licenseDeclarations.Count -eq 0) {
            throw "METADATA License-File is missing for $($requirement.Name)==$($requirement.Version)."
        }
        foreach ($declaration in $licenseDeclarations) {
            $declaredRelative = $declaration.Replace('\', '/')
            if (-not (Test-SafeRelativePath $declaredRelative)) {
                throw "Unsafe METADATA License-File for $($requirement.Name)==$($requirement.Version)."
            }
            $candidates = @(@(
                    (Join-Path $matchingMetadataDirectory ($declaredRelative.Replace('/', '\'))),
                    (Join-Path (Join-Path $matchingMetadataDirectory 'licenses') `
                        ($declaredRelative.Replace('/', '\')))
                ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
            if ($candidates.Count -ne 1) {
                throw "Declared license text is missing or ambiguous for $($requirement.Name)==$($requirement.Version): $declaration"
            }
            [void]$declaredLicenseFiles.Add([IO.Path]::GetFullPath($candidates[0]))
        }
    }
    $licenseFiles = [string[]]@($declaredLicenseFiles)
    [Array]::Sort($licenseFiles, [StringComparer]::Ordinal)
    $noticeParts.Add("`n===== Locked MPL-2.0 complete license text =====`n")
    $noticeParts.Add(([IO.File]::ReadAllText($mplLicenseSourcePath).Replace("`r`n", "`n")))
    foreach ($licenseFile in $licenseFiles) {
        $relativeLicense = (Get-RelativeChildPath $sitePackages $licenseFile).Replace('\', '/')
        $noticeParts.Add("`n===== $relativeLicense =====`n")
        $noticeParts.Add(([IO.File]::ReadAllText($licenseFile).Replace("`r`n", "`n")))
    }
    $packagedNotice = (($noticeParts -join '').TrimEnd() + "`n")
    [IO.File]::WriteAllText((Join-Path $staging 'THIRD_PARTY_NOTICES.txt'),
        $packagedNotice, $utf8NoBom)

    $payloadPaths = [string[]]@([IO.Directory]::EnumerateFiles(
        $staging, '*', [IO.SearchOption]::AllDirectories))
    [Array]::Sort($payloadPaths, [StringComparer]::Ordinal)
    $manifestFiles = @()
    foreach ($file in $payloadPaths) {
        $relative = (Get-RelativeChildPath $staging $file).Replace('\', '/')
        if ($relative -eq 'helper-manifest.json') { continue }
        if (-not (Test-SafeRelativePath $relative)) {
            throw "Unsafe helper payload path: $relative"
        }
        $info = [IO.FileInfo]::new($file)
        $manifestFiles += [pscustomobject][ordered]@{
            path = $relative
            length = $info.Length
            sha256 = Get-LowerSha256 $file
        }
    }
    $identityText = "manifestVersion=$manifestVersion`nprotocolVersion=$protocolVersion`nexecutionEngineVersion=$executionEngineVersion`npythonVersion=$pythonVersion`ndistributionMode=$DistributionMode`n"
    foreach ($file in $manifestFiles) {
        $identityText += "$($file.path)`0$($file.length)`0$($file.sha256)`n"
    }
    $identityBytes = $utf8NoBom.GetBytes($identityText)
    $buildIdentity = Get-LowerSha256Bytes $identityBytes
    $manifest = [pscustomobject][ordered]@{
        manifestVersion = $manifestVersion
        protocolVersion = $protocolVersion
        executionEngineVersion = $executionEngineVersion
        pythonVersion = $pythonVersion
        distributionMode = $DistributionMode
        buildIdentity = $buildIdentity
        files = $manifestFiles
    }
    $manifestJson = (($manifest | ConvertTo-Json -Depth 5).Replace("`r`n", "`n").TrimEnd() + "`n")
    [IO.File]::WriteAllText((Join-Path $staging 'helper-manifest.json'), $manifestJson, $utf8NoBom)
    $generatedManifestBytes = $utf8NoBom.GetBytes($manifestJson)
    $checkedInManifestBytes = [IO.File]::ReadAllBytes($embeddedManifestPath)
    if (-not [Linq.Enumerable]::SequenceEqual(
            [byte[]]$generatedManifestBytes, [byte[]]$checkedInManifestBytes)) {
        throw 'The generated manifest differs from the checked-in manifest.'
    }
    $generatedNoticeBytes = $utf8NoBom.GetBytes($packagedNotice)
    $checkedInNoticeBytes = [IO.File]::ReadAllBytes($noticeSourcePath)
    if (-not [Linq.Enumerable]::SequenceEqual(
            [byte[]]$generatedNoticeBytes, [byte[]]$checkedInNoticeBytes)) {
        throw 'The generated notice differs from the checked-in notice.'
    }
    [IO.Directory]::Move($staging, $outputPath)
    $staging = $null
    Write-Output "Managed browser helper package created. BuildIdentity=$buildIdentity"
}
finally {
    if ($null -ne $staging -and (Test-Path -LiteralPath $staging)) {
        [IO.Directory]::Delete($staging, $true)
    }
    if (Test-Path -LiteralPath $inputStaging) {
        [IO.Directory]::Delete($inputStaging, $true)
    }
}
