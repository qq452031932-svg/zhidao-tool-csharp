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
        'tests/BaiduBatchTransfer.App.Tests/Features/TransferRecoveryAuthorizationSourceTests.cs'
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
)

Push-Location $repositoryRoot
try {
    $stagedPaths = @(git diff --cached --name-only --diff-filter=ACMRDTUXB)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read the staged path set.' }
    $stagedPaths = @($stagedPaths | ForEach-Object { $_.Replace('\', '/') } | Where-Object { $_ })
    if ($stagedPaths.Count -eq 0) {
        throw "Commit group '$CommitGroup' has no staged paths."
    }

    $allowed = @{};
    foreach ($path in $groups[$CommitGroup]) { $allowed[$path] = $true }
    $outside = @($stagedPaths | Where-Object { -not $allowed.ContainsKey($_) })
    if ($outside.Count -gt 0) {
        throw "Staged paths outside '$CommitGroup': $($outside -join ', ')"
    }

    $missingNew = New-Object Collections.Generic.List[string]
    foreach ($path in $requiredNew[$CommitGroup]) {
        $entry = @(git ls-files --stage -- $path)
        if ($LASTEXITCODE -ne 0) { throw "Unable to inspect staged entry '$path'." }
        if ($entry.Count -eq 0 -or $stagedPaths -notcontains $path) { $missingNew.Add($path) }
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
            if ($stagedPaths -notcontains $path) { continue }
            $indexLine = @(git ls-files --stage -- $path | Select-Object -First 1)
            if ($indexLine.Count -eq 0) { throw "Mixed tracked path is missing from the index: $path" }
            $indexHash = ($indexLine[0] -split '\s+')[1]
            $worktreeHash = (git hash-object -- $path).Trim()
            if ($LASTEXITCODE -ne 0) { throw "Unable to hash mixed path '$path'." }
            if ($indexHash -ceq $worktreeHash) {
                throw "Mixed path was staged as the whole working-copy file instead of reviewed hunks: $path"
            }
        }

        $changedLines = @(git diff --cached --unified=0 -- @($groups.task12) |
            Where-Object { $_ -match '^[+-](?![+-])' })
        $forbiddenHunkPattern = 'ManagedBrowserHelperPackageDirectory|HelperSourceDirectory|ITransferTargetDirectory|transferTargetDirectoryPicker|TargetDirectoryPicker|ShareAnalyticsPreference|startup-error\.txt|DirectBackground|PendingRowRuntimeStatus|WorkspaceLog|LocalResultExport|ReplacementWorkbook|ManagedChromium'
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
