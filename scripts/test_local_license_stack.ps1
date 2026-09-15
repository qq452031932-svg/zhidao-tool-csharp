[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'ProductIdentity.ps1')
$productIdentity = Get-ProductIdentity -Path (Join-Path $repositoryRoot 'eng\ProductIdentity.props')
if ([string]::IsNullOrWhiteSpace($productIdentity.ProductMainAssemblyName) -or
    $productIdentity.ProductMainDllName -cne "$($productIdentity.ProductMainAssemblyName).dll") {
    throw 'Product identity does not define the expected main application assembly.'
}
$runScript = Join-Path $PSScriptRoot 'run_local_license_stack.ps1'
$stopScript = Join-Path $PSScriptRoot 'stop_local_license_stack.ps1'
$harnessProject = Join-Path $repositoryRoot 'tools\license_client_harness\SuperPan.Licensing.AcceptanceHarness.csproj'
$harnessAssembly = Join-Path $repositoryRoot 'tools\license_client_harness\bin\Debug\net8.0-windows\SuperPan.Licensing.AcceptanceHarness.dll'
$runtimeRoot = Join-Path $repositoryRoot '.tmp\license-local'
$clientConfig = Join-Path $runtimeRoot 'client.json'
$adminPasswordPath = Join-Path $runtimeRoot 'admin-password.txt'
$acceptanceOutput = Join-Path $repositoryRoot '.artifacts\license-acceptance'
$acceptanceAssembly = Join-Path $acceptanceOutput $productIdentity.ProductMainDllName
$adminOrigin = 'http://127.0.0.1:19090'
$productFixture = '11111111-2222-4333-8444-555555555555'
$userAStore = Join-Path $runtimeRoot 'acceptance-user-a'
$userBStore = Join-Path $runtimeRoot 'acceptance-user-b'
$renewalStore = Join-Path $runtimeRoot 'acceptance-renewal-original'
$rebindStore = Join-Path $runtimeRoot 'acceptance-renewal-rebound'
$rebindFixture = '66666666-7777-4888-8999-aaaaaaaaaaaa'
$offlineRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot ('.tmp\license-e2e-offline-' + $PID)))
$offlineConfig = Join-Path $offlineRoot 'client.json'
$offlineStore = Join-Path $offlineRoot 'store'
$stackStarted = $false
$adminPassword = $null
$rotatedPassword = $null
$card = $null
$bodyFailure = $null
$cleanupFailures = New-Object Collections.Generic.List[string]
$successSummary = $null

function Invoke-AdminMutation {
    param(
        [Parameter(Mandatory = $true)][Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [Parameter(Mandatory = $true)][string]$Csrf,
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Body
    )
    $headers = @{
        Origin = $adminOrigin
        'X-CSRF-Token' = $Csrf
        'Idempotency-Key' = [Guid]::NewGuid().ToString('D')
    }
    return Invoke-RestMethod -Method $Method -Uri ($adminOrigin + $Path) -WebSession $Session -Headers $headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Compress -Depth 6) -TimeoutSec 15
}

function Invoke-Harness {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string]$ProductId,
        [Parameter(Mandatory = $true)][string]$StoreRoot,
        [string]$CardInput,
        [string]$ConfigPath = $clientConfig,
        [string]$MachineFixture = $productFixture,
        [string]$UtcNow
    )
    $arguments = @(
        $Command,
        '--config', $ConfigPath,
        '--product-id', $ProductId,
        '--store-root', $StoreRoot,
        '--machine-fixture', $MachineFixture
    )
    if (-not [string]::IsNullOrWhiteSpace($UtcNow)) { $arguments += @('--utc-now', $UtcNow) }
    if ($Command -eq 'activate') {
        $arguments += '--card-stdin'
        $startInfo = New-Object Diagnostics.ProcessStartInfo
        $startInfo.FileName = 'dotnet'
        $quotedAssembly = '"' + $harnessAssembly.Replace('"', '\"') + '"'
        $quotedArguments = @($arguments | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' })
        $startInfo.Arguments = $quotedAssembly + ' ' + ($quotedArguments -join ' ')
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $process = New-Object Diagnostics.Process
        $process.StartInfo = $startInfo
        if (-not $process.Start()) { throw 'Acceptance harness process did not start.' }
        try {
            $process.StandardInput.WriteLine($CardInput)
            $process.StandardInput.Close()
            $output = $process.StandardOutput.ReadToEnd().TrimEnd("`r", "`n")
            $errorOutput = $process.StandardError.ReadToEnd()
            $process.WaitForExit()
            $exitCode = $process.ExitCode
        }
        finally { $process.Dispose() }
        if (-not [string]::IsNullOrWhiteSpace($errorOutput)) { throw 'Acceptance harness wrote unexpected diagnostics.' }
    }
    else {
        $arguments = @($harnessAssembly) + $arguments + @('--operation', 'Transfer')
        $output = & dotnet @arguments
        $exitCode = $LASTEXITCODE
    }
    $lines = @($output)
    if ($lines.Count -ne 1) { throw 'Acceptance harness returned an invalid output shape.' }
    $result = $lines[0] | ConvertFrom-Json
    return [pscustomobject]@{ ExitCode = $exitCode; Result = $result }
}

