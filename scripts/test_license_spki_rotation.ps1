#requires -Version 7.2
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
        'superpan/BaiduBatchTransfer.App.exe' = $false
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
        return ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($captured.PublicKey.ExportSubjectPublicKeyInfo()))).ToLowerInvariant()
    } finally { if($null-ne $captured){$captured.Dispose()};$client.Dispose() }
}
function Wait-ForPin([Uri]$Uri,[string]$Expected,[string]$Phase) { $deadline=[DateTime]::UtcNow.AddSeconds($PhaseTimeoutSeconds); do { try { if((Get-ObservedTlsSpki $Uri)-ceq $Expected){return} } catch{}; Start-Sleep 2 } while([DateTime]::UtcNow-lt $deadline); throw "TLS $Phase phase did not present the expected pin." }

function Invoke-ExactSmoke([string]$Executable,[Security.SecureString]$Card) {
    $bstr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($Card)
    try {
        $plain=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr); $start=[Diagnostics.ProcessStartInfo]::new($Executable); $start.UseShellExecute=$false; $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true; $start.ArgumentList.Add('activate');$start.ArgumentList.Add('--card-stdin')
        $process=[Diagnostics.Process]::Start($start); $process.StandardInput.WriteLine($plain); $process.StandardInput.Close(); $output=$process.StandardOutput.ReadToEnd().Trim(); $process.WaitForExit()
        if($process.ExitCode-ne 0-or $output-cne 'ok'){throw 'Exact signed smoke client did not authorize through the observed TLS phase.'}
    } finally { if($null-ne $plain){$plain=$null};[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$uri=[Uri]$Endpoint
if($uri.AbsoluteUri-cne 'https://ota.fanfh.cn/' -or $ServerHost-ne $uri.DnsSafeHost){throw 'Rotation endpoint and installed host must be the exact production origin.'}
$a=Read-VerifiedClientRoot $ClientRootA; $bc=Read-VerifiedClientRoot $ClientRootBC
if($a.nextTlsSpki-cne $bc.currentTlsSpki-or @(@($a.currentTlsSpki,$a.nextTlsSpki,$bc.nextTlsSpki)|Sort-Object -Unique).Count-ne 3-or $a.signingSpkiDerBase64-cne $bc.signingSpkiDerBase64-or $a.signingKeyId-cne $bc.signingKeyId){throw 'A/B and B/C signed manifests do not form one trust chain.'}

$challenge=[Text.Encoding]::UTF8.GetBytes(([ordered]@{nonce=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant();endpoint=$uri.AbsoluteUri;sourceCommit=$ExpectedSourceCommit;issuedUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json -Compress))
$remote='set -eu; umask 077; t=$(mktemp); trap ''rm -f "$t"'' EXIT; base64 -d >"$t"; cat /etc/superpan-license/response-signing.key-id; base64 -w0 /etc/superpan-license/response-signing.spki.der; printf "\n"; openssl dgst -sha256 -sign /etc/superpan-license/response-signing.pem "$t" | base64 -w0; printf "\n"'
$remoteLines=@([Convert]::ToBase64String($challenge)|& ssh -- ("root@"+$ServerHost) $remote)
if($LASTEXITCODE-ne 0-or $remoteLines.Count-ne 3-or $remoteLines[0]-cne $a.signingKeyId-or $remoteLines[1]-cne $a.signingSpkiDerBase64){throw 'Installed response-signing identity does not match the exact clients.'}
$der=[Convert]::FromBase64String($remoteLines[1]);$key=[Security.Cryptography.ECDsa]::Create()
try{$read=0;$key.ImportSubjectPublicKeyInfo($der,[ref]$read);if($read-ne $der.Length-or $key.KeySize-ne 256-or[Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())-cne $remoteLines[1]-or-not $key.VerifyData($challenge,[Convert]::FromBase64String($remoteLines[2]),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)){throw 'Fresh installed response-signing challenge failed.'};$spkiHash=([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($der))).ToLowerInvariant()}finally{$key.Dispose()}

$card=Read-Host 'Enter the deployment-smoke card (kept only in secure memory and sent only to the signed smoke client stdin)' -AsSecureString
$smoke=Join-Path (Join-Path ([IO.Path]::GetFullPath($ClientRootA)) 'smoke-nonshipping') 'SuperPan.Licensing.ProductionSmoke.NonShipping.exe'
Wait-ForPin $uri $a.currentTlsSpki 'A'; Invoke-ExactSmoke $smoke $card
Write-Host 'A verified. Switch the authorized endpoint to B.';Wait-ForPin $uri $a.nextTlsSpki 'B';Invoke-ExactSmoke $smoke $card
Write-Host 'B verified. Roll the authorized endpoint back to A.';Wait-ForPin $uri $a.currentTlsSpki 'rollback-A';Invoke-ExactSmoke $smoke $card

$probe=Join-Path (Join-Path ([IO.Path]::GetFullPath($ClientRootA)) 'boundary-probe-nonshipping') 'SuperPan.Licensing.BOUNDARY-PROBE-NONSHIPPING.exe'
& $probe --utc-now $ProbeUtcNow.ToUniversalTime().ToString('O') --store-root ([IO.Path]::GetFullPath($ProbeStoreRoot));if($LASTEXITCODE-ne 0){throw 'Exact signed boundary probe did not authorize the cached lease.'}
$record=[ordered]@{rehearsal='A-to-B-to-A';nextManifest='B-to-C';sourceCommit=$ExpectedSourceCommit;endpointHost=$uri.DnsSafeHost;pinA=$a.currentTlsSpki;pinB=$a.nextTlsSpki;pinC=$bc.nextTlsSpki;responseSigningKeyId=$a.signingKeyId;responseSigningSpkiSha256=$spkiHash;challengeSha256=([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($challenge))).ToLowerInvariant();privateMaterial='private material was never read or recorded';completedUtc=[DateTime]::UtcNow.ToString('O')}
$json=$record|ConvertTo-Json;if($json-match'PRIVATE KEY|card=|password|secret'){throw'Evidence redaction failed.'};$full=[IO.Path]::GetFullPath($EvidencePath);[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))|Out-Null;[IO.File]::WriteAllText($full,$json+"`n",[Text.UTF8Encoding]::new($false))
