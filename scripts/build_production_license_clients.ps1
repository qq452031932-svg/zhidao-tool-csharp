#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]{1,128}$')][string]$LicenseSigningKeyId,
    [Parameter(Mandatory)][string]$LicenseSigningPublicKey,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$CurrentTlsSpkiPin,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$NextTlsSpkiPin,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$SigningCertificateThumbprint,
    [Parameter(Mandatory)][ValidatePattern('^https://[^\s;,]+$')][string]$TimestampUrl,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$ManagedBrowserHelperPackageDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert-NoReparsePointAncestors([string]$Path) {
    $cursor = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path)))
    while ($null -ne $cursor) {
        if ($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Production output ancestors cannot be reparse points.' }
        $cursor = $cursor.Parent
    }
}
function Assert-LocalDrivePath([string]$Path) {
    if ($Path.StartsWith('\\', [StringComparison]::Ordinal) -or [IO.Path]::GetPathRoot($Path) -notmatch '^[A-Za-z]:\\$') { throw 'Production output must use a local drive path.' }
}
$origin = 'https://ota.fanfh.cn'
$superPanProduct = '8270e8d7-1791-487d-ad76-0da7a0cbed12'
$smokeProduct = '6ebd0ccf-f462-47db-958e-cfd426451c88'
if ($CurrentTlsSpkiPin -ceq $NextTlsSpkiPin) { throw 'Current and next TLS SPKI pins must be distinct.' }
if ($LicenseSigningKeyId -match 'placeholder|changeme|example') { throw 'Installed response-signing key ID is required.' }
if ($LicenseSigningPublicKey -match 'PRIVATE KEY|BEGIN |placeholder|changeme|example') { throw 'Only canonical public SPKI DER base64 is accepted.' }
$ec = $null
try {
    $der = [Convert]::FromBase64String($LicenseSigningPublicKey)
    $ec = [Security.Cryptography.ECDsa]::Create()
    $read = 0; $ec.ImportSubjectPublicKeyInfo($der, [ref]$read)
    if ($read -ne $der.Length -or $ec.KeySize -ne 256 -or [Convert]::ToBase64String($ec.ExportSubjectPublicKeyInfo()) -cne $LicenseSigningPublicKey) { throw 'noncanonical' }
} catch { throw 'LicenseSigningPublicKey must be canonical ECDSA P-256 SPKI DER base64.' } finally { if ($null -ne $ec) { $ec.Dispose() } }

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$helperPackage = [IO.Path]::GetFullPath($ManagedBrowserHelperPackageDirectory)
Assert-LocalDrivePath $helperPackage
Assert-NoReparsePointAncestors $helperPackage
if ($ManagedBrowserHelperPackageDirectory -match '[;,%"'']' -or -not (Test-Path -LiteralPath (Join-Path $helperPackage 'helper-manifest.json') -PathType Leaf) -or
    ((Get-Item -LiteralPath $helperPackage).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Managed-browser helper package path is invalid or injection-prone.' }
$head = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cne $SourceCommit) { throw 'SourceCommit must equal the checked-out HEAD.' }
$dirty = @(& git -C $repo status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'Production client builds require a clean recorded source commit.' }

$thumbprint = $SigningCertificateThumbprint.ToUpperInvariant()
$matches = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -ceq $thumbprint })
if ($matches.Count -ne 1 -or -not $matches[0].HasPrivateKey) { throw 'Exactly one usable certificate-store signing identity is required.' }
$certificate = $matches[0]
$codeSigningOid = '1.3.6.1.5.5.7.3.3'
if (@($certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] -and @($_.EnhancedKeyUsages | Where-Object Value -CEQ $codeSigningOid).Count -eq 1 }).Count -ne 1) { throw 'Signing certificate lacks Code Signing EKU.' }
$timestamp = [Uri]$TimestampUrl
if ($timestamp.Scheme -cne 'https' -or $timestamp.UserInfo -or $timestamp.Query -or $timestamp.Fragment) { throw 'TimestampUrl must be a credential-free HTTPS origin/path.' }

