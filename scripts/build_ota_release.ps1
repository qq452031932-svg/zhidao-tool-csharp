[CmdletBinding(DefaultParameterSetName = 'Acceptance')]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true, ParameterSetName = 'Acceptance')][string]$AcceptanceFeedPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$PreviousReleaseDirectory,
    [string]$ReleaseNotesPath,
    [Parameter(ParameterSetName = 'Acceptance')][switch]$OtaAcceptanceBuild,
    [Parameter(ParameterSetName = 'Acceptance')][switch]$AllowUnsignedLocal,
    [Parameter(Mandatory = $true, ParameterSetName = 'Production')][switch]$Production,
    [Parameter(Mandatory = $true, ParameterSetName = 'Production')][string]$ProductionFeedUrl,
    [Parameter(Mandatory = $true, ParameterSetName = 'Production')][string]$SigningCertificateThumbprint,
    [Parameter(Mandatory = $true, ParameterSetName = 'Production')][string]$LicenseSigningKeyId,
    [Parameter(Mandatory = $true, ParameterSetName = 'Production')][string]$LicenseSigningPublicKey,
    [Parameter(Mandatory = $true, ParameterSetName = 'Production')][string]$LicenseCurrentSpkiPin,
    [Parameter(Mandatory = $true, ParameterSetName = 'Production')][string]$LicenseNextSpkiPin,
    [Parameter(Mandatory = $true, ParameterSetName = 'Production')][string]$LicenseServiceUrl,
    [Parameter(ParameterSetName = 'Production')][string]$TimestampUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$acceptancePackageId = 'SuperPan.BaiduBatchTransfer.OtaAcceptance'
$acceptanceChannel = 'win-x64-ota-acceptance'
$productionPackageId = 'SuperPan.BaiduBatchTransfer'
$productionChannel = 'win-x64-stable'
$requiredVpkVersion = '1.2.0'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.artifacts'))
$productAppRelativeDirectory = 'src\BaiduBatchTransfer.App'

function Initialize-ProductIdentity(
    [string]$IdentityScriptPath,
    [string]$IdentityPropsPath,
    [string]$IdentityRoot) {
    . $IdentityScriptPath
    $script:productIdentity = Get-ProductIdentity -Path $IdentityPropsPath
    $script:productMainExecutableName = [string]$productIdentity.ProductMainExecutableName
    $script:productMainDllName = [string]$productIdentity.ProductMainDllName
    $script:productDisplayName = [string]$productIdentity.ProductDisplayName
    $script:legacyLauncherExecutableName = [string]$productIdentity.ProductLegacyExecutableName
    $script:productIconRelativePath = Join-Path $productAppRelativeDirectory (
        'Assets\Branding\' + [IO.Path]::GetFileNameWithoutExtension($productMainExecutableName) + '.ico')
    $script:productIconPath = [IO.Path]::GetFullPath((Join-Path $IdentityRoot $productIconRelativePath))
}

# Acceptance remains an explicitly unsigned, local-only gate. Production is a separate
# parameter set and has no unsigned bypass.
if ($PSCmdlet.ParameterSetName -eq 'Acceptance' -and
    (-not $OtaAcceptanceBuild -or -not $AllowUnsignedLocal)) {
    throw 'Both -OtaAcceptanceBuild and -AllowUnsignedLocal are required for the unsigned local OTA acceptance pipeline.'
}
$inheritedVpkVariables = @(Get-ChildItem Env: | Where-Object {
        $_.Name.StartsWith('VPK_', [StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::IsNullOrWhiteSpace([string]$_.Value)
    })
if ($inheritedVpkVariables.Count -ne 0) {
    $unsafeNames = @($inheritedVpkVariables | ForEach-Object { $_.Name } | Sort-Object) -join ', '
    throw "Inherited VPK_* environment variables are not allowed: $unsafeNames"
}
if ($acceptancePackageId -ceq $productionPackageId -or
    $acceptanceChannel -ceq $productionChannel) {
    throw 'The OTA acceptance package ID and channel must remain distinct from production.'
}

$rawPathInputs = [ordered]@{
    AcceptanceFeedPath = $AcceptanceFeedPath
    OutputDirectory = $OutputDirectory
    PreviousReleaseDirectory = $PreviousReleaseDirectory
    ReleaseNotesPath = $ReleaseNotesPath
}
foreach ($entry in $rawPathInputs.GetEnumerator()) {
    if (-not [string]::IsNullOrWhiteSpace([string]$entry.Value) -and
        [string]$entry.Value -match '^\\\\') {
        throw "$($entry.Key) must be an absolute local filesystem path, not a UNC path."
    }
}

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version -cnotmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') {
    throw 'Version must be a canonical major.minor.patch value.'
}
$versionParts = @($Version.Split('.') | ForEach-Object { [int]::Parse($_) })
if (@($versionParts | Where-Object { $_ -gt 65534 }).Count -ne 0) {
    throw 'Version parts must be between 0 and 65534.'
}

function Resolve-AbsolutePath([string]$Value, [string]$ParameterName) {
    $isDriveAbsolute = $Value -match '^[A-Za-z]:[\\/]'
    $isUncAbsolute = $Value -match '^\\\\[^\\/]+[\\/][^\\/]+'
    if ([string]::IsNullOrWhiteSpace($Value) -or
        (-not $isDriveAbsolute -and -not $isUncAbsolute)) {
        throw "$ParameterName must be an absolute local path."
    }
    try {
        return [IO.Path]::GetFullPath($Value)
    }
    catch {
        throw "$ParameterName is not a valid local path."
    }
}

function Test-PathWithin([string]$Candidate, [string]$Parent) {
    $parentPrefix = $Parent.TrimEnd([IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $Candidate.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-ExistingPathIsNotReparsePoint([string]$Path, [string]$Description) {
    if (Test-Path -LiteralPath $Path) {
        $attributes = [IO.File]::GetAttributes($Path)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description must not be a reparse point."
        }
    }
}

function Assert-LocalDrivePath([string]$Path, [string]$Description) {
    $root = [IO.Path]::GetPathRoot($Path)
    if ([string]::IsNullOrWhiteSpace($root) -or $root -match '^\\\\') {
        throw "$Description must be on a local filesystem drive."
    }
    try { $drive = [IO.DriveInfo]::new($root) }
    catch { throw "$Description must be on an available local filesystem drive." }
    if ($drive.DriveType -eq [IO.DriveType]::Network) {
        throw "$Description must not use a mapped network drive."
    }
}

function Assert-NoReparsePointAncestors([string]$Path, [string]$Description) {
    $root = [IO.Path]::GetPathRoot($Path).TrimEnd('\', '/')
    $current = $Path.TrimEnd('\', '/')
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description has a reparse-point ancestor: $current"
            }
        }
        if ($current.Equals($root, [StringComparison]::OrdinalIgnoreCase)) { return }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrWhiteSpace($parent) -or
            $parent.Equals($current, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Description did not reach its local drive root."
        }
        $current = $parent.TrimEnd('\', '/')
    }
    throw "$Description did not reach its local drive root."
}

function Get-LowerSha256([string]$Path) {
    $stream = [IO.File]::Open(
        $Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($stream) }
    finally { $algorithm.Dispose(); $stream.Dispose() }
    return ([BitConverter]::ToString($hash).Replace('-', '')).ToLowerInvariant()
}

function Get-LowerSha1([string]$Path) {
    $stream = [IO.File]::Open(
        $Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $algorithm = [Security.Cryptography.SHA1]::Create()
    try { $hash = $algorithm.ComputeHash($stream) }
    finally { $algorithm.Dispose(); $stream.Dispose() }
    return ([BitConverter]::ToString($hash).Replace('-', '')).ToLowerInvariant()
}

function Assert-PublishedApplicationLayout([string]$Directory, [string]$Description) {
    $mainStem = [IO.Path]::GetFileNameWithoutExtension($productMainExecutableName)
    foreach ($name in @(
            $productMainExecutableName,
            $productMainDllName,
            "$mainStem.deps.json",
            "$mainStem.runtimeconfig.json")) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $name) -PathType Leaf)) {
            throw "$Description is missing required application file: $name"
        }
    }
}

function Install-VerifiedLegacyLauncher(
    [string]$LauncherPublishDirectory,
    [string]$MainPublishDirectory,
    [string]$ProbeParent) {
    $launcherSource = Join-Path $LauncherPublishDirectory $legacyLauncherExecutableName
    if (-not (Test-Path -LiteralPath $launcherSource -PathType Leaf)) {
        throw 'Published legacy launcher executable is missing.'
    }

    $legacyLauncherProbeDirectory = Join-Path $ProbeParent (
        '.legacy-launcher-probe-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($legacyLauncherProbeDirectory) | Out-Null
    $legacyLauncherProbeCanBeRemoved = $true
    $legacyLauncherFailure = $null
    $legacyLauncherPrimaryError = $null
    $legacyLauncherCleanupError = $null
    try {
        $probeLauncher = Join-Path $legacyLauncherProbeDirectory $legacyLauncherExecutableName
        [IO.File]::Copy($launcherSource, $probeLauncher, $false)
        if (Test-Path -LiteralPath (Join-Path $legacyLauncherProbeDirectory $productMainExecutableName)) {
            throw 'Legacy launcher probe directory unexpectedly contains the main application.'
        }

        $startInfo = New-Object Diagnostics.ProcessStartInfo
        $startInfo.FileName = $probeLauncher
        $startInfo.WorkingDirectory = $legacyLauncherProbeDirectory
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.EnvironmentVariables['BAIDU_BATCH_TRANSFER_LEGACY_LAUNCHER_SUPPRESS_DIALOG'] = '1'
        $process = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) {
            throw 'Legacy launcher single-file probe could not be started.'
        }
        $legacyLauncherProbeCanBeRemoved = $false
        try {
            if (-not $process.WaitForExit(15000)) {
                $legacyLauncherFailure = 'Legacy launcher single-file probe timed out.'
                try {
                    $process.Kill()
                }
                catch {
                    $legacyLauncherFailure += " Termination request failed: $($_.Exception.Message)"
                }

                $legacyLauncherExitConfirmed = $false
                try {
                    $legacyLauncherExitConfirmed = $process.WaitForExit(5000)
                }
                catch {
                    $legacyLauncherFailure += " Exit confirmation failed: $($_.Exception.Message)"
                }
                if ($legacyLauncherExitConfirmed) {
                    $legacyLauncherProbeCanBeRemoved = $true
                    $legacyLauncherFailure += ' Process termination was confirmed.'
                }
                else {
                    $legacyLauncherFailure += " Process exit could not be confirmed within 5000 ms; probe directory retained: $legacyLauncherProbeDirectory"
                }
            }
            else {
                $legacyLauncherProbeCanBeRemoved = $true
                $legacyLauncherExitCode = $process.ExitCode
            }
        }
        finally { $process.Dispose() }

        if ($null -ne $legacyLauncherFailure) { throw $legacyLauncherFailure }
        if ($legacyLauncherExitCode -ne 2) {
            throw "Legacy launcher single-file probe returned unexpected exit code $legacyLauncherExitCode."
        }

        $probeEntries = @([IO.Directory]::EnumerateFileSystemEntries($legacyLauncherProbeDirectory))
        if ($probeEntries.Count -ne 1 -or
            [IO.Path]::GetFileName($probeEntries[0]) -cne $legacyLauncherExecutableName) {
            throw 'Legacy launcher single-file probe created an unexpected dependency or side-effect file.'
        }
        [IO.File]::Copy(
            $probeLauncher,
            (Join-Path $MainPublishDirectory $legacyLauncherExecutableName),
            $false)
    }
    catch {
        $legacyLauncherPrimaryError = $_
    }
    finally {
        if ($legacyLauncherProbeCanBeRemoved -and
            (Test-Path -LiteralPath $legacyLauncherProbeDirectory)) {
            try {
                Remove-Item -LiteralPath $legacyLauncherProbeDirectory -Recurse -Force
            }
            catch {
                $legacyLauncherCleanupError = $_
            }
        }
    }

    if ($null -ne $legacyLauncherPrimaryError) {
        $combinedMessage = [string]$legacyLauncherPrimaryError.Exception.Message
        if ($null -ne $legacyLauncherCleanupError) {
            $combinedMessage += " Probe cleanup also failed: $($legacyLauncherCleanupError.Exception.Message) Probe directory retained: $legacyLauncherProbeDirectory"
        }
        elseif (-not $legacyLauncherProbeCanBeRemoved) {
            $combinedMessage += " Probe directory retained because process exit was not confirmed: $legacyLauncherProbeDirectory"
        }
        if ($combinedMessage -cne [string]$legacyLauncherPrimaryError.Exception.Message) {
            throw [InvalidOperationException]::new(
                $combinedMessage,
                $legacyLauncherPrimaryError.Exception)
        }
        throw $legacyLauncherPrimaryError
    }
    if ($null -ne $legacyLauncherCleanupError) {
        throw [InvalidOperationException]::new(
            "Legacy launcher probe cleanup failed: $($legacyLauncherCleanupError.Exception.Message) Probe directory retained: $legacyLauncherProbeDirectory",
            $legacyLauncherCleanupError.Exception)
    }
}

function Test-SafeNupkgFileName([string]$FileName) {
    if ([string]::IsNullOrWhiteSpace($FileName) -or $FileName.Length -gt 255 -or
        $FileName -cne [IO.Path]::GetFileName($FileName) -or
        [IO.Path]::GetExtension($FileName) -cne '.nupkg' -or
        $FileName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $FileName.EndsWith(' ') -or $FileName.EndsWith('.')) {
        return $false
    }
    $stem = $FileName.Split('.')[0].ToUpperInvariant()
    return $stem -notin @(
        'CON', 'PRN', 'AUX', 'NUL',
        'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9',
        'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9')
}

function Assert-NonReparseExistingAncestors([string]$Path, [string]$Boundary) {
    if (-not $Path.Equals($Boundary, [StringComparison]::OrdinalIgnoreCase) -and
        -not (Test-PathWithin $Path $Boundary)) {
        throw 'OutputDirectory is outside its verified artifact boundary.'
    }
    $current = $Path
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "OutputDirectory has a reparse-point ancestor: $current"
            }
        }
        if ($current.Equals($Boundary, [StringComparison]::OrdinalIgnoreCase)) { return }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($parent)) {
            throw 'OutputDirectory did not reach its verified artifact boundary.'
        }
        $current = $parent
    }
}

