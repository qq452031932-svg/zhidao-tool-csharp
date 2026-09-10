[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('boundary', 'task12', 'task13')]
    [string]$CommitGroup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

$groups = @{
    boundary = @(
        'docs/superpowers/plans/2026-09-10-production-license-service-deployment.md'
        'docs/testing/2026-09-10-license-production-source-manifest.md'
        'scripts/verify_license_change_boundary.ps1'
    )
    task12 = @(
        'scripts/build_local_license_acceptance.ps1'
        'src/BaiduBatchTransfer.App/App.xaml.cs'
        'src/BaiduBatchTransfer.App/AppBootstrap.cs'
        'src/BaiduBatchTransfer.App/AppStartupController.cs'
        'src/BaiduBatchTransfer.App/ApplicationShutdownCoordinator.cs'
        'src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj'
        'src/BaiduBatchTransfer.App/Features/Common/WorkspaceRunPanelViewModel.cs'
        'src/BaiduBatchTransfer.App/Features/Jobs/JobResultsViewModel.cs'
        'src/BaiduBatchTransfer.App/Features/Licensing/LicenseAcceptanceSmokeRunner.cs'
        'src/BaiduBatchTransfer.App/Features/Licensing/LicenseRuntimeAuthorizationState.cs'
        'src/BaiduBatchTransfer.App/Features/ShareAnalytics/ShareAnalyticsViewModel.cs'
        'src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceViewModel.cs'
        'src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs'
        'src/BaiduBatchTransfer.App/Services/StandaloneShareCoordinatorHostedService.cs'
        'src/BaiduBatchTransfer.App/Services/TransferCoordinatorHostedService.cs'
        'tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/AppStartupControllerTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/ApplicationShutdownCoordinatorTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/Features/JobResultsViewModelTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/Features/LicenseAcceptanceSmokeRunnerTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/Features/LicenseOperationBoundaryTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/Features/ShareAnalyticsViewModelTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/Features/ShareWorkspaceViewModelTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/Features/WorkspaceRunPanelViewModelTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/Services/StandaloneShareCoordinatorHostedServiceTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/Services/TransferCoordinatorHostedServiceTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/Updates/OtaBuildConfigurationTests.cs'
    )
    task13 = @(
        'docs/testing/2026-09-08-local-license-acceptance.md'
        'scripts/build_local_license_acceptance.ps1'
        'scripts/test_local_license_stack.ps1'
        'services/ota_admin/internal/webui/browser_test.go'
        'tests/SuperPan.Licensing.Client.Tests/LicenseAuthorizationGateTests.cs'
        'tools/license_client_harness/Program.cs'
        'tools/license_client_harness/SuperPan.Licensing.AcceptanceHarness.csproj'
        'tools/license_client_harness/packages.lock.json'
    )
}

$requiredNew = @{
    boundary = @(
        'docs/superpowers/plans/2026-09-10-production-license-service-deployment.md'
        'docs/testing/2026-09-10-license-production-source-manifest.md'
        'scripts/verify_license_change_boundary.ps1'
    )
    task12 = @(
        'scripts/build_local_license_acceptance.ps1'
        'src/BaiduBatchTransfer.App/Features/Licensing/LicenseAcceptanceSmokeRunner.cs'
        'src/BaiduBatchTransfer.App/Features/Licensing/LicenseRuntimeAuthorizationState.cs'
        'tests/BaiduBatchTransfer.App.Tests/Features/LicenseAcceptanceSmokeRunnerTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/Features/LicenseOperationBoundaryTests.cs'
        'tests/BaiduBatchTransfer.App.Tests/Services/StandaloneShareCoordinatorHostedServiceTests.cs'
    )
    task13 = @(
        'docs/testing/2026-09-08-local-license-acceptance.md'
        'scripts/test_local_license_stack.ps1'
        'tools/license_client_harness/Program.cs'
        'tools/license_client_harness/SuperPan.Licensing.AcceptanceHarness.csproj'
        'tools/license_client_harness/packages.lock.json'
    )
}