$output = [IO.Path]::GetFullPath($OutputDirectory)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repo '.artifacts'))
Assert-LocalDrivePath $output
Assert-NoReparsePointAncestors $output
if (-not [IO.Path]::GetDirectoryName($output).Equals($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetFileName($output).StartsWith('license-production-clients-', [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory must be a dedicated .artifacts/license-production-clients-* directory, never an OTA stable feed.' }
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory must not already exist.' }
$stage = "$output.partial-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    $buildRepo = Join-Path $stage 'source'
    $archive = Join-Path $stage 'source.tar'
    [IO.Directory]::CreateDirectory($buildRepo) | Out-Null
    & git -C $repo archive --format=tar --output=$archive $SourceCommit
    if ($LASTEXITCODE -ne 0) { throw 'Unable to export the exact source commit.' }
    & tar -xf $archive -C $buildRepo
    if ($LASTEXITCODE -ne 0) { throw 'Unable to extract the exact source commit.' }
    Remove-Item -LiteralPath $archive -Force
    $common = @('-c','Release','-p:RestoreLockedMode=true',"-p:LicenseSigningKeyId=$LicenseSigningKeyId","-p:LicenseSigningPublicKey=$LicenseSigningPublicKey","-p:LicenseCurrentSpkiPin=$CurrentTlsSpkiPin","-p:LicenseNextSpkiPin=$NextTlsSpkiPin")
    $superpan = Join-Path $stage 'superpan'; $smoke = Join-Path $stage 'smoke-nonshipping'; $probe = Join-Path $stage 'boundary-probe-nonshipping'
    $appArgs = @('publish',(Join-Path $buildRepo 'src\BaiduBatchTransfer.App\BaiduBatchTransfer.App.csproj'),'-o',$superpan) + $common + @('-p:LicenseServiceUrl='+$origin,'-p:LicenseProductId='+$superPanProduct,'-p:ManagedBrowserHelperPackageDirectory='+$helperPackage)
    & dotnet @appArgs; if ($LASTEXITCODE -ne 0) { throw 'SuperPan production publish failed.' }
    & dotnet publish (Join-Path $buildRepo 'tools\license_production_smoke\SuperPan.Licensing.ProductionSmoke.csproj') -o $smoke @common; if ($LASTEXITCODE -ne 0) { throw 'Smoke publish failed.' }
    & dotnet publish (Join-Path $buildRepo 'tools\license_production_boundary_probe\SuperPan.Licensing.ProductionBoundaryProbe.csproj') -o $probe @common; if ($LASTEXITCODE -ne 0) { throw 'Boundary probe publish failed.' }
    Remove-Item -LiteralPath $buildRepo -Recurse -Force

    $artifacts = @((Join-Path $superpan 'BaiduBatchTransfer.App.exe'),(Join-Path $smoke 'SuperPan.Licensing.ProductionSmoke.NonShipping.exe'),(Join-Path $probe 'SuperPan.Licensing.BOUNDARY-PROBE-NONSHIPPING.exe'))
    $signtool = (Get-Command signtool.exe -ErrorAction Stop).Source
    $manifest = @()
    foreach ($artifact in $artifacts) {
        & $signtool sign /sha1 $thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $artifact | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed.' }
        $verification = @(& $signtool verify /pa /all /v $artifact 2>&1)
        if ($LASTEXITCODE -ne 0) { throw 'Authenticode SHA256/timestamp policy verification failed.' }
        if (($verification -join "`n") -notmatch '(?i)sha256') { throw 'Authenticode verification did not prove SHA256 policy.' }
        $signature = Get-AuthenticodeSignature -LiteralPath $artifact
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -cne $thumbprint -or $null -eq $signature.TimeStamperCertificate) { throw 'Authenticode signer or timestamp identity mismatch.' }
        $manifest += [ordered]@{ role = Split-Path (Split-Path $artifact -Parent) -Leaf; file = Split-Path $artifact -Leaf; sha256 = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant(); signerThumbprint = $thumbprint }
    }
    $files = foreach ($role in @('superpan','smoke-nonshipping','boundary-probe-nonshipping')) { Get-ChildItem -LiteralPath (Join-Path $stage $role) -File -Recurse | ForEach-Object { [ordered]@{ path=($role + '/' + [IO.Path]::GetRelativePath((Join-Path $stage $role), $_.FullName).Replace('\','/')); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } } }
    $manifestJson = [ordered]@{ sourceCommit=$SourceCommit; origin=$origin; superPanProduct=$superPanProduct; smokeProduct=$smokeProduct; signingKeyId=$LicenseSigningKeyId; signingSpkiDerBase64=$LicenseSigningPublicKey; signingSpkiSha256=([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($der))).ToLowerInvariant(); currentTlsSpki=$CurrentTlsSpkiPin; nextTlsSpki=$NextTlsSpkiPin; artifacts=$manifest; files=@($files) } | ConvertTo-Json -Depth 5
    $manifestPath = Join-Path $stage 'production-license-clients.json'
    [IO.File]::WriteAllText($manifestPath, $manifestJson + "`n", [Text.UTF8Encoding]::new($false))
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes($manifestPath)), $true)
    $cmsSigner = [Security.Cryptography.Pkcs.CmsSigner]::new($certificate); $cmsSigner.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $cms.ComputeSignature($cmsSigner); [IO.File]::WriteAllBytes($manifestPath + '.p7s', $cms.Encode())
    $checkHead = (& git -C $repo rev-parse HEAD).Trim(); $checkDirty = @(& git -C $repo status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $checkHead -cne $SourceCommit -or $checkDirty.Count -ne 0) { throw 'Source changed during the exact snapshot build; publication aborted.' }
    [IO.Directory]::Move($stage, $output)
} catch { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }; throw }
