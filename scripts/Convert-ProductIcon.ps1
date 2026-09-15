[CmdletBinding()]
param(
    [Parameter()]
    [string]$SourceJpegPath,

    [Parameter()]
    [string]$CanonicalPngPath,

    [Parameter()]
    [string]$IconPath,

    [Parameter()]
    [switch]$VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ExpectedSourceSha256 = '94BD33FCEB2228C45F6CCF44862D49E90F6DBECD2859445D2DBA67241AFA0A9B'
$ExpectedCanonicalPngSha256 = '358C5937AC2791EF15D4C1B46FDC90EB401BBFD05996FE916C8B066C47AB1A39'
$ExpectedIconSha256 = '0CC6A91ABED05FAE671C4635E8903B9F2DB3E6931B72BDAD907B57840D7F97FA'
$RequiredSizes = @(16, 24, 32, 48, 64, 128, 256)
$ProductIconBaseName = [string]::Concat(
    [char]0x77E5,
    [char]0x9053,
    [char]0x7EFC,
    [char]0x5408,
    [char]0x5DE5,
    [char]0x5177)

if ([string]::IsNullOrWhiteSpace($CanonicalPngPath)) {
    $brandingDirectory = Join-Path $PSScriptRoot '..\src\BaiduBatchTransfer.App\Assets\Branding'
    $CanonicalPngPath = Join-Path $brandingDirectory ($ProductIconBaseName + '.png')
}
if ([string]::IsNullOrWhiteSpace($IconPath)) {
    $brandingDirectory = Join-Path $PSScriptRoot '..\src\BaiduBatchTransfer.App\Assets\Branding'
    $IconPath = Join-Path $brandingDirectory ($ProductIconBaseName + '.ico')
}

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    $stream = [System.IO.File]::OpenRead($LiteralPath)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '')
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Assert-ExpectedHash {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$Actual,
        [Parameter(Mandatory = $true)][string]$Expected
    )

    if ($Actual -ne $Expected) {
        throw "$Label SHA256 mismatch. Expected $Expected but found $Actual."
    }
}

function Get-BytesSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function ConvertTo-OnLoadBitmap {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $stream = New-Object System.IO.MemoryStream -ArgumentList @(,$Bytes)
    try {
        $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
            $stream,
            [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        if ($decoder.Frames.Count -ne 1) {
            throw "Expected exactly one image frame but found $($decoder.Frames.Count)."
        }
        $frame = $decoder.Frames[0]
        $frame.Freeze()
        return $frame
    }
    finally {
        $stream.Dispose()
    }
}

function Get-BitmapFromFile {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "Required image does not exist: $LiteralPath"
    }
    return ConvertTo-OnLoadBitmap -Bytes ([System.IO.File]::ReadAllBytes($LiteralPath))
}

function Assert-Square640 {
    param(
        [Parameter(Mandatory = $true)]$Bitmap,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Bitmap.PixelWidth -ne 640 -or $Bitmap.PixelHeight -ne 640) {
        throw "$Label must be exactly 640x640; found $($Bitmap.PixelWidth)x$($Bitmap.PixelHeight)."
    }
}

