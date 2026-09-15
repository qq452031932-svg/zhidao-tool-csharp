[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$AcceptanceConfigPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'ProductIdentity.ps1')
$productIdentity = Get-ProductIdentity -Path (Join-Path $repositoryRoot 'eng\ProductIdentity.props')
if ([string]::IsNullOrWhiteSpace($productIdentity.ProductMainAssemblyName) -or
    $productIdentity.ProductMainDllName -cne "$($productIdentity.ProductMainAssemblyName).dll") {
    throw 'Product identity does not define the expected main application assembly.'
}
$projectPath = Join-Path $repositoryRoot 'src\BaiduBatchTransfer.App\BaiduBatchTransfer.App.csproj'
if ([string]::IsNullOrWhiteSpace($AcceptanceConfigPath)) {
    $AcceptanceConfigPath = Join-Path $repositoryRoot '.tmp\license-local\client.json'
}
$acceptanceConfigPath = [IO.Path]::GetFullPath($AcceptanceConfigPath)
if (-not (Test-Path -LiteralPath $acceptanceConfigPath -PathType Leaf)) {
    throw 'Generated local acceptance client configuration is missing.'
}
$acceptanceConfig = Get-Content -LiteralPath $acceptanceConfigPath -Raw -Encoding utf8 | ConvertFrom-Json
$acceptanceEndpoint = $null
if (-not [bool]$acceptanceConfig.acceptance_only -or
    -not [Uri]::TryCreate([string]$acceptanceConfig.license_base_url, [UriKind]::Absolute, [ref]$acceptanceEndpoint) -or
    $acceptanceEndpoint.Scheme -ne [Uri]::UriSchemeHttp -or $acceptanceEndpoint.Host -ne '127.0.0.1' -or
    -not [string]::IsNullOrEmpty($acceptanceEndpoint.UserInfo) -or
    -not [string]::IsNullOrEmpty($acceptanceEndpoint.Query) -or
    -not [string]::IsNullOrEmpty($acceptanceEndpoint.Fragment)) {
    throw 'Generated local acceptance client configuration is not loopback-only.'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot '.artifacts\license-acceptance'
}
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputPath) | Out-Null

$publishArguments = @(
    'publish',
    $projectPath,
    '--configuration', 'Debug',
    '--no-restore',
    '--output', $outputPath,
    '/p:LicenseAcceptanceBuild=true',
    "/p:LicenseAcceptanceConfigPath=$acceptanceConfigPath",
    '/p:LicenseServiceUrl=http://127.0.0.1:19091'
)
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "Local license acceptance publish failed with exit code $LASTEXITCODE."
}

$assemblyPath = Join-Path $outputPath $productIdentity.ProductMainDllName
if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
    throw 'Local license acceptance output is missing the application assembly.'
}
$verificationRoot = Join-Path ([IO.Path]::GetTempPath()) ("bbt-license-metadata-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($verificationRoot) | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $verificationRoot 'verify.csproj'), @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
</Project>
'@)
    [IO.File]::WriteAllText((Join-Path $verificationRoot 'Program.cs'), @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

if (args.Length != 1) return 2;
var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
var metadata = assembly.GetCustomAttributesData()
    .Where(attribute => attribute.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute")
    .ToDictionary(
        attribute => (string?)attribute.ConstructorArguments[0].Value ?? string.Empty,
        attribute => (string?)attribute.ConstructorArguments[1].Value ?? string.Empty,
        StringComparer.Ordinal);
var valid = metadata.GetValueOrDefault("LicenseRuntimeMode") == "Acceptance"
    && metadata.GetValueOrDefault("LicenseAcceptanceBuild") == "true"
    && metadata.GetValueOrDefault("LicenseProductId") == "8270e8d7-1791-487d-ad76-0da7a0cbed12"
    && string.IsNullOrWhiteSpace(metadata.GetValueOrDefault("LicenseSigningPublicKey"))
    && string.IsNullOrWhiteSpace(metadata.GetValueOrDefault("LicenseCurrentSpkiPin"))
    && string.IsNullOrWhiteSpace(metadata.GetValueOrDefault("LicenseNextSpkiPin"));
return valid ? 0 : 1;
'@)
    & dotnet run --project (Join-Path $verificationRoot 'verify.csproj') --configuration Release -- $assemblyPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Local license acceptance output is not correctly stamped.'
    }
}
finally {
    if (Test-Path -LiteralPath $verificationRoot) {
        [IO.Directory]::Delete($verificationRoot, $true)
    }
}

Write-Output $outputPath
