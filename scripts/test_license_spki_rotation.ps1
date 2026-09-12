[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$PinA,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$PinB,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$PinC,
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][scriptblock]$ProbeA,
    [Parameter(Mandatory)][scriptblock]$ProbeB,
    [Parameter(Mandatory)][scriptblock]$ProbeRollbackA,
    [Parameter(Mandatory)][string]$ResponseSigningKeyId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ResponseSigningSpkiSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ChallengeSha256
)
$ErrorActionPreference = 'Stop'; Set-StrictMode -Version Latest
if (@(@($PinA,$PinB,$PinC) | Sort-Object -Unique).Count -ne 3) { throw 'A, B, and C must be distinct.' }
foreach ($probe in @($ProbeA,$ProbeB,$ProbeRollbackA)) { $result = & $probe; if ($result -ne 'verified') { throw 'A-to-B-to-A trust rehearsal failed closed.' } }
if ($ResponseSigningKeyId -match 'placeholder|changeme|example') { throw 'Installed response-signing key ID is required.' }
$record = [ordered]@{ rehearsal='A-to-B-to-A'; nextManifest='B-to-C'; pinA=$PinA; pinB=$PinB; pinC=$PinC; responseSigningKeyId=$ResponseSigningKeyId; responseSigningSpkiSha256=$ResponseSigningSpkiSha256; challengeSha256=$ChallengeSha256; privateMaterial='private material was never read or recorded'; completedUtc=[DateTime]::UtcNow.ToString('O') }
$json = $record | ConvertTo-Json
if ($json -match 'PRIVATE KEY|card=|password|secret') { throw 'Evidence redaction failed.' }
$directory = Split-Path -Parent ([IO.Path]::GetFullPath($EvidencePath)); [IO.Directory]::CreateDirectory($directory) | Out-Null
[IO.File]::WriteAllText([IO.Path]::GetFullPath($EvidencePath), $json + "`n", [Text.UTF8Encoding]::new($false))
