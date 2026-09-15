[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Pack', 'InstallUpgrade')]
    [string]$Phase,

    [string]$WorkRoot,

    [string]$ExpectedIsolatedProfilePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:RepositoryRoot = Split-Path -Parent $PSScriptRoot
$script:PackageId = 'VelopackExecutableRenameSpike'
$script:OldMainExe = 'BaiduBatchTransfer.App.exe'
# UTF-8 contract text: 知道综合工具.exe. Construct it from code points because Windows PowerShell 5.1
# decodes UTF-8-without-BOM scripts using the active ANSI code page.
$script:NewDisplayName = "$([char]0x77E5)$([char]0x9053)$([char]0x7EFC)$([char]0x5408)$([char]0x5DE5)$([char]0x5177)"
$script:NewMainExe = $script:NewDisplayName + '.exe'
$script:EvidenceDirectoryName = 'VelopackExecutableRenameSpikeEvidence'
$script:VelopackUpdatedArgument = '--veloapp-updated'

function Assert-IsolatedWindowsProfile {
    param([AllowEmptyString()][string]$ExpectedPath)

    if ($env:VELOPACK_RENAME_SPIKE_ISOLATED_PROFILE -cne 'I_UNDERSTAND_THIS_PROFILE_IS_DISPOSABLE') {
        throw 'InstallUpgrade requires an isolated Windows profile. Set VELOPACK_RENAME_SPIKE_ISOLATED_PROFILE only inside a disposable Sandbox, VM, or isolated test-user profile.'
    }

    $actualProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if ([string]::IsNullOrWhiteSpace($ExpectedPath)) {
        throw 'InstallUpgrade requires -ExpectedIsolatedProfilePath for the isolated Windows profile.'
    }

    $expectedFullPath = [IO.Path]::GetFullPath($ExpectedPath).TrimEnd('\')
    $actualFullPath = [IO.Path]::GetFullPath($actualProfile).TrimEnd('\')
    if (-not [string]::Equals($expectedFullPath, $actualFullPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "InstallUpgrade requires an isolated Windows profile. Expected '$expectedFullPath', actual '$actualFullPath'."
    }
    return $actualFullPath
}

function Get-PinnedVelopackVersions {
    $manifestPath = Join-Path $script:RepositoryRoot '.config\dotnet-tools.json'
    $packagesPath = Join-Path $script:RepositoryRoot 'Directory.Packages.props'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $cliVersion = [string]$manifest.tools.vpk.version
    [xml]$packages = Get-Content -LiteralPath $packagesPath -Raw
    $sdkNodes = @(@($packages.Project.ItemGroup.PackageVersion) |
        Where-Object { $_.Include -ceq 'Velopack' })
    if ($sdkNodes.Count -ne 1) {
        throw "Expected exactly one central Velopack SDK pin, found $($sdkNodes.Count)."
    }
    $sdkVersion = [string]$sdkNodes[0].Version

    if ([string]::IsNullOrWhiteSpace($cliVersion) -or [string]::IsNullOrWhiteSpace($sdkVersion)) {
        throw 'Unable to read the repository-pinned Velopack CLI/SDK versions.'
    }
    if ($cliVersion -cne $sdkVersion) {
        throw "Velopack CLI '$cliVersion' and SDK '$sdkVersion' must be pinned as a pair."
    }

    return [pscustomobject]@{ Cli = $cliVersion; Sdk = $sdkVersion }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
        $commandExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($commandExitCode -ne 0) {
        throw "Command failed with exit code ${commandExitCode}: $FilePath"
    }
}

function New-SampleProject {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$AssemblyName,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$VelopackVersion
    )

    New-Item -ItemType Directory -Path $Directory | Out-Null
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <AssemblyName>$AssemblyName</AssemblyName>
    <Version>$Version</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Velopack" Version="$VelopackVersion" />
  </ItemGroup>
</Project>
"@
    Set-Content -LiteralPath (Join-Path $Directory 'Spike.csproj') -Value $project -Encoding UTF8

    $program = @'
using Velopack;

var evidenceRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "VelopackExecutableRenameSpikeEvidence");
Directory.CreateDirectory(evidenceRoot);
File.AppendAllText(
    Path.Combine(evidenceRoot, "launches.log"),
    $"{DateTimeOffset.UtcNow:O}|{Environment.ProcessPath}|{string.Join(" ", args)}{Environment.NewLine}");

VelopackApp.Build().Run();

if (args.Length == 2 && args[0] == "--spike-apply")
{
    var manager = new UpdateManager(args[1]);
    var update = await manager.CheckForUpdatesAsync()
        ?? throw new InvalidOperationException("The rename spike feed contains no update.");
    await manager.DownloadUpdatesAsync(update);
    manager.ApplyUpdatesAndRestart(update.TargetFullRelease, new[] { "--spike-restarted" });
}
'@
    Set-Content -LiteralPath (Join-Path $Directory 'Program.cs') -Value $program -Encoding UTF8
}

function Assert-FullPackageContainsMainExecutable {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$MainExecutable
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $expectedEntry = 'lib/app/' + $MainExecutable
        if (-not @($archive.Entries | Where-Object { $_.FullName -ceq $expectedEntry })) {
            throw "Package '$PackagePath' is missing expected main executable '$expectedEntry'."
        }
        Write-Host "PACKAGE_MAIN_EXE=$expectedEntry"
    } finally {
        $archive.Dispose()
    }
}

function Invoke-PackPhase {
    param([Parameter(Mandatory = $true)][string]$Root)

    if (Test-Path -LiteralPath $Root) {
        throw "WorkRoot already exists; refusing to overwrite it: $Root"
    }
    New-Item -ItemType Directory -Path $Root | Out-Null

    $versions = Get-PinnedVelopackVersions
    Write-Host "Pinned Velopack CLI/SDK pair: $($versions.Cli)"
    $cliHelp = (& dotnet tool run vpk -- --help 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $cliHelp -notmatch "Velopack CLI\s+$([regex]::Escape($versions.Cli))(?=[^0-9]|$)") {
        throw "dotnet tool run vpk is not exactly the pinned CLI version $($versions.Cli)."
    }

    $releaseDirectory = Join-Path $Root 'Releases'
    New-Item -ItemType Directory -Path $releaseDirectory | Out-Null
    foreach ($sample in @(
        [pscustomobject]@{ Name = 'old'; Assembly = 'BaiduBatchTransfer.App'; Version = '1.0.0'; MainExe = $script:OldMainExe; Title = 'Baidu Batch Transfer' },
        [pscustomobject]@{ Name = 'new'; Assembly = $script:NewDisplayName; Version = '1.1.0'; MainExe = $script:NewMainExe; Title = $script:NewDisplayName }
    )) {
        $sourceDirectory = Join-Path $Root "$($sample.Name)-source"
        $publishDirectory = Join-Path $Root "$($sample.Name)-publish"
        New-SampleProject -Directory $sourceDirectory -AssemblyName $sample.Assembly -Version $sample.Version -VelopackVersion $versions.Sdk
        Invoke-Checked -FilePath 'dotnet' -WorkingDirectory $script:RepositoryRoot -Arguments @(
            'publish', (Join-Path $sourceDirectory 'Spike.csproj'), '-c', 'Release', '-r', 'win-x64',
            '--self-contained', 'false', '-o', $publishDirectory
        )
        Invoke-Checked -FilePath 'dotnet' -WorkingDirectory $script:RepositoryRoot -Arguments @(
            'tool', 'run', 'vpk', 'pack', '--packId', $script:PackageId,
            '--packVersion', $sample.Version, '--packDir', $publishDirectory,
            '--mainExe', $sample.MainExe, '--packTitle', $sample.Title,
            '--shortcuts', 'Desktop,StartMenuRoot', '--outputDir', $releaseDirectory,
            '--channel', 'win-x64', '--runtime', 'win-x64', '--delta', 'None'
        )
        if ($sample.Name -ceq 'old') {
            $generatedSetup = Join-Path $releaseDirectory "$($script:PackageId)-win-x64-Setup.exe"
            Copy-Item -LiteralPath $generatedSetup -Destination (Join-Path $Root 'old-setup.exe')
        }
    }

    $fullPackages = @(Get-ChildItem -LiteralPath $releaseDirectory -Filter '*-full.nupkg')
    if ($fullPackages.Count -ne 2) {
        throw "Expected two full packages, found $($fullPackages.Count)."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $releaseDirectory "$($script:PackageId)-win-x64-Setup.exe"))) {
        throw 'The pack phase did not produce the expected Setup executable.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Root 'old-setup.exe'))) {
        throw 'The pack phase did not preserve the old-version Setup executable.'
    }

    Assert-FullPackageContainsMainExecutable `
        -PackagePath (Join-Path $releaseDirectory "$($script:PackageId)-1.0.0-win-x64-full.nupkg") `
        -MainExecutable $script:OldMainExe
    Assert-FullPackageContainsMainExecutable `
        -PackagePath (Join-Path $releaseDirectory "$($script:PackageId)-1.1.0-win-x64-full.nupkg") `
        -MainExecutable $script:NewMainExe

    Write-Host "PACK_STRUCTURE_PASS=$releaseDirectory"
    Write-Host 'The install/upgrade phase remains unexecuted until this directory is copied into a disposable Windows profile.'
}