function Invoke-ProductionRelease {
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        throw 'Production OTA builds require PowerShell 7 (pwsh) for pinned-key cryptography.'
    }
    if (-not $Production) { throw '-Production is required for the production parameter set.' }
    function Assert-SafeProductionUriText([string]$Value, [string]$Name) {
        if ([string]::IsNullOrWhiteSpace($Value) -or $Value -match '[^\x21-\x7e]' -or $Value.IndexOfAny([char[]]'%,;$@''"') -ge 0) {
            throw "$Name contains whitespace, escaping, or command/property metacharacters."
        }
        $decoded = [Uri]::UnescapeDataString($Value)
        if ($decoded -cne $Value -or $decoded.IndexOfAny([char[]]'%,;$@''"') -ge 0) {
            throw "$Name contains encoded command/property metacharacters."
        }
    }
    Assert-SafeProductionUriText $ProductionFeedUrl 'ProductionFeedUrl'
    $parsedFeedUri = $null
    if (-not [Uri]::TryCreate($ProductionFeedUrl, [UriKind]::Absolute, [ref]$parsedFeedUri)) {
        throw 'ProductionFeedUrl must be an absolute HTTPS URL.'
    }
    $feedUri = $parsedFeedUri
    if ($feedUri.Scheme -cne 'https' -or -not [string]::IsNullOrEmpty($feedUri.UserInfo) -or
        -not [string]::IsNullOrEmpty($feedUri.Query) -or -not [string]::IsNullOrEmpty($feedUri.Fragment)) {
        throw 'ProductionFeedUrl must be an absolute HTTPS URL without credentials, query, or fragment.'
    }
    $normalizedFeedUrl = $feedUri.AbsoluteUri.TrimEnd('/') + '/'
    if ($SigningCertificateThumbprint -cnotmatch '^[0-9A-Fa-f]{40}$') {
        throw 'SigningCertificateThumbprint must be exactly 40 hexadecimal characters.'
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        Assert-SafeProductionUriText $TimestampUrl 'TimestampUrl'
        $timestampUri = $null
        if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
            $timestampUri.AbsoluteUri -cne 'http://timestamp.digicert.com/' -or
            -not [string]::IsNullOrEmpty($timestampUri.UserInfo) -or
            -not [string]::IsNullOrEmpty($timestampUri.Query) -or -not [string]::IsNullOrEmpty($timestampUri.Fragment)) {
            throw 'TimestampUrl must be the approved DigiCert RFC 3161 endpoint.'
        }
    }

    $productionOutput = Resolve-AbsolutePath $OutputDirectory 'OutputDirectory'
    Assert-LocalDrivePath $productionOutput 'OutputDirectory'
    Assert-NoReparsePointAncestors $productionOutput 'OutputDirectory'
    if (-not (Test-PathWithin $productionOutput $artifactRoot)) {
        throw 'Production OutputDirectory must be inside the repository .artifacts directory.'
    }
    Assert-NonReparseExistingAncestors $productionOutput $artifactRoot
    $relativeProductionOutput = $productionOutput.Substring($artifactRoot.TrimEnd('\','/').Length + 1).Replace('/', '\')
    if (-not $relativeProductionOutput.Split('\')[0].StartsWith('ota-production-', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Production OutputDirectory must use a dedicated .artifacts/ota-production-* directory.'
    }
    if (Test-Path -LiteralPath $productionOutput) {
        if (-not (Test-Path -LiteralPath $productionOutput -PathType Container) -or
            @([IO.Directory]::EnumerateFileSystemEntries($productionOutput)).Count -ne 0) {
            throw 'Production OutputDirectory must be an empty directory.'
        }
    }

    $productionPrevious = $null
    if (-not [string]::IsNullOrWhiteSpace($PreviousReleaseDirectory)) {
        $productionPrevious = Resolve-AbsolutePath $PreviousReleaseDirectory 'PreviousReleaseDirectory'
        Assert-LocalDrivePath $productionPrevious 'PreviousReleaseDirectory'
        Assert-NoReparsePointAncestors $productionPrevious 'PreviousReleaseDirectory'
        if (-not (Test-Path -LiteralPath $productionPrevious -PathType Container) -and
            (-not (Test-Path -LiteralPath $productionPrevious -PathType Leaf) -or
             [IO.Path]::GetExtension($productionPrevious) -cne '.zip')) {
            throw 'PreviousReleaseDirectory must be an existing signed production generation directory or upload ZIP.'
        }
    }
    $productionNotes = $null
    if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
        $productionNotes = Resolve-AbsolutePath $ReleaseNotesPath 'ReleaseNotesPath'
        Assert-LocalDrivePath $productionNotes 'ReleaseNotesPath'
        Assert-NoReparsePointAncestors $productionNotes 'ReleaseNotesPath'
        if (-not (Test-Path -LiteralPath $productionNotes -PathType Leaf)) { throw 'ReleaseNotesPath must name an existing file.' }
    }

    $signingCertificate = $null
    $signingRsa = $null
    $productionStage = $null
    $logDirectory = $null
    try {
    $expectedSpki = 'f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff'
    $thumbprint = $SigningCertificateThumbprint.ToUpperInvariant()
    $certificates = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -ceq $thumbprint })
    if ($certificates.Count -ne 1) { throw 'Exactly one enrolled signing certificate must match the requested thumbprint.' }
    $signingCertificate = $certificates[0]
    if (-not $signingCertificate.HasPrivateKey) { throw 'The enrolled signing certificate has no private key.' }
    if ([DateTime]::UtcNow -lt $signingCertificate.NotBefore.ToUniversalTime() -or
        [DateTime]::UtcNow -gt $signingCertificate.NotAfter.ToUniversalTime()) {
        throw 'The enrolled signing certificate is not currently valid.'
    }
    $signingRsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($signingCertificate)
    if ($null -eq $signingRsa -or $signingRsa.KeySize -ne 3072) { throw 'The enrolled signing certificate must have an RSA-3072 private key.' }
    $publicRsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($signingCertificate)
    try {
        $actualSpki = ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publicRsa.ExportSubjectPublicKeyInfo()))).ToLowerInvariant()
    }
    finally { $publicRsa.Dispose() }
    if ($actualSpki -cne $expectedSpki) { throw 'The signing certificate SPKI does not match the enrolled OTA publisher.' }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $hasCodeSigning = @($signingCertificate.Extensions | Where-Object {
            $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] -and
            @($_.EnhancedKeyUsages | Where-Object { $_.Value -ceq $codeSigningOid }).Count -eq 1
        }).Count -eq 1
    if (-not $hasCodeSigning) { throw 'The signing certificate is not authorized for code signing.' }

    if (Test-Path -LiteralPath $productionOutput) { Remove-Item -LiteralPath $productionOutput -Force }
    $productionParent = [IO.Path]::GetDirectoryName($productionOutput)
    $productionLeaf = [IO.Path]::GetFileName($productionOutput)
    $productionStage = Join-Path $productionParent (".$productionLeaf.partial-$([Guid]::NewGuid().ToString('N'))")
    [IO.Directory]::CreateDirectory($productionStage) | Out-Null
    $buildRoot = Join-Path $productionStage (".work-$([Guid]::NewGuid().ToString('N'))")
    $publishDirectory = Join-Path $buildRoot 'publish'
    $assetDirectory = Join-Path $buildRoot 'assets'
    $logDirectory = Join-Path $buildRoot 'logs'
    foreach ($directory in @($publishDirectory,$assetDirectory,$logDirectory)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    $commandLogPath = Join-Path $logDirectory 'commands.jsonl'
    $utf8NoBomProduction = [Text.UTF8Encoding]::new($false)

    function Invoke-ProductionCommand([string]$Name, [string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory) {
        $safe = $Name -replace '[^A-Za-z0-9_.-]','_'
        $stdoutPath = Join-Path $logDirectory "$safe.stdout.log"
        $stderrPath = Join-Path $logDirectory "$safe.stderr.log"
        $started = [DateTime]::UtcNow
        $old = Get-Location
        try {
            Set-Location -LiteralPath $WorkingDirectory
            & $FilePath @Arguments 1> $stdoutPath 2> $stderrPath
            $code = if ($null -eq $LASTEXITCODE) { if ($?) { 0 } else { 1 } } else { $LASTEXITCODE }
        }
        catch { $code = 1; [IO.File]::AppendAllText($stderrPath, [string]$_) }
        finally { Set-Location -LiteralPath $old.Path }
        $record = [ordered]@{ name=$Name; executable=$FilePath; arguments=@($Arguments); startedUtc=$started.ToString('O'); exitCode=$code }
        [IO.File]::AppendAllText($commandLogPath, (($record | ConvertTo-Json -Compress -Depth 4) + "`n"), $utf8NoBomProduction)
        if ($code -ne 0) { throw "Production release command '$Name' failed with exit code $code. See $commandLogPath" }
    }

    function Export-ReviewedGitBlob([string]$Source, [string]$Commit, [string]$RelativePath, [string]$DestinationPath) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($DestinationPath)) | Out-Null
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = 'git'
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.ArgumentList.Add('-C'); $startInfo.ArgumentList.Add($Source)
        $startInfo.ArgumentList.Add('show'); $startInfo.ArgumentList.Add("$($Commit):$RelativePath")
        $process = [Diagnostics.Process]::Start($startInfo)
        $output = [IO.File]::Create($DestinationPath)
        try {
            $process.StandardOutput.BaseStream.CopyTo($output)
            $process.WaitForExit()
            $exitCode = $process.ExitCode
            $standardError = $process.StandardError.ReadToEnd()
        }
        finally { $output.Dispose(); $process.Dispose() }
        if ($exitCode -ne 0) {
            if (Test-Path -LiteralPath $DestinationPath) { Remove-Item -LiteralPath $DestinationPath -Force }
            throw "Unable to export reviewed production source blob $($Commit):$RelativePath ($standardError)"
        }
    }

    function Get-TrackedGitPaths([string]$Source, [string]$Commit, [string[]]$Pathspecs) {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = 'git'
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in @('-C',$Source,'ls-tree','-r')) {
            $startInfo.ArgumentList.Add($argument)
        }
        $startInfo.ArgumentList.Add('-z')
        foreach ($argument in @('--name-only',$Commit,'--')) { $startInfo.ArgumentList.Add($argument) }
        foreach ($pathspec in $Pathspecs) { $startInfo.ArgumentList.Add($pathspec) }

        $process = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) { throw 'Unable to start git while enumerating production source.' }
        $output = [IO.MemoryStream]::new()
        try {
            $errorTask = $process.StandardError.ReadToEndAsync()
            $copyTask = $process.StandardOutput.BaseStream.CopyToAsync($output)
            $copyTask.GetAwaiter().GetResult()
            $standardError = $errorTask.GetAwaiter().GetResult()
            $process.WaitForExit()
            if ($process.ExitCode -ne 0) {
                throw "Unable to enumerate the commit-pinned production source allowlist ($standardError)"
            }
            try { $text = [Text.UTF8Encoding]::new($false, $true).GetString($output.ToArray()) }
            catch { throw 'Git returned a production source path list that is not strict UTF-8.' }
        }
        finally {
            $output.Dispose()
            $process.Dispose()
        }
        if ([string]::IsNullOrEmpty($text) -or $text[$text.Length - 1] -ne [char]0) {
            throw 'Git returned a non-canonical production source path list.'
        }
        $payload = $text.Substring(0, $text.Length - 1)
        if ($payload.Length -eq 0) { return @() }
        return @($payload.Split([char]0, [StringSplitOptions]::None))
    }

    function Copy-ProductionSourceSnapshot([string]$Source, [string]$Destination, [string]$Commit) {
        $sourcePathspecs = @(
            'src',
            'Directory.Build.props',
            'Directory.Build.targets',
            'Directory.Packages.props',
            'global.json',
            'NuGet.Config',
            '.config/dotnet-tools.json',
            'eng/ProductIdentity.props',
            'eng/license/ProductionLicenseTrust.targets',
            'eng/ota/SuperPan.OtaPublisher.cer',
            'eng/ota/production-source-overrides.sha256',
            'scripts/ProductIdentity.ps1',
            'scripts/Test-OtaUploadBundle.ps1',
            'scripts/verify_managed_browser_helper_package.ps1',
            'tools/managed_browser_helper')
        $tracked = @(Get-TrackedGitPaths $Source $Commit $sourcePathspecs)
        if ($tracked.Count -eq 0) { throw 'Unable to enumerate the commit-pinned production source allowlist.' }
        foreach ($relativePath in $tracked) {
            if ([string]::IsNullOrWhiteSpace($relativePath) -or $relativePath.Contains('..') -or [IO.Path]::IsPathRooted($relativePath)) {
                throw 'Git returned an unsafe commit-pinned production path.'
            }
            $extension = [IO.Path]::GetExtension($relativePath)
            if ($extension -in @('.pfx','.p12','.key','.pem')) { throw "Private-key material is forbidden from the production source snapshot: $relativePath" }
            Export-ReviewedGitBlob $Source $Commit $relativePath (Join-Path $Destination $relativePath)
        }

        $overrideManifestPath = Join-Path $Destination 'eng\ota\production-source-overrides.sha256'
        if (-not (Test-Path -LiteralPath $overrideManifestPath -PathType Leaf)) { throw 'The reviewed production source override manifest is missing from the source commit.' }
        $overrideText = [IO.File]::ReadAllText($overrideManifestPath, [Text.UTF8Encoding]::new($false, $true))
        if ($overrideText.Contains("`r") -or -not $overrideText.EndsWith("`n")) { throw 'The reviewed production source override manifest is not canonical LF UTF-8.' }
        $overridePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $allowedHelperOverrides = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($name in @(
            'helper-manifest.json','main.py','protocol.py','identity_query.py','transfer_source.py',
            'transfer_source_listing.py','transfer_directory_listing.py','transfer_directory_mutation.py',
            'transfer_picker.py','transfer_target_baseline.py','transfer_submission.py','share_submission.py',
            'session_operations.py')) {
            [void]$allowedHelperOverrides.Add("tools/managed_browser_helper/$name")
        }
        foreach ($line in @($overrideText.TrimEnd("`n").Split("`n"))) {
            if ($line -cnotmatch '^(?<hash>[0-9a-f]{64})  (?<path>[!-~]+)$') { throw 'The reviewed production source override manifest contains a non-canonical line.' }
            $relativePath = [string]$Matches.path
            $extension = [IO.Path]::GetExtension($relativePath)
            $allowedApplicationOverride = $relativePath.StartsWith('src/', [StringComparison]::Ordinal) -and
                $extension -in @('.cs','.csproj','.xaml','.sql')
            $allowedVerificationOverride = $relativePath -ceq 'scripts/verify_managed_browser_helper_package.ps1'
            if (-not $allowedApplicationOverride -and -not $allowedVerificationOverride -and -not $allowedHelperOverrides.Contains($relativePath)) {
                throw "Reviewed production source override is outside the narrow allowlist: $relativePath"
            }
            if ($relativePath.EndsWith('/packages.lock.json', [StringComparison]::OrdinalIgnoreCase) -or
                $relativePath.Contains('..') -or [IO.Path]::IsPathRooted($relativePath) -or
                -not $overridePaths.Add($relativePath)) {
                throw "Reviewed production source override path is unsafe or duplicated: $relativePath"
            }
            $sourcePath = Join-Path $Source $relativePath
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf) -or
                ([IO.File]::GetAttributes($sourcePath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reviewed production source override is missing or unsafe: $relativePath"
            }
            if ((Get-LowerSha256 $sourcePath) -cne [string]$Matches.hash) {
                throw "Reviewed production source override hash differs: $relativePath"
            }
            $destinationPath = Join-Path $Destination $relativePath
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
            [IO.File]::Copy($sourcePath, $destinationPath, $true)
            if ((Get-LowerSha256 $destinationPath) -cne [string]$Matches.hash) {
                Remove-Item -LiteralPath $destinationPath -Force -ErrorAction SilentlyContinue
                throw "Staged reviewed production source override hash differs: $relativePath"
            }
        }
        if ($overridePaths.Count -eq 0) { throw 'The reviewed production source override manifest is empty.' }
    }

    $zipPath = $null
    $reportPath = $null
        $sourceCommit = [string]$productionSourceCommit
        $sourceSnapshot = Join-Path $buildRoot 'source'
        Copy-ProductionSourceSnapshot $repoRoot $sourceSnapshot $sourceCommit
        Initialize-ProductIdentity `
            (Join-Path $sourceSnapshot 'scripts\ProductIdentity.ps1') `
            (Join-Path $sourceSnapshot 'eng\ProductIdentity.props') `
            $sourceSnapshot
        [IO.File]::WriteAllText((Join-Path $logDirectory 'source-commit.txt'), "$sourceCommit`n", $utf8NoBomProduction)
        $appProject = Join-Path $sourceSnapshot 'src\BaiduBatchTransfer.App\BaiduBatchTransfer.App.csproj'
        $legacyLauncherProject = Join-Path $sourceSnapshot 'src\BaiduBatchTransfer.LegacyLauncher\BaiduBatchTransfer.LegacyLauncher.csproj'
        $legacyLauncherPublishDirectory = Join-Path $buildRoot 'legacy-launcher-publish'
        [IO.Directory]::CreateDirectory($legacyLauncherPublishDirectory) | Out-Null
        $productionIconPath = $productIconPath
        $toolManifestPath = Join-Path $sourceSnapshot '.config\dotnet-tools.json'
        $helperVerificationScript = Join-Path $sourceSnapshot 'scripts\verify_managed_browser_helper_package.ps1'
        $helperManifestSource = Join-Path $sourceSnapshot 'tools\managed_browser_helper\helper-manifest.json'
        $helperSourceDirectory = Join-Path $sourceSnapshot 'tools\managed_browser_helper'
        $bundleValidator = Join-Path $sourceSnapshot 'scripts\Test-OtaUploadBundle.ps1'
        $publisherCertificate = Join-Path $sourceSnapshot 'eng\ota\SuperPan.OtaPublisher.cer'
        foreach ($required in @($appProject,$legacyLauncherProject,$productionIconPath,$toolManifestPath,$helperVerificationScript,$helperManifestSource,$bundleValidator,$publisherCertificate)) {
            if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required production input is missing: $required" }
        }
        $toolManifest = [IO.File]::ReadAllText($toolManifestPath) | ConvertFrom-Json
        if ([string]$toolManifest.tools.vpk.version -cne $requiredVpkVersion) { throw "The local vpk tool manifest must pin exactly version $requiredVpkVersion." }

        if ($null -ne $productionPrevious) {
            $previousUploadSource = $productionPrevious
            if (Test-Path -LiteralPath $productionPrevious -PathType Container) {
                $previousCandidates = @([IO.Directory]::EnumerateFiles(
                        $productionPrevious, "$productionPackageId-*-win-x64-stable-upload.zip", [IO.SearchOption]::TopDirectoryOnly) |
                    Where-Object {
                        [IO.Path]::GetFileName($_) -cmatch '^SuperPan\.BaiduBatchTransfer-[0-9]+\.[0-9]+\.[0-9]+-win-x64-stable-upload\.zip$'
                    })
                if ($previousCandidates.Count -ne 1) {
                    throw 'Previous production generation directory must contain exactly one canonical upload ZIP.'
                }
                $previousUploadSource = $previousCandidates[0]
            }
            Assert-ExistingPathIsNotReparsePoint $previousUploadSource 'Previous production upload ZIP'
            $validatedPrevious = Join-Path $buildRoot 'previous-upload.zip'
            [IO.File]::Copy($previousUploadSource, $validatedPrevious, $false)
            & $bundleValidator -BundlePath $validatedPrevious -PublisherCertificatePath $publisherCertificate -ExpectedPublisherSpkiSha256 $expectedSpki | Out-Null
            $previousArchive = [IO.Compression.ZipFile]::OpenRead($validatedPrevious)
            try {
                foreach ($entry in $previousArchive.Entries) {
                    $name = $entry.FullName
                    if ($name -ceq "releases.$productionChannel.json" -or $name.EndsWith('.nupkg', [StringComparison]::Ordinal)) {
                        if ($name -cne [IO.Path]::GetFileName($name)) { throw 'Previous production upload ZIP contains a nested release asset.' }
                        $destination = Join-Path $assetDirectory $name
                        if (Test-Path -LiteralPath $destination) { throw "Previous production upload ZIP contains a duplicate release asset: $name" }
                        $input = $entry.Open(); $output = [IO.File]::Create($destination)
                        try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
                    }
                }
            }
            finally { $previousArchive.Dispose() }
        }

        if ($null -eq $productionNotes) { $notesText = "# $Version`n`nSuperPan production release.`n" }
        else { $notesText = [IO.File]::ReadAllText($productionNotes) }
        $notesPath = Join-Path $buildRoot 'release-notes.vpk.md'
        [IO.File]::WriteAllText($notesPath, $notesText, [Text.UTF8Encoding]::new($true))

        Invoke-ProductionCommand 'dotnet-tool-restore' 'dotnet' @('tool','restore','--tool-manifest',$toolManifestPath) $sourceSnapshot
        Invoke-ProductionCommand 'vpk-root-help' 'dotnet' @('tool','run','vpk','--','--help') $sourceSnapshot
        Invoke-ProductionCommand 'vpk-pack-help' 'dotnet' @('tool','run','vpk','--','pack','--help') $sourceSnapshot
        $helperPackageDirectory = [IO.Path]::GetFullPath((Join-Path $repoRoot '.artifacts\managed-session-pipeline-20260901\helper-bundle-r2'))
        $licenseProperties = @(
            "-p:LicenseSigningKeyId=$LicenseSigningKeyId",
            "-p:LicenseSigningPublicKey=$LicenseSigningPublicKey",
            "-p:LicenseCurrentSpkiPin=$LicenseCurrentSpkiPin",
            "-p:LicenseNextSpkiPin=$LicenseNextSpkiPin",
            "-p:LicenseServiceUrl=$LicenseServiceUrl")
        $restoreProperties = @('-r','win-x64','-p:RuntimeIdentifier=win-x64','-p:SelfContained=true',
            "-p:VersionPrefix=$Version","-p:UpdateFeedUrl=$normalizedFeedUrl",'-p:OtaAcceptanceBuild=false','-p:OtaAcceptanceFeedPath=',
            "-p:UpdateChannel=$productionChannel","-p:ManagedBrowserHelperPackageDirectory=$helperPackageDirectory") + $licenseProperties
        Invoke-ProductionCommand 'dotnet-restore-locked' 'dotnet' (@('restore',$appProject,'--locked-mode') + $restoreProperties) $sourceSnapshot
        Invoke-ProductionCommand 'dotnet-restore-legacy-launcher-locked' 'dotnet' @(
            'restore',$legacyLauncherProject,'--locked-mode','-r','win-x64','-p:RuntimeIdentifier=win-x64','-p:SelfContained=true') $sourceSnapshot
        Invoke-ProductionCommand 'dotnet-publish' 'dotnet' (@(
            'publish',$appProject,'-c','Release','-r','win-x64','--self-contained','true','--no-restore','-o',$publishDirectory,
            "-p:VersionPrefix=$Version",'-p:RuntimeIdentifier=win-x64','-p:SelfContained=true',"-p:UpdateFeedUrl=$normalizedFeedUrl",
            '-p:OtaAcceptanceBuild=false','-p:OtaAcceptanceFeedPath=',"-p:UpdateChannel=$productionChannel",
            "-p:ManagedBrowserHelperPackageDirectory=$helperPackageDirectory") + $licenseProperties) $sourceSnapshot
        Invoke-ProductionCommand 'dotnet-publish-legacy-launcher' 'dotnet' @(
            'publish',$legacyLauncherProject,'-c','Release','-r','win-x64','--self-contained','true','--no-restore',
            '-o',$legacyLauncherPublishDirectory,"-p:VersionPrefix=$Version",'-p:RuntimeIdentifier=win-x64','-p:SelfContained=true') $sourceSnapshot
        Assert-PublishedApplicationLayout $publishDirectory 'Published production application'
        Install-VerifiedLegacyLauncher $legacyLauncherPublishDirectory $publishDirectory $buildRoot
        $publishedAssemblyPath = Join-Path $publishDirectory $productMainDllName
        $publishedAssembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($publishedAssemblyPath))
        $publishedMetadata = @{}
        foreach ($attribute in $publishedAssembly.GetCustomAttributes([Reflection.AssemblyMetadataAttribute], $false)) {
            if ($publishedMetadata.ContainsKey($attribute.Key)) { throw "Published production metadata contains duplicate key: $($attribute.Key)" }
            $publishedMetadata[$attribute.Key] = $attribute.Value
        }
        $publishedVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedAssemblyPath)
        if ([string]$publishedVersionInfo.ProductVersion -cne $Version -or
            [string]$publishedMetadata.UpdateFeedUrl -cne $normalizedFeedUrl -or
            [string]$publishedMetadata.UpdateChannel -cne $productionChannel -or
            [string]$publishedMetadata.OtaAcceptanceBuild -cne 'false') {
            throw "Published production metadata does not match the requested version, feed, channel, and build mode (version='$([string]$publishedVersionInfo.ProductVersion)', feed='$([string]$publishedMetadata.UpdateFeedUrl)', channel='$([string]$publishedMetadata.UpdateChannel)', acceptance='$([string]$publishedMetadata.OtaAcceptanceBuild)')."
        }
        Invoke-ProductionCommand 'verify-managed-browser-helper' 'pwsh' @('-NoProfile','-ExecutionPolicy','Bypass','-File',$helperVerificationScript,
            '-ManifestSource',$helperManifestSource,'-PackageDirectory',(Join-Path $publishDirectory 'managed-browser-helper'),'-HelperSourceDirectory',$helperSourceDirectory) $sourceSnapshot

        $signParams = "/sha1 $thumbprint /fd SHA256"
        if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) { $signParams += " /tr $TimestampUrl /td SHA256" }
        $productionVpkArguments = @(
            'tool', 'run', 'vpk', '--', 'pack',
            '--packId', $productionPackageId,
            '--packVersion', $Version,
            '--packDir', $publishDirectory,
            '--mainExe', $productMainExecutableName,
            '--packTitle', $productDisplayName,
            '--icon', $productionIconPath,
            '--channel', $productionChannel,
            '--runtime', 'win-x64',
            '--outputDir', $assetDirectory,
            '--releaseNotes', $notesPath,
            '--signParams', $signParams)
        Invoke-ProductionCommand 'vpk-pack' 'dotnet' $productionVpkArguments $sourceSnapshot

        # Velopack includes the channel in generated filenames. The signed-feed contract
        # intentionally exposes stable canonical names so client and server validators do
        # not have to accept aliases. Normalize once, then regenerate every reference.
        $releaseFeedPath = Join-Path $assetDirectory "releases.$productionChannel.json"
        $assetCatalogPath = Join-Path $assetDirectory "assets.$productionChannel.json"
        $releasesIndexPath = Join-Path $assetDirectory "RELEASES-$productionChannel"
        $releaseFeed = [IO.File]::ReadAllText($releaseFeedPath) | ConvertFrom-Json
        $releaseAssets = @($releaseFeed.Assets)
        if ($releaseAssets.Count -eq 0) { throw 'Velopack production feed contains no assets.' }
        foreach ($asset in $releaseAssets) {
            $assetVersion = [string]$asset.Version
            $assetType = [string]$asset.Type
            if ($assetType -cnotin @('Full','Delta')) { throw "Velopack produced an unknown package type: $assetType" }
            $canonicalName = "$productionPackageId-$assetVersion-$($assetType.ToLowerInvariant()).nupkg"
            $sourceName = [string]$asset.FileName
            $sourcePath = Join-Path $assetDirectory $sourceName
            $canonicalPath = Join-Path $assetDirectory $canonicalName
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Velopack feed references a missing package: $sourceName" }
            if ($sourceName -cne $canonicalName) {
                if (Test-Path -LiteralPath $canonicalPath) { throw "Canonical package already exists: $canonicalName" }
                [IO.File]::Move($sourcePath, $canonicalPath)
            }
            $asset.FileName = $canonicalName
            $asset.Size = [IO.FileInfo]::new($canonicalPath).Length
            $asset.SHA256 = (Get-LowerSha256 $canonicalPath).ToUpperInvariant()
            $asset.SHA1 = (Get-LowerSha1 $canonicalPath).ToUpperInvariant()
        }
        $setupGenerated = Join-Path $assetDirectory "$productionPackageId-$productionChannel-Setup.exe"
        $portableGenerated = Join-Path $assetDirectory "$productionPackageId-$productionChannel-Portable.zip"
        $setupCanonical = Join-Path $assetDirectory "$productionPackageId-Setup.exe"
        $portableCanonical = Join-Path $assetDirectory "$productionPackageId-Portable.zip"
        foreach ($rename in @(@($setupGenerated,$setupCanonical),@($portableGenerated,$portableCanonical))) {
            if (-not (Test-Path -LiteralPath $rename[0] -PathType Leaf) -or (Test-Path -LiteralPath $rename[1])) { throw 'Velopack fixed production alias normalization failed.' }
            [IO.File]::Move($rename[0], $rename[1])
        }
        [IO.File]::WriteAllText($releaseFeedPath, ($releaseFeed | ConvertTo-Json -Depth 8 -Compress), $utf8NoBomProduction)

        $currentTypes = @($releaseAssets | Where-Object { [string]$_.Version -ceq $Version } | ForEach-Object { [string]$_.Type })
        if (@($currentTypes | Where-Object { $_ -ceq 'Full' }).Count -ne 1) { throw 'Production feed requires exactly one current Full package.' }
        if ($currentTypes -ccontains 'Delta') {
            $priorFullAssets = @($releaseAssets | Where-Object {
                    [string]$_.Type -ceq 'Full' -and [version][string]$_.Version -lt [version]$Version
                } | Sort-Object { [version][string]$_.Version })
            if ($priorFullAssets.Count -eq 0) { throw 'Production Delta requires a preceding Full package.' }
            $basePackagePath = Join-Path $assetDirectory ([string]$priorFullAssets[-1].FileName)
            $deltaPackagePath = Join-Path $assetDirectory "$productionPackageId-$Version-delta.nupkg"
            $currentFullPath = Join-Path $assetDirectory "$productionPackageId-$Version-full.nupkg"
            $reconstructedFullPath = Join-Path $buildRoot 'delta-reconstructed-full.nupkg'
            Invoke-ProductionCommand 'verify-delta-reconstruction' 'dotnet' @(
                'tool','run','vpk','--','delta','patch','--base',$basePackagePath,'--patch',$deltaPackagePath,'--output',$reconstructedFullPath) $sourceSnapshot
            if ((Get-LowerSha256 $reconstructedFullPath) -cne (Get-LowerSha256 $currentFullPath)) {
                throw 'Delta reconstruction differs from the current Full package.'
            }
            Remove-Item -LiteralPath $reconstructedFullPath -Force
        }
        $catalog = [Collections.Generic.List[object]]::new()
        $catalog.Add([ordered]@{ RelativeFileName="$productionPackageId-Setup.exe"; Type='Installer' })
        $catalog.Add([ordered]@{ RelativeFileName="$productionPackageId-$Version-full.nupkg"; Type='Full' })
        if ($currentTypes -ccontains 'Delta') { $catalog.Add([ordered]@{ RelativeFileName="$productionPackageId-$Version-delta.nupkg"; Type='Delta' }) }
        $catalog.Add([ordered]@{ RelativeFileName="$productionPackageId-Portable.zip"; Type='Portable' })
        [IO.File]::WriteAllText($assetCatalogPath, ($catalog | ConvertTo-Json -Depth 4 -Compress), $utf8NoBomProduction)

        $fullAssets = @($releaseAssets | Where-Object { [string]$_.Type -ceq 'Full' } | Sort-Object { [version][string]$_.Version })
        $releaseLines = foreach ($asset in $fullAssets) {
            $path = Join-Path $assetDirectory ([string]$asset.FileName)
            "$((Get-LowerSha1 $path).ToUpperInvariant()) $([string]$asset.FileName) $([IO.FileInfo]::new($path).Length)"
        }
        [IO.File]::WriteAllText($releasesIndexPath, (($releaseLines -join "`n") + "`n"), $utf8NoBomProduction)

        $assetNames = @([IO.Directory]::EnumerateFiles($assetDirectory, '*', [IO.SearchOption]::TopDirectoryOnly) | ForEach-Object { [IO.Path]::GetFileName($_) })
        foreach ($name in $assetNames) {
            if ($name -cin @('upload-manifest.sha256','upload-manifest.sha256.sig') -or $name.Contains('/') -or $name.Contains('\')) { throw "Unexpected production asset: $name" }
        }
        [Array]::Sort($assetNames, [StringComparer]::Ordinal)
        $manifestLines = foreach ($name in $assetNames) { "$(Get-LowerSha256 (Join-Path $assetDirectory $name))  $name" }
        $manifestPath = Join-Path $assetDirectory 'upload-manifest.sha256'
        [IO.File]::WriteAllText($manifestPath, (($manifestLines -join "`n") + "`n"), $utf8NoBomProduction)
        $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
        $manifestSignature = $signingRsa.SignData($manifestBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        [IO.File]::WriteAllBytes((Join-Path $assetDirectory 'upload-manifest.sha256.sig'), $manifestSignature)

        $preZipReport = Join-Path $logDirectory 'pre-zip-verification.json'
        $validatorArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$bundleValidator,'-BundlePath',$assetDirectory,
            '-PublisherCertificatePath',$publisherCertificate,'-ExpectedVersion',$Version,'-ExpectedPublisherSpkiSha256',$expectedSpki,'-OutputReportPath',$preZipReport)
        if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) { $validatorArguments += '-RequireTimestamp' }
        Invoke-ProductionCommand 'verify-production-directory' 'pwsh' $validatorArguments $sourceSnapshot

        $zipName = "$productionPackageId-$Version-$productionChannel-upload.zip"
        $finalZipPath = Join-Path $productionStage $zipName
        $finalReportPath = Join-Path $productionStage "$productionPackageId-$Version-$productionChannel-verification.json"
        $partialSuffix = ".partial-$([Guid]::NewGuid().ToString('N'))"
        $partialZipPath = Join-Path $buildRoot ($zipName + $partialSuffix + '.zip')
        $partialReportPath = Join-Path $buildRoot (([IO.Path]::GetFileName($finalReportPath)) + $partialSuffix + '.json')
        $zipStream = [IO.File]::Open($partialZipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $zip = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $zipNames = @([IO.Directory]::EnumerateFiles($assetDirectory, '*', [IO.SearchOption]::TopDirectoryOnly) | ForEach-Object { [IO.Path]::GetFileName($_) })
            [Array]::Sort($zipNames, [StringComparer]::Ordinal)
            foreach ($name in $zipNames) {
                $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2026,1,1,0,0,0,[TimeSpan]::Zero)
                $source = [IO.File]::OpenRead((Join-Path $assetDirectory $name)); $destination = $entry.Open()
                try { $source.CopyTo($destination) } finally { $destination.Dispose(); $source.Dispose() }
            }
        }
        finally { $zip.Dispose(); $zipStream.Dispose() }

        $zipValidatorArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$bundleValidator,'-BundlePath',$partialZipPath,
            '-PublisherCertificatePath',$publisherCertificate,'-ExpectedVersion',$Version,'-ExpectedPublisherSpkiSha256',$expectedSpki,'-OutputReportPath',$partialReportPath)
        if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) { $zipValidatorArguments += '-RequireTimestamp' }
        Invoke-ProductionCommand 'verify-reopened-production-zip' 'pwsh' $zipValidatorArguments $sourceSnapshot
        [IO.File]::Move($partialReportPath, $finalReportPath)
        $reportPath = $finalReportPath
        [IO.File]::Move($partialZipPath, $finalZipPath)
        $zipPath = $finalZipPath
        $finalLogDirectory = Join-Path $productionStage 'build-logs'
        [IO.Directory]::Move($logDirectory, $finalLogDirectory)
        if (Test-Path -LiteralPath $buildRoot) { Remove-Item -LiteralPath $buildRoot -Recurse -Force }
        [IO.Directory]::Move($productionStage, $productionOutput)
        $zipPath = Join-Path $productionOutput $zipName
        $reportPath = Join-Path $productionOutput "$productionPackageId-$Version-$productionChannel-verification.json"
        Write-Output "Production OTA upload bundle created: $zipPath"
        Write-Output "Verification report: $reportPath"
        if ([string]::IsNullOrWhiteSpace($TimestampUrl)) { Write-Output 'Authenticode mode: signed and explicitly untimestamped.' }
        else { Write-Output "Authenticode mode: signed with required timestamp service $TimestampUrl" }
    }
    catch {
        if (-not [string]::IsNullOrWhiteSpace($logDirectory) -and (Test-Path -LiteralPath $logDirectory)) {
            $failureLogDirectory = "$productionOutput.failure-logs-$([Guid]::NewGuid().ToString('N'))"
            [IO.Directory]::Move($logDirectory, $failureLogDirectory)
        }
        if (-not [string]::IsNullOrWhiteSpace($productionStage) -and (Test-Path -LiteralPath $productionStage)) { Remove-Item -LiteralPath $productionStage -Recurse -Force }
        throw
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($productionStage) -and (Test-Path -LiteralPath $productionStage)) { Remove-Item -LiteralPath $productionStage -Recurse -Force }
        if ($null -ne $signingRsa) { $signingRsa.Dispose() }
        if ($null -ne $signingCertificate) { $signingCertificate.Dispose() }
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Production') {
    $productionSourceCommitLines = @(& git -C $repoRoot rev-parse --verify 'HEAD^{commit}')
    if ($LASTEXITCODE -ne 0 -or $productionSourceCommitLines.Count -ne 1 -or
        $productionSourceCommitLines[0] -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve exactly one production source commit SHA.'
    }
    $productionSourceCommit = [string]$productionSourceCommitLines[0]
    & git -C $repoRoot diff --quiet --exit-code $productionSourceCommit -- `
        'scripts/ProductIdentity.ps1' 'eng/ProductIdentity.props'
    $identityDiffExitCode = $LASTEXITCODE
    if ($identityDiffExitCode -eq 1) {
        throw 'Production product identity inputs differ from source commit.'
    }
    if ($identityDiffExitCode -ne 0) {
        throw 'Unable to verify production product identity inputs against source commit.'
    }
    Invoke-ProductionRelease
    return
}

Initialize-ProductIdentity `
    (Join-Path $PSScriptRoot 'ProductIdentity.ps1') `
    (Join-Path $repoRoot 'eng\ProductIdentity.props') `
    $repoRoot