function Assert-HarnessAllowed {
    param([Parameter(Mandatory = $true)]$Invocation, [Parameter(Mandatory = $true)][string]$Stage)
    if ($Invocation.ExitCode -ne 0 -or -not [bool]$Invocation.Result.authorized -or [string]$Invocation.Result.code -ne 'ok') {
        $safeCode = [string]$Invocation.Result.code
        if ($safeCode -notin @('ok','invalid_request','unsupported_protocol','license_not_found','license_expired','app_not_allowed','app_disabled','machine_mismatch','rate_limited','service_unavailable','activation_required','authorization_unavailable','harness_failed')) { $safeCode = 'unrecognized' }
        throw "$Stage did not authorize (exit=$($Invocation.ExitCode), code=$safeCode)."
    }
}

function Assert-HarnessRejected {
    param(
        [Parameter(Mandatory = $true)]$Invocation,
        [Parameter(Mandatory = $true)][string]$ExpectedCode,
        [Parameter(Mandatory = $true)][string]$Stage
    )
    if ($Invocation.ExitCode -ne 3 -or [bool]$Invocation.Result.authorized -or [bool]$Invocation.Result.used_cached_lease -or [string]$Invocation.Result.code -ne $ExpectedCode) {
        throw "$Stage did not return the expected signed rejection."
    }
}

