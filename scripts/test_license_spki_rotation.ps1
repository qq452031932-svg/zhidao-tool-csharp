[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^https://[^\s;,]+$')][string]$Endpoint,
    [Parameter(Mandatory)][string]$ClientRootA,
    [Parameter(Mandatory)][string]$ClientRootBC,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$ApprovedSigningCertificateThumbprint,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9.-]+$')][string]$ServerHost,
    [Parameter(Mandatory)][string]$ProbeStoreRoot,
    [Parameter(Mandatory)][DateTimeOffset]$ProbeUtcNow,
    [Parameter(Mandatory)][string]$EvidencePath,
    [ValidateRange(10,3600)][int]$PhaseTimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'; Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Security
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'ProductIdentity.ps1')
$productIdentity = Get-ProductIdentity -Path (Join-Path $repositoryRoot 'eng\ProductIdentity.props')
if ([string]::IsNullOrWhiteSpace($productIdentity.ProductMainAssemblyName) -or
    $productIdentity.ProductMainExecutableName -cne "$($productIdentity.ProductMainAssemblyName).exe") {
    throw 'Product identity does not define the expected main application executable.'
}

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
function Import-P256SubjectPublicKeyInfo([byte[]]$Der) {
    [byte[]]$prefix = 0x30,0x59,0x30,0x13,0x06,0x07,0x2a,0x86,0x48,0xce,0x3d,0x02,0x01,0x06,0x08,0x2a,0x86,0x48,0xce,0x3d,0x03,0x01,0x07,0x03,0x42,0x00,0x04
    if ($Der.Length -ne 91) { throw 'SPKI length is invalid.' }
    for ($index = 0; $index -lt $prefix.Length; $index++) { if ($Der[$index] -ne $prefix[$index]) { throw 'SPKI encoding is invalid.' } }
    [byte[]]$blob = 0x45,0x43,0x53,0x31,0x20,0x00,0x00,0x00
    [Array]::Resize([ref]$blob, 72)
    [Array]::Copy($Der, 27, $blob, 8, 64)
    return [Security.Cryptography.CngKey]::Import($blob, [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
}
function Copy-DerEcdsaInteger([byte[]]$Signature, [ref]$Offset, [byte[]]$Output, [int]$TargetOffset) {
    if ($Offset.Value + 2 -gt $Signature.Length -or $Signature[$Offset.Value] -ne 0x02) { throw 'ECDSA signature integer is invalid.' }
    $length = [int]$Signature[$Offset.Value + 1]; $Offset.Value += 2
    if ($length -lt 1 -or $length -gt 33 -or $Offset.Value + $length -gt $Signature.Length -or
        ($Signature[$Offset.Value] -band 0x80) -ne 0 -or
        ($length -gt 1 -and $Signature[$Offset.Value] -eq 0 -and ($Signature[$Offset.Value + 1] -band 0x80) -eq 0)) { throw 'ECDSA signature integer is invalid.' }
    if ($Signature[$Offset.Value] -eq 0) { $Offset.Value++; $length-- }
    if ($length -gt 32) { throw 'ECDSA signature integer is invalid.' }
    [Array]::Copy($Signature, $Offset.Value, $Output, $TargetOffset + 32 - $length, $length)
    $Offset.Value += $length
}
function Convert-DerEcdsaSignatureToP1363([byte[]]$Signature) {
    if ($Signature.Length -lt 8 -or $Signature[0] -ne 0x30 -or $Signature[1] -ge 0x80 -or $Signature[1] -ne $Signature.Length - 2) { throw 'ECDSA signature encoding is invalid.' }
    $output = New-Object byte[] 64; $offset = 2
    Copy-DerEcdsaInteger $Signature ([ref]$offset) $output 0
    Copy-DerEcdsaInteger $Signature ([ref]$offset) $output 32
    if ($offset -ne $Signature.Length) { throw 'ECDSA signature encoding is invalid.' }
    return ,$output
}
function Get-Sha256Hex([byte[]]$Bytes) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha256.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha256.Dispose() }
}
function Get-RandomHex([int]$ByteCount) {
    $bytes = New-Object byte[] $ByteCount; $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes); return ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $generator.Dispose() }
}

