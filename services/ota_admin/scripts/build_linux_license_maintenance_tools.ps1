param(
  [string]$OutputDirectory = "$PSScriptRoot\..\..\.artifacts\license-maintenance"
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$go = Join-Path $repo '.tools\go-1.27.1\go\bin\go.exe'
if (-not (Test-Path $go)) { throw "pinned Go toolchain not found" }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
& $go -C (Join-Path $repo 'services\ota_admin') build -trimpath -o (Join-Path $OutputDirectory 'license-db-snapshot') ./cmd/license-db-snapshot
if ($LASTEXITCODE -ne 0) { throw "maintenance tool build failed" }
Write-Output (Join-Path $OutputDirectory 'license-db-snapshot')