$acceptanceFeed = Resolve-AbsolutePath $AcceptanceFeedPath 'AcceptanceFeedPath'
Assert-LocalDrivePath $acceptanceFeed 'AcceptanceFeedPath'
Assert-NoReparsePointAncestors $acceptanceFeed 'AcceptanceFeedPath'
if (-not (Test-Path -LiteralPath $acceptanceFeed -PathType Container)) {
    throw 'AcceptanceFeedPath must be an existing local filesystem directory.'
}
$outputPath = Resolve-AbsolutePath $OutputDirectory 'OutputDirectory'
Assert-LocalDrivePath $outputPath 'OutputDirectory'
Assert-NoReparsePointAncestors $outputPath 'OutputDirectory'
if (-not (Test-PathWithin $outputPath $artifactRoot)) {
    throw 'OutputDirectory must be inside the repository .artifacts directory.'
}
Assert-NonReparseExistingAncestors $outputPath $artifactRoot
$relativeOutput = $outputPath.Substring(
    $artifactRoot.TrimEnd('\', '/').Length + 1).Replace('/', '\')
$acceptanceRootName = $relativeOutput.Split('\')[0]
if (-not $acceptanceRootName.StartsWith('ota-acceptance-', [StringComparison]::OrdinalIgnoreCase) -or
    $relativeOutput.IndexOf('\') -lt 1) {
    throw 'OutputDirectory must be a child of a dedicated .artifacts/ota-acceptance-* directory.'
}
if ($acceptanceFeed.Equals($outputPath, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-PathWithin $acceptanceFeed $outputPath) -or
    (Test-PathWithin $outputPath $acceptanceFeed)) {
    throw 'AcceptanceFeedPath and OutputDirectory must be separate directories.'
}
Assert-ExistingPathIsNotReparsePoint $acceptanceFeed 'AcceptanceFeedPath'
Assert-ExistingPathIsNotReparsePoint $outputPath 'OutputDirectory'
if (Test-Path -LiteralPath $outputPath) {
    if (-not (Test-Path -LiteralPath $outputPath -PathType Container)) {
        throw 'OutputDirectory must be a directory.'
    }
    if (@([IO.Directory]::EnumerateFileSystemEntries($outputPath)).Count -ne 0) {
        throw 'OutputDirectory must be empty; existing files are never overwritten.'
    }
}

$previousPath = $null
$validatedPreviousAssets = @()
if (-not [string]::IsNullOrWhiteSpace($PreviousReleaseDirectory)) {
    $previousPath = Resolve-AbsolutePath $PreviousReleaseDirectory 'PreviousReleaseDirectory'
    Assert-LocalDrivePath $previousPath 'PreviousReleaseDirectory'
    Assert-NoReparsePointAncestors $previousPath 'PreviousReleaseDirectory'
    if (-not (Test-Path -LiteralPath $previousPath -PathType Container)) {
        throw 'PreviousReleaseDirectory must be an existing directory.'
    }
    Assert-ExistingPathIsNotReparsePoint $previousPath 'PreviousReleaseDirectory'
    if ($previousPath.Equals($outputPath, [StringComparison]::OrdinalIgnoreCase) -or
        (Test-PathWithin $previousPath $outputPath) -or
        (Test-PathWithin $outputPath $previousPath)) {
        throw 'PreviousReleaseDirectory and OutputDirectory must be separate directories.'
    }

    $expectedPreviousFeedName = "releases.$acceptanceChannel.json"
    $previousFeedFiles = @([IO.Directory]::EnumerateFiles(
            $previousPath, 'releases.*.json', [IO.SearchOption]::TopDirectoryOnly))
    if ($previousFeedFiles.Count -ne 1 -or
        [IO.Path]::GetFileName($previousFeedFiles[0]) -cne $expectedPreviousFeedName) {
        throw "PreviousReleaseDirectory must contain only the acceptance release feed $expectedPreviousFeedName."
    }
    $previousFeedPath = $previousFeedFiles[0]
    Assert-ExistingPathIsNotReparsePoint $previousFeedPath 'Previous acceptance release feed'
    try {
        $previousFeed = [IO.File]::ReadAllText($previousFeedPath) | ConvertFrom-Json
    }
    catch {
        throw 'Previous acceptance release feed is not valid JSON.'
    }
    if ($null -eq $previousFeed.PSObject.Properties['Assets']) {
        throw 'Previous acceptance release feed has no Assets property.'
    }
    $previousFeedAssets = @($previousFeed.Assets)
    if ($previousFeedAssets.Count -eq 0) {
        throw 'Previous acceptance release feed contains no assets.'
    }
    $referencedPreviousPackages = New-Object `
        'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    $containsRequestedVersion = $false
    foreach ($asset in $previousFeedAssets) {
        foreach ($propertyName in @('PackageId', 'Version', 'FileName', 'SHA256', 'Size')) {
            if ($null -eq $asset -or $null -eq $asset.PSObject.Properties[$propertyName]) {
                throw "Previous acceptance release feed asset is missing $propertyName."
            }
        }
        if ([string]$asset.PackageId -cne $acceptancePackageId) {
            throw 'Previous acceptance release feed contains a foreign package ID.'
        }
        $fileName = [string]$asset.FileName
        if (-not (Test-SafeNupkgFileName $fileName)) {
            throw 'Previous acceptance release feed contains an unsafe package file name.'
        }
        if ($referencedPreviousPackages.ContainsKey($fileName)) {
            throw "Previous acceptance release feed contains duplicate package file name: $fileName"
        }
        $declaredHash = [string]$asset.SHA256
        if ($declaredHash -cnotmatch '^[0-9A-Fa-f]{64}$') {
            throw "Previous acceptance release feed contains an invalid SHA-256: $fileName"
        }
        try { $declaredSize = [long]$asset.Size }
        catch { throw "Previous acceptance release feed contains an invalid size: $fileName" }
        if ($declaredSize -lt 0) {
            throw "Previous acceptance release feed contains an invalid size: $fileName"
        }
        $packagePath = Join-Path $previousPath $fileName
        if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
            throw "Previous acceptance release feed referenced package is missing: $fileName"
        }
        Assert-ExistingPathIsNotReparsePoint $packagePath 'Previous acceptance package'
        $packageInfo = [IO.FileInfo]::new($packagePath)
        if ($packageInfo.Length -ne $declaredSize) {
            throw "Previous acceptance package size does not match its feed: $fileName"
        }
        $actualHash = Get-LowerSha256 $packagePath
        if ($actualHash -cne $declaredHash.ToLowerInvariant()) {
            throw "Previous acceptance package SHA-256 does not match its feed: $fileName"
        }
        $referencedPreviousPackages.Add($fileName, [pscustomobject]@{
                FileName = $fileName
                FullPath = $packagePath
            })
        if ([string]$asset.Version -ceq $Version) { $containsRequestedVersion = $true }
    }
    foreach ($packageFile in [IO.Directory]::EnumerateFiles(
            $previousPath, '*.nupkg', [IO.SearchOption]::TopDirectoryOnly)) {
        Assert-ExistingPathIsNotReparsePoint $packageFile 'Previous acceptance package'
        $actualName = [IO.Path]::GetFileName($packageFile)
        if (-not $referencedPreviousPackages.ContainsKey($actualName)) {
            throw "PreviousReleaseDirectory contains an unreferenced nupkg: $actualName"
        }
        if ($actualName -cne [string]$referencedPreviousPackages[$actualName].FileName) {
            throw "Previous acceptance package casing differs from its feed: $actualName"
        }
    }
    if ($containsRequestedVersion) {
        throw "Version $Version already exists in the previous release feed."
    }
    $validatedPreviousAssets = @($previousFeedPath) + @(
        $referencedPreviousPackages.Values |
            Sort-Object FileName |
            ForEach-Object { $_.FullPath })
}

$releaseNotes = $null
if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
    $releaseNotes = Resolve-AbsolutePath $ReleaseNotesPath 'ReleaseNotesPath'
    Assert-LocalDrivePath $releaseNotes 'ReleaseNotesPath'
    Assert-NoReparsePointAncestors $releaseNotes 'ReleaseNotesPath'
    if (-not (Test-Path -LiteralPath $releaseNotes -PathType Leaf)) {
        throw 'ReleaseNotesPath must name an existing file.'
    }
    Assert-ExistingPathIsNotReparsePoint $releaseNotes 'ReleaseNotesPath'
}

$appProject = Join-Path $repoRoot 'src\BaiduBatchTransfer.App\BaiduBatchTransfer.App.csproj'
$legacyLauncherProject = Join-Path $repoRoot 'src\BaiduBatchTransfer.LegacyLauncher\BaiduBatchTransfer.LegacyLauncher.csproj'
$toolManifestPath = Join-Path $repoRoot '.config\dotnet-tools.json'
$helperVerificationScript = Join-Path $repoRoot 'scripts\verify_managed_browser_helper_package.ps1'
$helperManifestSource = Join-Path $repoRoot 'tools\managed_browser_helper\helper-manifest.json'
$helperSourceDirectory = Join-Path $repoRoot 'tools\managed_browser_helper'
foreach ($requiredInput in @($appProject, $legacyLauncherProject, $productIconPath, $toolManifestPath, $helperVerificationScript, $helperManifestSource)) {
    if (-not (Test-Path -LiteralPath $requiredInput -PathType Leaf)) {
        throw "Required release input is missing: $requiredInput"
    }
}
try { $toolManifest = [IO.File]::ReadAllText($toolManifestPath) | ConvertFrom-Json }
catch { throw 'The local dotnet tool manifest is not valid JSON.' }
if ([string]$toolManifest.tools.vpk.version -cne $requiredVpkVersion) {
    throw "The local vpk tool manifest must pin exactly version $requiredVpkVersion."
}

[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$workingDirectory = Join-Path $outputPath '.build'
$publishDirectory = Join-Path $workingDirectory 'publish'
$legacyLauncherPublishDirectory = Join-Path $workingDirectory 'legacy-launcher-publish'
$logDirectory = Join-Path $workingDirectory 'logs'
[IO.Directory]::CreateDirectory($publishDirectory) | Out-Null
[IO.Directory]::CreateDirectory($legacyLauncherPublishDirectory) | Out-Null
[IO.Directory]::CreateDirectory($logDirectory) | Out-Null
$commandLogPath = Join-Path $logDirectory 'commands.jsonl'
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$utf8Bom = New-Object Text.UTF8Encoding($true)

function Invoke-RecordedCommand(
    [string]$Name,
    [string]$FilePath,
    [string[]]$Arguments) {
    $safeName = $Name -replace '[^A-Za-z0-9_.-]', '_'
    $stdoutPath = Join-Path $logDirectory "$safeName.stdout.log"
    $stderrPath = Join-Path $logDirectory "$safeName.stderr.log"
    $startedUtc = [DateTime]::UtcNow
    $exitCode = $null
    $status = 'Exception'
    $invocationException = $null
    $originalLocation = Get-Location
    $originalCurrentDirectory = [Environment]::CurrentDirectory
    $originalErrorActionPreference = $ErrorActionPreference
    try {
        Set-Location -LiteralPath $repoRoot
        [Environment]::CurrentDirectory = $repoRoot
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = $null
        & $FilePath @Arguments 1> $stdoutPath 2> $stderrPath
        $commandSucceeded = $?
        $exitCode = $LASTEXITCODE
        if ($null -eq $exitCode) {
            if (-not $commandSucceeded) {
                throw "Release command '$Name' could not be started."
            }
            $exitCode = 0
        }
        $status = if ($exitCode -eq 0) { 'Succeeded' } else { 'Failed' }
    }
    catch {
        if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
            $exitCode = $LASTEXITCODE
            $status = 'Failed'
        }
        else {
            $invocationException = $_
            $status = 'Exception'
        }
    }
    finally {
        $ErrorActionPreference = $originalErrorActionPreference
        Set-Location -LiteralPath $originalLocation.Path
        [Environment]::CurrentDirectory = $originalCurrentDirectory
        $stdout = if (Test-Path -LiteralPath $stdoutPath) {
            [IO.File]::ReadAllText($stdoutPath)
        } else { '' }
        $stderr = if (Test-Path -LiteralPath $stderrPath) {
            [IO.File]::ReadAllText($stderrPath)
        } else { '' }
        if ($null -ne $invocationException) {
            $exceptionText = [string]$invocationException
            if ([string]::IsNullOrWhiteSpace($stderr)) { $stderr = $exceptionText }
            else { $stderr = $stderr.TrimEnd() + "`n" + $exceptionText }
        }
        $record = [ordered]@{
            name = $Name
            executable = $FilePath
            arguments = @($Arguments)
            WorkingDirectory = $repoRoot
            startedUtc = $startedUtc.ToString('O')
            status = $status
            exitCode = $exitCode
            stdout = $stdout
            stderr = $stderr
        }
        [IO.File]::AppendAllText(
            $commandLogPath,
            (($record | ConvertTo-Json -Compress -Depth 4) + "`n"),
            $utf8NoBom)
    }
    if (-not [string]::IsNullOrEmpty($stdout)) { Write-Verbose $stdout.TrimEnd() }
    if (-not [string]::IsNullOrEmpty($stderr)) { Write-Warning $stderr.TrimEnd() }
    if ($null -ne $invocationException) { throw $invocationException }
    if ($exitCode -ne 0) {
        throw "Release command '$Name' failed with exit code $exitCode. See $commandLogPath"
    }
    return [pscustomobject]@{ Stdout = $stdout; Stderr = $stderr; ExitCode = $exitCode }
}

if ($null -eq $releaseNotes) {
    $defaultReleaseNotesLabel = ('{"value":"\u672c\u673a OTA \u9a8c\u6536\u6784\u5efa\u3002"}' |
        ConvertFrom-Json).value
    $effectiveReleaseNotesText = "# $Version`n`n$defaultReleaseNotesLabel`n"
}
else {
    try { $effectiveReleaseNotesText = [IO.File]::ReadAllText($releaseNotes) }
    catch { throw "Unable to read release notes '$releaseNotes': $($_.Exception.Message)" }
}
$expectedReleaseNotesText = $effectiveReleaseNotesText.TrimEnd()
$effectiveReleaseNotesPath = Join-Path $workingDirectory 'release-notes.vpk.md'
try {
    [IO.File]::WriteAllText(
        $effectiveReleaseNotesPath,
        $effectiveReleaseNotesText,
        $utf8Bom)
}
catch {
    throw "Unable to create the UTF-8 BOM release-notes copy: $($_.Exception.Message)"
}

$toolRestoreArguments = @('tool', 'restore')
[void](Invoke-RecordedCommand 'dotnet-tool-restore' 'dotnet' $toolRestoreArguments)

$vpkRootHelpArguments = @('tool', 'run', 'vpk', '--', '--help')
$vpkRootHelp = Invoke-RecordedCommand 'vpk-root-help' 'dotnet' $vpkRootHelpArguments
$vpkRootHelpOutput = $vpkRootHelp.Stdout + "`n" + $vpkRootHelp.Stderr
$vpkDescriptionMarkers = [Text.RegularExpressions.Regex]::Matches(
    $vpkRootHelpOutput,
    'Velopack CLI[ \t]+')
$requiredVpkTokenMatches = [Text.RegularExpressions.Regex]::Matches(
    $vpkRootHelpOutput,
    ('Velopack CLI[ \t]+' + [Text.RegularExpressions.Regex]::Escape($requiredVpkVersion) + '(?=$|[\s,])'))
if ($vpkDescriptionMarkers.Count -ne 1 -or $requiredVpkTokenMatches.Count -ne 1) {
    throw "The restored vpk executable root help must report exactly Velopack CLI $requiredVpkVersion."
}

$vpkHelpArguments = @('tool', 'run', 'vpk', '--', 'pack', '--help')
$vpkHelp = Invoke-RecordedCommand 'vpk-pack-help' 'dotnet' $vpkHelpArguments
foreach ($requiredOption in @('--packId', '--packVersion', '--packDir', '--mainExe',
        '--packTitle', '--icon', '--channel', '--runtime', '--outputDir', '--releaseNotes')) {
    if ($vpkHelp.Stdout.IndexOf($requiredOption, [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
        $vpkHelp.Stderr.IndexOf($requiredOption, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Locked vpk 1.2.0 help does not advertise required option $requiredOption."
    }
}

$restoreArguments = @(
    'restore', $appProject,
    '--locked-mode',
    '-r', 'win-x64',
    '-p:RuntimeIdentifier=win-x64',
    '-p:SelfContained=true',
    "-p:VersionPrefix=$Version",
    '-p:UpdateFeedUrl=',
    '-p:OtaAcceptanceBuild=true',
    "-p:OtaAcceptanceFeedPath=$acceptanceFeed",
    '-p:UpdateChannel=win-x64-ota-acceptance'
)
[void](Invoke-RecordedCommand 'dotnet-restore' 'dotnet' $restoreArguments)

$legacyLauncherRestoreArguments = @(
    'restore', $legacyLauncherProject,
    '--locked-mode',
    '-r', 'win-x64',
    '-p:RuntimeIdentifier=win-x64',
    '-p:SelfContained=true'
)
[void](Invoke-RecordedCommand 'dotnet-restore-legacy-launcher' 'dotnet' $legacyLauncherRestoreArguments)

$publishArguments = @(
    'publish', $appProject,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '-o', $publishDirectory,
    "-p:VersionPrefix=$Version",
    '-p:RuntimeIdentifier=win-x64',
    '-p:SelfContained=true',
    '-p:UpdateFeedUrl=',
    '-p:OtaAcceptanceBuild=true',
    "-p:OtaAcceptanceFeedPath=$acceptanceFeed",
    '-p:UpdateChannel=win-x64-ota-acceptance'
)
[void](Invoke-RecordedCommand 'dotnet-publish' 'dotnet' $publishArguments)

$legacyLauncherPublishArguments = @(
    'publish', $legacyLauncherProject,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '-o', $legacyLauncherPublishDirectory,
    "-p:VersionPrefix=$Version",
    '-p:RuntimeIdentifier=win-x64',
    '-p:SelfContained=true'
)
[void](Invoke-RecordedCommand 'dotnet-publish-legacy-launcher' 'dotnet' $legacyLauncherPublishArguments)

Assert-PublishedApplicationLayout $publishDirectory 'Published acceptance application'
Install-VerifiedLegacyLauncher $legacyLauncherPublishDirectory $publishDirectory $workingDirectory
$publishedAssembly = Join-Path $publishDirectory $productMainDllName
$metadataProjectDirectory = Join-Path $workingDirectory 'metadata-verifier'
[IO.Directory]::CreateDirectory($metadataProjectDirectory) | Out-Null
$metadataProject = Join-Path $metadataProjectDirectory 'MetadataVerifier.csproj'
$metadataProgram = Join-Path $metadataProjectDirectory 'Program.cs'
[IO.File]::WriteAllText($metadataProject, @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@, $utf8NoBom)
[IO.File]::WriteAllText($metadataProgram, @'
using System.Diagnostics;
using System.Reflection;

if (args.Length != 5) return 2;
var assemblyPath = Path.GetFullPath(args[0]);
var expectedVersion = args[1];
var expectedFeed = Path.GetFullPath(args[2]);
var expectedChannel = args[3];
var expectedAcceptance = args[4];
var assembly = Assembly.LoadFrom(assemblyPath);
var metadata = assembly.GetCustomAttributesData()
    .Where(a => a.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute")
    .ToDictionary(
        a => (string?)a.ConstructorArguments[0].Value ?? string.Empty,
        a => (string?)a.ConstructorArguments[1].Value ?? string.Empty,
        StringComparer.Ordinal);
var productVersion = FileVersionInfo.GetVersionInfo(assemblyPath).ProductVersion;
var valid = string.Equals(productVersion, expectedVersion, StringComparison.Ordinal)
    && metadata.TryGetValue("UpdateFeedUrl", out var productionFeed) && productionFeed.Length == 0
    && metadata.TryGetValue("OtaAcceptanceFeedPath", out var acceptanceFeed)
    && string.Equals(Path.GetFullPath(acceptanceFeed), expectedFeed, StringComparison.OrdinalIgnoreCase)
    && metadata.TryGetValue("UpdateChannel", out var channel)
    && string.Equals(channel, expectedChannel, StringComparison.Ordinal)
    && metadata.TryGetValue("OtaAcceptanceBuild", out var acceptance)
    && string.Equals(acceptance, expectedAcceptance, StringComparison.Ordinal);
if (!valid)
{
    Console.Error.WriteLine("Published assembly metadata did not match the requested OTA acceptance build.");
    return 1;
}
Console.WriteLine("Published assembly metadata verified.");
return 0;
'@, $utf8NoBom)
$metadataArguments = @(
    'run', '--project', $metadataProject,
    '-c', 'Release',
    '--', $publishedAssembly, $Version, $acceptanceFeed, $acceptanceChannel, 'true'
)
[void](Invoke-RecordedCommand 'verify-published-metadata' 'dotnet' $metadataArguments)

$publishedHelperDirectory = Join-Path $publishDirectory 'managed-browser-helper'
$helperGateArguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', $helperVerificationScript,
    '-ManifestSource', $helperManifestSource,
    '-PackageDirectory', $publishedHelperDirectory,
    '-HelperSourceDirectory', $helperSourceDirectory
)
[void](Invoke-RecordedCommand 'verify-managed-browser-helper' 'powershell' $helperGateArguments)

if ($null -ne $previousPath) {
    foreach ($previousAsset in $validatedPreviousAssets) {
        $name = [IO.Path]::GetFileName($previousAsset)
        Assert-ExistingPathIsNotReparsePoint $previousAsset 'Previous release asset'
        $destination = Join-Path $outputPath $name
        if (Test-Path -LiteralPath $destination) {
            throw "Previous release asset would overwrite an output file: $name"
        }
        [IO.File]::Copy($previousAsset, $destination, $false)
    }
}

$vpkArguments = @(
    'tool', 'run', 'vpk', 'pack',
    '--packId', $acceptancePackageId,
    '--packVersion', $Version,
    '--packDir', $publishDirectory,
    '--mainExe', $productMainExecutableName,
    '--packTitle', $productDisplayName,
    '--icon', $productIconPath,
    '--channel', $acceptanceChannel,
    '--runtime', 'win-x64',
    '--outputDir', $outputPath,
    '--releaseNotes', $effectiveReleaseNotesPath
)
[void](Invoke-RecordedCommand 'vpk-pack' 'dotnet' $vpkArguments)

$releaseFeedPath = Join-Path $outputPath "releases.$acceptanceChannel.json"
$releaseFeedFiles = @([IO.Directory]::EnumerateFiles(
        $outputPath, 'releases.*.json', [IO.SearchOption]::TopDirectoryOnly))
if ($releaseFeedFiles.Count -ne 1 -or
    [IO.Path]::GetFileName($releaseFeedFiles[0]) -cne [IO.Path]::GetFileName($releaseFeedPath) -or
    -not (Test-Path -LiteralPath $releaseFeedPath -PathType Leaf)) {
    throw "Expected release feed is missing: $releaseFeedPath"
}
Assert-ExistingPathIsNotReparsePoint $releaseFeedPath 'Release feed'
try { $releaseFeed = [IO.File]::ReadAllText($releaseFeedPath) | ConvertFrom-Json }
catch { throw 'Generated release feed is not valid JSON.' }
if ($null -eq $releaseFeed.PSObject.Properties['Assets']) {
    throw 'Generated release feed has no Assets property.'
}
$assets = @($releaseFeed.Assets)
if ($assets.Count -eq 0) { throw 'Release feed contains no assets.' }
$referencedReleasePackages = New-Object `
    'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
$referencedFullPackages = New-Object `
    'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
$currentReleasePackagesByType = New-Object `
    'Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
$targetAssetCount = 0
foreach ($asset in $assets) {
    foreach ($propertyName in @('PackageId', 'Version', 'Type', 'FileName', 'SHA256', 'Size')) {
        if ($null -eq $asset -or $null -eq $asset.PSObject.Properties[$propertyName]) {
            throw "Release feed asset is missing $propertyName."
        }
    }
    if ([string]$asset.PackageId -cne $acceptancePackageId) {
        throw 'Release feed contains a foreign package ID.'
    }
    $assetType = [string]$asset.Type
    if ($assetType -cnotin @('Full', 'Delta')) {
        throw "Release feed contains an unknown package type: $assetType"
    }
    $fileName = [string]$asset.FileName
    if (-not (Test-SafeNupkgFileName $fileName)) {
        throw 'Release feed contains an unsafe asset file name.'
    }
    if ($referencedReleasePackages.ContainsKey($fileName)) {
        throw "Release feed contains duplicate asset file name: $fileName"
    }
    $assetPath = Join-Path $outputPath $fileName
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Release feed asset is missing: $fileName"
    }
    Assert-ExistingPathIsNotReparsePoint $assetPath 'Release asset'
    $assetInfo = [IO.FileInfo]::new($assetPath)
    $declaredHash = [string]$asset.SHA256
    if ($declaredHash -cnotmatch '^[0-9A-Fa-f]{64}$') {
        throw "Release feed contains an invalid SHA-256: $fileName"
    }
    try { $declaredSize = [long]$asset.Size }
    catch { throw "Release feed contains an invalid size: $fileName" }
    if ($declaredSize -lt 0 -or $declaredSize -ne $assetInfo.Length) {
        throw "Release feed size differs from the asset: $fileName"
    }
    $actualHash = Get-LowerSha256 $assetPath
    if ($declaredHash.ToLowerInvariant() -cne $actualHash) {
        throw "Release feed SHA-256 differs from the asset: $fileName"
    }
    $validatedReleasePackage = [pscustomobject]@{
            FileName = $fileName
            FullPath = $assetPath
        }
    $referencedReleasePackages.Add($fileName, $validatedReleasePackage)
    if ($assetType -ceq 'Full') {
        $referencedFullPackages.Add($fileName, $validatedReleasePackage)
    }
    if ([string]$asset.Version -ceq $Version) {
        $targetAssetCount++
        if ($currentReleasePackagesByType.ContainsKey($assetType)) {
            throw "Release feed contains duplicate $assetType package for this version."
        }
        $currentReleasePackagesByType.Add($assetType, $assetPath)
        if ($null -eq $asset.PSObject.Properties['NotesMarkdown'] -or
            [string]$asset.NotesMarkdown -cne $expectedReleaseNotesText) {
            throw "Release feed did not preserve release notes for the current $assetType package."
        }
    }
}
if ($targetAssetCount -eq 0) {
    throw 'Release feed does not contain the requested acceptance package and version.'
}
foreach ($packageFile in [IO.Directory]::EnumerateFiles(
        $outputPath, '*.nupkg', [IO.SearchOption]::TopDirectoryOnly)) {
    $actualName = [IO.Path]::GetFileName($packageFile)
    if (-not $referencedReleasePackages.ContainsKey($actualName)) {
        throw "Output contains an unreferenced nupkg: $actualName"
    }
    if ($actualName -cne [string]$referencedReleasePackages[$actualName].FileName) {
        throw "Output package casing differs from the release feed: $actualName"
    }
}
if (-not $currentReleasePackagesByType.ContainsKey('Full')) {
    throw 'Release feed must contain exactly one full package for this version.'
}
$hasCurrentDelta = $currentReleasePackagesByType.ContainsKey('Delta')
if ($null -ne $previousPath -and -not $hasCurrentDelta) {
    throw 'A release built from previous assets must contain exactly one delta package for this version.'
}
if ($null -eq $previousPath -and $hasCurrentDelta) {
    throw 'A first release without previous assets must not contain a delta package.'
}

$setupFiles = @([IO.Directory]::EnumerateFiles($outputPath, "$acceptancePackageId*Setup*.exe",
    [IO.SearchOption]::TopDirectoryOnly))
$portableFiles = @([IO.Directory]::EnumerateFiles($outputPath, "$acceptancePackageId*Portable*.zip",
    [IO.SearchOption]::TopDirectoryOnly))
if ($setupFiles.Count -ne 1 -or $portableFiles.Count -ne 1) {
    throw 'Setup, Portable ZIP, or full nupkg acceptance output is missing.'
}

$assetCatalogPath = Join-Path $outputPath "assets.$acceptanceChannel.json"
$assetCatalogFiles = @([IO.Directory]::EnumerateFiles(
        $outputPath, 'assets.*.json', [IO.SearchOption]::TopDirectoryOnly))
if ($assetCatalogFiles.Count -ne 1 -or
    [IO.Path]::GetFileName($assetCatalogFiles[0]) -cne [IO.Path]::GetFileName($assetCatalogPath) -or
    -not (Test-Path -LiteralPath $assetCatalogPath -PathType Leaf)) {
    throw "Expected asset catalog is missing: $assetCatalogPath"
}
Assert-ExistingPathIsNotReparsePoint $assetCatalogPath 'Asset catalog'
try { $parsedAssetCatalog = [IO.File]::ReadAllText($assetCatalogPath) | ConvertFrom-Json }
catch { throw 'Generated asset catalog is not valid JSON.' }
if ($parsedAssetCatalog -isnot [array]) {
    throw 'Generated asset catalog must be a JSON array.'
}
$assetCatalogEntries = @($parsedAssetCatalog)
$expectedCatalogFiles = New-Object `
    'Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
$expectedCatalogFiles.Add('Installer', [IO.Path]::GetFileName($setupFiles[0]))
$expectedCatalogFiles.Add('Portable', [IO.Path]::GetFileName($portableFiles[0]))
foreach ($currentPackage in $currentReleasePackagesByType.GetEnumerator()) {
    $expectedCatalogFiles.Add(
        $currentPackage.Key,
        [IO.Path]::GetFileName($currentPackage.Value))
}
if ($assetCatalogEntries.Count -ne $expectedCatalogFiles.Count) {
    throw 'Generated asset catalog does not match the current release asset count.'
}
$catalogTypes = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$catalogFileNames = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($catalogEntry in $assetCatalogEntries) {
    if ($null -eq $catalogEntry) {
        throw 'Generated asset catalog contains a null asset.'
    }
    $propertyNames = @($catalogEntry.PSObject.Properties | ForEach-Object { $_.Name })
    if ($propertyNames.Count -ne 2 -or
        -not ($propertyNames -ccontains 'RelativeFileName') -or
        -not ($propertyNames -ccontains 'Type')) {
        throw 'Generated asset catalog contains missing or unknown properties.'
    }
    $catalogType = [string]$catalogEntry.Type
    $relativeFileName = [string]$catalogEntry.RelativeFileName
    if (-not $expectedCatalogFiles.ContainsKey($catalogType)) {
        throw "Generated asset catalog contains an unknown asset type: $catalogType"
    }
    if (-not $catalogTypes.Add($catalogType)) {
        throw "Generated asset catalog contains a duplicate asset type: $catalogType"
    }
    if ([string]::IsNullOrWhiteSpace($relativeFileName) -or
        $relativeFileName -cne [IO.Path]::GetFileName($relativeFileName) -or
        $relativeFileName -cne $expectedCatalogFiles[$catalogType] -or
        -not $catalogFileNames.Add($relativeFileName)) {
        throw "Generated asset catalog contains an unsafe or unexpected reference: $relativeFileName"
    }
    $catalogAssetPath = Join-Path $outputPath $relativeFileName
    if (-not (Test-Path -LiteralPath $catalogAssetPath -PathType Leaf)) {
        throw "Generated asset catalog references a missing asset: $relativeFileName"
    }
    Assert-ExistingPathIsNotReparsePoint $catalogAssetPath 'Asset catalog reference'
    if ([IO.FileInfo]::new($catalogAssetPath).Length -le 0) {
        throw "Generated asset catalog references an empty asset: $relativeFileName"
    }
}

$releasesIndexPath = Join-Path $outputPath "RELEASES-$acceptanceChannel"
$releasesIndexFiles = @([IO.Directory]::EnumerateFiles(
        $outputPath, 'RELEASES-*', [IO.SearchOption]::TopDirectoryOnly))
if ($releasesIndexFiles.Count -ne 1 -or
    [IO.Path]::GetFileName($releasesIndexFiles[0]) -cne [IO.Path]::GetFileName($releasesIndexPath) -or
    -not (Test-Path -LiteralPath $releasesIndexPath -PathType Leaf)) {
    throw "Expected RELEASES index is missing: $releasesIndexPath"
}
Assert-ExistingPathIsNotReparsePoint $releasesIndexPath 'RELEASES index'
try { $releasesLines = @([IO.File]::ReadAllLines($releasesIndexPath)) }
catch { throw 'Generated RELEASES index cannot be read.' }
if ($releasesLines.Count -ne $referencedFullPackages.Count -or $releasesLines.Count -eq 0) {
    throw 'Generated RELEASES index does not reference every full package exactly once.'
}
$indexedReleasePackages = New-Object `
    'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($releaseLine in $releasesLines) {
    if ($releaseLine -cnotmatch '^(?<sha1>[0-9A-F]{40}) (?<file>[^\s]+) (?<size>[0-9]+)$') {
        throw 'Generated RELEASES index contains an invalid line.'
    }
    $indexedFileName = [string]$Matches.file
    if (-not (Test-SafeNupkgFileName $indexedFileName) -or
        -not $referencedFullPackages.ContainsKey($indexedFileName) -or
        -not $indexedReleasePackages.Add($indexedFileName) -or
        $indexedFileName -cne [string]$referencedFullPackages[$indexedFileName].FileName) {
        throw "Generated RELEASES index contains an unsafe, foreign, or duplicate reference: $indexedFileName"
    }
    $indexedPath = [string]$referencedFullPackages[$indexedFileName].FullPath
    Assert-ExistingPathIsNotReparsePoint $indexedPath 'RELEASES index reference'
    try { $indexedSize = [long]$Matches.size }
    catch { throw "Generated RELEASES index contains an invalid size: $indexedFileName" }
    if ($indexedSize -le 0 -or $indexedSize -ne [IO.FileInfo]::new($indexedPath).Length) {
        throw "Generated RELEASES size differs from the asset: $indexedFileName"
    }
    if (([string]$Matches.sha1).ToLowerInvariant() -cne (Get-LowerSha1 $indexedPath)) {
        throw "Generated RELEASES SHA-1 differs from the asset: $indexedFileName"
    }
}

$manifestAssetPaths = @(
    @($releaseFeedPath) +
    @($assetCatalogPath) +
    @($releasesIndexPath) +
    @($referencedReleasePackages.Values | ForEach-Object { $_.FullPath }) +
    @($setupFiles) +
    @($portableFiles) |
        Sort-Object { [IO.Path]::GetFileName($_) }
)
$manifestAssetSet = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($assetPath in $manifestAssetPaths) {
    if (-not $manifestAssetSet.Add([IO.Path]::GetFullPath($assetPath))) {
        throw "Duplicate upload asset path: $assetPath"
    }
}
foreach ($candidate in [IO.Directory]::EnumerateFiles(
        $outputPath, '*', [IO.SearchOption]::TopDirectoryOnly)) {
    if (-not $manifestAssetSet.Contains([IO.Path]::GetFullPath($candidate))) {
        throw "Output contains a foreign asset that is excluded from the upload manifest: $candidate"
    }
}
$manifestLines = foreach ($assetPath in $manifestAssetPaths) {
    Assert-ExistingPathIsNotReparsePoint $assetPath 'Upload asset'
    $hash = Get-LowerSha256 $assetPath
    "$hash  $([IO.Path]::GetFileName($assetPath))"
}
$manifestPath = Join-Path $outputPath 'upload-manifest.sha256'
try {
    [IO.File]::WriteAllText($manifestPath, (($manifestLines -join "`n") + "`n"), $utf8NoBom)
}
catch {
    throw "Unable to write upload manifest '$manifestPath': $($_.Exception.Message)"
}

$readmePath = Join-Path $outputPath 'LOCAL-OTA-ACCEPTANCE-README.zh-CN.txt'
$readmeLabels = ('{' +
    '"heading":"\u672c\u673a OTA \u9a8c\u6536\u5305",' +
    '"version":"\u7248\u672c",' +
    '"package":"\u5305 ID",' +
    '"channel":"\u6e20\u9053",' +
    '"feed":"\u66f4\u65b0\u6e90",' +
    '"warning":"\u6b64\u76ee\u5f55\u4ec5\u7528\u4e8e\u672a\u7b7e\u540d\u7684\u672c\u673a OTA \u9a8c\u6536\uff0c\u4e0d\u662f\u751f\u4ea7\u53d1\u5e03\u7269\u3002",' +
    '"noUpload":"\u811a\u672c\u4e0d\u4f1a\u8fde\u63a5\u6216\u4e0a\u4f20\u5230\u4efb\u4f55\u670d\u52a1\u5668\u3002\u4e0a\u4f20\u524d\u5e94\u518d\u6b21\u6838\u5bf9 upload-manifest.sha256\u3002",' +
    '"log":"\u547d\u4ee4\u6267\u884c\u8bb0\u5f55"' +
    '}') | ConvertFrom-Json
$readme = @"
$($readmeLabels.heading)

$($readmeLabels.version): $Version
$($readmeLabels.package): $acceptancePackageId
$($readmeLabels.channel): $acceptanceChannel
$($readmeLabels.feed): $acceptanceFeed

$($readmeLabels.warning)
$($readmeLabels.noUpload)
$($readmeLabels.log): .build\logs\commands.jsonl
"@
try { [IO.File]::WriteAllText($readmePath, $readme.Replace("`r`n", "`n"), $utf8NoBom) }
catch { throw "Unable to write release description '$readmePath': $($_.Exception.Message)" }

Write-Output "Local OTA acceptance release created: $outputPath"