function Read-VerifiedClientRoot([string]$Root) {
    $root = [IO.Path]::GetFullPath($Root); $manifestPath = Join-Path $root 'production-license-clients.json'; $signaturePath = $manifestPath + '.p7s'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or -not (Test-Path -LiteralPath $signaturePath -PathType Leaf)) { throw 'Signed client manifest is missing.' }
    $bytes = [IO.File]::ReadAllBytes($manifestPath)
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes), $true); $cms.Decode([IO.File]::ReadAllBytes($signaturePath)); $cms.CheckSignature($false)
    $approvedThumbprint = $ApprovedSigningCertificateThumbprint.ToUpperInvariant()
    if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Thumbprint -cne $approvedThumbprint) { throw 'Client manifest must have the externally approved signer.' }
    $value = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    if ($value.sourceCommit -cne $ExpectedSourceCommit -or $value.origin -cne 'https://ota.fanfh.cn' -or
        $value.superPanProduct -cne '8270e8d7-1791-487d-ad76-0da7a0cbed12' -or $value.currentTlsSpki -cnotmatch '^[0-9a-f]{64}$' -or
        $value.nextTlsSpki -cnotmatch '^[0-9a-f]{64}$' -or $value.currentTlsSpki -ceq $value.nextTlsSpki -or
        $value.artifacts.Count -ne 3) { throw 'Signed client manifest identity is invalid.' }
    $requiredArtifacts = @{
        "superpan/$($productIdentity.ProductMainExecutableName)" = $false
        'smoke-nonshipping/SuperPan.Licensing.ProductionSmoke.NonShipping.exe' = $false
        'boundary-probe-nonshipping/SuperPan.Licensing.BOUNDARY-PROBE-NONSHIPPING.exe' = $false
    }
    $expected = @{}
    foreach ($file in @($value.files)) {
        if ($file.path -notmatch '^(superpan|smoke-nonshipping|boundary-probe-nonshipping)/[^/].*$' -or $file.path -match '\.\.') { throw 'Manifest file path is invalid.' }
        $path = [IO.Path]::GetFullPath((Join-Path $root $file.path.Replace('/',[IO.Path]::DirectorySeparatorChar)))
        if (-not $path.StartsWith($root.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $expected.ContainsKey($path)) { throw 'Manifest path escapes or duplicates.' }
        $expected.Add($path,$file.sha256)
    }
    $actual = @(foreach ($role in @('superpan','smoke-nonshipping','boundary-probe-nonshipping')) { Get-ChildItem -LiteralPath (Join-Path $root $role) -File -Recurse | Select-Object -ExpandProperty FullName })
    if ($actual.Count -ne $expected.Count) { throw 'Client tree contains missing or extra files.' }
    foreach ($path in $actual) { Assert-NoReparseContainedPath $root $path; if (-not $expected.ContainsKey($path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected[$path]) { throw 'Client artifact hash mismatch.' } }
    foreach ($artifact in @($value.artifacts)) {
        $artifactKey = "$($artifact.role)/$($artifact.file)"
        if (-not $requiredArtifacts.ContainsKey($artifactKey) -or $requiredArtifacts[$artifactKey] -or $artifact.signerThumbprint -cne $approvedThumbprint) { throw 'Manifest must contain exactly the three expected signed clients.' }
        $requiredArtifacts[$artifactKey] = $true
        $path = [IO.Path]::GetFullPath((Join-Path (Join-Path $root $artifact.role) $artifact.file)); Assert-NoReparseContainedPath $root $path
        $auth = Get-AuthenticodeSignature -LiteralPath $path
        $signtoolOutput = @(& (Get-Command signtool.exe -ErrorAction Stop).Source verify /pa /all /v $path 2>&1)
        if ($LASTEXITCODE -ne 0 -or ($signtoolOutput -join "`n") -notmatch '(?i)sha256' -or $auth.Status -ne 'Valid' -or $auth.SignerCertificate.Thumbprint -cne $approvedThumbprint -or $null -eq $auth.TimeStamperCertificate -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $artifact.sha256) { throw 'Exact client Authenticode identity is invalid.' }
    }
    return $value
}

function Assert-NoReparseContainedPath([string]$Root,[string]$Path) {
    $root=[IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar);$path=[IO.Path]::GetFullPath($Path)
    if(-not $path.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw'Client artifact escapes its signed root.'}
    $file=[IO.FileInfo]::new($path)
    if($file.Exists-and($file.Attributes-band[IO.FileAttributes]::ReparsePoint)){throw'Client artifact path contains a reparse point.'}
    $cursor=$file.Directory
    while($null-ne $cursor-and $cursor.FullName.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){if($cursor.Exists-and($cursor.Attributes-band[IO.FileAttributes]::ReparsePoint)){throw'Client artifact path contains a reparse point.'};$cursor=$cursor.Parent}
}

function Get-ObservedTlsSpki([Uri]$Uri) {
    $port = if ($Uri.IsDefaultPort) { 443 } else { $Uri.Port }; $captured = $null; $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.Connect($Uri.DnsSafeHost,$port)
        $callback = { param($sender,$certificate,$chain,$errors) $script:capturedCertificate=[Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate); return $errors -eq [Net.Security.SslPolicyErrors]::None }
        $stream=[Net.Security.SslStream]::new($client.GetStream(),$false,$callback)
        try { $stream.AuthenticateAsClient($Uri.DnsSafeHost); $captured=$script:capturedCertificate } finally { $stream.Dispose(); Remove-Variable capturedCertificate -Scope Script -ErrorAction SilentlyContinue }
        return Get-Sha256Hex (Get-CertificateSubjectPublicKeyInfo $captured)
    } finally { if($null-ne $captured){$captured.Dispose()};$client.Dispose() }
}
function Wait-ForPin([Uri]$Uri,[string]$Expected,[string]$Phase) { $deadline=[DateTime]::UtcNow.AddSeconds($PhaseTimeoutSeconds); do { try { if((Get-ObservedTlsSpki $Uri)-ceq $Expected){return} } catch{}; Start-Sleep 2 } while([DateTime]::UtcNow-lt $deadline); throw "TLS $Phase phase did not present the expected pin." }

