param([string]$OutputDirectory = "$PSScriptRoot\..\..\.artifacts\license-test-tools")
$ErrorActionPreference='Stop'
$repo=(Resolve-Path "$PSScriptRoot\..\..\..").Path; $go=Join-Path $repo '.tools\go-1.27.1\go\bin\go.exe'
if(-not (Test-Path $go)){throw 'pinned Go toolchain not found'}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
& $go -C (Join-Path $repo 'services\ota_admin') build -trimpath -o (Join-Path $OutputDirectory 'license-loadtest') ./cmd/license-loadtest
if($LASTEXITCODE -ne 0){throw 'loadtest build failed'}
& $go -C (Join-Path $repo 'services\ota_admin') build -trimpath -o (Join-Path $OutputDirectory 'license-dbcheck') ./cmd/license-dbcheck
if($LASTEXITCODE -ne 0){throw 'dbcheck build failed'}
