Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Xml.Linq

function Get-ProductIdentity {
    [CmdletBinding()]
    param(
        [Parameter()]
        [string]$Path = (Join-Path (Split-Path -Parent $PSScriptRoot) 'eng\ProductIdentity.props')
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not [System.IO.File]::Exists($resolvedPath)) {
        throw [System.IO.FileNotFoundException]::new('Product identity source was not found.', $resolvedPath)
    }

    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    try {
        $reader = [System.Xml.XmlReader]::Create($resolvedPath, $settings)
        try {
            $document = [System.Xml.Linq.XDocument]::Load($reader, [System.Xml.Linq.LoadOptions]::None)
        }
        finally {
            $reader.Dispose()
        }
    }
    catch {
        throw [System.IO.InvalidDataException]::new("Product identity source is not valid XML: $resolvedPath. $($_.Exception.Message)", $_.Exception)
    }

    if ($null -eq $document.Root -or $document.Root.Name.LocalName -ne 'Project' -or $document.Root.Name.NamespaceName -ne '') {
        throw [System.IO.InvalidDataException]::new('Product identity source must have an unqualified Project root element.')
    }

    $requiredNames = @(
        'ProductDisplayName',
        'ProductDescription',
        'ProductMainAssemblyName',
        'ProductMainExecutableName',
        'ProductMainDllName',
        'ProductLegacyAssemblyName',
        'ProductLegacyExecutableName',
        'ProductPackageId',
        'ProductDataDirectoryName',
        'ProductLicenseProductId'
    )
    $propertyGroups = @($document.Root.Elements() |
        Where-Object { $_.Name.LocalName -eq 'PropertyGroup' -and $_.Name.NamespaceName -eq '' })
    foreach ($propertyGroup in $propertyGroups) {
        if ($propertyGroup.HasAttributes) {
            throw [System.IO.InvalidDataException]::new('Product identity PropertyGroup must not be conditional or attributed.')
        }
    }
    $propertyElements = @($propertyGroups | ForEach-Object { $_.Elements() })
    $values = [ordered]@{}

    foreach ($name in $requiredNames) {
        $matches = @($propertyElements | Where-Object { $_.Name.LocalName -eq $name -and $_.Name.NamespaceName -eq '' })
        if ($matches.Count -ne 1) {
            throw [System.IO.InvalidDataException]::new("Product identity field '$name' must appear exactly once.")
        }
        if ($matches[0].HasAttributes) {
            throw [System.IO.InvalidDataException]::new("Product identity field '$name' must not be conditional or attributed.")
        }
        $value = $matches[0].Value
        if ([string]::IsNullOrWhiteSpace($value) -or $value -ne $value.Trim() -or $value.IndexOfAny([char[]]@("`r", "`n", "`0")) -ge 0) {
            throw [System.IO.InvalidDataException]::new("Product identity field '$name' must be a non-empty single-line value without surrounding whitespace.")
        }
        $values[$name] = $value
    }

    if ($values.ProductMainExecutableName -ne "$($values.ProductMainAssemblyName).exe") {
        throw [System.IO.InvalidDataException]::new('ProductMainExecutableName must match ProductMainAssemblyName.')
    }
    if ($values.ProductMainDllName -ne "$($values.ProductMainAssemblyName).dll") {
        throw [System.IO.InvalidDataException]::new('ProductMainDllName must match ProductMainAssemblyName.')
    }
    if ($values.ProductLegacyExecutableName -ne "$($values.ProductLegacyAssemblyName).exe") {
        throw [System.IO.InvalidDataException]::new('ProductLegacyExecutableName must match ProductLegacyAssemblyName.')
    }
    $licenseProductId = [guid]::Empty
    if (-not [guid]::TryParseExact($values.ProductLicenseProductId, 'D', [ref]$licenseProductId)) {
        throw [System.IO.InvalidDataException]::new('ProductLicenseProductId must be a canonical GUID.')
    }

    return [pscustomobject]$values
}
