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

function Test-CngExportPolicyIsStrictlyNonExportable {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.CngExportPolicies]$ExportPolicy
    )

    return $ExportPolicy -eq [System.Security.Cryptography.CngExportPolicies]::None
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
            return Test-CngExportPolicyIsStrictlyNonExportable $privateRsa.Key.ExportPolicy
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

function Test-CertificateIsSelfSigned {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    if ($Certificate.Subject -cne $Certificate.Issuer) {
        return $false
    }

    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode =
            [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $chain.ChainPolicy.VerificationFlags =
            [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        $chain.ChainPolicy.TrustMode =
            [System.Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
        $chain.ChainPolicy.DisableCertificateDownloads = $true
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::Zero
        $chain.ChainPolicy.CustomTrustStore.Clear()
        [void]$chain.ChainPolicy.CustomTrustStore.Add($Certificate)

        if (-not $chain.Build($Certificate) -or $chain.ChainElements.Count -ne 1) {
            return $false
        }

        return [System.Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
            $chain.ChainElements[0].Certificate.RawData,
            $Certificate.RawData)
    }
    catch {
        return $false
    }
    finally {
        $chain.Dispose()
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
    if (-not (Test-CertificateIsSelfSigned -Certificate $Certificate)) {
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

function Resolve-PublisherCertificateSelection {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$SubjectPrivateCertificates,

        [AllowNull()]
        [string]$RepositorySpkiSha256,

        [scriptblock]$CertificateValidator = {
            param($certificate, $expectedSpkiSha256)
            Test-PublisherCertificate `
                -Certificate $certificate `
                -ExpectedSpkiSha256 $expectedSpkiSha256 `
                -RequirePrivateKey $true
        }
    )

    $candidates = @($SubjectPrivateCertificates)
    if ($candidates.Count -gt 1) {
        throw 'Refusing enrollment because multiple fixed-subject private certificate candidates exist.'
    }

    $repositoryIdentityExists = -not [string]::IsNullOrEmpty($RepositorySpkiSha256)
    if ($candidates.Count -eq 0) {
        if ($repositoryIdentityExists) {
            throw 'The repository public certificate has no matching private identity in CurrentUser\My.'
        }

        return [pscustomobject]@{
            Action = 'create'
            Certificate = $null
        }
    }

    $candidate = $candidates[0]
    if (-not (& $CertificateValidator $candidate $RepositorySpkiSha256)) {
        if ($repositoryIdentityExists) {
            throw 'The repository public certificate and CurrentUser\My publisher identity disagree.'
        }

        throw 'An existing fixed-subject private certificate violates the publisher policy.'
    }

    return [pscustomobject]@{
        Action = 'reused'
        Certificate = $candidate
    }
}

function Invoke-PublisherEnrollmentWorkflow {
    param(
        [AllowNull()]
        [object]$RepositoryCertificate,

        [Parameter(Mandatory)]
        [scriptblock]$GetSubjectPrivateCertificates,

        [Parameter(Mandatory)]
        [scriptblock]$CreatePublisherCertificate,

        [scriptblock]$ValidatePublicCertificate = {
            param($certificate)
            Test-PublisherCertificate `
                -Certificate $certificate `
                -ExpectedSpkiSha256 $null `
                -RequirePrivateKey $false
        },

        [scriptblock]$ValidatePrivateCertificate = {
            param($certificate, $expectedSpkiSha256)
            Test-PublisherCertificate `
                -Certificate $certificate `
                -ExpectedSpkiSha256 $expectedSpkiSha256 `
                -RequirePrivateKey $true
        },

        [scriptblock]$GetSpkiSha256 = {
            param($certificate)
            Get-SubjectPublicKeyInfoSha256 -Certificate $certificate
        },

        [scriptblock]$ExportPublicCertificate = {
            param($certificate)
            return ,([byte[]]$certificate.Export(
                [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        },

        [scriptblock]$GetThumbprint = {
            param($certificate)
            return $certificate.Thumbprint.ToUpperInvariant()
        },

        [Parameter(Mandatory)]
        [scriptblock]$WritePublicCertificate,

        [Parameter(Mandatory)]
        [scriptblock]$WriteThumbprint
    )

    $repositorySpkiSha256 = $null
    if ($null -ne $RepositoryCertificate) {
        if (-not (& $ValidatePublicCertificate $RepositoryCertificate)) {
            throw 'The repository OTA publisher certificate is malformed or violates the fixed publisher policy.'
        }
        $repositorySpkiSha256 = [string](& $GetSpkiSha256 $RepositoryCertificate)
    }

    $subjectPrivateCertificates = @(& $GetSubjectPrivateCertificates)
    $selection = Resolve-PublisherCertificateSelection `
        -SubjectPrivateCertificates $subjectPrivateCertificates `
        -RepositorySpkiSha256 $repositorySpkiSha256 `
        -CertificateValidator $ValidatePrivateCertificate
    if ($selection.Action -eq 'create') {
        $certificate = & $CreatePublisherCertificate
        if (-not (& $ValidatePrivateCertificate $certificate $null)) {
            throw 'The newly created publisher identity failed post-creation validation.'
        }
        $action = 'created'
    }
    else {
        $certificate = $selection.Certificate
        $action = $selection.Action
    }

    $spkiSha256 = [string](& $GetSpkiSha256 $certificate)
    if ($repositorySpkiSha256 -and $spkiSha256 -cne $repositorySpkiSha256) {
        throw 'The repository public certificate and CurrentUser\My publisher SPKI disagree.'
    }

    [byte[]]$publicDer = & $ExportPublicCertificate $certificate
    $thumbprint = [string](& $GetThumbprint $certificate)
    $null = & $WritePublicCertificate -Bytes $publicDer
    $null = & $WriteThumbprint -Thumbprint $thumbprint

    return [pscustomobject]@{
        Action = $action
        Certificate = $certificate
        SpkiSha256 = $spkiSha256
        PublicCertificate = $publicDer
        Thumbprint = $thumbprint
    }
}

function Get-PublisherEnrollmentMutexName {
    param(
        [System.Security.Principal.SecurityIdentifier]$CurrentSid =
            [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    )

    if ($null -eq $CurrentSid -or [string]::IsNullOrWhiteSpace($CurrentSid.Value)) {
        throw 'Unable to determine the current Windows user SID for OTA publisher enrollment serialization.'
    }

    return "Global\SuperPan.OtaPublisher.Enrollment.$($CurrentSid.Value)"
}

function Test-PublisherEnrollmentMutexSecurityDescriptor {
    param(
        [Parameter(Mandatory)]
        [System.Security.AccessControl.MutexSecurity]$Security,

        [Parameter(Mandatory)]
        [System.Security.Principal.SecurityIdentifier]$CurrentSid
    )

    if (-not $Security.AreAccessRulesProtected) {
        return $false
    }

    $owner = $Security.GetOwner([System.Security.Principal.SecurityIdentifier])
    if ($null -eq $owner -or $owner.Value -cne $CurrentSid.Value) {
        return $false
    }

    $rules = @($Security.GetAccessRules(
        $true,
        $true,
        [System.Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 1) {
        return $false
    }

    $rule = $rules[0]
    return (
        -not $rule.IsInherited -and
        $rule.IdentityReference.Value -ceq $CurrentSid.Value -and
        $rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and
        $rule.MutexRights -eq [System.Security.AccessControl.MutexRights]::FullControl)
}

function New-PublisherEnrollmentMutexSecurity {
    param(
        [System.Security.Principal.SecurityIdentifier]$CurrentSid =
            [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    )

    try {
        if ($null -eq $CurrentSid) {
            throw 'The current Windows user SID is unavailable.'
        }

        $security = [System.Security.AccessControl.MutexSecurity]::new()
        $security.SetOwner($CurrentSid)
        $security.SetAccessRuleProtection($true, $false)
        $rule = [System.Security.AccessControl.MutexAccessRule]::new(
            $CurrentSid,
            [System.Security.AccessControl.MutexRights]::FullControl,
            [System.Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule) | Out-Null
        if (-not (Test-PublisherEnrollmentMutexSecurityDescriptor `
                -Security $security `
                -CurrentSid $CurrentSid)) {
            throw 'The constructed mutex security descriptor failed validation.'
        }
        return $security
    }
    catch {
        throw [System.InvalidOperationException]::new(
            'Unable to construct the restricted OTA publisher enrollment mutex security descriptor.',
            $_.Exception)
    }
}

function Assert-PublisherEnrollmentMutexSecurity {
    param(
        [Parameter(Mandatory)]
        [string]$MutexName,

        [System.Security.Principal.SecurityIdentifier]$CurrentSid =
            [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    )

    try {
        $sections =
            [System.Security.AccessControl.AccessControlSections]::Access -bor
            [System.Security.AccessControl.AccessControlSections]::Owner
        $security = [System.Security.AccessControl.MutexSecurity]::new($MutexName, $sections)
        if (-not (Test-PublisherEnrollmentMutexSecurityDescriptor `
                -Security $security `
                -CurrentSid $CurrentSid)) {
            throw 'The mutex security descriptor grants unexpected access or ownership.'
        }
    }
    catch {
        throw [System.InvalidOperationException]::new(
            "Unable to validate restricted access for OTA publisher enrollment mutex '$MutexName'.",
            $_.Exception)
    }
}

function New-PublisherEnrollmentMutex {
    param(
        [Parameter(Mandatory)]
        [string]$MutexName,

        [System.Security.Principal.SecurityIdentifier]$CurrentSid =
            [System.Security.Principal.WindowsIdentity]::GetCurrent().User,

        [scriptblock]$SecurityFactory = {
            param($currentSid)
            return New-PublisherEnrollmentMutexSecurity -CurrentSid $currentSid
        },

        [scriptblock]$MutexFactory = {
            param($name, $security)
            $createdNew = $false
            return [System.Threading.MutexAcl]::Create(
                $false,
                $name,
                [ref]$createdNew,
                $security)
        },

        [scriptblock]$SecurityValidator = {
            param($name, $currentSid)
            Assert-PublisherEnrollmentMutexSecurity -MutexName $name -CurrentSid $currentSid
        }
    )

    $mutex = $null
    try {
        $security = & $SecurityFactory $CurrentSid
        if ($null -eq $security) {
            throw 'The mutex security factory returned no security descriptor.'
        }
        $mutex = & $MutexFactory $MutexName $security
        if ($null -eq $mutex) {
            throw 'The restricted mutex factory returned no mutex.'
        }
        $null = & $SecurityValidator $MutexName $CurrentSid
        return $mutex
    }
    catch {
        if ($null -ne $mutex) {
            $mutex.Dispose()
        }
        throw [System.InvalidOperationException]::new(
            "Unable to create or open the restricted OTA publisher enrollment mutex '$MutexName'.",
            $_.Exception)
    }
}

function Invoke-WithPublisherEnrollmentMutex {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [string]$MutexName = (Get-PublisherEnrollmentMutexName),

        [TimeSpan]$Timeout = [TimeSpan]::FromSeconds(30),

        [scriptblock]$MutexFactory = {
            param($name)
            return New-PublisherEnrollmentMutex -MutexName $name
        }
    )

    $mutex = $null
    $acquired = $false
    try {
        $mutex = & $MutexFactory $MutexName
        if ($null -eq $mutex) {
            throw 'The OTA publisher enrollment mutex factory returned no mutex.'
        }

        try {
            $acquired = $mutex.WaitOne($Timeout)
        }
        catch [System.Threading.AbandonedMutexException] {
            # .NET grants ownership when WaitOne reports abandonment. Release it,
            # but fail closed so the next run revalidates every persisted artifact.
            $acquired = $true
            throw [System.InvalidOperationException]::new(
                'The OTA publisher enrollment mutex was abandoned; enrollment was stopped before state inspection.',
                $_.Exception)
        }
        catch {
            # Injectable mutex doubles surface method exceptions through a wrapper.
            # Preserve the same abandoned-mutex ownership semantics in that case.
            $cause = $_.Exception
            while ($cause -and $cause -isnot [System.Threading.AbandonedMutexException]) {
                $cause = $cause.InnerException
            }
            if ($cause -is [System.Threading.AbandonedMutexException]) {
                $acquired = $true
                throw [System.InvalidOperationException]::new(
                    'The OTA publisher enrollment mutex was abandoned; enrollment was stopped before state inspection.',
                    $_.Exception)
            }
            throw
        }

        if (-not $acquired) {
            throw [System.TimeoutException]::new(
                "Timed out waiting for the OTA publisher enrollment mutex '$MutexName'.")
        }

        return & $Action
    }
    finally {
        if ($null -ne $mutex) {
            try {
                if ($acquired) {
                    $mutex.ReleaseMutex()
                }
            }
            finally {
                $mutex.Dispose()
            }
        }
    }
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

function Invoke-OtaPublisherCertificateEnrollmentCore {
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $publicCertificatePath = Join-Path $repositoryRoot 'eng\ota\SuperPan.OtaPublisher.cer'
    $thumbprintPath = Join-Path $repositoryRoot '.artifacts\ota\publisher-thumbprint.txt'

    $repositoryCertificate = $null
    if ([System.IO.File]::Exists($publicCertificatePath)) {
        $repositoryCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            [System.IO.File]::ReadAllBytes($publicCertificatePath))
    }

    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        [System.Security.Cryptography.X509Certificates.StoreName]::My,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $result = Invoke-PublisherEnrollmentWorkflow `
            -RepositoryCertificate $repositoryCertificate `
            -GetSubjectPrivateCertificates {
                @($store.Certificates | Where-Object {
                    $_.Subject -ceq $PublisherSubject -and $_.HasPrivateKey
                })
            } `
            -CreatePublisherCertificate { New-PublisherCertificate -Store $store } `
            -WritePublicCertificate {
                param([byte[]]$Bytes)
                Write-BytesAtomically -Path $publicCertificatePath -Bytes $Bytes
            } `
            -WriteThumbprint {
                param([string]$Thumbprint)
                $thumbprintBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
                    ($Thumbprint + [Environment]::NewLine))
                Write-BytesAtomically -Path $thumbprintPath -Bytes $thumbprintBytes
            }

        Write-Output "OTA publisher identity $($result.Action)."
        Write-Output "Thumbprint: $($result.Thumbprint)"
        Write-Output "SPKI SHA-256: $($result.SpkiSha256)"
        Write-Output "Public certificate: $publicCertificatePath"
        Write-Output "Thumbprint artifact: $thumbprintPath"
        Write-Output "Expires (UTC): $($result.Certificate.NotAfter.ToUniversalTime().ToString('O'))"
    }
    finally {
        $store.Dispose()
        if ($repositoryCertificate) {
            $repositoryCertificate.Dispose()
        }
    }
}

function Invoke-OtaPublisherCertificateEnrollment {
    Invoke-WithPublisherEnrollmentMutex -Action {
        Invoke-OtaPublisherCertificateEnrollmentCore
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
