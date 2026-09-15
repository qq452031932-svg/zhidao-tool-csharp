Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Xml.Linq

function Assert-SafeProductIdentityLeaf {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )

    $isDotSegment = $Value -ceq '.' -or $Value -ceq '..'
    $hasSeparator = $Value.IndexOf('/') -ge 0 -or $Value.IndexOf('\') -ge 0
    $hasInvalidCharacter = $Value.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0
    $hasNormalizedSuffix = $Value.EndsWith('.', [System.StringComparison]::Ordinal) -or
        $Value.EndsWith(' ', [System.StringComparison]::Ordinal)
    $hasReservedDeviceBaseName = [System.Text.RegularExpressions.Regex]::IsMatch(
        $Value,
        '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)')
    if ([System.IO.Path]::IsPathRooted($Value) -or
        $isDotSegment -or
        $hasSeparator -or
        $hasInvalidCharacter -or
        $hasNormalizedSuffix -or
        $hasReservedDeviceBaseName) {
        throw [System.IO.InvalidDataException]::new("Product identity field '$Name' must be a safe leaf name.")
    }
}

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

    if ($null -eq $document.Root -or
        $document.Root.Name.LocalName -cne 'Project' -or
        $document.Root.Name.NamespaceName -cne '' -or
        $document.Root.HasAttributes) {
        throw [System.IO.InvalidDataException]::new('Product identity source must have an unqualified, unattributed Project root element.')
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
        'ProductIconSha256'
    )
    $rootElements = @($document.Root.Elements())
    if ($rootElements.Count -ne 1) {
        throw [System.IO.InvalidDataException]::new('Product identity Project must contain exactly one PropertyGroup element.')
    }
    $propertyGroup = $rootElements[0]
    if ($propertyGroup.Name.LocalName -cne 'PropertyGroup' -or
        $propertyGroup.Name.NamespaceName -cne '' -or
        $propertyGroup.HasAttributes) {
        throw [System.IO.InvalidDataException]::new('Product identity PropertyGroup must be direct, unqualified, and unattributed.')
    }
    $propertyElements = @($propertyGroup.Elements())
    if ($propertyElements.Count -ne $requiredNames.Count) {
        throw [System.IO.InvalidDataException]::new('Product identity PropertyGroup must contain exactly the required fields.')
    }
    $values = [ordered]@{}

    foreach ($element in $propertyElements) {
        $name = $element.Name.LocalName
        if ($element.Name.NamespaceName -cne '' -or $requiredNames -cnotcontains $name) {
            throw [System.IO.InvalidDataException]::new("Unknown or incorrectly cased product identity field '$name'.")
        }
        if ($values.Contains($name)) {
            throw [System.IO.InvalidDataException]::new("Product identity field '$name' must appear exactly once.")
        }
        if ($element.HasAttributes) {
            throw [System.IO.InvalidDataException]::new("Product identity field '$name' must not be conditional or attributed.")
        }
        $nonTextNodes = @($element.Nodes() | Where-Object { $_.NodeType -ne [System.Xml.XmlNodeType]::Text })
        if ($element.HasElements -or $nonTextNodes.Count -ne 0) {
            throw [System.IO.InvalidDataException]::new("Product identity field '$name' must contain text only.")
        }
        $value = $element.Value
        if ([string]::IsNullOrWhiteSpace($value) -or $value -ne $value.Trim() -or $value.IndexOfAny([char[]]@("`r", "`n", "`0")) -ge 0) {
            throw [System.IO.InvalidDataException]::new("Product identity field '$name' must be a non-empty single-line value without surrounding whitespace.")
        }
        $containsMsBuildExpression = $value.IndexOf('$(', [System.StringComparison]::Ordinal) -ge 0 -or
            $value.IndexOf('@(', [System.StringComparison]::Ordinal) -ge 0 -or
            $value.IndexOf('%(', [System.StringComparison]::Ordinal) -ge 0 -or
            [System.Text.RegularExpressions.Regex]::IsMatch($value, '%[0-9A-Fa-f]{2}')
        if ($containsMsBuildExpression) {
            throw [System.IO.InvalidDataException]::new("Product identity field '$name' must not contain MSBuild evaluation syntax or escapes.")
        }
        $values[$name] = $value
    }

    foreach ($name in @(
        'ProductMainAssemblyName',
        'ProductMainExecutableName',
        'ProductMainDllName',
        'ProductLegacyAssemblyName',
        'ProductLegacyExecutableName',
        'ProductDataDirectoryName')) {
        Assert-SafeProductIdentityLeaf -Name $name -Value $values[$name]
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
    if ($values.ProductIconSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw [System.IO.InvalidDataException]::new('ProductIconSha256 must be a lowercase SHA-256 value.')
    }

    return [pscustomobject]$values
}