try {
    if (Test-Path -LiteralPath $offlineRoot) { throw 'Offline acceptance snapshot root already exists.' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runScript | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Local license stack start failed with exit code $LASTEXITCODE."
    }
    $stackStarted = $true

    if (-not (Test-Path -LiteralPath $harnessProject -PathType Leaf)) {
        throw 'Acceptance harness project is missing.'
    }

    & dotnet build $harnessProject --no-restore | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $harnessAssembly -PathType Leaf)) {
        throw 'Acceptance harness build failed.'
    }

    $bootstrap = Invoke-RestMethod -Method Get -Uri ($adminOrigin + '/admin/api/session/bootstrap') -SessionVariable adminSession -TimeoutSec 10
    $adminPassword = (Get-Content -LiteralPath $adminPasswordPath -Raw -Encoding utf8).Trim()
    $loginHeaders = @{ Origin = $adminOrigin; 'X-CSRF-Token' = [string]$bootstrap.csrf_token }
    $loginBody = @{ username = 'admin'; password = $adminPassword } | ConvertTo-Json -Compress
    $login = Invoke-RestMethod -Method Post -Uri ($adminOrigin + '/admin/api/session/login') -WebSession $adminSession -Headers $loginHeaders -ContentType 'application/json' -Body $loginBody -TimeoutSec 10
    $csrf = [string]$login.csrf_token
    if ([bool]$login.must_change_password) {
        $rotatedPassword = 'Local-' + [Guid]::NewGuid().ToString('N') + '!9a'
        $changed = Invoke-RestMethod -Method Post -Uri ($adminOrigin + '/admin/api/session/password') -WebSession $adminSession -Headers @{ Origin = $adminOrigin; 'X-CSRF-Token' = $csrf } -ContentType 'application/json' -Body (@{ current_password = $adminPassword; new_password = $rotatedPassword } | ConvertTo-Json -Compress) -TimeoutSec 10
        $csrf = [string]$changed.csrf_token
    }
    if ([string]::IsNullOrWhiteSpace($csrf)) { throw 'Real admin login did not return a CSRF token.' }

    $softwareA = Invoke-AdminMutation -Session $adminSession -Csrf $csrf -Method Post -Path '/admin/api/license/software' -Body @{ name = 'Acceptance A' }
    $softwareB = Invoke-AdminMutation -Session $adminSession -Csrf $csrf -Method Post -Path '/admin/api/license/software' -Body @{ name = 'Acceptance B' }
    $package = Invoke-AdminMutation -Session $adminSession -Csrf $csrf -Method Post -Path '/admin/api/license/packages' -Body @{ name = 'Acceptance Package'; software_ids = @([string]$softwareA.id, [string]$softwareB.id) }
    $generated = Invoke-AdminMutation -Session $adminSession -Csrf $csrf -Method Post -Path '/admin/api/license/cards/generate' -Body @{ package_id = [string]$package.id; duration_days = 30; count = 1; default_note = 'local-e2e-note' }
    if (@($generated.items).Count -ne 1) { throw 'Card generation count was not one.' }
    $card = [string]$generated.items[0].plaintext
    if ([string]::IsNullOrWhiteSpace($card) -or [string]$generated.items[0].card.note -ne 'local-e2e-note') {
        throw 'Generated card metadata did not match the acceptance request.'
    }

    $activationA = Invoke-Harness -Command activate -ProductId ([string]$softwareA.id) -StoreRoot $userAStore -CardInput $card
    Assert-HarnessAllowed -Invocation $activationA -Stage 'Software A activation'
    $authorizationB = Invoke-Harness -Command authorize -ProductId ([string]$softwareB.id) -StoreRoot $userAStore
    Assert-HarnessAllowed -Invocation $authorizationB -Stage 'Software B shared-store authorization'
    $activationB = Invoke-Harness -Command activate -ProductId ([string]$softwareB.id) -StoreRoot $userBStore -CardInput $card
    Assert-HarnessAllowed -Invocation $activationB -Stage 'Same-machine empty-store activation'

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build_local_license_acceptance.ps1') -OutputDirectory $acceptanceOutput | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $acceptanceAssembly -PathType Leaf)) {
        throw 'The WPF acceptance build failed.'
    }
    $previousLocalAppData = $env:LOCALAPPDATA
    try {
        $env:LOCALAPPDATA = Join-Path $runtimeRoot 'acceptance-wpf-profile'
        & dotnet $acceptanceAssembly '--license-acceptance-smoke' ("--license-acceptance-config=" + $clientConfig) | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "The WPF acceptance smoke failed with exit code $LASTEXITCODE." }
    }
    finally { $env:LOCALAPPDATA = $previousLocalAppData }

    Invoke-AdminMutation -Session $adminSession -Csrf $csrf -Method Put -Path ("/admin/api/license/packages/{0}/members" -f [string]$package.id) -Body @{ software_ids = @([string]$softwareA.id) } | Out-Null
    $notAllowed = Invoke-Harness -Command authorize -ProductId ([string]$softwareB.id) -StoreRoot $userAStore
    Assert-HarnessRejected -Invocation $notAllowed -ExpectedCode 'app_not_allowed' -Stage 'Removed package member'

    Invoke-AdminMutation -Session $adminSession -Csrf $csrf -Method Put -Path ("/admin/api/license/packages/{0}/members" -f [string]$package.id) -Body @{ software_ids = @([string]$softwareA.id, [string]$softwareB.id) } | Out-Null
    $readded = Invoke-Harness -Command authorize -ProductId ([string]$softwareB.id) -StoreRoot $userAStore
    Assert-HarnessAllowed -Invocation $readded -Stage 'Re-added package member'

    Invoke-AdminMutation -Session $adminSession -Csrf $csrf -Method Post -Path ("/admin/api/license/software/{0}/disable" -f [string]$softwareB.id) -Body @{} | Out-Null
    $disabled = Invoke-Harness -Command authorize -ProductId ([string]$softwareB.id) -StoreRoot $userAStore
    Assert-HarnessRejected -Invocation $disabled -ExpectedCode 'app_disabled' -Stage 'Disabled software'
    Invoke-AdminMutation -Session $adminSession -Csrf $csrf -Method Post -Path ("/admin/api/license/software/{0}/enable" -f [string]$softwareB.id) -Body @{} | Out-Null
    $reenabled = Invoke-Harness -Command authorize -ProductId ([string]$softwareB.id) -StoreRoot $userAStore
    Assert-HarnessAllowed -Invocation $reenabled -Stage 'Re-enabled software'

    $renewGenerated = Invoke-AdminMutation -Session $adminSession -Csrf $csrf -Method Post -Path '/admin/api/license/cards/generate' -Body @{ package_id = [string]$package.id; duration_days = 30; count = 1; default_note = 'local-e2e-renewal' }
    if (@($renewGenerated.items).Count -ne 1) { throw 'Renewal card generation count was not one.' }
    $renewCard = [string]$renewGenerated.items[0].plaintext
    $renewCardID = [string]$renewGenerated.items[0].card.id
    $renewActivated = Invoke-Harness -Command activate -ProductId ([string]$softwareA.id) -StoreRoot $renewalStore -CardInput $renewCard
    Assert-HarnessAllowed -Invocation $renewActivated -Stage 'Renewal card activation'
    $cardsBeforeRenewal = Invoke-RestMethod -Method Get -Uri ($adminOrigin + '/admin/api/license/cards?page=1&page_size=100') -WebSession $adminSession -TimeoutSec 10
    $beforeRecord = @($cardsBeforeRenewal.items | Where-Object { [string]$_.id -eq $renewCardID })
    if ($beforeRecord.Count -ne 1 -or $null -eq $beforeRecord[0].expires_at) { throw 'Renewal card precondition was not observable.' }
    $expiresBefore = [DateTimeOffset]$beforeRecord[0].expires_at
    Invoke-AdminMutation -Session $adminSession -Csrf $csrf -Method Post -Path ("/admin/api/license/cards/{0}/renew" -f $renewCardID) -Body @{ duration_days = 30 } | Out-Null
    $cardsAfterRenewal = Invoke-RestMethod -Method Get -Uri ($adminOrigin + '/admin/api/license/cards?page=1&page_size=100') -WebSession $adminSession -TimeoutSec 10
    $afterRecord = @($cardsAfterRenewal.items | Where-Object { [string]$_.id -eq $renewCardID })
    if ($afterRecord.Count -ne 1 -or $null -eq $afterRecord[0].expires_at) { throw 'Renewal card result was not observable.' }
    $expiresAfter = [DateTimeOffset]$afterRecord[0].expires_at
    $renewalDays = ($expiresAfter - $expiresBefore).TotalDays
    if ($renewalDays -lt 29.99 -or $renewalDays -gt 30.01) { throw 'Renewal did not extend the server expiry by approximately 30 days.' }
    Invoke-AdminMutation -Session $adminSession -Csrf $csrf -Method Post -Path ("/admin/api/license/cards/{0}/unbind" -f $renewCardID) -Body @{} | Out-Null
    $rebound = Invoke-Harness -Command activate -ProductId ([string]$softwareA.id) -StoreRoot $rebindStore -CardInput $renewCard -MachineFixture $rebindFixture
    Assert-HarnessAllowed -Invocation $rebound -Stage 'Admin-unbound card reactivation'
    $renewCard = $null

    $refreshStartedUtc = [DateTimeOffset]::UtcNow
    $refreshedA = Invoke-Harness -Command authorize -ProductId ([string]$softwareA.id) -StoreRoot $userAStore
    $refreshCompletedUtc = [DateTimeOffset]::UtcNow
    Assert-HarnessAllowed -Invocation $refreshedA -Stage 'Software A pre-snapshot refresh'
    $liveStoreFile = Join-Path $userAStore 'licenses.dat'
    if (-not (Test-Path -LiteralPath $liveStoreFile -PathType Leaf)) { throw 'The encrypted lease store is missing.' }
    $storeTextProbe = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($liveStoreFile))
    if ($storeTextProbe.Contains($card)) { throw 'The lease store exposed card plaintext.' }
    $storeTextProbe = $null
    [IO.Directory]::CreateDirectory($offlineRoot) | Out-Null
    Copy-Item -LiteralPath $userAStore -Destination $offlineStore -Recurse
    $publicConfig = Get-Content -LiteralPath $clientConfig -Raw -Encoding utf8 | ConvertFrom-Json
    $publicKeySource = [IO.Path]::GetFullPath((Join-Path $runtimeRoot ([string]$publicConfig.signing_public_key_pem)))
    Copy-Item -LiteralPath $publicKeySource -Destination (Join-Path $offlineRoot 'signing-public.pem')
    $offlineSettings = [ordered]@{ license_base_url = 'http://127.0.0.1:19091'; signing_key_id = [string]$publicConfig.signing_key_id; signing_public_key_pem = 'signing-public.pem'; acceptance_only = $true }
    [IO.File]::WriteAllText($offlineConfig, (($offlineSettings | ConvertTo-Json) + "`n"), (New-Object Text.UTF8Encoding($false)))

    Invoke-AdminMutation -Session $adminSession -Csrf $csrf -Method Delete -Path ("/admin/api/license/cards/{0}" -f [string]$generated.items[0].card.id) -Body @{} | Out-Null
    $deletedOnline = Invoke-Harness -Command authorize -ProductId ([string]$softwareA.id) -StoreRoot $userAStore
    Assert-HarnessRejected -Invocation $deletedOnline -ExpectedCode 'license_not_found' -Stage 'Deleted card online validation'

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $stopScript | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Local license stack pre-offline stop failed with exit code $LASTEXITCODE." }
    $stackStarted = $false
    $beforeLeaseExpiry = $refreshStartedUtc.AddMinutes(29).ToString('O')
    $afterLeaseExpiry = $refreshCompletedUtc.AddMinutes(31).ToString('O')
    $offlineAllowed = Invoke-Harness -Command authorize -ProductId ([string]$softwareA.id) -StoreRoot $offlineStore -ConfigPath $offlineConfig -UtcNow $beforeLeaseExpiry
    if ($offlineAllowed.ExitCode -ne 0 -or -not [bool]$offlineAllowed.Result.authorized -or -not [bool]$offlineAllowed.Result.used_cached_lease -or [string]$offlineAllowed.Result.code -ne 'service_unavailable') {
        throw 'The pre-expiry offline lease was not accepted.'
    }
    $offlineRejected = Invoke-Harness -Command authorize -ProductId ([string]$softwareA.id) -StoreRoot $offlineStore -ConfigPath $offlineConfig -UtcNow $afterLeaseExpiry
    if ($offlineRejected.ExitCode -ne 3 -or [bool]$offlineRejected.Result.authorized -or [bool]$offlineRejected.Result.used_cached_lease -or [string]$offlineRejected.Result.code -ne 'service_unavailable') {
        throw 'The expired offline lease was not rejected.'
    }

    $successSummary = 'Local license E2E stage passed: software=2 package=1 cards=2 user_stores=4 wpf_smoke=1 policy_transitions=4 renewal_checks=1 unbind_checks=1 delete_checks=1 offline_checks=2.'
}
catch {
    $bodyFailure = $_
}
finally {
    $card = $null
    $adminPassword = $null
    $rotatedPassword = $null

    try {
        if ($stackStarted) {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $stopScript | Out-Null
            if ($LASTEXITCODE -ne 0) {
                $cleanupFailures.Add("Local license stack cleanup failed with exit code $LASTEXITCODE.") | Out-Null
            }
        }
    }
    catch {
        $cleanupFailures.Add('Local license stack cleanup could not be invoked.') | Out-Null
    }

    try {
        $expectedOfflineRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot ('.tmp\license-e2e-offline-' + $PID)))
        if (Test-Path -LiteralPath $offlineRoot) {
            $offlineItem = Get-Item -LiteralPath $offlineRoot -Force
            if (-not [string]::Equals([IO.Path]::GetFullPath($offlineItem.FullName), $expectedOfflineRoot, [StringComparison]::OrdinalIgnoreCase) -or
                -not $offlineItem.PSIsContainer -or (($offlineItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                $cleanupFailures.Add('Offline acceptance cleanup refused an unexpected root.') | Out-Null
            }
            else {
                [IO.Directory]::Delete($expectedOfflineRoot, $true)
            }
        }
    }
    catch {
        $cleanupFailures.Add('Offline acceptance snapshot cleanup failed.') | Out-Null
    }

    try {
        $loopback = [Net.IPAddress]::Loopback
        $anyAddress = [Net.IPAddress]::Any
        $unexpectedListeners = @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object {
            $_.Port -in @(19090, 19091) -and ($_.Address.Equals($loopback) -or $_.Address.Equals($anyAddress))
        })
        if ($unexpectedListeners.Count -gt 0) {
            $cleanupFailures.Add('Local license cleanup left an acceptance port listening.') | Out-Null
        }
    }
    catch {
        $cleanupFailures.Add('Local license listener cleanup verification failed.') | Out-Null
    }
}

if ($null -ne $bodyFailure) {
    if ($cleanupFailures.Count -gt 0) {
        $cleanupFailure = New-Object InvalidOperationException -ArgumentList ('Cleanup failures: ' + ($cleanupFailures -join ' '))
        throw (New-Object AggregateException -ArgumentList 'Local license acceptance and cleanup both failed.', ([Exception[]]@($bodyFailure.Exception, $cleanupFailure)))
    }
    throw $bodyFailure
}
if ($cleanupFailures.Count -gt 0) {
    throw ('Local license acceptance cleanup failed: ' + ($cleanupFailures -join ' '))
}
Write-Output $successSummary