$mixedTask12 = @(
    'src/BaiduBatchTransfer.App/App.xaml.cs'
    'src/BaiduBatchTransfer.App/AppBootstrap.cs'
    'src/BaiduBatchTransfer.App/BaiduBatchTransfer.App.csproj'
    'src/BaiduBatchTransfer.App/Features/Common/WorkspaceRunPanelViewModel.cs'
    'src/BaiduBatchTransfer.App/Features/Jobs/JobResultsViewModel.cs'
    'src/BaiduBatchTransfer.App/Features/Shares/ShareWorkspaceViewModel.cs'
    'src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceViewModel.cs'
    'src/BaiduBatchTransfer.App/Services/TransferCoordinatorHostedService.cs'
    'tests/BaiduBatchTransfer.App.Tests/AppBootstrapTests.cs'
    'tests/BaiduBatchTransfer.App.Tests/Features/JobResultsViewModelTests.cs'
    'tests/BaiduBatchTransfer.App.Tests/Features/ShareWorkspaceViewModelTests.cs'
    'tests/BaiduBatchTransfer.App.Tests/Features/TransferWorkspaceViewModelTests.cs'
    'tests/BaiduBatchTransfer.App.Tests/Features/WorkspaceRunPanelViewModelTests.cs'
    'tests/BaiduBatchTransfer.App.Tests/Services/TransferCoordinatorHostedServiceTests.cs'
    'tests/BaiduBatchTransfer.App.Tests/Updates/OtaBuildConfigurationTests.cs'
)

function Test-HeadContainsPath([string]$Path) {
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        git cat-file -e ("HEAD:{0}" -f $Path) 2>$null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

function Get-StagedRawOutput {
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = 'git'
    $startInfo.Arguments = 'diff --cached --raw -z --no-abbrev'
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw 'Unable to start git staged-state inspection.' }
    try {
        $output = $process.StandardOutput.ReadToEnd()
        $errorOutput = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "Unable to read staged raw state: $errorOutput"
        }
        return $output
    }
    finally {
        $process.Dispose()
    }
}