function ConvertTo-PngBytes {
    param([Parameter(Mandatory = $true)]$Bitmap)

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $stream = New-Object System.IO.MemoryStream
    try {
        $encoder.Save($stream)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Resize-CanonicalBitmap {
    param(
        [Parameter(Mandatory = $true)]$Bitmap,
        [Parameter(Mandatory = $true)][int]$Size
    )

    # Exact scaling algorithm: WIC TransformedBitmap with a uniform Size/640 ScaleTransform.
    $transform = New-Object System.Windows.Media.ScaleTransform -ArgumentList @(
        ([double]$Size / [double]$Bitmap.PixelWidth),
        ([double]$Size / [double]$Bitmap.PixelHeight))
    $resized = New-Object System.Windows.Media.Imaging.TransformedBitmap
    $resized.BeginInit()
    $resized.Source = $Bitmap
    $resized.Transform = $transform
    $resized.EndInit()
    $resized.Freeze()
    return $resized
}

function Get-PixelSha256 {
    param([Parameter(Mandatory = $true)]$Bitmap)

    $converted = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap -ArgumentList @(
        $Bitmap,
        [System.Windows.Media.PixelFormats]::Bgra32,
        $null,
        0)
    $stride = $converted.PixelWidth * 4
    $pixels = New-Object byte[] ($stride * $converted.PixelHeight)
    $converted.CopyPixels($pixels, $stride, 0)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($pixels))).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Write-IconFile {
    param(
        [Parameter(Mandatory = $true)][byte[][]]$Frames,
        [Parameter(Mandatory = $true)][int[]]$Sizes,
        [Parameter(Mandatory = $true)][string]$LiteralPath
    )

    if ($Frames.Count -ne $Sizes.Count) {
        throw 'ICO frame and size counts differ.'
    }
    $directoryLength = 6 + (16 * $Frames.Count)
    $offset = $directoryLength
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter -ArgumentList $stream
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$Frames.Count)
        for ($index = 0; $index -lt $Frames.Count; $index++) {
            $sizeByte = if ($Sizes[$index] -eq 256) { [byte]0 } else { [byte]$Sizes[$index] }
            $writer.Write($sizeByte)
            $writer.Write($sizeByte)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$Frames[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $Frames[$index].Length
        }
        foreach ($frame in $Frames) {
            $writer.Write($frame)
        }
        $writer.Flush()
        $iconBytes = $stream.ToArray()
        $iconHash = Get-BytesSha256 -Bytes $iconBytes
        Assert-ExpectedHash -Label 'Generated ICO' -Actual $iconHash -Expected $ExpectedIconSha256
        [System.IO.File]::WriteAllBytes($LiteralPath, $iconBytes)
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Read-AndValidateIcon {
    param(
        [Parameter(Mandatory = $true)][string]$LiteralPath,
        [Parameter(Mandatory = $true)]$CanonicalBitmap
    )

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "Required ICO does not exist: $LiteralPath"
    }
    $bytes = [System.IO.File]::ReadAllBytes($LiteralPath)
    $stream = New-Object System.IO.MemoryStream -ArgumentList @(,$bytes)
    $reader = New-Object System.IO.BinaryReader -ArgumentList $stream
    try {
        if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1) {
            throw 'ICO header is invalid.'
        }
        $count = $reader.ReadUInt16()
        if ($count -ne $RequiredSizes.Count) {
            throw "ICO must contain exactly $($RequiredSizes.Count) frames; found $count."
        }
        $minimumOffset = 6 + (16 * $count)
        $entries = @()
        for ($index = 0; $index -lt $count; $index++) {
            $widthByte = $reader.ReadByte()
            $heightByte = $reader.ReadByte()
            [void]$reader.ReadByte()
            [void]$reader.ReadByte()
            $planes = $reader.ReadUInt16()
            $bits = $reader.ReadUInt16()
            $length = $reader.ReadUInt32()
            $offset = $reader.ReadUInt32()
            $width = if ($widthByte -eq 0) { 256 } else { [int]$widthByte }
            $height = if ($heightByte -eq 0) { 256 } else { [int]$heightByte }
            if ($width -ne $height -or $planes -ne 1 -or $bits -ne 32 -or $length -eq 0) {
                throw "ICO directory entry $index is invalid."
            }
            if ($offset -lt $minimumOffset -or ([uint64]$offset + [uint64]$length) -gt [uint64]$bytes.Length) {
                throw "ICO directory entry $index has an invalid byte range."
            }
            $entries += [pscustomobject]@{ Width = $width; Length = [int]$length; Offset = [int]$offset }
        }
        $actualSizes = @($entries | ForEach-Object { $_.Width } | Sort-Object)
        if (@(Compare-Object -ReferenceObject $RequiredSizes -DifferenceObject $actualSizes).Count -ne 0) {
            throw "ICO sizes differ from the required set: $($actualSizes -join ', ')."
        }
        $ordered = @($entries | Sort-Object Offset)
        for ($index = 1; $index -lt $ordered.Count; $index++) {
            if (($ordered[$index - 1].Offset + $ordered[$index - 1].Length) -gt $ordered[$index].Offset) {
                throw 'ICO frame byte ranges overlap.'
            }
        }
        foreach ($entry in $entries) {
            $frameBytes = New-Object byte[] $entry.Length
            [System.Array]::Copy($bytes, $entry.Offset, $frameBytes, 0, $entry.Length)
            $hasPngSignature = $frameBytes.Length -ge 8 `
                -and $frameBytes[0] -eq 137 `
                -and $frameBytes[1] -eq 80 `
                -and $frameBytes[2] -eq 78 `
                -and $frameBytes[3] -eq 71 `
                -and $frameBytes[4] -eq 13 `
                -and $frameBytes[5] -eq 10 `
                -and $frameBytes[6] -eq 26 `
                -and $frameBytes[7] -eq 10
            if (-not $hasPngSignature) {
                throw "ICO $($entry.Width)x$($entry.Width) frame is not PNG encoded."
            }
            $decoded = ConvertTo-OnLoadBitmap -Bytes $frameBytes
            if ($decoded.PixelWidth -ne $entry.Width -or $decoded.PixelHeight -ne $entry.Width) {
                throw "ICO $($entry.Width)x$($entry.Width) frame dimensions do not match its directory entry."
            }
            if ($entry.Width -eq 256) {
                $expected = Resize-CanonicalBitmap -Bitmap $CanonicalBitmap -Size 256
                if ((Get-PixelSha256 -Bitmap $decoded) -ne (Get-PixelSha256 -Bitmap $expected)) {
                    throw 'ICO 256x256 frame pixels do not match the canonical WIC scaling result.'
                }
            }
            Write-Output "Frame: $($entry.Width)x$($entry.Width)"
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

$canonicalFullPath = [System.IO.Path]::GetFullPath($CanonicalPngPath)
$iconFullPath = [System.IO.Path]::GetFullPath($IconPath)

if ($SourceJpegPath) {
    $sourceFullPath = [System.IO.Path]::GetFullPath($SourceJpegPath)
    if (-not (Test-Path -LiteralPath $sourceFullPath -PathType Leaf)) {
        throw "Source JPEG does not exist: $sourceFullPath"
    }
    $actualSourceHash = Get-FileSha256 -LiteralPath $sourceFullPath
    Assert-ExpectedHash -Label 'Source JPEG' -Actual $actualSourceHash -Expected $ExpectedSourceSha256
    $sourceBytes = [System.IO.File]::ReadAllBytes($sourceFullPath)
    if ($sourceBytes.Length -lt 2 -or $sourceBytes[0] -ne 0xFF -or $sourceBytes[1] -ne 0xD8) {
        throw 'Source file is not a JPEG.'
    }
    $sourceBitmap = ConvertTo-OnLoadBitmap -Bytes $sourceBytes
    Assert-Square640 -Bitmap $sourceBitmap -Label 'Source JPEG'
    $sourceDimensions = "$($sourceBitmap.PixelWidth)x$($sourceBitmap.PixelHeight)"
}
else {
    $actualSourceHash = $ExpectedSourceSha256
    $sourceDimensions = '640x640'
}

if (-not $VerifyOnly) {
    $canonicalDirectory = [System.IO.Path]::GetDirectoryName($canonicalFullPath)
    $iconDirectory = [System.IO.Path]::GetDirectoryName($iconFullPath)
    [System.IO.Directory]::CreateDirectory($canonicalDirectory) | Out-Null
    [System.IO.Directory]::CreateDirectory($iconDirectory) | Out-Null

    if ($SourceJpegPath) {
        $canonicalBytes = ConvertTo-PngBytes -Bitmap $sourceBitmap
        $generatedCanonicalHash = Get-BytesSha256 -Bytes $canonicalBytes
        Assert-ExpectedHash -Label 'Generated canonical PNG' -Actual $generatedCanonicalHash -Expected $ExpectedCanonicalPngSha256
        [System.IO.File]::WriteAllBytes($canonicalFullPath, $canonicalBytes)
    }
    $canonicalForGeneration = Get-BitmapFromFile -LiteralPath $canonicalFullPath
    Assert-Square640 -Bitmap $canonicalForGeneration -Label 'Canonical PNG'
    $canonicalGenerationHash = Get-FileSha256 -LiteralPath $canonicalFullPath
    Assert-ExpectedHash -Label 'Canonical PNG' -Actual $canonicalGenerationHash -Expected $ExpectedCanonicalPngSha256

    [byte[][]]$frames = @(
        foreach ($size in $RequiredSizes) {
            $resized = Resize-CanonicalBitmap -Bitmap $canonicalForGeneration -Size $size
            ,(ConvertTo-PngBytes -Bitmap $resized)
        }
    )
    Write-IconFile -Frames $frames -Sizes $RequiredSizes -LiteralPath $iconFullPath
}

$canonicalBitmap = Get-BitmapFromFile -LiteralPath $canonicalFullPath
Assert-Square640 -Bitmap $canonicalBitmap -Label 'Canonical PNG'
$canonicalHash = Get-FileSha256 -LiteralPath $canonicalFullPath
$iconHash = Get-FileSha256 -LiteralPath $iconFullPath
Assert-ExpectedHash -Label 'Canonical PNG' -Actual $canonicalHash -Expected $ExpectedCanonicalPngSha256
Assert-ExpectedHash -Label 'ICO' -Actual $iconHash -Expected $ExpectedIconSha256
if ($SourceJpegPath -and (Get-PixelSha256 -Bitmap $sourceBitmap) -ne (Get-PixelSha256 -Bitmap $canonicalBitmap)) {
    throw 'Canonical PNG pixels do not exactly match the decoded user JPEG.'
}

Write-Output "Source JPEG SHA256: $actualSourceHash"
Write-Output "Canonical PNG SHA256: $canonicalHash"
Write-Output "ICO SHA256: $iconHash"
Write-Output "SourceDimensions=$sourceDimensions"
Write-Output "CanonicalDimensions=$($canonicalBitmap.PixelWidth)x$($canonicalBitmap.PixelHeight)"
Read-AndValidateIcon -LiteralPath $iconFullPath -CanonicalBitmap $canonicalBitmap