function Get-ShortcutTarget {
    param([Parameter(Mandatory = $true)][string]$Path)
    $shell = New-Object -ComObject WScript.Shell
    return [string]$shell.CreateShortcut($Path).TargetPath
}

function Assert-PathEndsWith {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$Actual,
        [Parameter(Mandatory = $true)][string]$ExpectedSuffix
    )
    if (-not $Actual.EndsWith($ExpectedSuffix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label expected suffix '$ExpectedSuffix', actual '$Actual'."
    }
    Write-Host "$Label=$Actual"
}

function Invoke-InstallUpgradePhase {
    param([Parameter(Mandatory = $true)][string]$Root)

    $isolatedProfile = Assert-IsolatedWindowsProfile -ExpectedPath $ExpectedIsolatedProfilePath
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Pack-phase WorkRoot does not exist: $Root"
    }

    $releaseDirectory = Join-Path $Root 'Releases'
    $setup = Join-Path $Root 'old-setup.exe'
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
        throw "Setup executable does not exist: $setup"
    }

    $localAppData = Join-Path $isolatedProfile 'AppData\Local'
    $desktop = Join-Path $isolatedProfile 'Desktop'
    $startMenu = Join-Path $isolatedProfile 'AppData\Roaming\Microsoft\Windows\Start Menu'
    $installRoot = Join-Path $localAppData $script:PackageId
    if (Test-Path -LiteralPath $installRoot) {
        throw "Isolated profile is not clean; install root already exists: $installRoot"
    }

    Invoke-Checked -FilePath $setup -WorkingDirectory $releaseDirectory -Arguments @('--silent')
    $oldCurrentMain = Join-Path $installRoot "current\$($script:OldMainExe)"
    if (-not (Test-Path -LiteralPath $oldCurrentMain -PathType Leaf)) {
        throw "Old version did not install at the expected current path: $oldCurrentMain"
    }

    Invoke-Checked -FilePath $oldCurrentMain -WorkingDirectory (Split-Path -Parent $oldCurrentMain) -Arguments @(
        '--spike-apply', $releaseDirectory
    )

    $evidenceLog = Join-Path $localAppData "$($script:EvidenceDirectoryName)\launches.log"
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $lifecycleEvidence = $null
    $restartEvidence = $null
    do {
        Start-Sleep -Milliseconds 250
        if (Test-Path -LiteralPath $evidenceLog) {
            $launchEvidence = @(Get-Content -LiteralPath $evidenceLog -Encoding UTF8)
            $lifecycleEvidence = $launchEvidence | Where-Object { $_ -like "*$($script:VelopackUpdatedArgument)*" } | Select-Object -Last 1
            $restartEvidence = $launchEvidence | Where-Object { $_ -like '*--spike-restarted*' } | Select-Object -Last 1
        }
        if ($lifecycleEvidence -and $restartEvidence) { break }
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $lifecycleEvidence -or -not $restartEvidence) {
        throw "UpgradeRestartTarget evidence was not recorded within 45 seconds: $evidenceLog"
    }

    $rootLauncher = Join-Path $installRoot $script:NewMainExe
    $currentMain = Join-Path $installRoot "current\$($script:NewMainExe)"
    $desktopShortcut = Join-Path $desktop ($script:NewDisplayName + '.lnk')
    $startMenuShortcut = Join-Path $startMenu ('Programs\' + $script:NewDisplayName + '.lnk')
    foreach ($required in @($rootLauncher, $currentMain, $desktopShortcut, $startMenuShortcut)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Required renamed surface is missing: $required"
        }
    }

    Assert-PathEndsWith -Label 'InstallRootLauncher' -Actual $rootLauncher -ExpectedSuffix $script:NewMainExe
    Assert-PathEndsWith -Label 'CurrentMainExecutable' -Actual $currentMain -ExpectedSuffix "current\$($script:NewMainExe)"
    Assert-PathEndsWith -Label 'DesktopShortcut' -Actual (Get-ShortcutTarget $desktopShortcut) -ExpectedSuffix $script:NewMainExe
    Assert-PathEndsWith -Label 'StartMenuShortcut' -Actual (Get-ShortcutTarget $startMenuShortcut) -ExpectedSuffix $script:NewMainExe
    Assert-PathEndsWith -Label 'UpgradeRestartTarget' -Actual (($restartEvidence -split '\|')[1]) -ExpectedSuffix "current\$($script:NewMainExe)"
    Write-Host 'INSTALL_UPGRADE_PASS'
}

if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    $WorkRoot = Join-Path $script:RepositoryRoot ".artifacts\velopack-executable-rename-spike-$([Guid]::NewGuid().ToString('N'))"
}
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)

if ($Phase -ceq 'Pack') {
    Invoke-PackPhase -Root $WorkRoot
} else {
    Invoke-InstallUpgradePhase -Root $WorkRoot
}