function Invoke-ExactSmoke([string]$Executable,[Security.SecureString]$Card) {
    $bstr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($Card)
    try {
        $plain=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr); $start=[Diagnostics.ProcessStartInfo]::new($Executable); $start.UseShellExecute=$false; $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true; $start.Arguments='activate --card-stdin'
        $process=[Diagnostics.Process]::Start($start); $process.StandardInput.WriteLine($plain); $process.StandardInput.Close(); $output=$process.StandardOutput.ReadToEnd().Trim(); $process.WaitForExit()
        if($process.ExitCode-ne 0-or $output-cne 'ok'){throw "Exact signed smoke client did not authorize through the observed TLS phase (exit=$($process.ExitCode), safe_code=$output)."}
    } finally { if($null-ne $plain){$plain=$null};[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$uri=[Uri]$Endpoint
if($uri.AbsoluteUri-cne 'https://ota.fanfh.cn/' -or $ServerHost-ne $uri.DnsSafeHost){throw 'Rotation endpoint and installed host must be the exact production origin.'}
$a=Read-VerifiedClientRoot $ClientRootA; $bc=Read-VerifiedClientRoot $ClientRootBC
if($a.nextTlsSpki-cne $bc.currentTlsSpki-or @(@($a.currentTlsSpki,$a.nextTlsSpki,$bc.nextTlsSpki)|Sort-Object -Unique).Count-ne 3-or $a.signingSpkiDerBase64-cne $bc.signingSpkiDerBase64-or $a.signingKeyId-cne $bc.signingKeyId){throw 'A/B and B/C signed manifests do not form one trust chain.'}

$challenge=[Text.Encoding]::UTF8.GetBytes(([ordered]@{nonce=(Get-RandomHex 32);endpoint=$uri.AbsoluteUri;sourceCommit=$ExpectedSourceCommit;issuedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json -Compress))
$remote='set -eu; umask 077; t=$(mktemp); trap ''rm -f "$t"'' EXIT; tr -d ''\r\n'' | base64 -d >"$t"; cat /etc/superpan-license/response-signing.key-id; base64 -w0 /etc/superpan-license/response-signing.spki.der; printf "\n"; openssl dgst -sha256 -sign /etc/superpan-license/response-signing.key "$t" | base64 -w0; printf "\n"'
$remoteLines=@([Convert]::ToBase64String($challenge)|& ssh -- ("root@"+$ServerHost) $remote)
if($LASTEXITCODE-ne 0-or $remoteLines.Count-ne 3-or $remoteLines[0]-cne $a.signingKeyId-or $remoteLines[1]-cne $a.signingSpkiDerBase64){throw 'Installed response-signing identity does not match the exact clients.'}
$der=[Convert]::FromBase64String($remoteLines[1]);$cngKey=$null;$key=$null
try{$cngKey=Import-P256SubjectPublicKeyInfo $der;if([Convert]::ToBase64String($der)-cne $remoteLines[1]){throw 'Fresh installed response-signing challenge failed.'};$key=[Security.Cryptography.ECDsaCng]::new($cngKey);$key.HashAlgorithm=[Security.Cryptography.CngAlgorithm]::Sha256;$signature=Convert-DerEcdsaSignatureToP1363 ([Convert]::FromBase64String($remoteLines[2]));if(-not $key.VerifyData($challenge,$signature)){throw 'Fresh installed response-signing challenge failed.'};$spkiHash=Get-Sha256Hex $der}finally{if($null-ne $key){$key.Dispose()};if($null-ne $cngKey){$cngKey.Dispose()}}

$card=Read-Host 'Enter the deployment-smoke card (kept only in secure memory and sent only to the signed smoke client stdin)' -AsSecureString
$smoke=Join-Path (Join-Path ([IO.Path]::GetFullPath($ClientRootA)) 'smoke-nonshipping') 'SuperPan.Licensing.ProductionSmoke.NonShipping.exe'
Wait-ForPin $uri $a.currentTlsSpki 'A'; Invoke-ExactSmoke $smoke $card
Write-Host 'A verified. Switch the authorized endpoint to B.';Wait-ForPin $uri $a.nextTlsSpki 'B';Invoke-ExactSmoke $smoke $card
Write-Host 'B verified. Roll the authorized endpoint back to A.';Wait-ForPin $uri $a.currentTlsSpki 'rollback-A';Invoke-ExactSmoke $smoke $card

$probe=Join-Path (Join-Path ([IO.Path]::GetFullPath($ClientRootA)) 'boundary-probe-nonshipping') 'SuperPan.Licensing.BOUNDARY-PROBE-NONSHIPPING.exe'
& $probe --utc-now $ProbeUtcNow.ToUniversalTime().ToString('O') --store-root ([IO.Path]::GetFullPath($ProbeStoreRoot));if($LASTEXITCODE-ne 0){throw 'Exact signed boundary probe did not authorize the cached lease.'}
$record=[ordered]@{rehearsal='A-to-B-to-A';nextManifest='B-to-C';sourceCommit=$ExpectedSourceCommit;endpointHost=$uri.DnsSafeHost;pinA=$a.currentTlsSpki;pinB=$a.nextTlsSpki;pinC=$bc.nextTlsSpki;responseSigningKeyId=$a.signingKeyId;responseSigningSpkiSha256=$spkiHash;challengeSha256=(Get-Sha256Hex $challenge);privateMaterial='private material was never read or recorded';completedUtc=[DateTime]::UtcNow.ToString('O')}
$json=$record|ConvertTo-Json;if($json-match'PRIVATE KEY|card=|password|secret'){throw'Evidence redaction failed.'};$full=[IO.Path]::GetFullPath($EvidencePath);[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))|Out-Null;[IO.File]::WriteAllText($full,$json+"`n",[Text.UTF8Encoding]::new($false))
