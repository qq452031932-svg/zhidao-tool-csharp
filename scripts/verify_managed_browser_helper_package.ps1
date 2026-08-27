[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestSource,

    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$manifestVersion = 1
$protocolVersion = 1
$executionEngineVersion = 'ManagedChromium-v1'
$pythonVersion = '3.14.0'
$distributionMode = 'PersonalNonCommercial'
$maximumEntries = 32768
$maximumDepth = 64
$maximumFileBytes = 1024L * 1024 * 1024
$maximumTotalBytes = 4L * 1024 * 1024 * 1024
$manifestName = 'helper-manifest.json'

function Fail([string]$Reason) { throw "Managed browser helper Release gate failed: $Reason" }

function Get-LowerSha256([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($stream) }
    finally { $algorithm.Dispose(); $stream.Dispose() }
    return ([BitConverter]::ToString($hash).Replace('-', '')).ToLowerInvariant()
}

function Get-LowerSha256Bytes([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($Bytes) }
    finally { $algorithm.Dispose() }
    return ([BitConverter]::ToString($hash).Replace('-', '')).ToLowerInvariant()
}

function Test-SafeRelativePath([string]$RelativePath) {
    if ([string]::IsNullOrEmpty($RelativePath) -or $RelativePath.Length -gt 1024 -or
        $RelativePath.Contains('\') -or $RelativePath.Contains(':') -or
        $RelativePath.StartsWith('/') -or $RelativePath.EndsWith('/')) { return $false }
    foreach ($character in $RelativePath.ToCharArray()) {
        if ([int]$character -lt 32 -or [int]$character -eq 127) { return $false }
    }
    if ([IO.Path]::IsPathRooted($RelativePath.Replace('/', '\'))) { return $false }
    foreach ($segment in $RelativePath.Split('/')) {
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

$sourcePath = [IO.Path]::GetFullPath($ManifestSource)
$packagePath = [IO.Path]::GetFullPath($PackageDirectory)
$externalManifestPath = Join-Path $packagePath $manifestName
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { Fail 'fixed manifest source is missing' }
if (-not (Test-Path -LiteralPath $packagePath -PathType Container)) { Fail 'package is missing' }
if (([IO.File]::GetAttributes($packagePath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    Fail 'package root is a reparse point'
}
if (-not (Test-Path -LiteralPath $externalManifestPath -PathType Leaf)) { Fail 'manifest is missing' }
$sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
$externalBytes = [IO.File]::ReadAllBytes($externalManifestPath)
if ($sourceBytes.Length -eq 0 -or $sourceBytes.Length -gt 4 * 1024 * 1024 -or
    -not [Linq.Enumerable]::SequenceEqual([byte[]]$sourceBytes, [byte[]]$externalBytes)) {
    Fail 'external manifest differs from the fixed source manifest'
}
if ($sourceBytes[$sourceBytes.Length - 1] -ne 10 -or $sourceBytes -contains 13 -or
    ($sourceBytes.Length -ge 3 -and $sourceBytes[0] -eq 239 -and
        $sourceBytes[1] -eq 187 -and $sourceBytes[2] -eq 191)) {
    Fail 'manifest encoding is not canonical UTF-8/LF'
}
$manifest = [Text.Encoding]::UTF8.GetString($sourceBytes) | ConvertFrom-Json
if ($manifest.manifestVersion -ne $manifestVersion -or
    $manifest.protocolVersion -ne $protocolVersion -or
    $manifest.executionEngineVersion -cne $executionEngineVersion -or
    $manifest.pythonVersion -cne $pythonVersion -or
    $manifest.distributionMode -cne $distributionMode -or
    [string]$manifest.buildIdentity -cnotmatch '^[0-9a-f]{64}$') {
    Fail 'manifest metadata is incompatible'
}
$manifestFiles = @($manifest.files)
if ($manifestFiles.Count -lt 1 -or $manifestFiles.Count -gt 16384) {
    Fail 'manifest file count is outside the bound'
}
$expected = New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
$previous = $null
$identityText = "manifestVersion=$manifestVersion`nprotocolVersion=$protocolVersion`nexecutionEngineVersion=$executionEngineVersion`npythonVersion=$pythonVersion`ndistributionMode=$distributionMode`n"
$expectedTotal = 0L
foreach ($file in $manifestFiles) {
    $path = [string]$file.path
    $length = [long]$file.length
    $hash = [string]$file.sha256
    if (-not (Test-SafeRelativePath $path) -or $path -ieq $manifestName -or
        ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $path) -ge 0) -or
        $length -lt 0 -or $length -gt $maximumFileBytes -or
        $hash -cnotmatch '^[0-9a-f]{64}$' -or $expected.ContainsKey($path)) {
        Fail 'manifest contains an invalid file entry'
    }
    $expected.Add($path, $file)
    if ($length -gt $maximumTotalBytes - $expectedTotal) { Fail 'manifest payload exceeds the byte bound' }
    $expectedTotal += $length
    if ($expectedTotal -gt $maximumTotalBytes) { Fail 'manifest payload exceeds the byte bound' }
    $identityText += "$path`0$length`0$hash`n"
    $previous = $path
}
$utf8NoBom = New-Object Text.UTF8Encoding($false)
if ((Get-LowerSha256Bytes $utf8NoBom.GetBytes($identityText)) -cne [string]$manifest.buildIdentity) {
    Fail 'manifest build identity is invalid'
}
foreach ($required in @('python.exe', 'python314.dll', 'main.py', 'protocol.py',
        'requirements.lock', 'THIRD_PARTY_NOTICES.txt')) {
    if (-not $expected.ContainsKey($required)) { Fail "required payload is absent: $required" }
}

$actual = New-Object 'Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
$directories = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
[void]$directories.Add('')
$pending = New-Object 'Collections.Generic.Queue[object]'
$pending.Enqueue([pscustomobject]@{ Relative = ''; Full = $packagePath })
$entryCount = 0
while ($pending.Count -gt 0) {
    $current = $pending.Dequeue()
    foreach ($candidate in [IO.Directory]::EnumerateFileSystemEntries($current.Full)) {
        $entryCount++
        if ($entryCount -gt $maximumEntries) { Fail 'actual tree exceeds the entry bound' }
        $attributes = [IO.File]::GetAttributes($candidate)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Fail 'actual tree contains a reparse point' }
        $name = [IO.Path]::GetFileName($candidate)
        $relative = if ($current.Relative.Length -eq 0) { $name } else { "$($current.Relative)/$name" }
        if (-not (Test-SafeRelativePath $relative) -or
            ($relative.Split('/').Count -gt $maximumDepth)) { Fail 'actual tree contains an unsafe path' }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            if (-not $directories.Add($relative)) { Fail 'actual tree has a duplicate directory' }
            $pending.Enqueue([pscustomobject]@{ Relative = $relative; Full = $candidate })
        }
        else {
            if ($actual.ContainsKey($relative)) { Fail 'actual tree has a duplicate file' }
            $actual.Add($relative, $candidate)
        }
    }
}
if (-not $actual.ContainsKey($manifestName) -or $actual.Count -ne $expected.Count + 1) {
    Fail 'actual file inventory differs from the manifest'
}
foreach ($path in $actual.Keys) {
    if ($path -ieq $manifestName) { continue }
    if (-not $expected.ContainsKey($path) -or $path -cne [string]$expected[$path].path) {
        Fail 'actual file path is unlisted or has different casing'
    }
}
$requiredDirectories = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
[void]$requiredDirectories.Add('')
foreach ($path in $expected.Keys) {
    $slash = $path.LastIndexOf('/')
    while ($slash -gt 0) {
        [void]$requiredDirectories.Add($path.Substring(0, $slash))
        $slash = $path.LastIndexOf('/', $slash - 1)
    }
}
if ($directories.Count -ne $requiredDirectories.Count) { Fail 'actual directory inventory differs from the manifest' }
foreach ($directory in $directories) {
    if (-not $requiredDirectories.Contains($directory)) { Fail 'actual directory is unlisted' }
}
foreach ($path in $expected.Keys) {
    $actualInfo = [IO.FileInfo]::new($actual[$path])
    if ($actualInfo.Length -ne [long]$expected[$path].length -or
        (Get-LowerSha256 $actualInfo.FullName) -cne [string]$expected[$path].sha256) {
        Fail 'payload length or digest mismatch'
    }
}

Write-Output "Managed browser helper Release gate passed. BuildIdentity=$($manifest.buildIdentity)"
