[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PublisherSubject = 'CN=SuperPan OTA Publisher'
$CodeSigningOid = '1.3.6.1.5.5.7.3.3'
$Sha256WithRsaOid = '1.2.840.113549.1.1.11'
$CertificateLifetimeYears = 15

function Join-DerBytes {
    param([Parameter(Mandatory)][byte[][]]$Arrays)

    $stream = [System.IO.MemoryStream]::new()
    try {
        foreach ($array in $Arrays) {
            $stream.Write($array, 0, $array.Length)
        }

        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function ConvertTo-DerLength {
    param([Parameter(Mandatory)][ValidateRange(0, [int]::MaxValue)][int]$Length)

    if ($Length -lt 128) {
        return ,[byte[]]@([byte]$Length)
    }

    $bytes = [System.Collections.Generic.List[byte]]::new()
    while ($Length -gt 0) {
        $bytes.Insert(0, [byte]($Length -band 0xff))
        $Length = $Length -shr 8
    }

    $result = [System.Collections.Generic.List[byte]]::new()
    $result.Add([byte](0x80 -bor $bytes.Count))
    $result.AddRange($bytes)
    return ,$result.ToArray()
}

function Get-SubjectPublicKeyInfoBytes {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    if ($Certificate.PublicKey.Oid.Value -ne '1.2.840.113549.1.1.1') {
        throw 'The OTA publisher certificate public key is not RSA.'
    }

    # DER rsaEncryption AlgorithmIdentifier: SEQUENCE(OID rsaEncryption, NULL).
    [byte[]]$algorithmIdentifier = @(
        0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86,
        0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00)
    [byte[]]$keyValue = $Certificate.PublicKey.EncodedKeyValue.RawData
    [byte[]]$bitStringPayload = Join-DerBytes -Arrays @([byte[]]@(0x00), $keyValue)
    [byte[]]$bitString = Join-DerBytes -Arrays @(
        [byte[]]@(0x03),
        [byte[]](ConvertTo-DerLength -Length $bitStringPayload.Length),
        $bitStringPayload)
    [byte[]]$contents = Join-DerBytes -Arrays @($algorithmIdentifier, $bitString)
    return ,(Join-DerBytes -Arrays @(
        [byte[]]@(0x30),
        [byte[]](ConvertTo-DerLength -Length $contents.Length),
        $contents))
}

function Get-SubjectPublicKeyInfoSha256 {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    [byte[]]$subjectPublicKeyInfo = Get-SubjectPublicKeyInfoBytes -Certificate $Certificate
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        [byte[]]$digest = $sha256.ComputeHash($subjectPublicKeyInfo)
        return (($digest | ForEach-Object { $_.ToString('x2') }) -join '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Test-PrivateKeyIsNonExportable {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $privateRsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey(
        $Certificate)
    if ($null -eq $privateRsa) {
        return $false
    }

    try {
        if ($privateRsa -is [System.Security.Cryptography.RSACng]) {
            $exportPolicy = $privateRsa.Key.ExportPolicy
            $exportableFlags =
                [System.Security.Cryptography.CngExportPolicies]::AllowExport -bor
                [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
            return (($exportPolicy -band $exportableFlags) -eq 0)
        }

        if ($privateRsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
            return -not $privateRsa.CspKeyContainerInfo.Exportable
        }

        return $false
    }
    finally {
        $privateRsa.Dispose()
    }
}

function Test-PublisherCertificate {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,

        [AllowNull()]
        [string]$ExpectedSpkiSha256,

        [Parameter(Mandatory)]
        [bool]$RequirePrivateKey
    )

    if ($Certificate.Subject -cne $PublisherSubject) {
        return $false
    }
    if ($Certificate.SignatureAlgorithm.Value -ne $Sha256WithRsaOid) {
        return $false
    }
    if ($Certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or
        $Certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
        return $false
    }
    if ($RequirePrivateKey -and -not $Certificate.HasPrivateKey) {
        return $false
    }

    $publicRsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey(
        $Certificate)
    if ($null -eq $publicRsa) {
        return $false
    }
    try {
        if ($publicRsa.KeySize -ne 3072) {
            return $false
        }
    }
    finally {
        $publicRsa.Dispose()
    }

    $keyUsageExtensions = @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.15' })
    if ($keyUsageExtensions.Count -ne 1) {
        return $false
    }
    $keyUsage = [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]$keyUsageExtensions[0]
    if ($keyUsage.KeyUsages -ne [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) {
        return $false
    }

    $enhancedKeyUsageExtensions = @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    if ($enhancedKeyUsageExtensions.Count -ne 1) {
        return $false
    }
    $enhancedKeyUsage =
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$enhancedKeyUsageExtensions[0]
    $enhancedKeyUsageOids = @($enhancedKeyUsage.EnhancedKeyUsages | ForEach-Object { $_.Value })
    if ($enhancedKeyUsageOids.Count -ne 1 -or $enhancedKeyUsageOids[0] -ne $CodeSigningOid) {
        return $false
    }

    $actualSpkiSha256 = Get-SubjectPublicKeyInfoSha256 -Certificate $Certificate
    if ($ExpectedSpkiSha256 -and $actualSpkiSha256 -cne $ExpectedSpkiSha256) {
        return $false
    }

    if ($RequirePrivateKey -and -not (Test-PrivateKeyIsNonExportable -Certificate $Certificate)) {
        return $false
    }

    return $true
}

function Write-BytesAtomically {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][byte[]]$Bytes
    )

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporaryPath = [System.IO.Path]::Combine(
        $directory,
        ([System.IO.Path]::GetFileName($Path) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'))
    $backupPath = [System.IO.Path]::Combine(
        $directory,
        ([System.IO.Path]::GetFileName($Path) + '.' + [Guid]::NewGuid().ToString('N') + '.bak'))
    try {
        [System.IO.File]::WriteAllBytes($temporaryPath, $Bytes)
        if ([System.IO.File]::Exists($Path)) {
            [System.IO.File]::Replace($temporaryPath, $Path, $backupPath, $true)
        }
        else {
            [System.IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if ([System.IO.File]::Exists($temporaryPath)) {
            [System.IO.File]::Delete($temporaryPath)
        }
        if ([System.IO.File]::Exists($backupPath)) {
            [System.IO.File]::Delete($backupPath)
        }
    }
}

function New-PublisherCertificate {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Store]$Store
    )

    $keyName = 'SuperPan.OtaPublisher.' + [Guid]::NewGuid().ToString('N')
    $keyCreationParameters = [System.Security.Cryptography.CngKeyCreationParameters]::new()
    $keyCreationParameters.Provider =
        [System.Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
    $keyCreationParameters.KeyUsage = [System.Security.Cryptography.CngKeyUsages]::Signing
    $keyCreationParameters.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::None
    $keyCreationParameters.KeyCreationOptions = [System.Security.Cryptography.CngKeyCreationOptions]::None
    $keyCreationParameters.Parameters.Add(
        [System.Security.Cryptography.CngProperty]::new(
            'Length',
            [System.BitConverter]::GetBytes(3072),
            [System.Security.Cryptography.CngPropertyOptions]::None))

    $cngKey = $null
    $rsa = $null
    $certificate = $null
    $addedToStore = $false
    $deleteKeyOnFailure = $false
    try {
        $cngKey = [System.Security.Cryptography.CngKey]::Create(
            [System.Security.Cryptography.CngAlgorithm]::Rsa,
            $keyName,
            $keyCreationParameters)
        $rsa = [System.Security.Cryptography.RSACng]::new($cngKey)
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            [System.Security.Cryptography.X509Certificates.X500DistinguishedName]::new($PublisherSubject),
            $rsa,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
                $true))
        $enhancedKeyUsages = [System.Security.Cryptography.OidCollection]::new()
        $enhancedKeyUsages.Add([System.Security.Cryptography.Oid]::new($CodeSigningOid)) | Out-Null
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
                $enhancedKeyUsages,
                $true))

        $certificate = $request.CreateSelfSigned(
            [DateTimeOffset]::Now.AddMinutes(-5),
            [DateTimeOffset]::Now.AddYears($CertificateLifetimeYears))
        $certificate.FriendlyName = 'SuperPan OTA Publisher'
        if (-not (Test-PublisherCertificate `
                -Certificate $certificate `
                -ExpectedSpkiSha256 $null `
                -RequirePrivateKey $true)) {
            throw 'The newly created publisher identity failed pre-enrollment validation.'
        }
        $Store.Add($certificate)
        $addedToStore = $true
        return $certificate
    }
    catch {
        if ($addedToStore -and $certificate) {
            $Store.Remove($certificate)
        }
        if ($certificate) {
            $certificate.Dispose()
        }
        $deleteKeyOnFailure = $true
        throw
    }
    finally {
        if ($rsa) {
            $rsa.Dispose()
        }
        if ($cngKey) {
            if ($deleteKeyOnFailure) {
                $cngKey.Delete()
            }
            $cngKey.Dispose()
        }
    }
}

function Invoke-OtaPublisherCertificateEnrollment {
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $publicCertificatePath = Join-Path $repositoryRoot 'eng\ota\SuperPan.OtaPublisher.cer'
    $thumbprintPath = Join-Path $repositoryRoot '.artifacts\ota\publisher-thumbprint.txt'

    $repositoryCertificate = $null
    $repositorySpkiSha256 = $null
    if ([System.IO.File]::Exists($publicCertificatePath)) {
        $repositoryCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            [System.IO.File]::ReadAllBytes($publicCertificatePath))
        if (-not (Test-PublisherCertificate `
                -Certificate $repositoryCertificate `
                -ExpectedSpkiSha256 $null `
                -RequirePrivateKey $false)) {
            throw "The repository OTA publisher certificate is malformed or violates the fixed publisher policy."
        }
        $repositorySpkiSha256 = Get-SubjectPublicKeyInfoSha256 -Certificate $repositoryCertificate
    }

    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        [System.Security.Cryptography.X509Certificates.StoreName]::My,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $subjectPrivateCertificates = @(
            $store.Certificates |
                Where-Object { $_.Subject -ceq $PublisherSubject -and $_.HasPrivateKey })

        if ($repositoryCertificate) {
            if ($subjectPrivateCertificates.Count -eq 0) {
                throw 'The repository public certificate has no matching private identity in CurrentUser\My.'
            }
            if ($subjectPrivateCertificates.Count -gt 1) {
                throw 'Refusing enrollment because multiple fixed-subject private certificate candidates exist.'
            }

            $certificate = $subjectPrivateCertificates[0]
            if (-not (Test-PublisherCertificate `
                    -Certificate $certificate `
                    -ExpectedSpkiSha256 $repositorySpkiSha256 `
                    -RequirePrivateKey $true)) {
                throw 'The repository public certificate and CurrentUser\My publisher identity disagree.'
            }
            $action = 'reused'
        }
        else {
            if ($subjectPrivateCertificates.Count -gt 1) {
                throw 'Refusing enrollment because multiple fixed-subject private certificate candidates exist.'
            }
            if ($subjectPrivateCertificates.Count -eq 1) {
                $certificate = $subjectPrivateCertificates[0]
                if (-not (Test-PublisherCertificate `
                        -Certificate $certificate `
                        -ExpectedSpkiSha256 $null `
                        -RequirePrivateKey $true)) {
                    throw 'An existing fixed-subject private certificate violates the publisher policy.'
                }
                $action = 'reused'
            }
            else {
                $certificate = New-PublisherCertificate -Store $store
                if (-not (Test-PublisherCertificate `
                        -Certificate $certificate `
                        -ExpectedSpkiSha256 $null `
                        -RequirePrivateKey $true)) {
                    throw 'The newly created publisher identity failed post-creation validation.'
                }
                $action = 'created'
            }
        }

        $spkiSha256 = Get-SubjectPublicKeyInfoSha256 -Certificate $certificate
        if ($repositorySpkiSha256 -and $spkiSha256 -cne $repositorySpkiSha256) {
            throw 'The repository public certificate and CurrentUser\My publisher SPKI disagree.'
        }

        [byte[]]$publicDer = $certificate.Export(
            [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
        Write-BytesAtomically -Path $publicCertificatePath -Bytes $publicDer
        $thumbprintBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
            ($certificate.Thumbprint.ToUpperInvariant() + [Environment]::NewLine))
        Write-BytesAtomically -Path $thumbprintPath -Bytes $thumbprintBytes

        Write-Output "OTA publisher identity $action."
        Write-Output "Thumbprint: $($certificate.Thumbprint.ToUpperInvariant())"
        Write-Output "SPKI SHA-256: $spkiSha256"
        Write-Output "Public certificate: $publicCertificatePath"
        Write-Output "Thumbprint artifact: $thumbprintPath"
        Write-Output "Expires (UTC): $($certificate.NotAfter.ToUniversalTime().ToString('O'))"
    }
    finally {
        $store.Dispose()
        if ($repositoryCertificate) {
            $repositoryCertificate.Dispose()
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        $powerShell7 = Get-Command 'pwsh' -ErrorAction SilentlyContinue
        if (-not $powerShell7) {
            throw 'OTA publisher enrollment requires PowerShell 7 or later (pwsh).'
        }

        & $powerShell7.Source -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
        exit $LASTEXITCODE
    }

    Invoke-OtaPublisherCertificateEnrollment
}
