[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$ModuleRoot = Join-Path $RepositoryRoot 'services\ota_admin'
$GoExe = Join-Path $RepositoryRoot '.tools\go-1.27.1\go\bin\go.exe'
$OutputDirectory = Join-Path $RepositoryRoot '.artifacts\ota-admin'
$OutputPath = Join-Path $OutputDirectory 'superpan-ota-admin-linux-amd64'

if (-not (Test-Path -LiteralPath $GoExe)) {
    throw "Pinned Go toolchain is missing. Run $PSScriptRoot\bootstrap_go_toolchain.ps1 first."
}

& $GoExe -C $ModuleRoot mod verify
if ($LASTEXITCODE -ne 0) { throw 'go mod verify failed.' }

& $GoExe -C $ModuleRoot test ./...
if ($LASTEXITCODE -ne 0) { throw 'go test failed.' }

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$env:CGO_ENABLED = '0'
$env:GOOS = 'linux'
$env:GOARCH = 'amd64'
& $GoExe -C $ModuleRoot build -trimpath -ldflags '-s -w' -o $OutputPath ./cmd/ota-admin
if ($LASTEXITCODE -ne 0) { throw 'static linux/amd64 build failed.' }
if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
    throw "Build did not create $OutputPath"
}

$hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "Built $OutputPath"
Write-Output "SHA256 $hash"