Push-Location $repositoryRoot
try {
    $rawOutput = Get-StagedRawOutput
    $rawTokens = @($rawOutput.Split([char[]]@([char]0), [StringSplitOptions]::RemoveEmptyEntries))
    $changes = New-Object Collections.Generic.List[object]
    for ($index = 0; $index -lt $rawTokens.Count;) {
        $metadata = $rawTokens[$index++]
        if ($metadata -notmatch '^:([0-9]{6}) ([0-9]{6}) ([0-9a-f]{40}) ([0-9a-f]{40}) ([A-Z])([0-9]{0,3})?$') {
            throw "Unrecognized staged raw record: $metadata"
        }
        $oldMode = $Matches[1]
        $newMode = $Matches[2]
        $status = $Matches[5]
        if ($status -in @('R', 'C')) {
            if ($index + 1 -ge $rawTokens.Count) { throw "Incomplete staged $status record." }
            $sourcePath = $rawTokens[$index++]
            $destinationPath = $rawTokens[$index++]
            throw "Staged status '$status' is forbidden: $sourcePath -> $destinationPath"
        }
        if ($index -ge $rawTokens.Count) { throw "Staged status '$status' has no path." }
        $path = $rawTokens[$index++]
        if ($status -notin @('A', 'M')) {
            throw "Staged status '$status' is forbidden: $path"
        }
        $changes.Add([pscustomobject]@{
            Path = $path
            Status = $status
            OldMode = $oldMode
            NewMode = $newMode
        })
    }

    $stagedPaths = @($changes | ForEach-Object { $_.Path })
    if ($stagedPaths.Count -eq 0) {
        throw "Commit group '$CommitGroup' has no staged paths."
    }

    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $groups[$CommitGroup]) { [void]$allowed.Add($path) }
    $requiredNewSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $requiredNew[$CommitGroup]) { [void]$requiredNewSet.Add($path) }
    $stagedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $stagedPaths) { [void]$stagedSet.Add($path) }
    $outside = @($stagedPaths | Where-Object { -not $allowed.Contains($_) })
    if ($outside.Count -gt 0) {
        throw "Staged paths outside '$CommitGroup': $($outside -join ', ')"
    }

    $indexRecords = New-Object Collections.Generic.List[object]
    foreach ($line in @(git ls-files --stage)) {
        if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect index entries.' }
        if ($line -notmatch '^([0-9]{6}) ([0-9a-f]{40}) ([0-3])\t(.+)$') {
            throw "Unrecognized index entry: $line"
        }
        $indexRecords.Add([pscustomobject]@{
            Mode = $Matches[1]
            ObjectId = $Matches[2]
            Stage = [int]$Matches[3]
            Path = $Matches[4]
        })
    }

    foreach ($change in $changes) {
        $entries = @($indexRecords | Where-Object {
            [StringComparer]::Ordinal.Equals($_.Path, $change.Path)
        })
        if ($entries.Count -ne 1 -or $entries[0].Stage -ne 0) {
            throw "Staged path must have exactly one stage-0 index entry: $($change.Path)"
        }
        if ($entries[0].Mode -notin @('100644', '100755')) {
            throw "Staged path mode is forbidden: $($entries[0].Mode) $($change.Path)"
        }
        $headContainsPath = Test-HeadContainsPath $change.Path
        if (-not $headContainsPath -and -not $requiredNewSet.Contains($change.Path)) {
            throw "Staged add is not declared as a required new path: $($change.Path)"
        }
        $expectedStatus = if ($headContainsPath) { 'M' } else { 'A' }
        if ($change.Status -ne $expectedStatus) {
            throw "Staged status '$($change.Status)' does not match HEAD state: $($change.Path)"
        }
    }

    $missingNew = New-Object Collections.Generic.List[string]
    foreach ($path in $requiredNew[$CommitGroup]) {
        $entries = @($indexRecords | Where-Object {
            [StringComparer]::Ordinal.Equals($_.Path, $path)
        })
        if ($entries.Count -ne 1 -or $entries[0].Stage -ne 0) {
            $missingNew.Add($path)
            continue
        }
        if (-not (Test-HeadContainsPath $path) -and -not $stagedSet.Contains($path)) {
            $missingNew.Add($path)
        }
    }
    if ($missingNew.Count -gt 0) {
        throw "Required new paths are absent from the index: $($missingNew -join ', ')"
    }

    $forbiddenPathPattern = '(^|/)(\.tmp|\.artifacts|bin|obj|runtime|secrets?|private[-_]?keys?)(/|$)|\.(db|sqlite|sqlite3|pem|key|pfx|p12)$'
    $forbiddenPaths = @($stagedPaths | Where-Object { $_ -match $forbiddenPathPattern })
    if ($forbiddenPaths.Count -gt 0) {
        throw "Runtime, secret, or build paths are staged: $($forbiddenPaths -join ', ')"
    }

    if ($CommitGroup -eq 'task12') {
        foreach ($path in $mixedTask12) {
            if (-not $stagedSet.Contains($path)) { continue }
            $indexEntry = @($indexRecords | Where-Object {
                [StringComparer]::Ordinal.Equals($_.Path, $path)
            })[0]
            $indexHash = $indexEntry.ObjectId
            $worktreeHash = (git hash-object -- $path).Trim()
            if ($LASTEXITCODE -ne 0) { throw "Unable to hash mixed path '$path'." }
            if ($indexHash -ceq $worktreeHash) {
                throw "Mixed path was staged as the whole working-copy file instead of reviewed hunks: $path"
            }
        }

        $changedLines = @(git diff --cached --unified=0 -- @($groups.task12) |
            Where-Object { $_ -match '^[+-](?![+-])' })
        $forbiddenHunkPattern = 'ManagedBrowserHelperPackageDirectory|HelperSourceDirectory|ITransferTargetDirectory|transferTargetDirectoryPicker|TargetDirectoryPicker|ShareAnalyticsPreference|startup-error\.txt|DirectBackground|PendingRowRuntimeStatus|WorkspaceLog|LocalResultExport|ReplacementWorkbook|ManagedChromium|OtaAcceptanceBuild|OtaAcceptanceFeedPath|AcceptanceBuildDerivesDedicatedPackageId'
        $unrelated = @($changedLines | Where-Object { $_ -match $forbiddenHunkPattern })
        if ($unrelated.Count -gt 0) {
            throw "A staged Task 12 hunk matches an unrelated-work marker: $($unrelated[0])"
        }
    }

    Write-Output "License change boundary '$CommitGroup' passed for $($stagedPaths.Count) staged path(s)."
}
finally {
    Pop-Location
}
