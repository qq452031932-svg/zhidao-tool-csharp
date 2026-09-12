#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^https://[^\s;,]+$')][string]$Endpoint,
    [Parameter(Mandatory)][string]$ManifestAPath,
    [Parameter(Mandatory)][string]$ManifestBCPath,
    [Parameter(Mandatory)][string]$InstalledResponseSigningKeyId,
    [Parameter(Mandatory)][string]$InstalledResponseSigningSpkiDerBase64,
    [Parameter(Mandatory)][string]$ChallengePath,
    [Parameter(Mandatory)][string]$ChallengeSignatureBase64,
    [Parameter(Mandatory)][string]$EvidencePath,
    [ValidateRange(10,3600)][int]$PhaseTimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'; Set-StrictMode -Version Latest

function Read-Manifest([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw 'Client trust manifest is missing.' }
    $value = Get-Content -LiteralPath $full -Raw | ConvertFrom-Json
    if ($value.origin -cne 'https://ota.fanfh.cn' -or $value.superPanProduct -cne '8270e8d7-1791-487d-ad76-0da7a0cbed12' -or
        $value.currentTlsSpki -cnotmatch '^[0-9a-f]{64}$' -or $value.nextTlsSpki -cnotmatch '^[0-9a-f]{64}$' -or
        $value.currentTlsSpki -ceq $value.nextTlsSpki -or $value.signingKeyId -match 'placeholder|changeme|example' -or
        [string]::IsNullOrWhiteSpace($value.signingSpkiDerBase64)) { throw 'Client trust manifest is invalid.' }
    return $value
}

function Import-CanonicalP256([string]$Base64) {
    $der = [Convert]::FromBase64String($Base64)
    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        $read = 0
        $key.ImportSubjectPublicKeyInfo($der, [ref]$read)
        if ($read -ne $der.Length -or $key.KeySize -ne 256 -or [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()) -cne $Base64) { throw 'invalid' }
        return $key
    } catch { $key.Dispose(); throw 'Response-signing SPKI must be canonical ECDSA P-256 DER base64.' }
}

function Get-ObservedTlsSpki([Uri]$Uri) {
    $port = if ($Uri.IsDefaultPort) { 443 } else { $Uri.Port }
    $captured = $null
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.Connect($Uri.DnsSafeHost, $port)
        $callback = { param($sender,$certificate,$chain,$errors) $script:capturedCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate); return $errors -eq [Net.Security.SslPolicyErrors]::None }
        $stream = [Net.Security.SslStream]::new($client.GetStream(), $false, $callback)
        try { $stream.AuthenticateAsClient($Uri.DnsSafeHost); $captured = $script:capturedCertificate }
        finally { $stream.Dispose(); Remove-Variable capturedCertificate -Scope Script -ErrorAction SilentlyContinue }
        if ($null -eq $captured) { throw 'TLS peer certificate was not captured.' }
        return ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($captured.PublicKey.ExportSubjectPublicKeyInfo()))).ToLowerInvariant()
    } finally { if ($null -ne $captured) { $captured.Dispose() }; $client.Dispose() }
}

function Wait-ForPin([Uri]$Uri, [string]$Expected, [string]$Phase) {
    $deadline = [DateTime]::UtcNow.AddSeconds($PhaseTimeoutSeconds)
    do {
        try { if ((Get-ObservedTlsSpki $Uri) -ceq $Expected) { return } } catch { }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "TLS $Phase phase did not present the expected SPKI pin."
}

$uri = [Uri]$Endpoint
if ($uri.Scheme -cne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'Endpoint must be credential-free HTTPS.' }
$a = Read-Manifest $ManifestAPath; $bc = Read-Manifest $ManifestBCPath
if ($a.nextTlsSpki -cne $bc.currentTlsSpki -or @(@($a.currentTlsSpki,$a.nextTlsSpki,$bc.nextTlsSpki) | Sort-Object -Unique).Count -ne 3) { throw 'A-to-B and B-to-C manifests are not a valid three-pin chain.' }
if ($a.signingKeyId -cne $InstalledResponseSigningKeyId -or $bc.signingKeyId -cne $InstalledResponseSigningKeyId -or
    $a.signingSpkiDerBase64 -cne $InstalledResponseSigningSpkiDerBase64 -or $bc.signingSpkiDerBase64 -cne $InstalledResponseSigningSpkiDerBase64) { throw 'Installed response-signing identity differs from the exact clients.' }
$key = Import-CanonicalP256 $InstalledResponseSigningSpkiDerBase64
try {
    $challenge = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($ChallengePath)); $signature = [Convert]::FromBase64String($ChallengeSignatureBase64)
    if ($challenge.Length -lt 32 -or -not $key.VerifyData($challenge, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)) { throw 'Response-signing challenge verification failed.' }
    $spkiHash = ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($key.ExportSubjectPublicKeyInfo()))).ToLowerInvariant()
} finally { $key.Dispose() }

Wait-ForPin $uri $a.currentTlsSpki 'A'
Write-Host 'A verified. Switch the authorized local/staging endpoint to B; this process will wait and verify it.'
Wait-ForPin $uri $a.nextTlsSpki 'B'
Write-Host 'B verified. Roll the authorized local/staging endpoint back to A; this process will wait and verify it.'
Wait-ForPin $uri $a.currentTlsSpki 'rollback-A'

$record = [ordered]@{ rehearsal='A-to-B-to-A'; nextManifest='B-to-C'; endpointHost=$uri.DnsSafeHost; pinA=$a.currentTlsSpki; pinB=$a.nextTlsSpki; pinC=$bc.nextTlsSpki; responseSigningKeyId=$InstalledResponseSigningKeyId; responseSigningSpkiSha256=$spkiHash; challengeSha256=([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($challenge))).ToLowerInvariant(); privateMaterial='private material was never read or recorded'; completedUtc=[DateTime]::UtcNow.ToString('O') }
$json = $record | ConvertTo-Json
if ($json -match 'PRIVATE KEY|card=|password|secret') { throw 'Evidence redaction failed.' }
$evidenceFull = [IO.Path]::GetFullPath($EvidencePath); [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidenceFull)) | Out-Null
[IO.File]::WriteAllText($evidenceFull, $json + "`n", [Text.UTF8Encoding]::new($false))
